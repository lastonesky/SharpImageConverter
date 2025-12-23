using System;
using System.IO;

public static class BmpWriter
{
    public static void Write24(string path, int width, int height, byte[] rgb)
    {
        int rowStride = ((width * 3 + 3) / 4) * 4; // 4字节对齐
        int imageSize = rowStride * height;
        int fileSize = 14 + 40 + imageSize;

        // 预分配整文件缓冲：头(14+40) + 像素数据
        byte[] file = new byte[fileSize];

        // BITMAPFILEHEADER
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        WriteLe32ToBuffer(file, 2, fileSize);
        WriteLe16ToBuffer(file, 6, 0); // reserved1
        WriteLe16ToBuffer(file, 8, 0); // reserved2
        WriteLe32ToBuffer(file, 10, 14 + 40); // offset

        // BITMAPINFOHEADER
        WriteLe32ToBuffer(file, 14, 40);
        WriteLe32ToBuffer(file, 18, width);
        WriteLe32ToBuffer(file, 22, height);
        WriteLe16ToBuffer(file, 26, 1); // planes
        WriteLe16ToBuffer(file, 28, 24); // bpp
        WriteLe32ToBuffer(file, 30, 0); // compression
        WriteLe32ToBuffer(file, 34, imageSize);
        WriteLe32ToBuffer(file, 38, 2835); // hres (72 DPI)
        WriteLe32ToBuffer(file, 42, 2835); // vres
        WriteLe32ToBuffer(file, 46, 0); // colors
        WriteLe32ToBuffer(file, 50, 0); // important colors

        // 像素区（自底向上）
        int pixelOffset = 14 + 40;
        int srcRowSize = width * 3;
        for (int y = height - 1, rowIdx = 0; y >= 0; y--, rowIdx++)
        {
            int src = y * srcRowSize;
            int dst = pixelOffset + rowIdx * rowStride;
            for (int x = 0; x < width; x++)
            {
                int si = src + x * 3;
                int di = dst + x * 3;
                file[di + 0] = rgb[si + 2];
                file[di + 1] = rgb[si + 1];
                file[di + 2] = rgb[si + 0];
            }
            // 剩余的对齐填充字节保持为 0（数组默认初始化为0）
        }

        // 单次写出
        File.WriteAllBytes(path, file);
    }

    private static void WriteLe16(Stream s, int v)
        => s.Write(new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) }, 0, 2);
    private static void WriteLe32(Stream s, int v)
        => s.Write(new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF) }, 0, 4);

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
