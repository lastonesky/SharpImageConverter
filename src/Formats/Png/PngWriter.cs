using System;
using System.IO;
using System.Text;

namespace SharpImageConverter;

public static class PngWriter
{
    public static void Write(string path, int width, int height, byte[] rgb)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            // PNG Signature
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            // IHDR
            WriteChunk(fs, "IHDR", CreateIHDR(width, height));

            // IDAT
            byte[] idatData = CreateIDAT(width, height, rgb);
            WriteChunk(fs, "IDAT", idatData);

            // IEND
            WriteChunk(fs, "IEND", new byte[0]);
        }
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] lenBytes = ToBigEndian((uint)data.Length);
        s.Write(lenBytes, 0, 4);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);

        if (data.Length > 0)
        {
            s.Write(data, 0, data.Length);
        }

        uint crc = Crc32.Compute(typeBytes);
        crc = Crc32.Update(crc, data, 0, data.Length);
        
        byte[] crcBytes = ToBigEndian(crc);
        s.Write(crcBytes, 0, 4);
    }

    private static byte[] CreateIHDR(int width, int height)
    {
        byte[] data = new byte[13];
        Array.Copy(ToBigEndian((uint)width), 0, data, 0, 4);
        Array.Copy(ToBigEndian((uint)height), 0, data, 4, 4);
        data[8] = 8; // BitDepth
        data[9] = 2; // ColorType: Truecolor
        data[10] = 0; // Compression
        data[11] = 0; // Filter
        data[12] = 0; // Interlace: None
        return data;
    }

    private static byte[] CreateIDAT(int width, int height, byte[] rgb)
    {
        // Filter: None (0) for all rows
        int stride = width * 3;
        int rawSize = (stride + 1) * height;
        byte[] rawData = new byte[rawSize];

        int rawIdx = 0;
        int rgbIdx = 0;

        for (int y = 0; y < height; y++)
        {
            rawData[rawIdx++] = 0; // Filter Type: None
            Array.Copy(rgb, rgbIdx, rawData, rawIdx, stride);
            rgbIdx += stride;
            rawIdx += stride;
        }

        // Compress
        return ZlibHelper.Compress(rawData);
    }

    private static byte[] ToBigEndian(uint val)
    {
        return new byte[]
        {
            (byte)((val >> 24) & 0xFF),
            (byte)((val >> 16) & 0xFF),
            (byte)((val >> 8) & 0xFF),
            (byte)(val & 0xFF)
        };
    }
}
