using System;
using System.IO;

public static class BmpWriter
{
    public static void Write24(string path, int width, int height, byte[] rgb)
    {
        int rowStride = ((width * 3 + 3) / 4) * 4; // 4字节对齐
        int imageSize = rowStride * height;
        int fileSize = 14 + 40 + imageSize;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        // BITMAPFILEHEADER
        fs.WriteByte((byte)'B');
        fs.WriteByte((byte)'M');
        WriteLe32(fs, fileSize);
        WriteLe16(fs, 0); // reserved1
        WriteLe16(fs, 0); // reserved2
        WriteLe32(fs, 14 + 40); // offset
        // BITMAPINFOHEADER
        WriteLe32(fs, 40);
        WriteLe32(fs, width);
        WriteLe32(fs, height);
        WriteLe16(fs, 1); // planes
        WriteLe16(fs, 24); // bpp
        WriteLe32(fs, 0); // compression
        WriteLe32(fs, imageSize);
        WriteLe32(fs, 2835); // hres (72 DPI)
        WriteLe32(fs, 2835); // vres
        WriteLe32(fs, 0); // colors
        WriteLe32(fs, 0); // important colors

        byte[] row = new byte[rowStride];
        for (int y = height - 1; y >= 0; y--)
        {
            int src = y * width * 3;
            Array.Copy(rgb, src, row, 0, width * 3);
            fs.Write(row, 0, rowStride);
        }
    }

    private static void WriteLe16(Stream s, int v)
        => s.Write(new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) }, 0, 2);
    private static void WriteLe32(Stream s, int v)
        => s.Write(new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF) }, 0, 4);
}