using System;
using System.IO;
using System.Text;

namespace SharpImageConverter;

/// <summary>
/// 简单的 PNG 写入器，支持 RGB24 与 RGBA32。
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// 写入 RGB24 PNG 文件（颜色类型 2）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write(string path, int width, int height, byte[] rgb)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            Write(fs, width, height, rgb);
        }
    }

    /// <summary>
    /// 写入 RGB24 PNG 流（颜色类型 2）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write(Stream stream, int width, int height, byte[] rgb)
    {
        // PNG Signature
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        // IHDR
        WriteChunk(stream, "IHDR", CreateIHDR(width, height));

        // IDAT
        byte[] idatData = CreateIDAT(width, height, rgb);
        WriteChunk(stream, "IDAT", idatData);

        // IEND
        WriteChunk(stream, "IEND", new byte[0]);
    }

    /// <summary>
    /// 写入 RGBA32 PNG 文件（颜色类型 6）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            WriteRgba(fs, width, height, rgba);
        }
    }

    /// <summary>
    /// 写入 RGBA32 PNG 流（颜色类型 6）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    public static void WriteRgba(Stream stream, int width, int height, byte[] rgba)
    {
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        WriteChunk(stream, "IHDR", CreateIHDRRgba(width, height));
        byte[] idatData = CreateIDATRgba(width, height, rgba);
        WriteChunk(stream, "IDAT", idatData);
        WriteChunk(stream, "IEND", new byte[0]);
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
        data[8] = 8;
        data[9] = 2;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CreateIHDRRgba(int width, int height)
    {
        byte[] data = new byte[13];
        Array.Copy(ToBigEndian((uint)width), 0, data, 0, 4);
        Array.Copy(ToBigEndian((uint)height), 0, data, 4, 4);
        data[8] = 8;
        data[9] = 6;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CreateIDAT(int width, int height, byte[] rgb)
    {
        int stride = width * 3;
        int rawSize = (stride + 1) * height;
        byte[] rawData = new byte[rawSize];

        int rawIdx = 0;
        int rgbIdx = 0;

        for (int y = 0; y < height; y++)
        {
            rawData[rawIdx++] = 0;
            Array.Copy(rgb, rgbIdx, rawData, rawIdx, stride);
            rgbIdx += stride;
            rawIdx += stride;
        }

        return ZlibHelper.Compress(rawData);
    }

    private static byte[] CreateIDATRgba(int width, int height, byte[] rgba)
    {
        int stride = width * 4;
        int rawSize = (stride + 1) * height;
        byte[] rawData = new byte[rawSize];
        int rawIdx = 0;
        int srcIdx = 0;
        for (int y = 0; y < height; y++)
        {
            rawData[rawIdx++] = 0;
            Array.Copy(rgba, srcIdx, rawData, rawIdx, stride);
            srcIdx += stride;
            rawIdx += stride;
        }
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
