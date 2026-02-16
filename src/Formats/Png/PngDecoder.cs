using System;
using System.Collections.Generic;
using System.Buffers;
using System.IO;
using System.Text;
using System.Numerics;

namespace SharpImageConverter.Formats.Png;

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
    private byte[]? _transparency;
    private const uint TypeIHDR = 0x49484452;
    private const uint TypePLTE = 0x504C5445;
    private const uint TypeIDAT = 0x49444154;
    private const uint TypeTRNS = 0x74524E53;
    private const uint TypeIEND = 0x49454E44;
    private static readonly int[] Adam7StartX = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] Adam7StartY = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] Adam7StepX = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] Adam7StepY = [8, 8, 8, 4, 4, 2, 2];

    /// <summary>
    /// 解码 PNG 文件为 RGB24 像素数据
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>按 RGB 顺序排列的字节数组（长度为 Width*Height*3）</returns>
    public byte[] DecodeToRGB(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return DecodeToRGB(fs);
    }

    /// <summary>
    /// 解码 PNG 流为 RGB24 像素数据
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>按 RGB 顺序排列的字节数组（长度为 Width*Height*3）</returns>
    public byte[] DecodeToRGB(Stream stream)
    {
        byte[] sig = new byte[8];
        int readCount = 0;
        while (readCount < 8)
        {
            int n = stream.Read(sig, readCount, 8 - readCount);
            if (n == 0) break;
            readCount += n;
        }
        if (readCount != 8) throw new InvalidDataException("File too short");
        if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");

        using var idatStream = new PooledMemoryStream(4096);
        bool endChunkFound = false;
        byte[] lenBytes = new byte[4];
        byte[] typeBytes = new byte[4];
        byte[] crcBytes = new byte[4];
        while (!endChunkFound && stream.Position < stream.Length)
        {
            if (stream.Read(lenBytes, 0, 4) != 4) break;
            uint length = ReadBigEndianUint32(lenBytes, 0);

            if (stream.Read(typeBytes, 0, 4) != 4) break;
            uint type = ReadBigEndianUint32(typeBytes, 0);

            byte[] data;
            byte[]? rented = null;
            int dataLength = (int)length;
            if (dataLength > 0)
            {
                if (type == TypeIHDR || type == TypePLTE || type == TypeTRNS)
                {
                    data = new byte[dataLength];
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent(dataLength);
                    data = rented;
                }
                if (stream.Read(data, 0, dataLength) != dataLength) throw new InvalidDataException("Unexpected EOF in chunk data");
            }
            else
            {
                data = Array.Empty<byte>();
            }

            if (stream.Read(crcBytes, 0, 4) != 4) break;
            uint fileCrc = ReadBigEndianUint32(crcBytes, 0);

            uint calcCrc = Crc32.Compute(typeBytes);
            if (dataLength > 0) calcCrc = Crc32.Update(calcCrc, data, 0, dataLength);
            if (calcCrc != fileCrc)
            {
                string typeName = Encoding.ASCII.GetString(typeBytes);
                Console.WriteLine($"Warning: CRC mismatch in chunk {typeName}. Expected {fileCrc:X8}, got {calcCrc:X8}");
            }

            switch (type)
            {
                case TypeIHDR:
                    ParseIHDR(data);
                    break;
                case TypePLTE:
                    _palette = data;
                    break;
                case TypeIDAT:
                    idatStream.Write(data, 0, dataLength);
                    break;
                case TypeTRNS:
                    _transparency = data;
                    break;
                case TypeIEND:
                    endChunkFound = true;
                    break;
                default:
                    break;
            }

            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        ArraySegment<byte> idatSegment = idatStream.GetBuffer();
        byte[] decompressed = ZlibHelper.Decompress(idatSegment.Array, idatSegment.Offset, idatSegment.Count);
        return ProcessImage(decompressed);
    }

    /// <summary>
    /// 解码 PNG 文件为 RGBA32 像素数据
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>按 RGBA 顺序排列的字节数组（长度为 Width*Height*4）</returns>
    public byte[] DecodeToRGBA(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return DecodeToRGBA(fs);
    }

    /// <summary>
    /// 解码 PNG 流为 RGBA32 像素数据
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>按 RGBA 顺序排列的字节数组（长度为 Width*Height*4）</returns>
    public byte[] DecodeToRGBA(Stream stream)
    {
        byte[] sig = new byte[8];
        int readCount = 0;
        while (readCount < 8)
        {
            int n = stream.Read(sig, readCount, 8 - readCount);
            if (n == 0) break;
            readCount += n;
        }
        if (readCount != 8) throw new InvalidDataException("File too short");
        if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");
        using var idatStream = new PooledMemoryStream(4096);
        bool endChunkFound = false;
        byte[] lenBytes = new byte[4];
        byte[] typeBytes = new byte[4];
        byte[] crcBytes = new byte[4];
        while (!endChunkFound && stream.Position < stream.Length)
        {
            if (stream.Read(lenBytes, 0, 4) != 4) break;
            uint length = ReadBigEndianUint32(lenBytes, 0);
            if (stream.Read(typeBytes, 0, 4) != 4) break;
            uint type = ReadBigEndianUint32(typeBytes, 0);
            byte[] data;
            byte[]? rented = null;
            int dataLength = (int)length;
            if (dataLength > 0)
            {
                if (type == TypeIHDR || type == TypePLTE || type == TypeTRNS)
                {
                    data = new byte[dataLength];
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent(dataLength);
                    data = rented;
                }
                if (stream.Read(data, 0, dataLength) != dataLength) throw new InvalidDataException("Unexpected EOF in chunk data");
            }
            else
            {
                data = Array.Empty<byte>();
            }
            if (stream.Read(crcBytes, 0, 4) != 4) break;
            switch (type)
            {
                case TypeIHDR:
                    ParseIHDR(data);
                    break;
                case TypePLTE:
                    _palette = data;
                    break;
                case TypeIDAT:
                    idatStream.Write(data, 0, dataLength);
                    break;
                case TypeTRNS:
                    _transparency = data;
                    break;
                case TypeIEND:
                    endChunkFound = true;
                    break;
                default:
                    break;
            }
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        ArraySegment<byte> idatSegment = idatStream.GetBuffer();
        byte[] decompressed = ZlibHelper.Decompress(idatSegment.Array, idatSegment.Offset, idatSegment.Count);
        return ProcessImageRgba(decompressed);
    }

    private static bool IsPngSignature(byte[] sig)
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
        
        int[] startX = Adam7StartX;
        int[] startY = Adam7StartY;
        int[] stepX = Adam7StepX;
        int[] stepY = Adam7StepY;

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
        int[] startX = Adam7StartX;
        int[] startY = Adam7StartY;
        int[] stepX = Adam7StepX;
        int[] stepY = Adam7StepY;
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

        byte[] prevRow = ArrayPool<byte>.Shared.Rent(stride);
        byte[] curRow = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            Array.Clear(prevRow, 0, stride);
            for (int y = 0; y < h; y++)
            {
                byte filterType = rawData[rawIdx++];
                Array.Copy(rawData, rawIdx, curRow, 0, stride);
                rawIdx += stride;

                if (filterType == 0)
                {
                    Array.Copy(curRow, 0, recon, reconIdx, stride);
                    reconIdx += stride;
                    (curRow, prevRow) = (prevRow, curRow);
                    continue;
                }

                if (filterType == 2)
                {
                    ApplyUpFilterDecodeSimd(curRow, prevRow, stride);
                }
                else
                {
                    for (int i = 0; i < stride; i++)
                    {
                        byte x = curRow[i];
                        byte a = (i >= bpp) ? curRow[i - bpp] : (byte)0;
                        byte b = prevRow[i];
                        byte c = (i >= bpp) ? prevRow[i - bpp] : (byte)0;

                        switch (filterType)
                        {
                            case 1:
                                x += a;
                                break;
                            case 3:
                                x += (byte)((a + b) / 2);
                                break;
                            case 4:
                                x += PaethPredictor(a, b, c);
                                break;
                        }
                        curRow[i] = x;
                    }
                }

                Array.Copy(curRow, 0, recon, reconIdx, stride);
                reconIdx += stride;
                (curRow, prevRow) = (prevRow, curRow);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prevRow);
            ArrayPool<byte>.Shared.Return(curRow);
        }

        return recon;
    }

    private static void ApplyUpFilterDecodeSimd(byte[] curRow, byte[] prevRow, int length)
    {
        Span<byte> curSpan = curRow;
        Span<byte> prevSpan = prevRow;
        int i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            int simdCount = Vector<byte>.Count;
            var mask = new Vector<ushort>(0xFF);
            for (; i <= length - simdCount; i += simdCount)
            {
                var curVec = new Vector<byte>(curSpan.Slice(i));
                var prevVec = new Vector<byte>(prevSpan.Slice(i));
                Vector.Widen(curVec, out Vector<ushort> curLow, out Vector<ushort> curHigh);
                Vector.Widen(prevVec, out Vector<ushort> prevLow, out Vector<ushort> prevHigh);
                var resLow = (curLow + prevLow) & mask;
                var resHigh = (curHigh + prevHigh) & mask;
                var result = Vector.Narrow(resLow, resHigh);
                result.CopyTo(curSpan.Slice(i));
            }
        }
        for (; i < length; i++)
        {
            curSpan[i] = (byte)(curSpan[i] + prevSpan[i]);
        }
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
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
        int dstIdx = 0;

        // Note: data is packed by scanlines (stride).
        // If BitDepth < 8, pixels are packed into bytes.
        int stride = (w * GetBitsPerPixel() + 7) / 8;

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * stride;
            var row = data.AsSpan(rowStart, stride);
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
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 1];
                                b = row[idx + 2];
                                bitOffset += 24;
                            }
                            else if (BitDepth == 16)
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 2];
                                b = row[idx + 4];
                                bitOffset += 48;
                            }
                        }
                        break;
                    case 3: // Indexed
                        {
                            int index = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                            if (_palette != null && index * 3 + 2 < _palette.Length)
                            {
                                int p = index * 3;
                                r = _palette[p];
                                g = _palette[p + 1];
                                b = _palette[p + 2];
                            }
                        }
                        break;
                    case 4: // Grayscale + Alpha
                        {
                            int val;
                            if (BitDepth == 8)
                            {
                                val = row[bitOffset / 8];
                                bitOffset += 16;
                            }
                            else // 16
                            {
                                val = row[bitOffset / 8];
                                bitOffset += 32;
                            }
                            r = g = b = (byte)val;
                        }
                        break;
                    case 6: // Truecolor + Alpha
                        {
                             if (BitDepth == 8)
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 1];
                                b = row[idx + 2];
                                bitOffset += 32;
                            }
                            else // 16
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 2];
                                b = row[idx + 4];
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
            var row = data.AsSpan(rowStart, stride);
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
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 1];
                            b = row[idx + 2];
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
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 2];
                            b = row[idx + 4];
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
                            int idx = bitOffset / 8;
                            byte v = row[idx];
                            byte al = row[idx + 1];
                            bitOffset += 16;
                            r = g = b = v;
                            a = al;
                        }
                        else
                        {
                            int idx = bitOffset / 8;
                            byte v = row[idx];
                            byte al = row[idx + 2];
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
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 1];
                            b = row[idx + 2];
                            a = row[idx + 3];
                            bitOffset += 32;
                        }
                        else
                        {
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 2];
                            b = row[idx + 4];
                            a = row[idx + 6];
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

    private static int ReadBits(byte[] data, int rowStart, ref int bitOffset, int bits)
    {
        int byteIdx = rowStart + bitOffset / 8;
        int bitShift = 8 - (bitOffset % 8) - bits;
        int val = (data[byteIdx] >> bitShift) & ((1 << bits) - 1);
        bitOffset += bits;
        return val;
    }

    private static int ScaleTo8Bit(int val, int depth)
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
        return ColorType switch
        {
            0 => BitDepth,
            2 => 3 * BitDepth,
            3 => BitDepth,
            4 => 2 * BitDepth,
            6 => 4 * BitDepth,
            _ => throw new NotSupportedException("Invalid color type"),
        };
    }

    // Helper for filtering
    private int GetBytesPerPixel()
    {
        return (GetBitsPerPixel() + 7) / 8;
    }

    private static uint ReadBigEndianUint32(byte[] buffer, int offset)
    {
        return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }
}
