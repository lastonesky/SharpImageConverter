using System;
using System.IO;

namespace SharpImageConverter;

public static class BmpReader
{
    public static byte[] Read(string path, out int width, out int height)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[54];
        int read = fs.Read(header);
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

        fs.Position = dataOffset;
        byte[] row = new byte[rowStride];
        for (int rowIndex = 0; rowIndex < height; rowIndex++)
        {
            fs.ReadExactly(row, 0, rowStride);
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
