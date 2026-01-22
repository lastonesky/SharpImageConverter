using System;
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

            int remaining = paletteBytes - palette.Length;
            if (remaining > 0)
            {
                byte[] temp = new byte[remaining];
                stream.ReadExactly(temp, 0, remaining);
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
                    byte[] temp = new byte[bytesUntilPixelData];
                    stream.ReadExactly(temp, 0, bytesUntilPixelData);
                }
            }
        }

        byte[] row = new byte[rowStride];
        for (int rowIndex = 0; rowIndex < height; rowIndex++)
        {
            stream.ReadExactly(row, 0, rowStride);
            int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
            int dstOffset = dstY * width * 3;

            for (int x = 0; x < width; x++)
            {
                int src = x * pixelSize;
                int dst = dstOffset + x * 3;

                byte r, g, b;

                if (bpp == 8)
                {
                    int index = row[src];
                    if (palette == null || paletteEntries == 0)
                        throw new InvalidDataException("8-bit BMP palette is missing.");
                    if (index >= paletteEntries)
                        index = 0;
                    int palOffset = index * 4;
                    b = palette[palOffset + 0];
                    g = palette[palOffset + 1];
                    r = palette[palOffset + 2];
                }
                else
                {
                    // BMP is BGR(A)
                    b = row[src];
                    g = row[src + 1];
                    r = row[src + 2];
                }

                rgb[dst] = r;
                rgb[dst + 1] = g;
                rgb[dst + 2] = b;
            }
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
