using System;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using SharpImageConverter.Metadata;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

/// <summary>
/// JPEG 编码器，支持 4:2:0 与 4:4:4 采样，将 RGB24 编码为 JPEG。
/// </summary>
public static class JpegEncoder
{
    /// <summary>
    /// 是否在编码时打印配置（质量、采样）
    /// </summary>
    public static bool DebugPrintConfig { get; set; }

    private static readonly int[] YR = BuildScale(77);
    private static readonly int[] YG = BuildScale(150);
    private static readonly int[] YB = BuildScale(29);
    private static readonly int[] CbR = BuildScale(-43);
    private static readonly int[] CbG = BuildScale(-85);
    private static readonly int[] CbB = BuildScale(128);
    private static readonly int[] CrR = BuildScale(128);
    private static readonly int[] CrG = BuildScale(-107);
    private static readonly int[] CrB = BuildScale(-21);

    private static long _ticksFillMcu420;
    private static long _ticksFillBlock444;

    private static int[] BuildScale(int k)
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++) t[i] = k * i;
        return t;
    }

    private struct HuffCode
    {
        public ushort Code;
        public byte Length;
    }

    private sealed class JpegBitWriter(Stream stream)
    {
        private readonly Stream _stream = stream;
        private uint _bitBuffer;
        private int _bitCount;
        private readonly byte[] _out = new byte[64 * 1024];
        private int _outPos;

        private void WriteByteBuffered(byte b)
        {
            if (_outPos == _out.Length)
            {
                _stream.Write(_out, 0, _outPos);
                _outPos = 0;
            }
            _out[_outPos++] = b;
        }

        public void WriteBits(uint bits, int count)
        {
            _bitBuffer = (_bitBuffer << count) | (bits & ((1u << count) - 1u));
            _bitCount += count;

            while (_bitCount >= 8)
            {
                int shift = _bitCount - 8;
                byte b = (byte)((_bitBuffer >> shift) & 0xFF);
                WriteByteBuffered(b);
                if (b == 0xFF) WriteByteBuffered(0x00);
                _bitCount -= 8;
                _bitBuffer &= (uint)((1 << _bitCount) - 1);
            }
        }

        public void WriteHuff(HuffCode hc)
        {
            WriteBits(hc.Code, hc.Length);
        }

        public void FlushFinal()
        {
            if (_bitCount != 0)
            {
                uint pad = (uint)((1 << (8 - _bitCount)) - 1);
                WriteBits(pad, 8 - _bitCount);
            }
            if (_outPos > 0)
            {
                _stream.Write(_out, 0, _outPos);
                _outPos = 0;
            }
        }
    }

    private static readonly byte[] StdLumaQuant =
    [
        16,11,10,16,24,40,51,61,
        12,12,14,19,26,58,60,55,
        14,13,16,24,40,57,69,56,
        14,17,22,29,51,87,80,62,
        18,22,37,56,68,109,103,77,
        24,35,55,64,81,104,113,92,
        49,64,78,87,103,121,120,101,
        72,92,95,98,112,100,103,99
    ];

    private static readonly byte[] StdChromaQuant =
    [
        17,18,24,47,99,99,99,99,
        18,21,26,66,99,99,99,99,
        24,26,56,99,99,99,99,99,
        47,66,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99
    ];

    private static readonly byte[] DcLumaCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcLumaSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLumaCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcLumaSymbols =
    [
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12,0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
        0x22,0x71,0x14,0x32,0x81,0x91,0xA1,0x08,0x23,0x42,0xB1,0xC1,0x15,0x52,0xD1,0xF0,
        0x24,0x33,0x62,0x72,0x82,0x09,0x0A,0x16,0x17,0x18,0x19,0x1A,0x25,0x26,0x27,0x28,
        0x29,0x2A,0x34,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
        0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
        0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
        0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,0xA6,0xA7,
        0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,0xC4,0xC5,
        0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xE1,0xE2,
        0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xF1,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,
        0xF9,0xFA
    ];

    private static readonly byte[] DcChromaCounts = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChromaSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcChromaCounts = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChromaSymbols =
    [
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21,0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
        0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91,0xA1,0xB1,0xC1,0x09,0x23,0x33,0x52,0xF0,
        0x15,0x62,0x72,0xD1,0x0A,0x16,0x24,0x34,0xE1,0x25,0xF1,0x17,0x18,0x19,0x1A,0x26,
        0x27,0x28,0x29,0x2A,0x35,0x36,0x37,0x38,0x39,0x3A,0x43,0x44,0x45,0x46,0x47,0x48,
        0x49,0x4A,0x53,0x54,0x55,0x56,0x57,0x58,0x59,0x5A,0x63,0x64,0x65,0x66,0x67,0x68,
        0x69,0x6A,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x82,0x83,0x84,0x85,0x86,0x87,
        0x88,0x89,0x8A,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0xA2,0xA3,0xA4,0xA5,
        0xA6,0xA7,0xA8,0xA9,0xAA,0xB2,0xB3,0xB4,0xB5,0xB6,0xB7,0xB8,0xB9,0xBA,0xC2,0xC3,
        0xC4,0xC5,0xC6,0xC7,0xC8,0xC9,0xCA,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,
        0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA,0xF2,0xF3,0xF4,0xF5,0xF6,0xF7,0xF8,
        0xF9,0xFA
    ];

    /// <summary>
    /// 将 RGB24 编码为 JPEG 文件（默认使用 4:2:0 采样）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb24">RGB24 像素数据</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    public static void Write(string path, int width, int height, byte[] rgb24, int quality = 75)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(fs, width, height, rgb24, quality);
    }

    /// <summary>
    /// 将 RGB24 编码为 JPEG 流（默认使用 4:2:0 采样）
    /// </summary>
    public static void Write(Stream stream, int width, int height, byte[] rgb24, int quality = 75)
    {
        ArgumentNullException.ThrowIfNull(rgb24);
        if (rgb24.Length != checked(width * height * 3)) throw new ArgumentException("RGB24 像素长度不匹配", nameof(rgb24));
        if (quality < 1) quality = 1;
        if (quality > 100) quality = 100;

        bool subsample420 = true;
        WriteInternal(stream, width, height, rgb24, quality, subsample420, null, false);
    }

    /// <summary>
    /// 将 RGB24 编码为 JPEG 流，可选择 4:2:0 或 4:4:4 采样
    /// </summary>
    public static void Write(Stream stream, int width, int height, byte[] rgb24, int quality, bool subsample420)
    {
        ArgumentNullException.ThrowIfNull(rgb24);
        if (rgb24.Length != checked(width * height * 3)) throw new ArgumentException("RGB24 像素长度不匹配", nameof(rgb24));
        if (quality < 1) quality = 1;
        if (quality > 100) quality = 100;

        WriteInternal(stream, width, height, rgb24, quality, subsample420, null, false);
    }

    public static void Write(Stream stream, int width, int height, byte[] rgb24, int quality, bool subsample420, ImageMetadata? metadata, bool keepMetadata)
    {
        ArgumentNullException.ThrowIfNull(rgb24);
        if (rgb24.Length != checked(width * height * 3)) throw new ArgumentException("RGB24 像素长度不匹配", nameof(rgb24));
        if (quality < 1) quality = 1;
        if (quality > 100) quality = 100;

        WriteInternal(stream, width, height, rgb24, quality, subsample420, metadata, keepMetadata);
    }

    public static void WriteGray8(string path, int width, int height, byte[] gray8, int quality = 75)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteGray8(fs, width, height, gray8, quality);
    }

    public static void WriteGray8(Stream stream, int width, int height, byte[] gray8, int quality = 75)
    {
        ArgumentNullException.ThrowIfNull(gray8);
        if (gray8.Length != checked(width * height)) throw new ArgumentException("Gray8 像素长度不匹配", nameof(gray8));
        if (quality < 1) quality = 1;
        if (quality > 100) quality = 100;

        WriteInternalGray(stream, width, height, gray8, quality, null, false);
    }

    /// <summary>
    /// 根据图像类型自动选择编码方式
    /// </summary>
    /// <typeparam name="T">像素类型</typeparam>
    /// <param name="image">输入图像</param>
    /// <param name="stream">输出流</param>
    /// <param name="quality">质量 (1-100)</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样（仅对 RGB 有效）</param>
    /// <param name="keepMetadata">是否保留元数据</param>
    public static void Encode<T>(Image<T> image, Stream stream, int quality = 75, bool subsample420 = true, bool keepMetadata = true) where T : struct, IPixel
    {
        if (typeof(T) == typeof(Gray8))
        {
            WriteInternalGray(stream, image.Width, image.Height, image.Buffer, quality, image.Metadata, keepMetadata);
        }
        else if (typeof(T) == typeof(Rgb24))
        {
            WriteInternal(stream, image.Width, image.Height, image.Buffer, quality, subsample420, image.Metadata, keepMetadata);
        }
        else
        {
            throw new NotSupportedException($"JPEG encoding not supported for pixel type: {typeof(T).Name}");
        }
    }

    /// <summary>
    /// 根据图像类型自动选择编码方式
    /// </summary>
    /// <typeparam name="T">像素类型</typeparam>
    /// <param name="path">输出文件路径</param>
    /// <param name="image">输入图像</param>
    /// <param name="quality">质量 (1-100)</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样（仅对 RGB 有效）</param>
    /// <param name="keepMetadata">是否保留元数据</param>
    public static void Encode<T>(Image<T> image, string path, int quality = 75, bool subsample420 = true, bool keepMetadata = true) where T : struct, IPixel
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Encode(image, fs, quality, subsample420, keepMetadata);
    }

    private static void WriteInternal(Stream stream, int width, int height, byte[] rgb24, int quality, bool subsample420, ImageMetadata? metadata, bool keepMetadata)
    {
        long totalStartTicks = Stopwatch.GetTimestamp();

        _ticksFillMcu420 = 0;
        _ticksFillBlock444 = 0;

        if (DebugPrintConfig)
        {
            Console.WriteLine($"[jpeg] quality={quality} subsample={(subsample420 ? "420" : "444")}");
        }

        byte[] qY = BuildQuantTable(StdLumaQuant, quality);
        byte[] qC = BuildQuantTable(StdChromaQuant, quality);
        int[] qYRecip = BuildQuantRecipIntDct(qY);
        int[] qCRecip = BuildQuantRecipIntDct(qC);

        HuffCode[] dcY = BuildHuffTable(DcLumaCounts, DcLumaSymbols);
        HuffCode[] acY = BuildHuffTable(AcLumaCounts, AcLumaSymbols);
        HuffCode[] dcC = BuildHuffTable(DcChromaCounts, DcChromaSymbols);
        HuffCode[] acC = BuildHuffTable(AcChromaCounts, AcChromaSymbols);

        WriteMarker(stream, 0xD8);
        WriteApp0Jfif(stream);
        if (keepMetadata && metadata != null)
        {
            WriteMetadata(stream, metadata);
        }
        WriteDqt(stream, 0, qY);
        WriteDqt(stream, 1, qC);
        WriteSof0(stream, width, height, subsample420);
        WriteDht(stream, 0, 0, DcLumaCounts, DcLumaSymbols);
        WriteDht(stream, 1, 0, AcLumaCounts, AcLumaSymbols);
        WriteDht(stream, 0, 1, DcChromaCounts, DcChromaSymbols);
        WriteDht(stream, 1, 1, AcChromaCounts, AcChromaSymbols);
        WriteSos(stream);

        var bw = new JpegBitWriter(stream);

        int prevYdc = 0, prevCbdc = 0, prevCrdc = 0;

        if (subsample420)
        {
            int mcusX = (width + 15) / 16;
            int mcusY = (height + 15) / 16;

            Span<int> yBlocks = stackalloc int[64 * 4];
            Span<int> cbBlock = stackalloc int[64];
            Span<int> crBlock = stackalloc int[64];

            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    FillMcu420RgbToYCbCr(
                        rgb24,
                        width,
                        height,
                        mx * 16,
                        my * 16,
                        yBlocks[..64],
                        yBlocks[64..128],
                        yBlocks[128..192],
                        yBlocks[192..256],
                        cbBlock,
                        crBlock);

                    EncodeBlock(bw, yBlocks[..64], qYRecip, dcY, acY, ref prevYdc);
                    EncodeBlock(bw, yBlocks[64..128], qYRecip, dcY, acY, ref prevYdc);
                    EncodeBlock(bw, yBlocks[128..192], qYRecip, dcY, acY, ref prevYdc);
                    EncodeBlock(bw, yBlocks[192..256], qYRecip, dcY, acY, ref prevYdc);
                    EncodeBlock(bw, cbBlock, qCRecip, dcC, acC, ref prevCbdc);
                    EncodeBlock(bw, crBlock, qCRecip, dcC, acC, ref prevCrdc);
                }
            }
        }
        else
        {
            int blocksX = (width + 7) / 8;
            int blocksY = (height + 7) / 8;

            Span<int> yBlock = stackalloc int[64];
            Span<int> cbBlock = stackalloc int[64];
            Span<int> crBlock = stackalloc int[64];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    FillBlockRgbToYCbCr444(rgb24, width, height, bx * 8, by * 8, yBlock, cbBlock, crBlock);
                    EncodeBlock(bw, yBlock, qYRecip, dcY, acY, ref prevYdc);
                    EncodeBlock(bw, cbBlock, qCRecip, dcC, acC, ref prevCbdc);
                    EncodeBlock(bw, crBlock, qCRecip, dcC, acC, ref prevCrdc);
                }
            }
        }

        bw.FlushFinal();
        WriteMarker(stream, 0xD9);

        long totalEndTicks = Stopwatch.GetTimestamp();

        if (DebugPrintConfig)
        {
            long totalTicks = totalEndTicks - totalStartTicks;
            double totalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
            double mcu420Ms = _ticksFillMcu420 * 1000.0 / Stopwatch.Frequency;
            double block444Ms = _ticksFillBlock444 * 1000.0 / Stopwatch.Frequency;
            double mcu420Pct = totalTicks != 0 ? (double)_ticksFillMcu420 * 100.0 / totalTicks : 0.0;
            double block444Pct = totalTicks != 0 ? (double)_ticksFillBlock444 * 100.0 / totalTicks : 0.0;

            Console.WriteLine($"[jpeg-timing] total={totalMs:F3}ms FillMcu420={mcu420Ms:F3}ms ({mcu420Pct:F1}%) FillBlock444={block444Ms:F3}ms ({block444Pct:F1}%)");
        }
    }

    private static void WriteInternalGray(Stream stream, int width, int height, byte[] gray8, int quality, ImageMetadata? metadata, bool keepMetadata)
    {
        long totalStartTicks = Stopwatch.GetTimestamp();

        if (DebugPrintConfig)
        {
            Console.WriteLine($"[jpeg] quality={quality} grayscale");
        }

        byte[] qY = BuildQuantTable(StdLumaQuant, quality);
        int[] qYRecip = BuildQuantRecipIntDct(qY);

        HuffCode[] dcY = BuildHuffTable(DcLumaCounts, DcLumaSymbols);
        HuffCode[] acY = BuildHuffTable(AcLumaCounts, AcLumaSymbols);

        WriteMarker(stream, 0xD8);
        WriteApp0Jfif(stream);
        if (keepMetadata && metadata != null)
        {
            WriteMetadata(stream, metadata);
        }
        WriteDqt(stream, 0, qY);
        WriteSof0Gray(stream, width, height);
        WriteDht(stream, 0, 0, DcLumaCounts, DcLumaSymbols);
        WriteDht(stream, 1, 0, AcLumaCounts, AcLumaSymbols);
        WriteSosGray(stream);

        var bw = new JpegBitWriter(stream);

        int prevYdc = 0;

        int blocksX = (width + 7) / 8;
        int blocksY = (height + 7) / 8;

        Span<int> yBlock = stackalloc int[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                FillBlockGray8ToY(gray8, width, height, bx * 8, by * 8, yBlock);
                EncodeBlock(bw, yBlock, qYRecip, dcY, acY, ref prevYdc);
            }
        }

        bw.FlushFinal();
        WriteMarker(stream, 0xD9);

        long totalEndTicks = Stopwatch.GetTimestamp();

        if (DebugPrintConfig)
        {
            long totalTicks = totalEndTicks - totalStartTicks;
            double totalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"[jpeg-timing] total={totalMs:F3}ms Gray8");
        }
    }

    private static void EncodeBlock(JpegBitWriter bw, Span<int> block, int[] quantRecip, HuffCode[] dc, HuffCode[] ac, ref int prevDc)
    {
        FDCT8x8IntInPlace(block);

        for (int i = 0; i < 64; i++)
        {
            int v = block[i];
            block[i] = QuantizeNearest(v, quantRecip[i]);
        }

        int dcCoeff = block[0];
        int diff = dcCoeff - prevDc;
        prevDc = dcCoeff;

        int dcCat = MagnitudeCategory(diff);
        bw.WriteHuff(dc[dcCat]);
        if (dcCat != 0)
        {
            uint bits = EncodeMagnitudeBits(diff, dcCat);
            bw.WriteBits(bits, dcCat);
        }

        int lastNz = 0;
        for (int k = 63; k >= 1; k--)
        {
            int idx = JpegConstants.ZigZag[k];
            if (block[idx] != 0)
            {
                lastNz = k;
                break;
            }
        }

        if (lastNz == 0)
        {
            bw.WriteHuff(ac[0x00]);
            return;
        }

        int run = 0;
        for (int k = 1; k <= lastNz; k++)
        {
            int idx = JpegConstants.ZigZag[k];
            int v = block[idx];
            if (v == 0)
            {
                run++;
                continue;
            }

            while (run >= 16)
            {
                bw.WriteHuff(ac[0xF0]);
                run -= 16;
            }

            int cat = MagnitudeCategory(v);
            int sym = (run << 4) | cat;
            bw.WriteHuff(ac[sym]);
            uint bits = EncodeMagnitudeBits(v, cat);
            bw.WriteBits(bits, cat);
            run = 0;
        }

        if (lastNz != 63) bw.WriteHuff(ac[0x00]);
    }

    // float FDCT removed; always use integer FDCT

    private static unsafe void FillMcu420RgbToYCbCr(
        byte[] rgb,
        int width,
        int height,
        int baseX,
        int baseY,
        Span<int> y00,
        Span<int> y10,
        Span<int> y01,
        Span<int> y11,
        Span<int> cb,
        Span<int> cr)
    {
        long startTicks = Stopwatch.GetTimestamp();

        Span<int> cbAcc = stackalloc int[64];
        Span<int> crAcc = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            cbAcc[i] = 0;
            crAcc[i] = 0;
        }

        fixed (byte* rgbPtr = rgb)
        {
            int stride = width * 3;
            for (int yy = 0; yy < 16; yy++)
            {
                int sy = baseY + yy;
                if (sy >= height) sy = height - 1;
                int rowOffset = sy * stride;
                int localY = yy & 7;
                int yRowBase = localY * 8;
                bool top = yy < 8;
                for (int xx = 0; xx < 16; xx++)
                {
                    int sx = baseX + xx;
                    if (sx >= width) sx = width - 1;

                    int pixelOffset = rowOffset + sx * 3;
                    byte* p = rgbPtr + pixelOffset;
                    int r = p[0];
                    int g = p[1];
                    int b = p[2];

                    int yyVal = (YR[r] + YG[g] + YB[b]) >> 8;

                    int localX = xx & 7;
                    int yIndex = yRowBase + localX;

                    if (top)
                    {
                        if (xx < 8) y00[yIndex] = yyVal - 128;
                        else y10[yIndex] = yyVal - 128;
                    }
                    else
                    {
                        if (xx < 8) y01[yIndex] = yyVal - 128;
                        else y11[yIndex] = yyVal - 128;
                    }

                    int cbVal = ((CbR[r] + CbG[g] + CbB[b]) >> 8) + 128;
                    int crVal = ((CrR[r] + CrG[g] + CrB[b]) >> 8) + 128;

                    int cy = yy >> 1;
                    int cx = xx >> 1;
                    int cIndex = cy * 8 + cx;
                    cbAcc[cIndex] += cbVal;
                    crAcc[cIndex] += crVal;
                }
            }
        }

        for (int i = 0; i < 64; i++)
        {
            int cbVal = (cbAcc[i] + 2) >> 2;
            int crVal = (crAcc[i] + 2) >> 2;

            cb[i] = cbVal - 128;
            cr[i] = crVal - 128;
        }

        long endTicks = Stopwatch.GetTimestamp();
        _ticksFillMcu420 += endTicks - startTicks;
    }

    private static unsafe void FillBlockRgbToYCbCr444(byte[] rgb, int width, int height, int baseX, int baseY, Span<int> y, Span<int> cb, Span<int> cr)
    {
        long startTicks = Stopwatch.GetTimestamp();

        fixed (byte* rgbPtr = rgb)
        {
            int stride = width * 3;
            for (int yy = 0; yy < 8; yy++)
            {
                int sy = baseY + yy;
                if (sy >= height) sy = height - 1;
                int rowOffset = sy * stride;
                int yRowBase = yy * 8;
                for (int xx = 0; xx < 8; xx++)
                {
                    int sx = baseX + xx;
                    if (sx >= width) sx = width - 1;

                    int pixelOffset = rowOffset + sx * 3;
                    byte* p = rgbPtr + pixelOffset;
                    int r = p[0];
                    int g = p[1];
                    int b = p[2];

                    int yyVal = (YR[r] + YG[g] + YB[b]) >> 8;
                    int cbVal = ((CbR[r] + CbG[g] + CbB[b]) >> 8) + 128;
                    int crVal = ((CrR[r] + CrG[g] + CrB[b]) >> 8) + 128;

                    int i = yRowBase + xx;
                    y[i] = yyVal - 128;
                    cb[i] = cbVal - 128;
                    cr[i] = crVal - 128;
                }
            }
        }

        long endTicks = Stopwatch.GetTimestamp();
        _ticksFillBlock444 += endTicks - startTicks;
    }

    private static void FillBlockGray8ToY(byte[] gray, int width, int height, int baseX, int baseY, Span<int> y)
    {
        for (int yy = 0; yy < 8; yy++)
        {
            int sy = baseY + yy;
            if (sy >= height) sy = height - 1;
            int rowOffset = sy * width;
            int yRowBase = yy * 8;
            for (int xx = 0; xx < 8; xx++)
            {
                int sx = baseX + xx;
                if (sx >= width) sx = width - 1;
                int srcIndex = rowOffset + sx;
                int yyVal = gray[srcIndex];
                int i = yRowBase + xx;
                y[i] = yyVal - 128;
            }
        }
    }

    private static int MagnitudeCategory(int v)
    {
        if (v == 0) return 0;
        uint a = (uint)(v < 0 ? -v : v);
        return 32 - BitOperations.LeadingZeroCount(a);
    }

    private static uint EncodeMagnitudeBits(int v, int cat)
    {
        if (v >= 0) return (uint)v;
        return (uint)(v + ((1 << cat) - 1));
    }

    private static HuffCode[] BuildHuffTable(byte[] counts, byte[] symbols)
    {
        var table = new HuffCode[256];
        int code = 0;
        int idx = 0;
        for (int len = 1; len <= 16; len++)
        {
            int cnt = counts[len - 1];
            for (int i = 0; i < cnt; i++)
            {
                byte sym = symbols[idx++];
                table[sym] = new HuffCode { Code = (ushort)code, Length = (byte)len };
                code++;
            }
            code <<= 1;
        }
        return table;
    }

    private static byte[] BuildQuantTable(byte[] baseTable, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var outTable = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            int q = (baseTable[i] * scale + 50) / 100;
            if (q < 1) q = 1;
            if (q > 255) q = 255;
            outTable[i] = (byte)q;
        }
        return outTable;
    }

    private static int[] BuildQuantRecipIntDct(byte[] quant)
    {
        var recip = new int[64];
        for (int i = 0; i < 64; i++)
        {
            recip[i] = (int)((1L << 20) / (quant[i] * 8L));
        }
        return recip;
    }

    private static int QuantizeNearest(int v, int recip20)
    {
        if (v >= 0)
        {
            return (int)(((long)v * recip20 + (1L << 19)) >> 20);
        }
        int a = -v;
        return -(int)(((long)a * recip20 + (1L << 19)) >> 20);
    }

    private const int FDCT_CONST_BITS = 13;
    private const int FDCT_PASS1_BITS = 2;

    private const int FDCT_FIX_0_298631336 = 2446;
    private const int FDCT_FIX_0_390180644 = 3196;
    private const int FDCT_FIX_0_541196100 = 4433;
    private const int FDCT_FIX_0_765366865 = 6270;
    private const int FDCT_FIX_0_899976223 = 7373;
    private const int FDCT_FIX_1_175875602 = 9633;
    private const int FDCT_FIX_1_501321110 = 12299;
    private const int FDCT_FIX_1_847759065 = 15137;
    private const int FDCT_FIX_1_961570560 = 16069;
    private const int FDCT_FIX_2_053119869 = 16819;
    private const int FDCT_FIX_2_562915447 = 20995;
    private const int FDCT_FIX_3_072711026 = 25172;

    private static int FDctDescale(int x, int n)
    {
        return (x + (1 << (n - 1))) >> n;
    }

    private static int FDctMultiply(int x, int c)
    {
        return x * c;
    }

    private static void FDCT8x8IntInPlace(Span<int> data)
    {
        for (int row = 0; row < 64; row += 8)
        {
            int tmp0 = data[row + 0] + data[row + 7];
            int tmp7 = data[row + 0] - data[row + 7];
            int tmp1 = data[row + 1] + data[row + 6];
            int tmp6 = data[row + 1] - data[row + 6];
            int tmp2 = data[row + 2] + data[row + 5];
            int tmp5 = data[row + 2] - data[row + 5];
            int tmp3 = data[row + 3] + data[row + 4];
            int tmp4 = data[row + 3] - data[row + 4];

            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;

            data[row + 0] = (tmp10 + tmp11) << FDCT_PASS1_BITS;
            data[row + 4] = (tmp10 - tmp11) << FDCT_PASS1_BITS;

            int z1 = FDctMultiply(tmp12 + tmp13, FDCT_FIX_0_541196100);
            data[row + 2] = FDctDescale(z1 + FDctMultiply(tmp13, FDCT_FIX_0_765366865), FDCT_CONST_BITS - FDCT_PASS1_BITS);
            data[row + 6] = FDctDescale(z1 + FDctMultiply(tmp12, -FDCT_FIX_1_847759065), FDCT_CONST_BITS - FDCT_PASS1_BITS);

            int z11 = tmp4 + tmp7;
            int z12 = tmp5 + tmp6;
            int z13 = tmp4 + tmp6;
            int z14 = tmp5 + tmp7;
            int z15 = FDctMultiply(z13 + z14, FDCT_FIX_1_175875602);

            tmp4 = FDctMultiply(tmp4, FDCT_FIX_0_298631336);
            tmp5 = FDctMultiply(tmp5, FDCT_FIX_2_053119869);
            tmp6 = FDctMultiply(tmp6, FDCT_FIX_3_072711026);
            tmp7 = FDctMultiply(tmp7, FDCT_FIX_1_501321110);
            z11 = FDctMultiply(z11, -FDCT_FIX_0_899976223);
            z12 = FDctMultiply(z12, -FDCT_FIX_2_562915447);
            z13 = FDctMultiply(z13, -FDCT_FIX_1_961570560);
            z14 = FDctMultiply(z14, -FDCT_FIX_0_390180644);

            z13 += z15;
            z14 += z15;

            data[row + 7] = FDctDescale(tmp4 + z11 + z13, FDCT_CONST_BITS - FDCT_PASS1_BITS);
            data[row + 5] = FDctDescale(tmp5 + z12 + z14, FDCT_CONST_BITS - FDCT_PASS1_BITS);
            data[row + 3] = FDctDescale(tmp6 + z12 + z13, FDCT_CONST_BITS - FDCT_PASS1_BITS);
            data[row + 1] = FDctDescale(tmp7 + z11 + z14, FDCT_CONST_BITS - FDCT_PASS1_BITS);
        }

        for (int col = 0; col < 8; col++)
        {
            int tmp0 = data[col + 0 * 8] + data[col + 7 * 8];
            int tmp7 = data[col + 0 * 8] - data[col + 7 * 8];
            int tmp1 = data[col + 1 * 8] + data[col + 6 * 8];
            int tmp6 = data[col + 1 * 8] - data[col + 6 * 8];
            int tmp2 = data[col + 2 * 8] + data[col + 5 * 8];
            int tmp5 = data[col + 2 * 8] - data[col + 5 * 8];
            int tmp3 = data[col + 3 * 8] + data[col + 4 * 8];
            int tmp4 = data[col + 3 * 8] - data[col + 4 * 8];

            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;

            data[col + 0 * 8] = FDctDescale(tmp10 + tmp11, FDCT_PASS1_BITS);
            data[col + 4 * 8] = FDctDescale(tmp10 - tmp11, FDCT_PASS1_BITS);

            int z1 = FDctMultiply(tmp12 + tmp13, FDCT_FIX_0_541196100);
            data[col + 2 * 8] = FDctDescale(z1 + FDctMultiply(tmp13, FDCT_FIX_0_765366865), FDCT_CONST_BITS + FDCT_PASS1_BITS);
            data[col + 6 * 8] = FDctDescale(z1 + FDctMultiply(tmp12, -FDCT_FIX_1_847759065), FDCT_CONST_BITS + FDCT_PASS1_BITS);

            int z11 = tmp4 + tmp7;
            int z12 = tmp5 + tmp6;
            int z13 = tmp4 + tmp6;
            int z14 = tmp5 + tmp7;
            int z15 = FDctMultiply(z13 + z14, FDCT_FIX_1_175875602);

            tmp4 = FDctMultiply(tmp4, FDCT_FIX_0_298631336);
            tmp5 = FDctMultiply(tmp5, FDCT_FIX_2_053119869);
            tmp6 = FDctMultiply(tmp6, FDCT_FIX_3_072711026);
            tmp7 = FDctMultiply(tmp7, FDCT_FIX_1_501321110);
            z11 = FDctMultiply(z11, -FDCT_FIX_0_899976223);
            z12 = FDctMultiply(z12, -FDCT_FIX_2_562915447);
            z13 = FDctMultiply(z13, -FDCT_FIX_1_961570560);
            z14 = FDctMultiply(z14, -FDCT_FIX_0_390180644);

            z13 += z15;
            z14 += z15;

            data[col + 7 * 8] = FDctDescale(tmp4 + z11 + z13, FDCT_CONST_BITS + FDCT_PASS1_BITS);
            data[col + 5 * 8] = FDctDescale(tmp5 + z12 + z14, FDCT_CONST_BITS + FDCT_PASS1_BITS);
            data[col + 3 * 8] = FDctDescale(tmp6 + z12 + z13, FDCT_CONST_BITS + FDCT_PASS1_BITS);
            data[col + 1 * 8] = FDctDescale(tmp7 + z11 + z14, FDCT_CONST_BITS + FDCT_PASS1_BITS);
        }
    }

    private static void WriteMarker(Stream s, byte markerLow)
    {
        s.WriteByte(0xFF);
        s.WriteByte(markerLow);
    }

    private static void WriteBe16(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteApp0Jfif(Stream s)
    {
        WriteMarker(s, 0xE0);
        WriteBe16(s, 16);
        s.WriteByte((byte)'J'); s.WriteByte((byte)'F'); s.WriteByte((byte)'I'); s.WriteByte((byte)'F'); s.WriteByte(0);
        s.WriteByte(1); s.WriteByte(1);
        s.WriteByte(0);
        WriteBe16(s, 1);
        WriteBe16(s, 1);
        s.WriteByte(0);
        s.WriteByte(0);
    }

    private static void WriteMetadata(Stream s, ImageMetadata metadata)
    {
        if (metadata.ExifRaw != null && metadata.ExifRaw.Length > 0)
        {
            byte[] exif = PatchExifOrientation(metadata.ExifRaw, 1);
            WriteAppSegment(s, 0xE1, exif);
        }

        if (metadata.IccProfile != null && metadata.IccProfile.Length > 0)
        {
            WriteIccProfile(s, metadata.IccProfile);
        }
    }

    private static void WriteAppSegment(Stream s, byte markerLow, byte[] payload)
    {
        int len = payload.Length + 2;
        if (len > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(payload), "APP 段长度过大");
        WriteMarker(s, markerLow);
        WriteBe16(s, len);
        s.Write(payload, 0, payload.Length);
    }

    private static void WriteIccProfile(Stream s, byte[] profile)
    {
        byte[] sig =
        [
            (byte)'I',(byte)'C',(byte)'C',(byte)'_',(byte)'P',(byte)'R',(byte)'O',(byte)'F',(byte)'I',(byte)'L',(byte)'E',0x00
        ];

        const int overhead = 14;
        int maxPayload = 0xFFFD - overhead;
        int count = (profile.Length + maxPayload - 1) / maxPayload;
        if (count <= 0) count = 1;
        if (count > 255) throw new ArgumentOutOfRangeException(nameof(profile), "ICC Profile 过大");

        int offset = 0;
        for (int i = 0; i < count; i++)
        {
            int take = Math.Min(maxPayload, profile.Length - offset);
            byte[] payload = new byte[overhead + take];
            Buffer.BlockCopy(sig, 0, payload, 0, sig.Length);
            payload[12] = (byte)(i + 1);
            payload[13] = (byte)count;
            Buffer.BlockCopy(profile, offset, payload, overhead, take);
            WriteAppSegment(s, 0xE2, payload);
            offset += take;
        }
    }

    private static byte[] PatchExifOrientation(byte[] exifApp1, ushort newOrientation)
    {
        if (exifApp1.Length < 14) return exifApp1;
        if (!(exifApp1[0] == (byte)'E' && exifApp1[1] == (byte)'x' && exifApp1[2] == (byte)'i' && exifApp1[3] == (byte)'f' && exifApp1[4] == 0 && exifApp1[5] == 0))
            return exifApp1;

        byte[] buf = new byte[exifApp1.Length];
        Buffer.BlockCopy(exifApp1, 0, buf, 0, exifApp1.Length);

        int tiffBase = 6;
        if (buf.Length < tiffBase + 8) return buf;

        bool littleEndian;
        if (buf[tiffBase + 0] == (byte)'I' && buf[tiffBase + 1] == (byte)'I') littleEndian = true;
        else if (buf[tiffBase + 0] == (byte)'M' && buf[tiffBase + 1] == (byte)'M') littleEndian = false;
        else return buf;

        ushort ReadU16(int offset)
        {
            if (offset < 0 || offset + 2 > buf.Length) return 0;
            return littleEndian ? (ushort)(buf[offset] | (buf[offset + 1] << 8)) : (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

        uint ReadU32(int offset)
        {
            if (offset < 0 || offset + 4 > buf.Length) return 0;
            return littleEndian
                ? (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24))
                : (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
        }

        void WriteU16(int offset, ushort v)
        {
            if (offset < 0 || offset + 2 > buf.Length) return;
            if (littleEndian)
            {
                buf[offset] = (byte)(v & 0xFF);
                buf[offset + 1] = (byte)((v >> 8) & 0xFF);
            }
            else
            {
                buf[offset] = (byte)((v >> 8) & 0xFF);
                buf[offset + 1] = (byte)(v & 0xFF);
            }
        }

        ushort magic = ReadU16(tiffBase + 2);
        if (magic != 42) return buf;
        int ifd0 = tiffBase + (int)ReadU32(tiffBase + 4);
        if (ifd0 < 0 || ifd0 + 2 > buf.Length) return buf;

        ushort numEntries = ReadU16(ifd0);
        int entryBase = ifd0 + 2;
        for (int i = 0; i < numEntries; i++)
        {
            int e = entryBase + i * 12;
            if (e + 12 > buf.Length) break;
            ushort tag = ReadU16(e + 0);
            ushort type = ReadU16(e + 2);
            uint count = ReadU32(e + 4);
            if (tag != 0x0112 || type != 3 || count < 1) continue;
            WriteU16(e + 8, newOrientation);
            break;
        }

        return buf;
    }

    private static void WriteDqt(Stream s, byte tableId, byte[] tableNatural)
    {
        WriteMarker(s, 0xDB);
        WriteBe16(s, 2 + 1 + 64);
        s.WriteByte((byte)(0x00 | (tableId & 0x0F)));
        for (int i = 0; i < 64; i++)
        {
            s.WriteByte(tableNatural[JpegConstants.ZigZag[i]]);
        }
    }

    private static void WriteSof0(Stream s, int width, int height, bool subsample420)
    {
        WriteMarker(s, 0xC0);
        WriteBe16(s, 17);
        s.WriteByte(8);
        WriteBe16(s, height);
        WriteBe16(s, width);
        s.WriteByte(3);

        s.WriteByte(1);
        s.WriteByte(subsample420 ? (byte)0x22 : (byte)0x11);
        s.WriteByte(0);

        s.WriteByte(2);
        s.WriteByte(0x11);
        s.WriteByte(1);

        s.WriteByte(3);
        s.WriteByte(0x11);
        s.WriteByte(1);
    }

    private static void WriteSof0Gray(Stream s, int width, int height)
    {
        WriteMarker(s, 0xC0);
        WriteBe16(s, 11);
        s.WriteByte(8);
        WriteBe16(s, height);
        WriteBe16(s, width);
        s.WriteByte(1);

        s.WriteByte(1);
        s.WriteByte(0x11);
        s.WriteByte(0);
    }

    private static void WriteDht(Stream s, int tableClass, int tableId, byte[] counts, byte[] symbols)
    {
        WriteMarker(s, 0xC4);
        int len = 2 + 1 + 16 + symbols.Length;
        WriteBe16(s, len);
        s.WriteByte((byte)(((tableClass & 1) << 4) | (tableId & 0x0F)));
        for (int i = 0; i < 16; i++) s.WriteByte(counts[i]);
        s.Write(symbols, 0, symbols.Length);
    }

    private static void WriteSos(Stream s)
    {
        WriteMarker(s, 0xDA);
        WriteBe16(s, 12);
        s.WriteByte(3);

        s.WriteByte(1);
        s.WriteByte(0x00);

        s.WriteByte(2);
        s.WriteByte(0x11);

        s.WriteByte(3);
        s.WriteByte(0x11);

        s.WriteByte(0);
        s.WriteByte(63);
        s.WriteByte(0);
    }

    private static void WriteSosGray(Stream s)
    {
        WriteMarker(s, 0xDA);
        WriteBe16(s, 8);
        s.WriteByte(1);

        s.WriteByte(1);
        s.WriteByte(0x00);

        s.WriteByte(0);
        s.WriteByte(63);
        s.WriteByte(0);
    }
}
