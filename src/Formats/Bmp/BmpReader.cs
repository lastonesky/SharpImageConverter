using System;
using System.Buffers;
using System.IO;

namespace SharpImageConverter.Formats.Bmp;

/// <summary>
/// 简单的 BMP 读取器，支持 8/24/32 位非压缩 BMP，输出 RGB24。
/// </summary>
public static class BmpReader
{
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
        Span<byte> header = stackalloc byte[54];
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

        int dataOffset = ReadLe32(header, 10);
        int dibSize = ReadLe32(header, 14);
        if (dibSize < 40) throw new NotSupportedException($"Unsupported DIB header size: {dibSize}");

        width = ReadLe32(header, 18);
        height = ReadLe32(header, 22);
        short bpp = ReadLe16(header, 28);
        int compression = ReadLe32(header, 30);

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

        // Assuming standard top-down or bottom-up
        bool bottomUp = height > 0;
        height = Math.Abs(height);

        int rowStride = ((width * bpp + 31) / 32) * 4;
        byte[] rgb = new byte[width * height * 3];

        int pixelSize = bpp / 8;

        byte[]? palette = null;
        byte[]? paletteRgb = null;
        int paletteEntries = 0;

        // 如果 stream 支持 Seek，则跳转到 dataOffset。
        // 注意：dataOffset 是相对于文件开头的。
        // 如果 stream 是部分流，可能需要考虑偏移。但通常 BMP 是整个文件。
        // 假设 stream 当前位置是 header 之后。
        // dataOffset 通常大于 54。
        int bytesUntilPixelData = dataOffset - (int)stream.Position;
        if (bytesUntilPixelData < 0)
            throw new InvalidDataException("Invalid BMP data offset.");

        if (bpp == 8)
        {
            int paletteBytes = bytesUntilPixelData;
            if (paletteBytes < 4)
                throw new NotSupportedException("8-bit BMP missing palette.");

            int maxEntries = paletteBytes / 4;
            paletteEntries = Math.Min(256, maxEntries);
            palette = new byte[paletteEntries * 4];
            stream.ReadExactly(palette, 0, palette.Length);
            paletteRgb = new byte[paletteEntries * 3];
            int palSrc = 0;
            int palDst = 0;
            for (int i = 0; i < paletteEntries; i++)
            {
                paletteRgb[palDst + 0] = palette[palSrc + 2];
                paletteRgb[palDst + 1] = palette[palSrc + 1];
                paletteRgb[palDst + 2] = palette[palSrc + 0];
                palSrc += 4;
                palDst += 3;
            }

            int remaining = paletteBytes - palette.Length;
            if (remaining > 0)
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent(remaining);
                try
                {
                    stream.ReadExactly(temp, 0, remaining);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }
        else
        {
            if (bytesUntilPixelData > 0)
            {
                if (stream.CanSeek)
                {
                    stream.Seek(bytesUntilPixelData, SeekOrigin.Current);
                }
                else
                {
                    byte[] temp = ArrayPool<byte>.Shared.Rent(bytesUntilPixelData);
                    try
                    {
                        stream.ReadExactly(temp, 0, bytesUntilPixelData);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(temp);
                    }
                }
            }
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
            else
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

    private static short ReadLe16(ReadOnlySpan<byte> buf, int offset)
    {
        return (short)(buf[offset] | (buf[offset + 1] << 8));
    }

    private static int ReadLe32(ReadOnlySpan<byte> buf, int offset)
    {
        return buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);
    }
}
