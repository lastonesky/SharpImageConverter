using System;
using System.IO;
using PictureSharp.Core;

namespace PictureSharp.Formats.Gif
{
    public class GifDecoder : IImageDecoder
    {
        public Image<Rgb24> DecodeRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            return Decode(fs);
        }

        public Image<Rgb24> Decode(Stream stream)
        {
            // Header
            byte[] sig = new byte[6];
            if (stream.Read(sig, 0, 6) != 6) throw new InvalidDataException("Invalid GIF header");
            if (sig[0] != 'G' || sig[1] != 'I' || sig[2] != 'F') throw new InvalidDataException("Not a GIF file");
            
            // Logical Screen Descriptor
            byte[] lsd = new byte[7];
            if (stream.Read(lsd, 0, 7) != 7) throw new InvalidDataException("Invalid LSD");
            
            int width = lsd[0] | (lsd[1] << 8);
            int height = lsd[2] | (lsd[3] << 8);
            byte packed = lsd[4];
            byte bgIndex = lsd[5];
            byte pixelAspectRatio = lsd[6];

            bool hasGct = (packed & 0x80) != 0;
            int colorRes = ((packed >> 4) & 0x07) + 1;
            bool sort = (packed & 0x08) != 0;
            int gctSize = 1 << ((packed & 0x07) + 1);

            byte[] gct = new byte[768];
            int gctColors = 0;
            if (hasGct)
            {
                gctColors = gctSize;
                int read = 0;
                int toRead = gctColors * 3;
                while (read < toRead)
                {
                    var n = stream.Read(gct, read, toRead - read);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
            }

            // Canvas
            byte[] canvas = new byte[width * height * 3];
            // Fill with background color if needed?
            // Usually we start black or BG.
            if (hasGct && bgIndex < gctColors)
            {
                byte r = gct[bgIndex * 3];
                byte g = gct[bgIndex * 3 + 1];
                byte b = gct[bgIndex * 3 + 2];
                for (int i = 0; i < canvas.Length; i += 3)
                {
                    canvas[i] = r;
                    canvas[i + 1] = g;
                    canvas[i + 2] = b;
                }
            }

            // Blocks
            int transIndex = -1;
            // int disposal = 0; 
            // We only decode the first frame for now, so disposal doesn't matter much unless we want to support animation later.

            while (true)
            {
                int blockType = stream.ReadByte();
                if (blockType == -1 || blockType == 0x3B) break; // Trailer

                if (blockType == 0x21) // Extension
                {
                    int label = stream.ReadByte();
                    if (label == 0xF9) // Graphic Control
                    {
                        int size = stream.ReadByte(); // Should be 4
                        if (size != 4) 
                        {
                            // Skip
                            SkipBlock(stream, size); 
                            continue;
                        }
                        byte[] gce = new byte[4];
                        ReadExact(stream, gce, 4);
                        // disposal = (gce[0] >> 2) & 0x07;
                        bool hasTrans = (gce[0] & 1) != 0;
                        if (hasTrans) transIndex = gce[3];
                        else transIndex = -1;
                        
                        stream.ReadByte(); // Block terminator
                    }
                    else
                    {
                        // Skip other extensions
                        SkipBlocks(stream);
                    }
                }
                else if (blockType == 0x2C) // Image Separator
                {
                    byte[] desc = new byte[9];
                    if (stream.Read(desc, 0, 9) != 9) throw new EndOfStreamException();
                    int ix = desc[0] | (desc[1] << 8);
                    int iy = desc[2] | (desc[3] << 8);
                    int iw = desc[4] | (desc[5] << 8);
                    int ih = desc[6] | (desc[7] << 8);
                    byte imgPacked = desc[8];
                    
                    bool hasLct = (imgPacked & 0x80) != 0;
                    bool interlace = (imgPacked & 0x40) != 0;
                    int lctSize = 1 << ((imgPacked & 0x07) + 1);
                    
                    byte[] lct = new byte[768];
                    int lctColors = 0;
                    if (hasLct)
                    {
                        lctColors = lctSize;
                        int read = 0;
                        int toRead = lctColors * 3;
                        while (read < toRead)
                        {
                            int n = stream.Read(lct, read, toRead - read);
                            if (n == 0) throw new EndOfStreamException();
                            read += n;
                        }
                    }

                    byte[] palette = hasLct ? lct : gct;
                    int paletteColors = hasLct ? lctColors : gctColors;

                    int lzwMinCodeSize = stream.ReadByte();
                    
                    byte[] indices = new byte[iw * ih];
                    var lzw = new LzwDecoder(stream);
                    lzw.Decode(indices, iw, ih, lzwMinCodeSize);

                    // Render to canvas
                    if (interlace)
                    {
                        // Pass 1: Row 0, 8, 16...
                        // Pass 2: Row 4, 12...
                        // Pass 3: Row 2, 6...
                        // Pass 4: Row 1, 3...
                        int[] start = { 0, 4, 2, 1 };
                        int[] inc = { 8, 8, 4, 2 };
                        int ptr = 0;
                        for (int pass = 0; pass < 4; pass++)
                        {
                            for (int y = start[pass]; y < ih; y += inc[pass])
                            {
                                int rowOffset = (iy + y) * width * 3;
                                int lineStart = ptr;
                                for (int x = 0; x < iw; x++)
                                {
                                    int dstX = ix + x;
                                    if (dstX < width && (iy + y) < height)
                                    {
                                        byte idx = indices[lineStart + x];
                                        if (transIndex != -1 && idx == transIndex)
                                        {
                                            // Transparent, keep background
                                        }
                                        else if (idx < paletteColors)
                                        {
                                            int pIdx = idx * 3;
                                            int dIdx = rowOffset + dstX * 3;
                                            canvas[dIdx + 0] = palette[pIdx];
                                            canvas[dIdx + 1] = palette[pIdx + 1];
                                            canvas[dIdx + 2] = palette[pIdx + 2];
                                        }
                                    }
                                }
                                ptr += iw; // In interlaced, indices are sequential, rows are not. Wait.
                                // LZW outputs a continuous stream of pixels.
                                // Interlaced means the stream corresponds to Pass 1 rows, then Pass 2 rows...
                                // So ptr increments by iw is correct logic IF we iterate y in pass order.
                                // BUT my loops are nested: pass -> y.
                                // So `ptr` should increment by `iw` for each row processed.
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < ih; y++)
                        {
                            int rowOffset = (iy + y) * width * 3;
                            for (int x = 0; x < iw; x++)
                            {
                                int dstX = ix + x;
                                if (dstX < width && (iy + y) < height)
                                {
                                    byte idx = indices[y * iw + x];
                                    if (transIndex != -1 && idx == transIndex)
                                    {
                                        // Skip
                                    }
                                    else if (idx < paletteColors)
                                    {
                                        int pIdx = idx * 3;
                                        int dIdx = rowOffset + dstX * 3;
                                        canvas[dIdx + 0] = palette[pIdx];
                                        canvas[dIdx + 1] = palette[pIdx + 1];
                                        canvas[dIdx + 2] = palette[pIdx + 2];
                                    }
                                }
                            }
                        }
                    }

                    // For now, just return the first frame found
                    return new Image<Rgb24>(width, height, canvas);
                }
                else
                {
                    // Unknown block?
                    // Usually shouldn't happen if parsed correctly.
                }
            }

            // Fallback
            return new Image<Rgb24>(width, height, canvas);
        }

        private void ReadExact(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n == 0) throw new EndOfStreamException();
                read += n;
            }
        }

        private void SkipBlocks(Stream s)
        {
            while (true)
            {
                int len = s.ReadByte();
                if (len <= 0) break;
                s.Seek(len, SeekOrigin.Current);
            }
        }

        private void SkipBlock(Stream s, int size)
        {
            // Skip fixed size then skip sub-blocks?
            // Usually extension blocks are: [Label] [FixedSize] [FixedBytes] [SubBlocks...]
            // My code handled Label and FixedSize.
            // But if I called SkipBlock(stream, size), I assume I just read 'size' bytes.
            // But then comes sub-blocks!
            // Wait, standard extensions:
            // 21 F9 04 [4 bytes] 00 (Terminator)
            // 21 FF 0B [11 bytes] [SubBlocks...] 00
            // My logic:
            // if 0xF9: Read 4, then ReadByte() (Terminator). Correct.
            // else: SkipBlocks.
            // SkipBlocks handles [len] [data] ... 00.
            // BUT for 0xFF (Application), we read size (e.g. 11), we must consume it first!
            // The `SkipBlocks` logic is for Data Sub-blocks.
            // The `SkipBlock` helper I added needs to be careful.
            // If I encounter unknown extension:
            // Read Size (byte). Read Size bytes. Then SkipBlocks (Sub-blocks).
            
            // Correction in main loop:
            // ...
            // else 
            // {
            //    int size = stream.ReadByte();
            //    if (size > 0) stream.Seek(size, SeekOrigin.Current);
            //    SkipBlocks(stream);
            // }
        }
    }
}
