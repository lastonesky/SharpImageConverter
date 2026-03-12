using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace SharpImageConverter.Formats.Bmp;

/// <summary>
/// 简单的 BMP 读取器，支持 8/24/32 位非压缩 BMP，输出 RGB24。
/// </summary>
public static class BmpReader
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int HeaderSize = FileHeaderSize + InfoHeaderSize; // 54

    /// <summary>
    /// 读取 BMP 文件并返回 RGB24 像素数据
    /// </summary>
    /// <param name="path">输入文件路径</param>
    /// <param name="width">输出图像宽度</param>
    /// <param name="height">输出图像高度</param>
    /// <returns>按 RGB 顺序排列的字节数组</returns>
    public static byte[] Read(string path, out int width, out int height)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Read(fs, out width, out height);
    }

    /// <summary>
    /// 读取 BMP 数据流并返回 RGB24 像素数据
    /// </summary>
    /// <param name="stream">输入数据流</param>
    /// <param name="width">输出图像宽度</param>
    /// <param name="height">输出图像高度</param>
    /// <returns>按 RGB 顺序排列的字节数组</returns>
    public static byte[] Read(Stream stream, out int width, out int height)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        int totalRead = 0;
        while (totalRead < header.Length)
        {
            int n = stream.Read(header.Slice(totalRead));
            if (n == 0) break;
            totalRead += n;
        }
        if (totalRead != header.Length) throw new EndOfStreamException("BMP header 不完整");

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
            throw new InvalidDataException("Not a BMP file");

        // File Header
        // int fileSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(2));
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(10));

        // Info Header
        int dibSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(14));
        if (dibSize < 40) throw new NotSupportedException($"Unsupported DIB header size: {dibSize}");

        width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18));
        height = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22));
        // short planes = BinaryPrimitives.ReadInt16LittleEndian(header.Slice(26));
        short bpp = BinaryPrimitives.ReadInt16LittleEndian(header.Slice(28));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(30));
        // int imageSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(34));
        // int xPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(38));
        // int yPelsPerMeter = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(42));
        int clrUsed = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(46));
        // int clrImportant = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(50));

        if (bpp == 8)
        {
            if (compression != 0)
                throw new NotSupportedException("Only uncompressed 8-bit BMPs (BI_RGB) are supported.");
        }
        else if (bpp == 24 || bpp == 32)
        {
            if (compression != 0 && compression != 3) // BI_RGB or BI_BITFIELDS
                throw new NotSupportedException("Compressed BMPs are not supported");
        }
        else
        {
            throw new NotSupportedException($"Only 8/24/32-bit BMPs are supported. Found {bpp}-bit.");
        }

        bool bottomUp = height > 0;
        height = Math.Abs(height);

        int rowStride = ((width * bpp + 31) / 32) * 4;
        byte[] rgb = new byte[width * height * 3];

        byte[]? paletteRgb = null;
        int paletteEntries = 0;

        // Track bytes read from the start of BMP (relative to what we passed in)
        long currentPosition = HeaderSize;

        if (bpp == 8)
        {
            paletteEntries = clrUsed == 0 ? 256 : clrUsed;
            int paletteByteCount = paletteEntries * 4;
            
            // Read palette
            byte[] paletteRaw = ArrayPool<byte>.Shared.Rent(paletteByteCount);
            try
            {
                int pRead = 0;
                while (pRead < paletteByteCount)
                {
                    int n = stream.Read(paletteRaw, pRead, paletteByteCount - pRead);
                    if (n == 0) break;
                    pRead += n;
                }
                if (pRead < paletteByteCount) throw new EndOfStreamException("BMP palette incomplete");
                
                currentPosition += paletteByteCount;

                paletteRgb = new byte[paletteEntries * 3];
                int palSrc = 0;
                int palDst = 0;
                for (int i = 0; i < paletteEntries; i++)
                {
                    paletteRgb[palDst + 0] = paletteRaw[palSrc + 2]; // R
                    paletteRgb[palDst + 1] = paletteRaw[palSrc + 1]; // G
                    paletteRgb[palDst + 2] = paletteRaw[palSrc + 0]; // B
                    palSrc += 4;
                    palDst += 3;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(paletteRaw);
            }
        }

        // Skip to pixel data
        long gap = dataOffset - currentPosition;
        if (gap > 0)
        {
            if (stream.CanSeek)
            {
                stream.Seek(gap, SeekOrigin.Current);
            }
            else
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent((int)Math.Min(gap, 4096));
                try
                {
                    long remaining = gap;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(remaining, temp.Length);
                        int n = stream.Read(temp, 0, toRead);
                        if (n == 0) break;
                        remaining -= n;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }
        else if (gap < 0)
        {
            // Invalid dataOffset, it points inside header/palette.
            throw new InvalidDataException($"Invalid BMP data offset: {dataOffset}. Current read: {currentPosition}");
        }

        byte[] row = ArrayPool<byte>.Shared.Rent(rowStride);
        try
        {
            if (bpp == 8)
            {
                if (paletteRgb == null || paletteEntries == 0)
                    throw new InvalidDataException("8-bit BMP palette is missing.");
                unsafe
                {
                    fixed (byte* pRow = row)
                    fixed (byte* pDst = rgb)
                    fixed (byte* pPal = paletteRgb)
                    {
                        for (int rowIndex = 0; rowIndex < height; rowIndex++)
                        {
                            stream.ReadExactly(row, 0, rowStride);
                            // if (n < rowStride) throw new EndOfStreamException(); // ReadExactly throws on failure

                            int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                            byte* dst = pDst + (dstY * width * 3);
                            byte* src = pRow;
                            for (int x = 0; x < width; x++)
                            {
                                int index = src[x];
                                if (index >= paletteEntries)
                                    index = 0;
                                int palOffset = index * 3;
                                dst[0] = pPal[palOffset + 0];
                                dst[1] = pPal[palOffset + 1];
                                dst[2] = pPal[palOffset + 2];
                                dst += 3;
                            }
                        }
                    }
                }
            }
            else if (bpp == 24)
            {
                int srcRowSize = width * 3;
                unsafe
                {
                    fixed (byte* pRow = row)
                    fixed (byte* pDst = rgb)
                    {
                        for (int rowIndex = 0; rowIndex < height; rowIndex++)
                        {
                            stream.ReadExactly(row, 0, rowStride);
                            // if (n < rowStride) throw new EndOfStreamException();

                            int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                            byte* src = pRow;
                            byte* dst = pDst + (dstY * width * 3);
                            byte* srcEnd = src + srcRowSize;
                            while (src < srcEnd)
                            {
                                dst[0] = src[2];
                                dst[1] = src[1];
                                dst[2] = src[0];
                                src += 3;
                                dst += 3;
                            }
                        }
                    }
                }
            }
            else // 32
            {
                int srcRowSize = width * 4;
                unsafe
                {
                    fixed (byte* pRow = row)
                    fixed (byte* pDst = rgb)
                    {
                        for (int rowIndex = 0; rowIndex < height; rowIndex++)
                        {
                            stream.ReadExactly(row, 0, rowStride);
                            // if (n < rowStride) throw new EndOfStreamException();

                            int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                            byte* src = pRow;
                            byte* dst = pDst + (dstY * width * 3);
                            byte* srcEnd = src + srcRowSize;
                            while (src < srcEnd)
                            {
                                dst[0] = src[2];
                                dst[1] = src[1];
                                dst[2] = src[0];
                                src += 4;
                                dst += 3;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }

        return rgb;
    }
}
