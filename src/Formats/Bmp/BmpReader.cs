using System;
using System.IO;

namespace SharpImageConverter;

/// <summary>
/// 简单的 BMP 读取器，支持 24/32 位非压缩 BMP，输出 RGB24。
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
        int read = stream.Read(header);
        if (read != header.Length) throw new EndOfStreamException("BMP header 不完整");

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
            throw new InvalidDataException("Not a BMP file");

        int dataOffset = ReadLe32(header, 10);
        int dibSize = ReadLe32(header, 14);
        if (dibSize < 40) throw new NotSupportedException($"Unsupported DIB header size: {dibSize}");

        width = ReadLe32(header, 18);
        height = ReadLe32(header, 22);
        short bpp = ReadLe16(header, 28);
        int compression = ReadLe32(header, 30);

        if (bpp != 24 && bpp != 32)
            throw new NotSupportedException($"Only 24/32-bit BMPs are supported. Found {bpp}-bit.");
        
        if (compression != 0 && compression != 3) // BI_RGB or BI_BITFIELDS
            throw new NotSupportedException("Compressed BMPs are not supported");

        // Assuming standard top-down or bottom-up
        bool bottomUp = height > 0;
        height = Math.Abs(height);

        int rowStride = ((width * bpp + 31) / 32) * 4;
        byte[] rgb = new byte[width * height * 3];

        int pixelSize = bpp / 8;

        // 如果 stream 支持 Seek，则跳转到 dataOffset。
        // 注意：dataOffset 是相对于文件开头的。
        // 如果 stream 是部分流，可能需要考虑偏移。但通常 BMP 是整个文件。
        // 假设 stream 当前位置是 header 之后。
        // dataOffset 通常大于 54。
        if (stream.CanSeek)
        {
            // 这里假设 dataOffset 是相对于 stream 起始位置的绝对偏移
            // 但如果 stream 是从中间开始的（例如 MemoryStream 的 slice），Position 可能不是 0
            // 简单起见，假设 stream 是完整的文件流，或者 dataOffset 是相对于当前 stream 起始位置的。
            // 实际上 BMP 格式的 dataOffset 是绝对偏移。
            // 如果传入的是 FileStream，Position = dataOffset 是对的。
            // 如果传入的是包含其他数据的流，我们需要知道 BMP 在流中的起始位置。
            // 但 Read 方法无法知道 BMP 在流中的起始位置，除非我们传递它，或者假设当前 Position - 54 就是起始位置。
            // 让我们假设 stream 刚开始读取 header 时的位置是 BMP 的起始位置。
            // 也就是 currentPos - 54。
            // 更好的做法是，不要 Seek 到绝对位置，而是 skip 掉中间的数据。
            long currentPos = stream.Position;
            // 已经读了 54 字节。
            // 需要跳过 dataOffset - 54 字节。
            int skip = dataOffset - 54;
            if (skip > 0)
            {
                if (stream.CanSeek)
                {
                    stream.Seek(skip, SeekOrigin.Current);
                }
                else
                {
                    byte[] temp = new byte[skip];
                    stream.ReadExactly(temp, 0, skip);
                }
            }
        }
        else
        {
             // 无法 Seek，必须读取并丢弃
            int skip = dataOffset - 54;
            if (skip > 0)
            {
                byte[] temp = new byte[skip];
                stream.ReadExactly(temp, 0, skip);
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

                // BMP is BGR(A)
                byte b = row[src];
                byte g = row[src + 1];
                byte r = row[src + 2];

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
