using System;
using System.Buffers;
using System.IO;

namespace SharpImageConverter;

/// <summary>
/// 简单的 BMP 写入器，支持将 RGB24 写出为 24 位 BMP。
/// </summary>
public static class BmpWriter
{
    public static void Write8(string path, int width, int height, byte[] gray)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write8(fs, width, height, gray);
    }

    public static void Write8(Stream stream, int width, int height, byte[] gray)
    {
        int rowStride = ((width + 3) / 4) * 4;
        int imageSize = rowStride * height;
        int paletteSize = 256 * 4;
        int fileSize = 14 + 40 + paletteSize + imageSize;

        byte[] header = new byte[14 + 40];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        WriteLe32ToBuffer(header, 2, fileSize);
        WriteLe16ToBuffer(header, 6, 0);
        WriteLe16ToBuffer(header, 8, 0);
        WriteLe32ToBuffer(header, 10, 14 + 40 + paletteSize);
        WriteLe32ToBuffer(header, 14, 40);
        WriteLe32ToBuffer(header, 18, width);
        WriteLe32ToBuffer(header, 22, height);
        WriteLe16ToBuffer(header, 26, 1);
        WriteLe16ToBuffer(header, 28, 8);
        WriteLe32ToBuffer(header, 30, 0);
        WriteLe32ToBuffer(header, 34, imageSize);
        WriteLe32ToBuffer(header, 38, 2835);
        WriteLe32ToBuffer(header, 42, 2835);
        WriteLe32ToBuffer(header, 46, 256);
        WriteLe32ToBuffer(header, 50, 0);
        stream.Write(header, 0, header.Length);

        byte[] palette = new byte[paletteSize];
        int pi = 0;
        for (int i = 0; i < 256; i++)
        {
            palette[pi + 0] = (byte)i;
            palette[pi + 1] = (byte)i;
            palette[pi + 2] = (byte)i;
            palette[pi + 3] = 0;
            pi += 4;
        }
        stream.Write(palette, 0, palette.Length);

        byte[] row = ArrayPool<byte>.Shared.Rent(rowStride);
        try
        {
            for (int y = height - 1; y >= 0; y--)
            {
                int srcBase = y * width;
                Array.Copy(gray, srcBase, row, 0, width);
                for (int i = width; i < rowStride; i++)
                {
                    row[i] = 0;
                }
                stream.Write(row, 0, rowStride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }
    /// <summary>
    /// 以 24 位 BMP 格式写出 RGB24 像素数据
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write24(string path, int width, int height, byte[] rgb)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write24(fs, width, height, rgb);
    }

    /// <summary>
    /// 以 24 位 BMP 格式写出 RGB24 像素数据
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write24(Stream stream, int width, int height, byte[] rgb)
    {
        int rowStride = ((width * 3 + 3) / 4) * 4;
        int imageSize = rowStride * height;
        int fileSize = 14 + 40 + imageSize;

        byte[] header = new byte[14 + 40];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        WriteLe32ToBuffer(header, 2, fileSize);
        WriteLe16ToBuffer(header, 6, 0);
        WriteLe16ToBuffer(header, 8, 0);
        WriteLe32ToBuffer(header, 10, 14 + 40);
        WriteLe32ToBuffer(header, 14, 40);
        WriteLe32ToBuffer(header, 18, width);
        WriteLe32ToBuffer(header, 22, height);
        WriteLe16ToBuffer(header, 26, 1);
        WriteLe16ToBuffer(header, 28, 24);
        WriteLe32ToBuffer(header, 30, 0);
        WriteLe32ToBuffer(header, 34, imageSize);
        WriteLe32ToBuffer(header, 38, 2835);
        WriteLe32ToBuffer(header, 42, 2835);
        WriteLe32ToBuffer(header, 46, 0);
        WriteLe32ToBuffer(header, 50, 0);
        stream.Write(header, 0, header.Length);

        int srcRowSize = width * 3;
        byte[] row = ArrayPool<byte>.Shared.Rent(rowStride);
        try
        {
            for (int y = height - 1; y >= 0; y--)
            {
                int srcBase = y * srcRowSize;
                int dstIndex = 0;
                int srcIndex = srcBase;
                int srcEnd = srcBase + srcRowSize;
                while (srcIndex < srcEnd)
                {
                    byte r = rgb[srcIndex + 0];
                    byte g = rgb[srcIndex + 1];
                    byte b = rgb[srcIndex + 2];
                    row[dstIndex + 0] = b;
                    row[dstIndex + 1] = g;
                    row[dstIndex + 2] = r;
                    srcIndex += 3;
                    dstIndex += 3;
                }
                while (dstIndex < rowStride)
                {
                    row[dstIndex++] = 0;
                }
                stream.Write(row, 0, rowStride);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    private static void WriteLe16(Stream s, int v)
        => s.Write([(byte)(v & 0xFF), (byte)((v >> 8) & 0xFF)]);
    private static void WriteLe32(Stream s, int v)
        => s.Write([(byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF)]);

    private static void WriteLe16ToBuffer(byte[] buf, int offset, int v)
    {
        buf[offset + 0] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)((v >> 8) & 0xFF);
    }
    private static void WriteLe32ToBuffer(byte[] buf, int offset, int v)
    {
        buf[offset + 0] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)((v >> 8) & 0xFF);
        buf[offset + 2] = (byte)((v >> 16) & 0xFF);
        buf[offset + 3] = (byte)((v >> 24) & 0xFF);
    }
}
