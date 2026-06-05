using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Formats.Bmp;

/// <summary>
/// 增强型 BMP 读取器，支持 8/16/24/32 位非压缩 BMP，输出 RGB24。
/// 已启用 SIMD 加速（32-bit BGR0→RGB）和行缓冲分级分配。
/// </summary>
public static class BmpReader
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int HeaderSize = FileHeaderSize + InfoHeaderSize;

    // 行缓冲阈值：宽度 ≤ 1024（即 1024*4 = 4096 字节）用 stackalloc
    private const int StackallocRowThreshold = 1024;

    // SSE2 32-bit→RGB24 的 shuffle 控制掩码
    // 输入 16 字节: [B0,G0,R0,X0, B1,G1,R1,X1, B2,G2,R2,X2, B3,G3,R3,X3]
    // 输出 12 字节: [R0,G0,B0, R1,G1,B1, R2,G2,B2, R3,G3,B3]
    private static readonly Vector128<byte> ShuffleBgr0ToRgb =
        Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0, 0, 0, 0);

    /// <summary>
    /// 读取 BMP 文件并返回 RGB24 像素数据
    /// </summary>
    /// <param name="path">输入文件路径</param>
    /// <param name="width">输出图像宽度</param>
    /// <param name="height">输出图像高度</param>
    /// <param name="xDpi">输出水平 DPI</param>
    /// <param name="yDpi">输出垂直 DPI</param>
    /// <returns>按 RGB 顺序排列的字节数组</returns>
    public static byte[] Read(string path, out int width, out int height, out double xDpi, out double yDpi)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Read(fs, out width, out height, out xDpi, out yDpi);
    }

    /// <summary>
    /// 读取 BMP 数据流并返回 RGB24 像素数据
    /// </summary>
    /// <param name="stream">输入数据流</param>
    /// <param name="width">输出图像宽度</param>
    /// <param name="height">输出图像高度</param>
    /// <param name="xDpi">输出水平 DPI</param>
    /// <param name="yDpi">输出垂直 DPI</param>
    /// <returns>按 RGB 顺序排列的字节数组</returns>
    public static byte[] Read(Stream stream, out int width, out int height, out double xDpi, out double yDpi)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[HeaderSize];
        int totalRead = 0;
        while (totalRead < header.Length)
        {
            int n = stream.Read(header.Slice(totalRead));
            if (n == 0) break;
            totalRead += n;
        }
        if (totalRead != header.Length) throw new EndOfStreamException("BMP header 不完整");

        if (header[0] != (byte)'B' || header[1] != (byte)'M')
            throw new InvalidDataException("Not a BMP file");

        int fileSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(2));
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(10));

        int dibSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(14));
        if (dibSize < 40) throw new NotSupportedException($"Unsupported DIB header size: {dibSize}");
        if (dataOffset < FileHeaderSize + dibSize) throw new InvalidDataException($"Invalid BMP data offset: {dataOffset}");
        if (fileSize > 0 && dataOffset > fileSize) throw new InvalidDataException($"Invalid BMP data offset: {dataOffset}");

        int rawWidth = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22));
        short planes = BinaryPrimitives.ReadInt16LittleEndian(header.Slice(26));
        short bpp = BinaryPrimitives.ReadInt16LittleEndian(header.Slice(28));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(30));
        int clrUsed = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(46));

        // DPI from BMP header (bXPelsPerMeter / bYPelsPerMeter at offsets 38/42)
        int pelsPerMeterX = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(38));
        int pelsPerMeterY = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(42));
        xDpi = pelsPerMeterX > 0 ? pelsPerMeterX * 0.0254 : 0.0;
        yDpi = pelsPerMeterY > 0 ? pelsPerMeterY * 0.0254 : 0.0;

        if (planes != 1) throw new InvalidDataException($"Invalid BMP planes value: {planes}");
        if (rawWidth <= 0) throw new InvalidDataException($"Invalid BMP width: {rawWidth}");
        if (rawHeight == 0 || rawHeight == int.MinValue) throw new InvalidDataException($"Invalid BMP height: {rawHeight}");

        // BI_ALPHABITFIELDS (6) is functionally identical to BI_BITFIELDS (3) for RGB decode
        if (compression == 6) compression = 3; // treat as bitfields (alpha is discarded for RGB24 output)

        // Validate compression + bpp combinations
        switch (bpp)
        {
            case 8:
                if (compression != 0)
                    throw new NotSupportedException("Only uncompressed 8-bit BMPs (BI_RGB) are supported.");
                break;
            case 16:
                if (compression != 0 && compression != 3)
                    throw new NotSupportedException("Compressed 16-bit BMPs are not supported");
                break;
            case 24:
            case 32:
                if (compression != 0 && compression != 3)
                    throw new NotSupportedException("Compressed BMPs are not supported");
                break;
            default:
                throw new NotSupportedException($"Only 8/16/24/32-bit BMPs are supported. Found {bpp}-bit.");
        }

        bool bottomUp = rawHeight > 0;
        width = rawWidth;
        height = Math.Abs(rawHeight);

        long rowBits = (long)width * bpp;
        long rowStrideLong = ((rowBits + 31L) / 32L) * 4L;
        if (rowStrideLong <= 0 || rowStrideLong > int.MaxValue) throw new InvalidDataException("BMP row stride is invalid");
        int rowStride = (int)rowStrideLong;

        long rgbLength = checked((long)width * height * 3L);
        if (rgbLength > int.MaxValue) throw new InvalidDataException("BMP image is too large");
        byte[] rgb = new byte[(int)rgbLength];

        byte[]? paletteRgb = null;
        int paletteEntries = 0;
        uint redMask = 0;
        uint greenMask = 0;
        uint blueMask = 0;
        bool useBitfields = compression == 3;

        long currentPosition = HeaderSize;
        if (dibSize > InfoHeaderSize)
        {
            int extraDibBytes = dibSize - InfoHeaderSize;
            byte[] extraDib = ArrayPool<byte>.Shared.Rent(extraDibBytes);
            try
            {
                stream.ReadExactly(extraDib, 0, extraDibBytes);
                if (useBitfields && extraDibBytes >= 12)
                {
                    redMask = BinaryPrimitives.ReadUInt32LittleEndian(extraDib.AsSpan(0, 4));
                    greenMask = BinaryPrimitives.ReadUInt32LittleEndian(extraDib.AsSpan(4, 4));
                    blueMask = BinaryPrimitives.ReadUInt32LittleEndian(extraDib.AsSpan(8, 4));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(extraDib);
            }
            currentPosition += extraDibBytes;
        }

        if (useBitfields && redMask == 0 && greenMask == 0 && blueMask == 0)
        {
            Span<byte> masks = stackalloc byte[12];
            stream.ReadExactly(masks);
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(0, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(4, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(masks.Slice(8, 4));
            currentPosition += masks.Length;
        }

        // Flattened mask info — avoid MaskInfo struct copy in loops
        uint rMask = 0, gMask = 0, bMask = 0;
        int rShift = 0, gShift = 0, bShift = 0;
        uint rMax = 0, gMax = 0, bMax = 0;

        if (useBitfields)
        {
            ExtractMask(redMask, out rMask, out rShift, out rMax);
            ExtractMask(greenMask, out gMask, out gShift, out gMax);
            ExtractMask(blueMask, out bMask, out bShift, out bMax);
        }

        if (bpp == 8)
        {
            if (clrUsed < 0) throw new InvalidDataException($"Invalid BMP palette size: {clrUsed}");
            paletteEntries = clrUsed == 0 ? 256 : clrUsed;
            if ((uint)paletteEntries > 256u || paletteEntries == 0) throw new InvalidDataException($"Invalid BMP palette entries: {paletteEntries}");
            int paletteByteCount = checked(paletteEntries * 4);

            byte[] paletteRaw = ArrayPool<byte>.Shared.Rent(paletteByteCount);
            try
            {
                stream.ReadExactly(paletteRaw, 0, paletteByteCount);
                currentPosition += paletteByteCount;

                paletteRgb = new byte[paletteEntries * 3];
                int palSrc = 0;
                int palDst = 0;
                for (int i = 0; i < paletteEntries; i++)
                {
                    paletteRgb[palDst + 0] = paletteRaw[palSrc + 2]; // R
                    paletteRgb[palDst + 1] = paletteRaw[palSrc + 1]; // G
                    paletteRgb[palDst + 2] = paletteRaw[palSrc + 0]; // B
                    palSrc += 4;
                    palDst += 3;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(paletteRaw);
            }
        }

        long gap = dataOffset - currentPosition;
        if (gap > 0)
        {
            if (stream.CanSeek)
            {
                stream.Seek(gap, SeekOrigin.Current);
            }
            else
            {
                byte[] temp = ArrayPool<byte>.Shared.Rent((int)Math.Min(gap, 4096));
                try
                {
                    long remaining = gap;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(remaining, temp.Length);
                        int n = stream.Read(temp, 0, toRead);
                        if (n == 0) throw new EndOfStreamException("BMP data offset exceeds stream length");
                        remaining -= n;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(temp);
                }
            }
        }
        else if (gap < 0)
        {
            throw new InvalidDataException($"Invalid BMP data offset: {dataOffset}. Current read: {currentPosition}");
        }

        // 行缓冲分级分配：小行走栈，大行走 ArrayPool
        bool useStackRow = width * 4 <= StackallocRowThreshold * 4;
        byte[]? rentedRow = null;
        Span<byte> rowSpan = useStackRow
            ? stackalloc byte[rowStride]
            : (rentedRow = ArrayPool<byte>.Shared.Rent(rowStride)).AsSpan(0, rowStride);

        try
        {
            if (bpp == 8)
            {
                Decode8Bit(stream, rowSpan, rowStride, width, height, bottomUp, rgb, paletteRgb!, paletteEntries);
            }
            else if (bpp == 16)
            {
                Decode16Bit(stream, rowSpan, rowStride, width, height, bottomUp, rgb,
                    useBitfields, rMask, rShift, rMax, gMask, gShift, gMax, bMask, bShift, bMax);
            }
            else if (bpp == 24)
            {
                Decode24Bit(stream, rowSpan, rowStride, width, height, bottomUp, rgb,
                    useBitfields, rMask, rShift, rMax, gMask, gShift, gMax, bMask, bShift, bMax);
            }
            else // bpp == 32
            {
                Decode32Bit(stream, rowSpan, rowStride, width, height, bottomUp, rgb,
                    useBitfields, rMask, rShift, rMax, gMask, gShift, gMax, bMask, bShift, bMax);
            }
        }
        finally
        {
            if (rentedRow != null)
                ArrayPool<byte>.Shared.Return(rentedRow);
        }

        return rgb;
    }

    private static void Decode8Bit(Stream stream, Span<byte> row, int rowStride,
        int width, int height, bool bottomUp, byte[] rgb, byte[] paletteRgb, int paletteEntries)
    {
        unsafe
        {
            fixed (byte* pRow = row)
            fixed (byte* pDst = rgb)
            fixed (byte* pPal = paletteRgb)
            {
                for (int rowIndex = 0; rowIndex < height; rowIndex++)
                {
                    stream.ReadExactly(row);

                    int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                    byte* dst = pDst + (dstY * width * 3);
                    byte* src = pRow;
                    for (int x = 0; x < width; x++)
                    {
                        int index = src[x];
                        if ((uint)index >= (uint)paletteEntries)
                            index = 0;
                        int palOffset = index * 3;
                        dst[0] = pPal[palOffset + 0];
                        dst[1] = pPal[palOffset + 1];
                        dst[2] = pPal[palOffset + 2];
                        dst += 3;
                    }
                }
            }
        }
    }

    private static void Decode16Bit(Stream stream, Span<byte> row, int rowStride,
        int width, int height, bool bottomUp, byte[] rgb,
        bool useBitfields,
        uint rMask, int rShift, uint rMax,
        uint gMask, int gShift, uint gMax,
        uint bMask, int bShift, uint bMax)
    {
        if (!useBitfields)
        {
            // BI_RGB 16-bit: default RGB555 (5-5-5, bit 15 unused)
            // Format: [B0..B4, G0..G4, R0..R4, X]
            // Little-endian: low 5 bits = blue, next 5 = green, next 5 = red, top bit = unused
            unsafe
            {
                fixed (byte* pRow = row)
                fixed (byte* pDst = rgb)
                {
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row);

                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        byte* dst = pDst + (dstY * width * 3);
                        ushort* src16 = (ushort*)pRow;
                        for (int x = 0; x < width; x++)
                        {
                            ushort pixel = src16[x];
                            byte r = (byte)((pixel >> 10) & 0x1F);
                            byte g = (byte)((pixel >> 5) & 0x1F);
                            byte b = (byte)(pixel & 0x1F);
                            // Scale 5-bit to 8-bit
                            dst[0] = (byte)((r << 3) | (r >> 2)); // * 255 / 31
                            dst[1] = (byte)((g << 3) | (g >> 2));
                            dst[2] = (byte)((b << 3) | (b >> 2));
                            dst += 3;
                        }
                    }
                }
            }
        }
        else
        {
            // BI_BITFIELDS 16-bit — custom masks
            unsafe
            {
                fixed (byte* pRow = row)
                fixed (byte* pDst = rgb)
                {
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row);

                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        byte* dst = pDst + (dstY * width * 3);
                        ushort* src16 = (ushort*)pRow;
                        for (int x = 0; x < width; x++)
                        {
                            uint pixel = src16[x];
                            dst[0] = ExtractChannel(pixel, rMask, rShift, rMax);
                            dst[1] = ExtractChannel(pixel, gMask, gShift, gMax);
                            dst[2] = ExtractChannel(pixel, bMask, bShift, bMax);
                            dst += 3;
                        }
                    }
                }
            }
        }
    }

    private static void Decode24Bit(Stream stream, Span<byte> row, int rowStride,
        int width, int height, bool bottomUp, byte[] rgb,
        bool useBitfields,
        uint rMask, int rShift, uint rMax,
        uint gMask, int gShift, uint gMax,
        uint bMask, int bShift, uint bMax)
    {
        int srcRowSize = width * 3;
        if (!useBitfields)
        {
            unsafe
            {
                fixed (byte* pRow = row)
                fixed (byte* pDst = rgb)
                {
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row);

                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        byte* src = pRow;
                        byte* dst = pDst + (dstY * width * 3);
                        byte* srcEnd = src + srcRowSize;
                        while (src < srcEnd)
                        {
                            dst[0] = src[2]; // R
                            dst[1] = src[1]; // G
                            dst[2] = src[0]; // B
                            src += 3;
                            dst += 3;
                        }
                    }
                }
            }
        }
        else
        {
            // BI_BITFIELDS 24-bit: read 3 bytes per pixel, apply bitfield masks
            unsafe
            {
                fixed (byte* pRow = row)
                fixed (byte* pDst = rgb)
                {
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row);

                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        byte* dst = pDst + (dstY * width * 3);
                        byte* src = pRow;
                        for (int x = 0; x < width; x++)
                        {
                            uint pixel = (uint)(src[0] | (src[1] << 8) | (src[2] << 16));
                            dst[0] = ExtractChannel(pixel, rMask, rShift, rMax);
                            dst[1] = ExtractChannel(pixel, gMask, gShift, gMax);
                            dst[2] = ExtractChannel(pixel, bMask, bShift, bMax);
                            src += 3;
                            dst += 3;
                        }
                    }
                }
            }
        }
    }

    private static void Decode32Bit(Stream stream, Span<byte> row, int rowStride,
        int width, int height, bool bottomUp, byte[] rgb,
        bool useBitfields,
        uint rMask, int rShift, uint rMax,
        uint gMask, int gShift, uint gMax,
        uint bMask, int bShift, uint bMax)
    {
        if (!useBitfields)
        {
            // BI_RGB 32-bit: BGRX → RGB (discard alpha)
            // Use SIMD (SSE2 Shuffle) for batches of 4 pixels
            unsafe
            {
                fixed (byte* pRow = row)
                fixed (byte* pDst = rgb)
                {
                    bool useSsse3 = Ssse3.IsSupported;
                    Vector128<byte> shuffleMask = ShuffleBgr0ToRgb;

                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row);

                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        byte* src = pRow;
                        byte* dst = pDst + (dstY * width * 3);
                        int x = 0;

                        if (useSsse3)
                        {
                            int simdEnd = width & ~3; // process 4 pixels per iteration
                            for (; x < simdEnd; x += 4)
                            {
                                // Load 4 pixels = 16 bytes
                                var v = Sse2.LoadVector128(src);
                                // Shuffle: BGRX → RGB padding (12 useful bytes)
                                var rgb128 = Ssse3.Shuffle(v, shuffleMask);
                                // Store first 8 bytes via pointer cast
                                *(ulong*)dst = *(ulong*)&rgb128;
                                // Store bytes 8-11 via pointer cast
                                *(uint*)(dst + 8) = *(uint*)((byte*)&rgb128 + 8);
                                src += 16;
                                dst += 12;
                            }
                        }

                        // Scalar remainder
                        for (; x < width; x++)
                        {
                            dst[0] = src[2]; // R
                            dst[1] = src[1]; // G
                            dst[2] = src[0]; // B
                            src += 4;
                            dst += 3;
                        }
                    }
                }
            }
        }
        else
        {
            // BI_BITFIELDS 32-bit: read 4 bytes per pixel, apply masks
            unsafe
            {
                fixed (byte* pRow = row)
                fixed (byte* pDst = rgb)
                {
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row);

                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        byte* dst = pDst + (dstY * width * 3);
                        uint* src32 = (uint*)pRow;
                        for (int x = 0; x < width; x++)
                        {
                            uint pixel = src32[x];
                            dst[0] = ExtractChannel(pixel, rMask, rShift, rMax);
                            dst[1] = ExtractChannel(pixel, gMask, gShift, gMax);
                            dst[2] = ExtractChannel(pixel, bMask, bShift, bMax);
                            dst += 3;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 解析位域掩码，输出展平的 mask/shift/max 值（避免 struct 拷贝）。
    /// </summary>
    private static void ExtractMask(uint mask, out uint outMask, out int outShift, out uint outMax)
    {
        if (mask == 0) throw new InvalidDataException("BMP color mask is missing");
        int shift = BitOperations.TrailingZeroCount(mask);
        uint normalized = mask >> shift;
        if ((normalized & (normalized + 1)) != 0)
            throw new NotSupportedException("BMP color mask is not contiguous");
        outMask = mask;
        outShift = shift;
        outMax = normalized;
    }

    /// <summary>
    /// 从 pixel 中提取特定位域并缩放到 8-bit。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ExtractChannel(uint pixel, uint mask, int shift, uint max)
    {
        uint value = (pixel & mask) >> shift;
        if (max == 255) return (byte)value;
        return (byte)((value * 255u + (max >> 1)) / max);
    }
}
