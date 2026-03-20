using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace SharpImageConverter.Formats.Bmp;

/// <summary>
/// 简单的 BMP 读取器，支持 8/24/32 位非压缩 BMP，输出 RGB24。
/// </summary>
public static class BmpReader
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int HeaderSize = FileHeaderSize + InfoHeaderSize;

    /// <summary>
    /// 读取 BMP 文件并返回 RGB24 像素数据
    /// </summary>
    /// <param name="path">输入文件路径</param>
    /// <param name="width">输出图像宽度</param>
    /// <param name="height">输出图像高度</param>
    /// <returns>按 RGB 顺序排列的字节数组</returns>
    public static byte[] Read(string path, out int width, out int height)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Read(fs, out width, out height);
    }

    /// <summary>
    /// 读取 BMP 数据流并返回 RGB24 像素数据
    /// </summary>
    /// <param name="stream">输入数据流</param>
    /// <param name="width">输出图像宽度</param>
    /// <param name="height">输出图像高度</param>
    /// <returns>按 RGB 顺序排列的字节数组</returns>
    public static byte[] Read(Stream stream, out int width, out int height)
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

        if (planes != 1) throw new InvalidDataException($"Invalid BMP planes value: {planes}");
        if (rawWidth <= 0) throw new InvalidDataException($"Invalid BMP width: {rawWidth}");
        if (rawHeight == 0 || rawHeight == int.MinValue) throw new InvalidDataException($"Invalid BMP height: {rawHeight}");

        if (bpp == 8)
        {
            if (compression != 0)
                throw new NotSupportedException("Only uncompressed 8-bit BMPs (BI_RGB) are supported.");
        }
        else if (bpp == 24 || bpp == 32)
        {
            if (compression != 0 && compression != 3) // BI_RGB or BI_BITFIELDS
                throw new NotSupportedException("Compressed BMPs are not supported");
        }
        else
        {
            throw new NotSupportedException($"Only 8/24/32-bit BMPs are supported. Found {bpp}-bit.");
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

        MaskInfo? redMaskInfo = null;
        MaskInfo? greenMaskInfo = null;
        MaskInfo? blueMaskInfo = null;
        if (useBitfields)
        {
            redMaskInfo = CreateMaskInfo(redMask, "red");
            greenMaskInfo = CreateMaskInfo(greenMask, "green");
            blueMaskInfo = CreateMaskInfo(blueMask, "blue");
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

        byte[] row = ArrayPool<byte>.Shared.Rent(rowStride);
        try
        {
            if (bpp == 8)
            {
                if (paletteRgb == null || paletteEntries == 0)
                    throw new InvalidDataException("8-bit BMP palette is missing.");
                unsafe
                {
                    fixed (byte* pRow = row)
                    fixed (byte* pDst = rgb)
                    fixed (byte* pPal = paletteRgb)
                    {
                        for (int rowIndex = 0; rowIndex < height; rowIndex++)
                        {
                            stream.ReadExactly(row, 0, rowStride);

                            int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                            byte* dst = pDst + (dstY * width * 3);
                            byte* src = pRow;
                            for (int x = 0; x < width; x++)
                            {
                                int index = src[x];
                                if (index >= paletteEntries)
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
            else if (bpp == 24)
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
                                stream.ReadExactly(row, 0, rowStride);

                                int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                                byte* src = pRow;
                                byte* dst = pDst + (dstY * width * 3);
                                byte* srcEnd = src + srcRowSize;
                                while (src < srcEnd)
                                {
                                    dst[0] = src[2];
                                    dst[1] = src[1];
                                    dst[2] = src[0];
                                    src += 3;
                                    dst += 3;
                                }
                            }
                        }
                    }
                }
                else
                {
                    var r = redMaskInfo!.Value;
                    var g = greenMaskInfo!.Value;
                    var b = blueMaskInfo!.Value;
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row, 0, rowStride);
                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        int dstBase = dstY * width * 3;
                        int src = 0;
                        int dst = dstBase;
                        for (int x = 0; x < width; x++)
                        {
                            uint pixel = (uint)(row[src] | (row[src + 1] << 8) | (row[src + 2] << 16));
                            rgb[dst] = ExtractChannel(pixel, r);
                            rgb[dst + 1] = ExtractChannel(pixel, g);
                            rgb[dst + 2] = ExtractChannel(pixel, b);
                            src += 3;
                            dst += 3;
                        }
                    }
                }
            }
            else
            {
                int srcRowSize = width * 4;
                if (!useBitfields)
                {
                    unsafe
                    {
                        fixed (byte* pRow = row)
                        fixed (byte* pDst = rgb)
                        {
                            for (int rowIndex = 0; rowIndex < height; rowIndex++)
                            {
                                stream.ReadExactly(row, 0, rowStride);

                                int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                                byte* src = pRow;
                                byte* dst = pDst + (dstY * width * 3);
                                byte* srcEnd = src + srcRowSize;
                                while (src < srcEnd)
                                {
                                    dst[0] = src[2];
                                    dst[1] = src[1];
                                    dst[2] = src[0];
                                    src += 4;
                                    dst += 3;
                                }
                            }
                        }
                    }
                }
                else
                {
                    var r = redMaskInfo!.Value;
                    var g = greenMaskInfo!.Value;
                    var b = blueMaskInfo!.Value;
                    for (int rowIndex = 0; rowIndex < height; rowIndex++)
                    {
                        stream.ReadExactly(row, 0, rowStride);
                        int dstY = bottomUp ? (height - 1 - rowIndex) : rowIndex;
                        int dstBase = dstY * width * 3;
                        int src = 0;
                        int dst = dstBase;
                        for (int x = 0; x < width; x++)
                        {
                            uint pixel = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(src, 4));
                            rgb[dst] = ExtractChannel(pixel, r);
                            rgb[dst + 1] = ExtractChannel(pixel, g);
                            rgb[dst + 2] = ExtractChannel(pixel, b);
                            src += 4;
                            dst += 3;
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }

        return rgb;
    }

    private static MaskInfo CreateMaskInfo(uint mask, string name)
    {
        if (mask == 0) throw new InvalidDataException($"BMP {name} mask is missing");
        int shift = BitOperations.TrailingZeroCount(mask);
        uint normalized = mask >> shift;
        if ((normalized & (normalized + 1)) != 0) throw new NotSupportedException($"BMP {name} mask is not contiguous");
        return new MaskInfo(mask, shift, normalized);
    }

    private static byte ExtractChannel(uint pixel, MaskInfo maskInfo)
    {
        uint value = (pixel & maskInfo.Mask) >> maskInfo.Shift;
        if (maskInfo.Max == 255) return (byte)value;
        return (byte)((value * 255u + (maskInfo.Max >> 1)) / maskInfo.Max);
    }

    private readonly struct MaskInfo(uint mask, int shift, uint max)
    {
        public uint Mask { get; } = mask;
        public int Shift { get; } = shift;
        public uint Max { get; } = max;
    }
}
