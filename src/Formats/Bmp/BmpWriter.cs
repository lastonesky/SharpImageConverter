using System;
using System.Buffers;
using System.IO;

namespace SharpImageConverter;

/// <summary>
/// 简单的 BMP 写入器，支持将 RGB24 写出为 24 位 BMP。
/// </summary>
public static class BmpWriter
{
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

        byte[] file = ArrayPool<byte>.Shared.Rent(fileSize);

        try
        {
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            WriteLe32ToBuffer(file, 2, fileSize);
            WriteLe16ToBuffer(file, 6, 0);
            WriteLe16ToBuffer(file, 8, 0);
            WriteLe32ToBuffer(file, 10, 14 + 40);

            WriteLe32ToBuffer(file, 14, 40);
            WriteLe32ToBuffer(file, 18, width);
            WriteLe32ToBuffer(file, 22, height);
            WriteLe16ToBuffer(file, 26, 1);
            WriteLe16ToBuffer(file, 28, 24);
            WriteLe32ToBuffer(file, 30, 0);
            WriteLe32ToBuffer(file, 34, imageSize);
            WriteLe32ToBuffer(file, 38, 2835);
            WriteLe32ToBuffer(file, 42, 2835);
            WriteLe32ToBuffer(file, 46, 0);
            WriteLe32ToBuffer(file, 50, 0);

            int pixelOffset = 14 + 40;
            int srcRowSize = width * 3;

            unsafe
            {
                fixed (byte* rgbPtr = rgb)
                fixed (byte* filePtr = file)
                {
                    byte* dstRow = filePtr + pixelOffset;

                    for (int y = height - 1; y >= 0; y--)
                    {
                        byte* src = rgbPtr + y * srcRowSize;
                        byte* dst = dstRow;
                        byte* srcEnd = src + srcRowSize;

                        while (src < srcEnd)
                        {
                            byte r = src[0];
                            byte g = src[1];
                            byte b = src[2];

                            dst[0] = b;
                            dst[1] = g;
                            dst[2] = r;

                            src += 3;
                            dst += 3;
                        }

                        dstRow += rowStride;
                    }
                }
            }
            stream.Write(file, 0, fileSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(file);
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
