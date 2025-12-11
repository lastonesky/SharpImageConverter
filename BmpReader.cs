using System;
using System.IO;

public static class BmpReader
{
    public static byte[] Read(string path, out int width, out int height)
    {
        byte[] file = File.ReadAllBytes(path);

        if (file[0] != 'B' || file[1] != 'M')
            throw new InvalidDataException("Not a BMP file");

        int dataOffset = ReadLe32(file, 10);
        width = ReadLe32(file, 18);
        height = ReadLe32(file, 22);
        short bpp = ReadLe16(file, 28);
        int compression = ReadLe32(file, 30);

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

        for (int y = 0; y < height; y++)
        {
            int srcY = bottomUp ? (height - 1 - y) : y;
            int srcOffset = dataOffset + srcY * rowStride;
            int dstOffset = y * width * 3;

            for (int x = 0; x < width; x++)
            {
                int src = srcOffset + x * pixelSize;
                int dst = dstOffset + x * 3;

                // BMP is BGR(A)
                byte b = file[src];
                byte g = file[src + 1];
                byte r = file[src + 2];

                rgb[dst] = r;
                rgb[dst + 1] = g;
                rgb[dst + 2] = b;
            }
        }

        return rgb;
    }

    private static short ReadLe16(byte[] buf, int offset)
    {
        return (short)(buf[offset] | (buf[offset + 1] << 8));
    }

    private static int ReadLe32(byte[] buf, int offset)
    {
        return buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);
    }
}