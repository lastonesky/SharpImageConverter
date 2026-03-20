using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace SharpImageConverter.Formats.Bmp;

/// <summary>
/// 简单的 BMP 写入器，支持将 RGB24 写出为 24 位 BMP。
/// </summary>
public static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int DefaultPelsPerMeter = 2835; // ~72 DPI

    public static void Write8(string path, int width, int height, byte[] gray)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        Write8(fs, width, height, gray);
    }

    public static void Write8(Stream stream, int width, int height, byte[] gray)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(gray);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");

        int expectedGrayLength = checked(width * height);
        if (gray.Length != expectedGrayLength) throw new ArgumentException("Gray buffer length does not match dimensions.", nameof(gray));

        int rowStride = checked(((width + 3) / 4) * 4);
        int imageSize = checked(rowStride * height);
        int paletteSize = 256 * 4;
        int fileSize = checked(FileHeaderSize + InfoHeaderSize + paletteSize + imageSize);

        // Header
        Span<byte> header = stackalloc byte[FileHeaderSize + InfoHeaderSize];
        
        // File Header
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(2), fileSize);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(6), 0); // Reserved 1
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(8), 0); // Reserved 2
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(10), FileHeaderSize + InfoHeaderSize + paletteSize); // Offset to data

        // Info Header
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(14), InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(26), 1); // Planes
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(28), 8); // BPP
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(30), 0); // Compression (BI_RGB)
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(34), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(38), DefaultPelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(42), DefaultPelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(46), 256); // Colors used
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(50), 0); // Colors important

        stream.Write(header);

        // Palette (Grayscale)
        Span<byte> palette = stackalloc byte[paletteSize];
        for (int i = 0; i < 256; i++)
        {
            int offset = i * 4;
            palette[offset + 0] = (byte)i; // B
            palette[offset + 1] = (byte)i; // G
            palette[offset + 2] = (byte)i; // R
            palette[offset + 3] = 0;       // A
        }
        stream.Write(palette);

        byte[] row = ArrayPool<byte>.Shared.Rent(rowStride);
        try
        {
            // Bottom-up
            for (int y = height - 1; y >= 0; y--)
            {
                int srcBase = y * width;
                Buffer.BlockCopy(gray, srcBase, row, 0, width);
                if (rowStride > width)
                {
                    row.AsSpan(width, rowStride - width).Clear();
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
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
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
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rgb);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");

        int srcRowSize = checked(width * 3);
        int expectedRgbLength = checked(srcRowSize * height);
        if (rgb.Length != expectedRgbLength) throw new ArgumentException("RGB buffer length does not match dimensions.", nameof(rgb));

        int rowStride = checked(((srcRowSize + 3) / 4) * 4);
        int imageSize = checked(rowStride * height);
        int fileSize = checked(FileHeaderSize + InfoHeaderSize + imageSize);

        Span<byte> header = stackalloc byte[FileHeaderSize + InfoHeaderSize];
        
        // File Header
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(2), fileSize);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(6), 0);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(10), FileHeaderSize + InfoHeaderSize); // Offset to data

        // Info Header
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(14), InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(30), 0); // Compression
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(34), imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(38), DefaultPelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(42), DefaultPelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(46), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(50), 0);
        
        stream.Write(header);

        int padding = rowStride - srcRowSize;
        
        byte[] row = ArrayPool<byte>.Shared.Rent(rowStride);
        try
        {
            unsafe
            {
                fixed (byte* pRgb = rgb)
                fixed (byte* pRow = row)
                {
                    for (int y = height - 1; y >= 0; y--)
                    {
                        byte* src = pRgb + (y * srcRowSize);
                        byte* dst = pRow;
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
                        
                        if (padding > 0)
                        {
                            row.AsSpan(srcRowSize, padding).Clear();
                        }
                        stream.Write(row, 0, rowStride);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }
}
