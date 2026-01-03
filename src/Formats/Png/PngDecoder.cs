using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpImageConverter;

/// <summary>
/// PNG 解码器，读取 PNG 文件并输出 RGB24 或 RGBA32 像素数据。
/// </summary>
public class PngDecoder
{
    /// <summary>
    /// 图像宽度（像素）
    /// </summary>
    public int Width { get; private set; }
    /// <summary>
    /// 图像高度（像素）
    /// </summary>
    public int Height { get; private set; }
    /// <summary>
    /// 位深（1、2、4、8 或 16）
    /// </summary>
    public byte BitDepth { get; private set; }
    /// <summary>
    /// 颜色类型（0 灰度、2 真彩、3 调色板、4 灰度+Alpha、6 真彩+Alpha）
    /// </summary>
    public byte ColorType { get; private set; }
    /// <summary>
    /// 压缩方法（PNG 规范固定为 0）
    /// </summary>
    public byte CompressionMethod { get; private set; }
    /// <summary>
    /// 滤波方法（PNG 规范固定为 0）
    /// </summary>
    public byte FilterMethod { get; private set; }
    /// <summary>
    /// 隔行方式（0：非隔行，1：Adam7）
    /// </summary>
    public byte InterlaceMethod { get; private set; }

    private byte[]? _palette;
    private byte[]? _transparency; // for tRNS chunk
    private List<byte> _idatData = new List<byte>();

    /// <summary>
    /// 解码 PNG 文件为 RGB24 像素数据
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>按 RGB 顺序排列的字节数组（长度为 Width*Height*3）</returns>
    public byte[] DecodeToRGB(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            return DecodeToRGB(fs);
        }
    }

    /// <summary>
    /// 解码 PNG 流为 RGB24 像素数据
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>按 RGB 顺序排列的字节数组（长度为 Width*Height*3）</returns>
    public byte[] DecodeToRGB(Stream stream)
    {
        // Check Signature
        byte[] sig = new byte[8];
        if (stream.Read(sig, 0, 8) != 8) throw new InvalidDataException("File too short");
        if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");

        bool endChunkFound = false;
        while (!endChunkFound && stream.Position < stream.Length)
        {
            // Read Length
            byte[] lenBytes = new byte[4];
            if (stream.Read(lenBytes, 0, 4) != 4) break;
            uint length = ReadBigEndianUint32(lenBytes, 0);

            // Read Type
            byte[] typeBytes = new byte[4];
            if (stream.Read(typeBytes, 0, 4) != 4) break;
            string type = Encoding.ASCII.GetString(typeBytes);

            // Read Data
            byte[] data = new byte[length];
            if (length > 0)
            {
                if (stream.Read(data, 0, (int)length) != length) throw new InvalidDataException("Unexpected EOF in chunk data");
            }

            // Read CRC
            byte[] crcBytes = new byte[4];
            if (stream.Read(crcBytes, 0, 4) != 4) break;
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

        // Decompress IDAT
        byte[] decompressed = ZlibHelper.Decompress(_idatData.ToArray());

        // Unfilter and convert to RGB
        return ProcessImage(decompressed);
    }

    /// <summary>
    /// 解码 PNG 文件为 RGBA32 像素数据
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>按 RGBA 顺序排列的字节数组（长度为 Width*Height*4）</returns>
    public byte[] DecodeToRGBA(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            return DecodeToRGBA(fs);
        }
    }

    /// <summary>
    /// 解码 PNG 流为 RGBA32 像素数据
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>按 RGBA 顺序排列的字节数组（长度为 Width*Height*4）</returns>
    public byte[] DecodeToRGBA(Stream stream)
    {
        byte[] sig = new byte[8];
        if (stream.Read(sig, 0, 8) != 8) throw new InvalidDataException("File too short");
        if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");
        bool endChunkFound = false;
        while (!endChunkFound && stream.Position < stream.Length)
        {
            byte[] lenBytes = new byte[4];
            if (stream.Read(lenBytes, 0, 4) != 4) break;
            uint length = ReadBigEndianUint32(lenBytes, 0);
            byte[] typeBytes = new byte[4];
            if (stream.Read(typeBytes, 0, 4) != 4) break;
            string type = Encoding.ASCII.GetString(typeBytes);
            byte[] data = new byte[length];
            if (length > 0)
            {
                if (stream.Read(data, 0, (int)length) != length) throw new InvalidDataException("Unexpected EOF in chunk data");
            }
            byte[] crcBytes = new byte[4];
            if (stream.Read(crcBytes, 0, 4) != 4) break;
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
                    break;
            }
        }
        byte[] decompressed = ZlibHelper.Decompress(_idatData.ToArray());
        return ProcessImageRgba(decompressed);
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

    private byte[] ProcessImageRgba(byte[] rawData)
    {
        int bpp = GetBytesPerPixel();
        if (InterlaceMethod == 0)
        {
            return ProcessPassRgba(rawData, Width, Height, bpp);
        }
        else
        {
            return ProcessInterlacedRgba(rawData, bpp);
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

    private byte[] ProcessInterlacedRgba(byte[] rawData, int bpp)
    {
        int[] startX = { 0, 4, 0, 2, 0, 1, 0 };
        int[] startY = { 0, 0, 4, 0, 2, 0, 1 };
        int[] stepX  = { 8, 8, 4, 4, 2, 2, 1 };
        int[] stepY  = { 8, 8, 8, 4, 4, 2, 2 };
        byte[] finalImage = new byte[Width * Height * 4];
        int dataOffset = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            int passW = (Width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int passH = (Height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (passW == 0 || passH == 0) continue;
            int stride = (passW * GetBitsPerPixel() + 7) / 8;
            int passSize = (stride + 1) * passH;
            byte[] passData = new byte[passSize];
            Array.Copy(rawData, dataOffset, passData, 0, passSize);
            dataOffset += passSize;
            byte[] decodedPass = Unfilter(passData, passW, passH, bpp, stride);
            byte[] rgbaPass = ConvertToRGBA(decodedPass, passW, passH);
            for (int y = 0; y < passH; y++)
            {
                for (int x = 0; x < passW; x++)
                {
                    int finalY = startY[pass] + y * stepY[pass];
                    int finalX = startX[pass] + x * stepX[pass];
                    int srcIdx = (y * passW + x) * 4;
                    int dstIdx = (finalY * Width + finalX) * 4;
                    finalImage[dstIdx + 0] = rgbaPass[srcIdx + 0];
                    finalImage[dstIdx + 1] = rgbaPass[srcIdx + 1];
                    finalImage[dstIdx + 2] = rgbaPass[srcIdx + 2];
                    finalImage[dstIdx + 3] = rgbaPass[srcIdx + 3];
                }
            }
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

    private byte[] ProcessPassRgba(byte[] rawData, int w, int h, int bpp)
    {
        int stride = (w * GetBitsPerPixel() + 7) / 8;
        byte[] decoded = Unfilter(rawData, w, h, bpp, stride);
        return ConvertToRGBA(decoded, w, h);
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

    private byte[] ConvertToRGBA(byte[] data, int w, int h)
    {
        byte[] rgba = new byte[w * h * 4];
        int dstIdx = 0;
        int stride = (w * GetBitsPerPixel() + 7) / 8;
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * stride;
            int bitOffset = 0;
            for (int x = 0; x < w; x++)
            {
                byte r = 0, g = 0, b = 0, a = 255;
                switch (ColorType)
                {
                    case 0:
                    {
                        int val = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                        val = ScaleTo8Bit(val, BitDepth);
                        r = g = b = (byte)val;
                        if (_transparency != null && _transparency.Length >= 2)
                        {
                            int t = (_transparency[0] << 8) | _transparency[1];
                            int ts = BitDepth == 16 ? (t >> 8) : t;
                            a = (val == ts) ? (byte)0 : (byte)255;
                        }
                    }
                    break;
                    case 2:
                    {
                        if (BitDepth == 8)
                        {
                            r = data[rowStart + bitOffset / 8];
                            g = data[rowStart + bitOffset / 8 + 1];
                            b = data[rowStart + bitOffset / 8 + 2];
                            bitOffset += 24;
                            if (_transparency != null && _transparency.Length >= 6)
                            {
                                byte tr = _transparency[1];
                                byte tg = _transparency[3];
                                byte tb = _transparency[5];
                                a = (r == tr && g == tg && b == tb) ? (byte)0 : (byte)255;
                            }
                        }
                        else
                        {
                            r = data[rowStart + bitOffset / 8];
                            g = data[rowStart + bitOffset / 8 + 2];
                            b = data[rowStart + bitOffset / 8 + 4];
                            bitOffset += 48;
                        }
                    }
                    break;
                    case 3:
                    {
                        int index = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                        if (_palette != null && index * 3 + 2 < _palette.Length)
                        {
                            r = _palette[index * 3];
                            g = _palette[index * 3 + 1];
                            b = _palette[index * 3 + 2];
                        }
                        if (_transparency != null && index < _transparency.Length)
                        {
                            a = _transparency[index];
                        }
                    }
                    break;
                    case 4:
                    {
                        if (BitDepth == 8)
                        {
                            byte v = data[rowStart + bitOffset / 8];
                            byte al = data[rowStart + bitOffset / 8 + 1];
                            bitOffset += 16;
                            r = g = b = v;
                            a = al;
                        }
                        else
                        {
                            byte v = data[rowStart + bitOffset / 8];
                            byte al = data[rowStart + bitOffset / 8 + 2];
                            bitOffset += 32;
                            r = g = b = v;
                            a = al;
                        }
                    }
                    break;
                    case 6:
                    {
                        if (BitDepth == 8)
                        {
                            r = data[rowStart + bitOffset / 8];
                            g = data[rowStart + bitOffset / 8 + 1];
                            b = data[rowStart + bitOffset / 8 + 2];
                            a = data[rowStart + bitOffset / 8 + 3];
                            bitOffset += 32;
                        }
                        else
                        {
                            r = data[rowStart + bitOffset / 8];
                            g = data[rowStart + bitOffset / 8 + 2];
                            b = data[rowStart + bitOffset / 8 + 4];
                            a = data[rowStart + bitOffset / 8 + 6];
                            bitOffset += 64;
                        }
                    }
                    break;
                }
                rgba[dstIdx++] = r;
                rgba[dstIdx++] = g;
                rgba[dstIdx++] = b;
                rgba[dstIdx++] = a;
            }
        }
        return rgba;
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
