using System;
using System.IO;

public static class JpegEncoder
{
    private static readonly int[] ZigZag = new int[]
    {
        0, 1, 5, 6,14,15,27,28,
        2, 4, 7,13,16,26,29,42,
        3, 8,12,17,25,30,41,43,
        9,11,18,24,31,40,44,53,
       10,19,23,32,39,45,52,54,
       20,22,33,38,46,51,55,60,
       21,34,37,47,50,56,59,61,
       35,36,48,49,57,58,62,63
    };

    private static readonly double[,] Cos = BuildCosTable();
    private static readonly double[] C = BuildC();

    private static double[,] BuildCosTable()
    {
        var t = new double[8, 8];
        for (int n = 0; n < 8; n++)
        {
            for (int k = 0; k < 8; k++)
            {
                t[n, k] = Math.Cos(((2 * n + 1) * k * Math.PI) / 16.0);
            }
        }
        return t;
    }

    private static double[] BuildC()
    {
        var c = new double[8];
        for (int k = 0; k < 8; k++) c[k] = (k == 0) ? 1.0 / Math.Sqrt(2) : 1.0;
        return c;
    }

    private struct HuffCode
    {
        public ushort Code;
        public byte Length;
    }

    private sealed class JpegBitWriter
    {
        private readonly Stream _stream;
        private uint _bitBuffer;
        private int _bitCount;

        public JpegBitWriter(Stream stream)
        {
            _stream = stream;
        }

        public void WriteBits(uint bits, int count)
        {
            _bitBuffer = (_bitBuffer << count) | (bits & ((1u << count) - 1u));
            _bitCount += count;

            while (_bitCount >= 8)
            {
                int shift = _bitCount - 8;
                byte b = (byte)((_bitBuffer >> shift) & 0xFF);
                _stream.WriteByte(b);
                if (b == 0xFF) _stream.WriteByte(0x00);
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
            if (_bitCount == 0) return;
            uint pad = (uint)((1 << (8 - _bitCount)) - 1);
            WriteBits(pad, 8 - _bitCount);
        }
    }

    private static readonly byte[] StdLumaQuant = new byte[]
    {
        16,11,10,16,24,40,51,61,
        12,12,14,19,26,58,60,55,
        14,13,16,24,40,57,69,56,
        14,17,22,29,51,87,80,62,
        18,22,37,56,68,109,103,77,
        24,35,55,64,81,104,113,92,
        49,64,78,87,103,121,120,101,
        72,92,95,98,112,100,103,99
    };

    private static readonly byte[] StdChromaQuant = new byte[]
    {
        17,18,24,47,99,99,99,99,
        18,21,26,66,99,99,99,99,
        24,26,56,99,99,99,99,99,
        47,66,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99
    };

    private static readonly byte[] DcLumaCounts = new byte[] { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcLumaSymbols = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] AcLumaCounts = new byte[] { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] AcLumaSymbols = new byte[]
    {
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
    };

    private static readonly byte[] DcChromaCounts = new byte[] { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcChromaSymbols = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly byte[] AcChromaCounts = new byte[] { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] AcChromaSymbols = new byte[]
    {
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
    };

    public static void Write(string path, int width, int height, byte[] rgb24, int quality = 75)
    {
        if (rgb24 == null) throw new ArgumentNullException(nameof(rgb24));
        if (rgb24.Length != checked(width * height * 3)) throw new ArgumentException("RGB24 像素长度不匹配", nameof(rgb24));
        if (quality < 1) quality = 1;
        if (quality > 100) quality = 100;

        byte[] qY = BuildQuantTable(StdLumaQuant, quality);
        byte[] qC = BuildQuantTable(StdChromaQuant, quality);

        HuffCode[] dcY = BuildHuffTable(DcLumaCounts, DcLumaSymbols);
        HuffCode[] acY = BuildHuffTable(AcLumaCounts, AcLumaSymbols);
        HuffCode[] dcC = BuildHuffTable(DcChromaCounts, DcChromaSymbols);
        HuffCode[] acC = BuildHuffTable(AcChromaCounts, AcChromaSymbols);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteMarker(fs, 0xD8);
        WriteApp0Jfif(fs);
        WriteDqt(fs, 0, qY);
        WriteDqt(fs, 1, qC);
        WriteSof0(fs, width, height);
        WriteDht(fs, 0, 0, DcLumaCounts, DcLumaSymbols);
        WriteDht(fs, 1, 0, AcLumaCounts, AcLumaSymbols);
        WriteDht(fs, 0, 1, DcChromaCounts, DcChromaSymbols);
        WriteDht(fs, 1, 1, AcChromaCounts, AcChromaSymbols);
        WriteSos(fs);

        var bw = new JpegBitWriter(fs);

        int blocksX = (width + 7) / 8;
        int blocksY = (height + 7) / 8;

        int prevYdc = 0, prevCbdc = 0, prevCrdc = 0;

        Span<int> yBlock = stackalloc int[64];
        Span<int> cbBlock = stackalloc int[64];
        Span<int> crBlock = stackalloc int[64];
        Span<int> qcoeff = stackalloc int[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                FillBlockRgbToYCbCr(rgb24, width, height, bx * 8, by * 8, yBlock, cbBlock, crBlock);

                EncodeBlock(bw, yBlock, qY, dcY, acY, ref prevYdc, qcoeff);
                EncodeBlock(bw, cbBlock, qC, dcC, acC, ref prevCbdc, qcoeff);
                EncodeBlock(bw, crBlock, qC, dcC, acC, ref prevCrdc, qcoeff);
            }
        }

        bw.FlushFinal();
        WriteMarker(fs, 0xD9);
    }

    private static void EncodeBlock(JpegBitWriter bw, Span<int> spatial, byte[] quant, HuffCode[] dc, HuffCode[] ac, ref int prevDc, Span<int> qcoeffOut)
    {
        FDCT8x8(spatial, qcoeffOut);

        for (int i = 0; i < 64; i++)
        {
            int q = quant[i];
            int v = qcoeffOut[i];
            int qq = (v >= 0) ? (v + (q >> 1)) / q : -(((-v) + (q >> 1)) / q);
            qcoeffOut[i] = qq;
        }

        int dcCoeff = qcoeffOut[0];
        int diff = dcCoeff - prevDc;
        prevDc = dcCoeff;

        int dcCat = MagnitudeCategory(diff);
        bw.WriteHuff(dc[dcCat]);
        if (dcCat != 0)
        {
            uint bits = EncodeMagnitudeBits(diff, dcCat);
            bw.WriteBits(bits, dcCat);
        }

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int idx = ZigZag[k];
            int v = qcoeffOut[idx];
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

        if (run > 0) bw.WriteHuff(ac[0x00]);
    }

    private static void FillBlockRgbToYCbCr(byte[] rgb, int width, int height, int baseX, int baseY, Span<int> y, Span<int> cb, Span<int> cr)
    {
        for (int yy = 0; yy < 8; yy++)
        {
            int sy = baseY + yy;
            if (sy >= height) sy = height - 1;
            for (int xx = 0; xx < 8; xx++)
            {
                int sx = baseX + xx;
                if (sx >= width) sx = width - 1;

                int src = (sy * width + sx) * 3;
                int r = rgb[src + 0];
                int g = rgb[src + 1];
                int b = rgb[src + 2];

                int yyVal = ((77 * r + 150 * g + 29 * b) >> 8);
                int cbVal = (((-43 * r - 85 * g + 128 * b) >> 8) + 128);
                int crVal = (((128 * r - 107 * g - 21 * b) >> 8) + 128);

                if (yyVal < 0) yyVal = 0; else if (yyVal > 255) yyVal = 255;
                if (cbVal < 0) cbVal = 0; else if (cbVal > 255) cbVal = 255;
                if (crVal < 0) crVal = 0; else if (crVal > 255) crVal = 255;

                int i = yy * 8 + xx;
                y[i] = yyVal - 128;
                cb[i] = cbVal - 128;
                cr[i] = crVal - 128;
            }
        }
    }

    private static void FDCT8x8(Span<int> spatial, Span<int> coeffOut)
    {
        Span<double> tmp = stackalloc double[64];
        Span<double> src = stackalloc double[64];
        for (int i = 0; i < 64; i++) src[i] = spatial[i];

        for (int y = 0; y < 8; y++)
        {
            int rowBase = y * 8;
            for (int u = 0; u < 8; u++)
            {
                double s = 0.0;
                for (int x = 0; x < 8; x++)
                {
                    s += src[rowBase + x] * Cos[x, u];
                }
                tmp[rowBase + u] = s;
            }
        }

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double s = 0.0;
                for (int y = 0; y < 8; y++)
                {
                    s += tmp[y * 8 + u] * Cos[y, v];
                }
                s *= 0.25 * C[u] * C[v];
                coeffOut[u + v * 8] = (int)Math.Round(s);
            }
        }
    }

    private static int MagnitudeCategory(int v)
    {
        if (v == 0) return 0;
        int a = v < 0 ? -v : v;
        int n = 0;
        while (a != 0) { a >>= 1; n++; }
        return n;
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

    private static void WriteDqt(Stream s, byte tableId, byte[] tableNatural)
    {
        WriteMarker(s, 0xDB);
        WriteBe16(s, 2 + 1 + 64);
        s.WriteByte((byte)(0x00 | (tableId & 0x0F)));
        for (int i = 0; i < 64; i++)
        {
            s.WriteByte(tableNatural[ZigZag[i]]);
        }
    }

    private static void WriteSof0(Stream s, int width, int height)
    {
        WriteMarker(s, 0xC0);
        WriteBe16(s, 17);
        s.WriteByte(8);
        WriteBe16(s, height);
        WriteBe16(s, width);
        s.WriteByte(3);

        s.WriteByte(1);
        s.WriteByte(0x11);
        s.WriteByte(0);

        s.WriteByte(2);
        s.WriteByte(0x11);
        s.WriteByte(1);

        s.WriteByte(3);
        s.WriteByte(0x11);
        s.WriteByte(1);
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
}

