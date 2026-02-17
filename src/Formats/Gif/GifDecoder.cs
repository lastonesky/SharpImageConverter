using System;
using System.Buffers;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif
{
    public sealed class GifAnimation(IReadOnlyList<Image<Rgb24>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount)
    {
        public IReadOnlyList<Image<Rgb24>> Frames { get; } = frames;
        public IReadOnlyList<int> FrameDurationsMs { get; } = frameDurationsMs;
        public int LoopCount { get; } = loopCount;
    }

    /// <summary>
    /// GIF 解码器，支持多帧与单帧解码为 Rgb24。
    /// </summary>
    public class GifDecoder : IImageDecoder
    {
        private static readonly int[] InterlaceStart = { 0, 4, 2, 1 };
        private static readonly int[] InterlaceInc = { 8, 8, 4, 2 };

        public GifAnimation DecodeAnimationRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            return DecodeAnimationRgb24(fs);
        }

        public GifAnimation DecodeAnimationRgb24(Stream stream)
        {
            byte[] sig = new byte[6];
            if (stream.Read(sig, 0, 6) != 6) throw new InvalidDataException("Invalid GIF header");
            if (sig[0] != 'G' || sig[1] != 'I' || sig[2] != 'F') throw new InvalidDataException("Not a GIF file");

            byte[] lsd = new byte[7];
            if (stream.Read(lsd, 0, 7) != 7) throw new InvalidDataException("Invalid LSD");
            int width = lsd[0] | (lsd[1] << 8);
            int height = lsd[2] | (lsd[3] << 8);
            byte packed = lsd[4];
            byte bgIndex = lsd[5];

            bool hasGct = (packed & 0x80) != 0;
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

            byte[] canvas = new byte[width * height * 3];
            int canvasStride = width * 3;
            byte bgR = 0, bgG = 0, bgB = 0;
            if (hasGct && bgIndex < gctColors)
            {
                bgR = gct[bgIndex * 3];
                bgG = gct[bgIndex * 3 + 1];
                bgB = gct[bgIndex * 3 + 2];
                for (int i = 0; i < canvas.Length; i += 3)
                {
                    canvas[i] = bgR;
                    canvas[i + 1] = bgG;
                    canvas[i + 2] = bgB;
                }
            }

            int transIndex = -1;
            int disposal = 0;
            int delayCs = 0;
            int loopCount = 1;

            var frames = new List<Image<Rgb24>>();
            var durations = new List<int>();

            byte[]? prevCanvas = null;
            byte[] lct = new byte[768];
            byte[] desc = new byte[9];
            byte[] gce = new byte[4];
            var pool = ArrayPool<byte>.Shared;
            while (true)
            {
                int blockType = stream.ReadByte();
                if (blockType == -1 || blockType == 0x3B) break;

                if (blockType == 0x21)
                {
                    int label = stream.ReadByte();
                    if (label == 0xF9)
                    {
                        int size = stream.ReadByte();
                        if (size != 4)
                        {
                            if (size > 0) stream.Seek(size, SeekOrigin.Current);
                            stream.ReadByte();
                            continue;
                        }
                        ReadExact(stream, gce, 4);
                        disposal = (gce[0] >> 2) & 0x07;
                        bool hasTrans = (gce[0] & 1) != 0;
                        delayCs = gce[1] | (gce[2] << 8);
                        transIndex = hasTrans ? gce[3] : -1;
                        stream.ReadByte();
                    }
                    else if (label == 0xFF)
                    {
                        int size = stream.ReadByte();
                        byte[] app = new byte[size];
                        if (size > 0) ReadExact(stream, app, size);
                        string appId = System.Text.Encoding.ASCII.GetString(app, 0, app.Length);
                        if (appId == "NETSCAPE2.0" || appId == "ANIMEXTS1.0")
                        {
                            int subLen = stream.ReadByte();
                            if (subLen < 0) throw new EndOfStreamException();
                            if (subLen > 0)
                            {
                                var sub = new byte[subLen];
                                ReadExact(stream, sub, subLen);
                                if (subLen >= 3 && sub[0] == 1)
                                {
                                    int rep = sub[1] | (sub[2] << 8);
                                    loopCount = rep == 0 ? 0 : rep;
                                }
                            }
                            SkipBlocks(stream);
                        }
                        else
                        {
                            SkipBlocks(stream);
                        }
                    }
                    else
                    {
                        int size = stream.ReadByte();
                        if (size > 0) stream.Seek(size, SeekOrigin.Current);
                        SkipBlocks(stream);
                    }
                }
                else if (blockType == 0x2C)
                {
                    if (stream.Read(desc, 0, 9) != 9) throw new EndOfStreamException();
                    int ix = desc[0] | (desc[1] << 8);
                    int iy = desc[2] | (desc[3] << 8);
                    int iw = desc[4] | (desc[5] << 8);
                    int ih = desc[6] | (desc[7] << 8);
                    byte imgPacked = desc[8];
                    bool hasLct = (imgPacked & 0x80) != 0;
                    bool interlace = (imgPacked & 0x40) != 0;
                    int lctSize = 1 << ((imgPacked & 0x07) + 1);

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
                    int indexCount = iw * ih;
                    byte[] indices = pool.Rent(indexCount);
                    var lzw = new LzwDecoder(stream);
                    try
                    {
                        lzw.Decode(indices, iw, ih, lzwMinCodeSize);

                        if (disposal == 3)
                            prevCanvas = (byte[])canvas.Clone();

                        if (interlace)
                        {
                            int ptr = 0;
                            for (int pass = 0; pass < 4; pass++)
                            {
                                int startY = InterlaceStart[pass];
                                int incY = InterlaceInc[pass];
                                for (int y = startY; y < ih; y += incY)
                                {
                                    int dstY = iy + y;
                                    int rowOffset = dstY * canvasStride;
                                    int lineStart = ptr;
                                    for (int x = 0; x < iw; x++)
                                    {
                                        int dstX = ix + x;
                                        if (dstX < width && dstY < height)
                                        {
                                            byte idx = indices[lineStart + x];
                                            if (transIndex != -1 && idx == transIndex)
                                            {
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
                                    ptr += iw;
                                }
                            }
                        }
                        else
                        {
                            for (int y = 0; y < ih; y++)
                            {
                                int dstY = iy + y;
                                int rowOffset = dstY * canvasStride;
                                for (int x = 0; x < iw; x++)
                                {
                                    int dstX = ix + x;
                                    if (dstX < width && dstY < height)
                                    {
                                        byte idx = indices[y * iw + x];
                                        if (transIndex != -1 && idx == transIndex)
                                        {
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

                        int lastIx = ix;
                        int lastIy = iy;
                        int lastIw = iw;
                        int lastIh = ih;
                        var frameBuf = new byte[canvas.Length];
                        Buffer.BlockCopy(canvas, 0, frameBuf, 0, canvas.Length);
                        frames.Add(new Image<Rgb24>(width, height, frameBuf));

                        int ms = delayCs * 10;
                        durations.Add(ms < 10 ? 10 : ms);

                        if (disposal == 2)
                        {
                            for (int y = 0; y < lastIh; y++)
                            {
                                int dstY = lastIy + y;
                                int rowOffset = dstY * canvasStride;
                                for (int x = 0; x < lastIw; x++)
                                {
                                    int dstX = lastIx + x;
                                    if (dstX < width && dstY < height)
                                    {
                                        int dIdx = rowOffset + dstX * 3;
                                        canvas[dIdx + 0] = bgR;
                                        canvas[dIdx + 1] = bgG;
                                        canvas[dIdx + 2] = bgB;
                                    }
                                }
                            }
                        }
                        else if (disposal == 3 && prevCanvas != null)
                        {
                            Buffer.BlockCopy(prevCanvas, 0, canvas, 0, canvas.Length);
                            prevCanvas = null;
                        }

                        transIndex = -1;
                        disposal = 0;
                        delayCs = 0;
                    }
                    finally
                    {
                        pool.Return(indices);
                    }
                }
                else
                {
                }
            }

            if (frames.Count == 0)
            {
                frames.Add(new Image<Rgb24>(width, height, canvas));
                durations.Add(100);
            }

            return new GifAnimation(frames, durations, loopCount);
        }

        /// <summary>
        /// 解码 GIF 所有帧为 Rgb24 图像列表
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>帧列表（Rgb24）</returns>
        public List<Image<Rgb24>> DecodeAllFrames(string path)
        {
            using var fs = File.OpenRead(path);
            return DecodeAllFrames(fs);
        }

        /// <summary>
        /// 从流解码 GIF 所有帧为 Rgb24 图像列表
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>帧列表（Rgb24）</returns>
        public List<Image<Rgb24>> DecodeAllFrames(Stream stream)
        {
            var anim = DecodeAnimationRgb24(stream);
            return [.. anim.Frames];
        }

        /// <summary>
        /// 解码单帧 GIF 为 Rgb24 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            return DecodeRgb24(fs);
        }

        /// <summary>
        /// 从流解码单帧 GIF 为 Rgb24 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(Stream stream)
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
            int canvasStride = width * 3;
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
            byte[] desc = new byte[9];
            byte[] lct = new byte[768];
            byte[] gce = new byte[4];
            var pool = ArrayPool<byte>.Shared;
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
                    if (stream.Read(desc, 0, 9) != 9) throw new EndOfStreamException();
                    int ix = desc[0] | (desc[1] << 8);
                    int iy = desc[2] | (desc[3] << 8);
                    int iw = desc[4] | (desc[5] << 8);
                    int ih = desc[6] | (desc[7] << 8);
                    byte imgPacked = desc[8];
                    
                    bool hasLct = (imgPacked & 0x80) != 0;
                    bool interlace = (imgPacked & 0x40) != 0;
                    int lctSize = 1 << ((imgPacked & 0x07) + 1);
                    
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
                    int indexCount = iw * ih;
                    byte[] indices = pool.Rent(indexCount);
                    var lzw = new LzwDecoder(stream);
                    try
                    {
                        lzw.Decode(indices, iw, ih, lzwMinCodeSize);

                        if (interlace)
                        {
                            int ptr = 0;
                            for (int pass = 0; pass < 4; pass++)
                            {
                                int startY = InterlaceStart[pass];
                                int incY = InterlaceInc[pass];
                                for (int y = startY; y < ih; y += incY)
                                {
                                    int dstY = iy + y;
                                    int rowOffset = dstY * canvasStride;
                                    int lineStart = ptr;
                                    for (int x = 0; x < iw; x++)
                                    {
                                        int dstX = ix + x;
                                        if (dstX < width && dstY < height)
                                        {
                                            byte idx = indices[lineStart + x];
                                            if (transIndex != -1 && idx == transIndex)
                                            {
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
                                    ptr += iw;
                                }
                            }
                        }
                        else
                        {
                            for (int y = 0; y < ih; y++)
                            {
                                int dstY = iy + y;
                                int rowOffset = dstY * canvasStride;
                                for (int x = 0; x < iw; x++)
                                {
                                    int dstX = ix + x;
                                    if (dstX < width && dstY < height)
                                    {
                                        byte idx = indices[y * iw + x];
                                        if (transIndex != -1 && idx == transIndex)
                                        {
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
                    }
                    finally
                    {
                        pool.Return(indices);
                    }

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

        /// <summary>
    /// 解码 GIF 文件为 RGBA32 图像
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>RGBA32 图像</returns>
    public Image<Rgba32> DecodeRgba32(string path)
    {
        using var stream = File.OpenRead(path);
        return DecodeRgba32(stream);
    }

    /// <summary>
    /// 解码 GIF 流为 RGBA32 图像
    /// </summary>
    /// <param name="stream">输入流</param>
    /// <returns>RGBA32 图像</returns>
    public Image<Rgba32> DecodeRgba32(Stream stream)
    {
            byte[] sig = new byte[6];
            if (stream.Read(sig, 0, 6) != 6) throw new InvalidDataException("Invalid GIF header");
            if (sig[0] != 'G' || sig[1] != 'I' || sig[2] != 'F') throw new InvalidDataException("Not a GIF file");
            byte[] lsd = new byte[7];
            if (stream.Read(lsd, 0, 7) != 7) throw new InvalidDataException("Invalid LSD");
            int width = lsd[0] | (lsd[1] << 8);
            int height = lsd[2] | (lsd[3] << 8);
            byte packed = lsd[4];
            byte bgIndex = lsd[5];
            bool hasGct = (packed & 0x80) != 0;
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
            byte[] canvas = new byte[width * height * 4];
            int canvasStride = width * 4;
            byte bgR = 0, bgG = 0, bgB = 0;
            if (hasGct && bgIndex < gctColors)
            {
                bgR = gct[bgIndex * 3];
                bgG = gct[bgIndex * 3 + 1];
                bgB = gct[bgIndex * 3 + 2];
                for (int i = 0; i < canvas.Length; i += 4)
                {
                    canvas[i] = bgR;
                    canvas[i + 1] = bgG;
                    canvas[i + 2] = bgB;
                    canvas[i + 3] = 255;
                }
            }
            int transIndex = -1;
            byte[] desc = new byte[9];
            byte[] lct = new byte[768];
            byte[] gce = new byte[4];
            var pool = ArrayPool<byte>.Shared;
            while (true)
            {
                int blockType = stream.ReadByte();
                if (blockType == -1 || blockType == 0x3B) break;
                if (blockType == 0x21)
                {
                    int label = stream.ReadByte();
                    if (label == 0xF9)
                    {
                        int size = stream.ReadByte();
                        if (size != 4)
                        {
                            if (size > 0) stream.Seek(size, SeekOrigin.Current);
                            stream.ReadByte();
                            continue;
                        }
                        ReadExact(stream, gce, 4);
                        bool hasTrans = (gce[0] & 1) != 0;
                        transIndex = hasTrans ? gce[3] : -1;
                        stream.ReadByte();
                    }
                    else
                    {
                        int size = stream.ReadByte();
                        if (size > 0) stream.Seek(size, SeekOrigin.Current);
                        SkipBlocks(stream);
                    }
                }
                else if (blockType == 0x2C)
                {
                    if (stream.Read(desc, 0, 9) != 9) throw new EndOfStreamException();
                    int ix = desc[0] | (desc[1] << 8);
                    int iy = desc[2] | (desc[3] << 8);
                    int iw = desc[4] | (desc[5] << 8);
                    int ih = desc[6] | (desc[7] << 8);
                    byte imgPacked = desc[8];
                    bool hasLct = (imgPacked & 0x80) != 0;
                    bool interlace = (imgPacked & 0x40) != 0;
                    int lctSize = 1 << ((imgPacked & 0x07) + 1);
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
                    int indexCount = iw * ih;
                    byte[] indices = pool.Rent(indexCount);
                    var lzw = new LzwDecoder(stream);
                    try
                    {
                        lzw.Decode(indices, iw, ih, lzwMinCodeSize);
                        if (interlace)
                        {
                            int ptr = 0;
                            for (int pass = 0; pass < 4; pass++)
                            {
                                int startY = InterlaceStart[pass];
                                int incY = InterlaceInc[pass];
                                for (int y = startY; y < ih; y += incY)
                                {
                                    int dstY = iy + y;
                                    int rowOffset = dstY * canvasStride;
                                    int lineStart = ptr;
                                    for (int x = 0; x < iw; x++)
                                    {
                                        int dstX = ix + x;
                                        if (dstX < width && dstY < height)
                                        {
                                            byte idx = indices[lineStart + x];
                                            if (transIndex != -1 && idx == transIndex)
                                            {
                                                int dIdx = rowOffset + dstX * 4;
                                                canvas[dIdx + 3] = 0;
                                            }
                                            else if (idx < paletteColors)
                                            {
                                                int pIdx = idx * 3;
                                                int dIdx = rowOffset + dstX * 4;
                                                canvas[dIdx + 0] = palette[pIdx];
                                                canvas[dIdx + 1] = palette[pIdx + 1];
                                                canvas[dIdx + 2] = palette[pIdx + 2];
                                                canvas[dIdx + 3] = 255;
                                            }
                                        }
                                    }
                                    ptr += iw;
                                }
                            }
                        }
                        else
                        {
                            for (int y = 0; y < ih; y++)
                            {
                                int dstY = iy + y;
                                int rowOffset = dstY * canvasStride;
                                for (int x = 0; x < iw; x++)
                                {
                                    int dstX = ix + x;
                                    if (dstX < width && dstY < height)
                                    {
                                        byte idx = indices[y * iw + x];
                                        if (transIndex != -1 && idx == transIndex)
                                        {
                                            int dIdx = rowOffset + dstX * 4;
                                            canvas[dIdx + 3] = 0;
                                        }
                                        else if (idx < paletteColors)
                                        {
                                            int pIdx = idx * 3;
                                            int dIdx = rowOffset + dstX * 4;
                                            canvas[dIdx + 0] = palette[pIdx];
                                            canvas[dIdx + 1] = palette[pIdx + 1];
                                            canvas[dIdx + 2] = palette[pIdx + 2];
                                            canvas[dIdx + 3] = 255;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        pool.Return(indices);
                    }
                    return new Image<Rgba32>(width, height, canvas);
                }
                else
                {
                }
            }
            return new Image<Rgba32>(width, height, canvas);
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
