using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Bmp;

/// <summary>
/// 增强型 BMP 写入器，支持 RGB24 写出为 24/8 位 BMP。
/// 已启用 SIMD 加速（RGB→BGR）和行缓冲分级分配。
/// </summary>
public static class BmpWriter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int DefaultPelsPerMeter = 2835; // ~72 DPI

    // 行缓冲阈值
    private const int StackallocRowThreshold = 1024;

    // SSSE3 RGB→BGR 的 shuffle 控制掩码（与 Reader 的 BGR→RGB 相反）
    // 输入 12 字节: [R0,G0,B0, R1,G1,B1, R2,G2,B2, R3,G3,B3] 补齐到 16 字节
    // 输出 12 字节: [B0,G0,R0, B1,G1,R1, B2,G2,R2, B3,G3,R3]
    private static readonly Vector128<byte> ShuffleRgbToBgr =
        Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 0, 0, 0, 0);

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

        // 行缓冲分级分配
        bool useStackRow = rowStride <= StackallocRowThreshold;
        byte[]? rentedRow = null;
        Span<byte> row = useStackRow
            ? stackalloc byte[rowStride]
            : (rentedRow = ArrayPool<byte>.Shared.Rent(rowStride)).AsSpan(0, rowStride);

        try
        {
            // Bottom-up
            for (int y = height - 1; y >= 0; y--)
            {
                int srcBase = y * width;
                gray.AsSpan(srcBase, width).CopyTo(row);
                if (rowStride > width)
                {
                    row.Slice(width, rowStride - width).Clear();
                }
                stream.Write(row);
            }
        }
        finally
        {
            if (rentedRow != null)
                ArrayPool<byte>.Shared.Return(rentedRow);
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
        Write24(fs, width, height, rgb, null);
    }

    /// <summary>
    /// 以 24 位 BMP 格式写出 RGB24 像素数据
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    /// <param name="metadata">图像元数据（含 DPI）</param>
    public static void Write24(string path, int width, int height, byte[] rgb, ImageMetadata? metadata)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        Write24(fs, width, height, rgb, metadata);
    }

    /// <summary>
    /// 以 24 位 BMP 格式写出 RGB24 像素数据（无元数据）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write24(Stream stream, int width, int height, byte[] rgb)
    {
        Write24(stream, width, height, rgb, null);
    }

    /// <summary>
    /// 以 24 位 BMP 格式写出 RGB24 像素数据
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    /// <param name="metadata">图像元数据（含 DPI），可为 null</param>
    public static void Write24(Stream stream, int width, int height, byte[] rgb, ImageMetadata? metadata)
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

        // 从 metadata 获取 DPI，若无则使用默认值
        int pelsPerMeterX, pelsPerMeterY;
        if (metadata != null && (metadata.HorizontalDpi > 0 || metadata.VerticalDpi > 0))
        {
            pelsPerMeterX = metadata.HorizontalDpi > 0
                ? (int)Math.Round(metadata.HorizontalDpi / 0.0254)
                : DefaultPelsPerMeter;
            pelsPerMeterY = metadata.VerticalDpi > 0
                ? (int)Math.Round(metadata.VerticalDpi / 0.0254)
                : DefaultPelsPerMeter;
        }
        else
        {
            pelsPerMeterX = DefaultPelsPerMeter;
            pelsPerMeterY = DefaultPelsPerMeter;
        }

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
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(38), pelsPerMeterX);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(42), pelsPerMeterY);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(46), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(50), 0);
        
        stream.Write(header);

        int padding = rowStride - srcRowSize;
        bool useSimd = Ssse3.IsSupported;

        // 行缓冲分级分配
        bool useStackRow = rowStride <= StackallocRowThreshold * 4;
        byte[]? rentedRow = null;
        Span<byte> row = useStackRow
            ? stackalloc byte[rowStride]
            : (rentedRow = ArrayPool<byte>.Shared.Rent(rowStride)).AsSpan(0, rowStride);

        try
        {
            unsafe
            {
                fixed (byte* pRgb = rgb)
                fixed (byte* pRow = row)
                {
                    Vector128<byte> shuffleMask = ShuffleRgbToBgr;

                    for (int y = height - 1; y >= 0; y--)
                    {
                        byte* src = pRgb + (y * srcRowSize);
                        byte* dst = pRow;
                        int x = 0;

                        if (useSimd)
                        {
                            int simdEnd = (width & ~3) * 3; // process 4 pixels per iteration
                            for (; x < simdEnd; x += 12)
                            {
                                // Load 12 bytes (with 4-byte over-read into 16-byte reg)
                                var v = Sse2.LoadVector128(src + x);
                                // Shuffle: RGB → BGR
                                var bgr128 = Ssse3.Shuffle(v, shuffleMask);
                                // Store 12 bytes
                                *(ulong*)(dst + x) = *(ulong*)&bgr128;
                                *(uint*)(dst + x + 8) = *(uint*)((byte*)&bgr128 + 8);
                            }
                        }

                        // Scalar remainder
                        for (; x < srcRowSize; x += 3)
                        {
                            dst[x + 0] = src[x + 2]; // B
                            dst[x + 1] = src[x + 1]; // G
                            dst[x + 2] = src[x + 0]; // R
                        }

                        if (padding > 0)
                        {
                            row.Slice(srcRowSize, padding).Clear();
                        }
                        stream.Write(row);
                    }
                }
            }
        }
        finally
        {
            if (rentedRow != null)
                ArrayPool<byte>.Shared.Return(rentedRow);
        }
    }
}
