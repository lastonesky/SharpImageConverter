using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PictureSharp;

public class PngDecoder
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte BitDepth { get; private set; }
    public byte ColorType { get; private set; }
    public byte CompressionMethod { get; private set; }
    public byte FilterMethod { get; private set; }
    public byte InterlaceMethod { get; private set; }

    private byte[]? _palette;
    private byte[]? _transparency; // for tRNS chunk
    private List<byte> _idatData = new List<byte>();

    public byte[] DecodeToRGB(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            // Check Signature
            byte[] sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8) throw new InvalidDataException("File too short");
            if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");

            bool endChunkFound = false;
            while (!endChunkFound && fs.Position < fs.Length)
            {
                // Read Length
                byte[] lenBytes = new byte[4];
                if (fs.Read(lenBytes, 0, 4) != 4) break;
                uint length = ReadBigEndianUint32(lenBytes, 0);

                // Read Type
                byte[] typeBytes = new byte[4];
                if (fs.Read(typeBytes, 0, 4) != 4) break;
                string type = Encoding.ASCII.GetString(typeBytes);

                // Read Data
                byte[] data = new byte[length];
                if (length > 0)
                {
                    if (fs.Read(data, 0, (int)length) != length) throw new InvalidDataException("Unexpected EOF in chunk data");
                }

                // Read CRC
                byte[] crcBytes = new byte[4];
                if (fs.Read(crcBytes, 0, 4) != 4) break;
                uint fileCrc = ReadBigEndianUint32(crcBytes, 0);

                // Verify CRC
                // CRC covers Type + Data
                uint calcCrc = Crc32.Compute(typeBytes);
                calcCrc = Crc32.Update(calcCrc, data, 0, (int)length);
                if (calcCrc != fileCrc)
                {
                    Console.WriteLine($"Warning: CRC mismatch in chunk {type}. Expected {fileCrc:X8}, got {calcCrc:X8}");
                }

                switch (type)
                {
                    case "IHDR":
                        ParseIHDR(data);
                        break;
                    case "PLTE":
                        _palette = data;
                        break;
                    case "IDAT":
                        _idatData.AddRange(data);
                        break;
                    case "tRNS":
                        _transparency = data;
                        break;
                    case "IEND":
                        endChunkFound = true;
                        break;
                    default:
                        // Skip ancillary chunks
                        break;
                }
            }
        }

        // Decompress IDAT
        byte[] decompressed = ZlibHelper.Decompress(_idatData.ToArray());

        // Unfilter and convert to RGB
        return ProcessImage(decompressed);
    }

    private bool IsPngSignature(byte[] sig)
    {
        return sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47 &&
               sig[4] == 0x0D && sig[5] == 0x0A && sig[6] == 0x1A && sig[7] == 0x0A;
    }

    private void ParseIHDR(byte[] data)
    {
        Width = (int)ReadBigEndianUint32(data, 0);
        Height = (int)ReadBigEndianUint32(data, 4);
        BitDepth = data[8];
        ColorType = data[9];
        CompressionMethod = data[10];
        FilterMethod = data[11];
        InterlaceMethod = data[12];

        if (CompressionMethod != 0) throw new NotSupportedException("Unknown compression method");
        if (FilterMethod != 0) throw new NotSupportedException("Unknown filter method");
        if (InterlaceMethod > 1) throw new NotSupportedException("Unknown interlace method");
    }

    private byte[] ProcessImage(byte[] rawData)
    {
        // Calculate bytes per pixel
        int bpp = GetBytesPerPixel();
        
        if (InterlaceMethod == 0)
        {
            return ProcessPass(rawData, Width, Height, bpp);
        }
        else
        {
            return ProcessInterlaced(rawData, bpp);
        }
    }

    private byte[] ProcessInterlaced(byte[] rawData, int bpp)
    {
        // Adam7 passes
        // Pass 1: start (0,0), step (8,8)
        // Pass 2: start (4,0), step (8,8)
        // Pass 3: start (0,4), step (4,8)
        // Pass 4: start (2,0), step (4,4)
        // Pass 5: start (0,2), step (2,4)
        // Pass 6: start (1,0), step (2,2)
        // Pass 7: start (0,1), step (1,2)
        
        int[] startX = { 0, 4, 0, 2, 0, 1, 0 };
        int[] startY = { 0, 0, 4, 0, 2, 0, 1 };
        int[] stepX  = { 8, 8, 4, 4, 2, 2, 1 };
        int[] stepY  = { 8, 8, 8, 4, 4, 2, 2 };

        // Final image buffer (RGBA or RGB)
        // We will decode everything to RGB first for simplicity, 
        // but since we need to support transparency, we might need intermediate storage.
        // The output of DecodeToRGB is byte[] rgb (3 bytes per pixel) as per current BmpWriter.
        // However, if PNG has transparency, we should ideally handle it.
        // For now, let's target 24-bit RGB output to match existing Jpeg2Bmp capability.
        // Transparent pixels will be blended with white or black? Or just dropped?
        // Let's output RGB, composition with background if needed.
        
        byte[] finalImage = new byte[Width * Height * 3]; 
        int dataOffset = 0;

        for (int pass = 0; pass < 7; pass++)
        {
            int passW = (Width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int passH = (Height - startY[pass] + stepY[pass] - 1) / stepY[pass];

            if (passW == 0 || passH == 0) continue;

            // Calculate raw size for this pass
            int stride = (passW * GetBitsPerPixel() + 7) / 8;
            int passSize = (stride + 1) * passH; // +1 for filter byte

            byte[] passData = new byte[passSize];
            Array.Copy(rawData, dataOffset, passData, 0, passSize);
            dataOffset += passSize;

            byte[] decodedPass = Unfilter(passData, passW, passH, bpp, stride);

            // Scatter pixels to final image
            ExpandPassToImage(decodedPass, finalImage, pass, passW, passH, startX[pass], startY[pass], stepX[pass], stepY[pass]);
        }

        return finalImage;
    }

    private byte[] ProcessPass(byte[] rawData, int w, int h, int bpp)
    {
        int stride = (w * GetBitsPerPixel() + 7) / 8;
        byte[] decoded = Unfilter(rawData, w, h, bpp, stride);
        
        // Convert to RGB 24-bit
        return ConvertToRGB(decoded, w, h);
    }

    private byte[] Unfilter(byte[] rawData, int w, int h, int bpp, int stride)
    {
        // Output size is same as input minus filter bytes
        byte[] recon = new byte[stride * h];
        int reconIdx = 0;
        int rawIdx = 0;

        byte[] prevRow = new byte[stride]; // Initialized to 0

        for (int y = 0; y < h; y++)
        {
            byte filterType = rawData[rawIdx++];
            byte[] curRow = new byte[stride];
            
            // Read scanline
            Array.Copy(rawData, rawIdx, curRow, 0, stride);
            rawIdx += stride;

            // Unfilter
            for (int i = 0; i < stride; i++)
            {
                byte x = curRow[i];
                byte a = (i >= bpp) ? curRow[i - bpp] : (byte)0;
                byte b = prevRow[i];
                byte c = (i >= bpp) ? prevRow[i - bpp] : (byte)0;

                switch (filterType)
                {
                    case 0: // None
                        break;
                    case 1: // Sub
                        x += a;
                        break;
                    case 2: // Up
                        x += b;
                        break;
                    case 3: // Average
                        x += (byte)((a + b) / 2);
                        break;
                    case 4: // Paeth
                        x += PaethPredictor(a, b, c);
                        break;
                }
                curRow[i] = x;
            }

            Array.Copy(curRow, 0, recon, reconIdx, stride);
            reconIdx += stride;
            prevRow = curRow;
        }

        return recon;
    }

    private byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc) return a;
        else if (pb <= pc) return b;
        else return c;
    }

    private void ExpandPassToImage(byte[] decodedPass, byte[] finalImage, int pass, int w, int h, int sx, int sy, int dx, int dy)
    {
        // This is complex because we need to convert the partial pass (which might be packed differently depending on color type)
        // into the final RGB buffer.
        // It's easier if we convert the pass to RGB first, then scatter.
        
        byte[] rgbPass = ConvertToRGB(decodedPass, w, h);
        
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int finalY = sy + y * dy;
                int finalX = sx + x * dx;
                
                int srcIdx = (y * w + x) * 3;
                int dstIdx = (finalY * Width + finalX) * 3;
                
                finalImage[dstIdx] = rgbPass[srcIdx];
                finalImage[dstIdx + 1] = rgbPass[srcIdx + 1];
                finalImage[dstIdx + 2] = rgbPass[srcIdx + 2];
            }
        }
    }

    private byte[] ConvertToRGB(byte[] data, int w, int h)
    {
        byte[] rgb = new byte[w * h * 3];
        // int srcIdx = 0; // Unused
        int dstIdx = 0;

        // Note: data is packed by scanlines (stride).
        // If BitDepth < 8, pixels are packed into bytes.
        int stride = (w * GetBitsPerPixel() + 7) / 8;

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * stride;
            int bitOffset = 0;

            for (int x = 0; x < w; x++)
            {
                byte r = 0, g = 0, b = 0;

                switch (ColorType)
                {
                    case 0: // Grayscale
                        {
                            int val = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                            // Scale to 8-bit
                            val = ScaleTo8Bit(val, BitDepth);
                            r = g = b = (byte)val;
                        }
                        break;
                    case 2: // Truecolor
                        {
                            if (BitDepth == 8)
                            {
                                r = data[rowStart + bitOffset / 8];
                                g = data[rowStart + bitOffset / 8 + 1];
                                b = data[rowStart + bitOffset / 8 + 2];
                                bitOffset += 24;
                            }
                            else if (BitDepth == 16)
                            {
                                r = data[rowStart + bitOffset / 8];
                                g = data[rowStart + bitOffset / 8 + 2];
                                b = data[rowStart + bitOffset / 8 + 4];
                                bitOffset += 48;
                            }
                        }
                        break;
                    case 3: // Indexed
                        {
                            int index = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                            if (_palette != null && index * 3 + 2 < _palette.Length)
                            {
                                r = _palette[index * 3];
                                g = _palette[index * 3 + 1];
                                b = _palette[index * 3 + 2];
                            }
                        }
                        break;
                    case 4: // Grayscale + Alpha
                        {
                            int val = 0; 
                            if (BitDepth == 8)
                            {
                                val = data[rowStart + bitOffset / 8];
                                // alpha = data[...]
                                bitOffset += 16;
                            }
                            else // 16
                            {
                                val = data[rowStart + bitOffset / 8];
                                bitOffset += 32;
                            }
                            r = g = b = (byte)val;
                        }
                        break;
                    case 6: // Truecolor + Alpha
                        {
                             if (BitDepth == 8)
                            {
                                r = data[rowStart + bitOffset / 8];
                                g = data[rowStart + bitOffset / 8 + 1];
                                b = data[rowStart + bitOffset / 8 + 2];
                                // alpha
                                bitOffset += 32;
                            }
                            else // 16
                            {
                                r = data[rowStart + bitOffset / 8];
                                g = data[rowStart + bitOffset / 8 + 2];
                                b = data[rowStart + bitOffset / 8 + 4];
                                bitOffset += 64;
                            }
                        }
                        break;
                }

                rgb[dstIdx++] = r;
                rgb[dstIdx++] = g;
                rgb[dstIdx++] = b;
            }
        }

        return rgb;
    }

    private int ReadBits(byte[] data, int rowStart, ref int bitOffset, int bits)
    {
        int byteIdx = rowStart + bitOffset / 8;
        int bitShift = 8 - (bitOffset % 8) - bits;
        int val = (data[byteIdx] >> bitShift) & ((1 << bits) - 1);
        bitOffset += bits;
        return val;
    }

    private int ScaleTo8Bit(int val, int depth)
    {
        if (depth == 1) return val * 255;
        if (depth == 2) return val * 85;
        if (depth == 4) return val * 17;
        if (depth == 8) return val;
        if (depth == 16) return val >> 8;
        return val;
    }

    private int GetBitsPerPixel()
    {
        switch (ColorType)
        {
            case 0: return BitDepth;
            case 2: return 3 * BitDepth;
            case 3: return BitDepth;
            case 4: return 2 * BitDepth;
            case 6: return 4 * BitDepth;
            default: throw new NotSupportedException("Invalid color type");
        }
    }

    // Helper for filtering
    private int GetBytesPerPixel()
    {
        return (GetBitsPerPixel() + 7) / 8;
    }

    private uint ReadBigEndianUint32(byte[] buffer, int offset)
    {
        return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }
}
