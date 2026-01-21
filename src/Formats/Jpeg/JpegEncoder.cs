namespace SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Core;
using SharpImageConverter.Metadata;

public static class JpegEncoder
    {
        public static bool DebugPrintConfig { get; set; }

        public static void Write(string path, int width, int height, byte[] rgb, int quality)
        {
            using var fs = File.Create(path);
            Write(fs, width, height, rgb, quality, true, null, false);
        }

        public static void Write(string path, int width, int height, byte[] rgb, int quality, bool subsample420)
        {
            using var fs = File.Create(path);
            Write(fs, width, height, rgb, quality, subsample420, null, false);
        }

        public static void Write(Stream stream, int width, int height, byte[] rgb, int quality)
        {
            Write(stream, width, height, rgb, quality, true, null, false);
        }

        public static void Write(Stream stream, int width, int height, byte[] rgb, int quality, bool subsample420)
        {
            Write(stream, width, height, rgb, quality, subsample420, null, false);
        }

        public static void Write(Stream stream, int width, int height, byte[] rgb, int quality, bool subsample420, ImageMetadata? metadata, bool keepMetadata)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (rgb == null) throw new ArgumentNullException(nameof(rgb));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (rgb.Length < checked(width * height * 3)) throw new ArgumentException("RGB 数据长度不足。", nameof(rgb));
            if (quality < 1) quality = 1;
            if (quality > 100) quality = 100;

            WriteMarker(stream, 0xD8);
            WriteJfifApp0(stream);
            if (keepMetadata && metadata?.ExifRaw != null)
            {
                WriteApp1(stream, metadata.ExifRaw);
            }

            var (qtY, qtC) = BuildQuantTables(quality);
            WriteDqt(stream, qtY, 0);
            WriteDqt(stream, qtC, 1);

            int hY = subsample420 ? 2 : 1;
            int vY = subsample420 ? 2 : 1;
            WriteSof0(stream, width, height, hY, vY);

            var dcLum = BuildHuffmanTable(StdDcLuminanceBits, StdDcLuminanceValues);
            var acLum = BuildHuffmanTable(StdAcLuminanceBits, StdAcLuminanceValues);
            var dcChr = BuildHuffmanTable(StdDcChrominanceBits, StdDcChrominanceValues);
            var acChr = BuildHuffmanTable(StdAcChrominanceBits, StdAcChrominanceValues);

            WriteDht(stream, 0, 0, StdDcLuminanceBits, StdDcLuminanceValues);
            WriteDht(stream, 1, 0, StdAcLuminanceBits, StdAcLuminanceValues);
            WriteDht(stream, 0, 1, StdDcChrominanceBits, StdDcChrominanceValues);
            WriteDht(stream, 1, 1, StdAcChrominanceBits, StdAcChrominanceValues);

            WriteSos(stream);

            ConvertToYCbCr(rgb, width, height, subsample420, out var yPlane, out var cbPlane, out var crPlane, out int cW, out int cH);

            int mcuWidth = subsample420 ? 16 : 8;
            int mcuHeight = subsample420 ? 16 : 8;
            int mcuXCount = (width + mcuWidth - 1) / mcuWidth;
            int mcuYCount = (height + mcuHeight - 1) / mcuHeight;

            var bitWriter = new BitWriter(stream);
            int prevY = 0;
            int prevCb = 0;
            int prevCr = 0;
            var block = new byte[64];
            var dct = new double[64];
            var quant = new short[64];

            for (int my = 0; my < mcuYCount; my++)
            {
                for (int mx = 0; mx < mcuXCount; mx++)
                {
                    if (subsample420)
                    {
                        for (int by = 0; by < 2; by++)
                        {
                            for (int bx = 0; bx < 2; bx++)
                            {
                                int blockX = (mx * 2) + bx;
                                int blockY = (my * 2) + by;
                                FillBlock(yPlane, width, height, blockX, blockY, block);
                                ForwardDct(block, dct);
                                Quantize(dct, qtY, quant);
                                prevY = EncodeBlock(bitWriter, quant, prevY, dcLum, acLum);
                            }
                        }

                        FillBlock(cbPlane, cW, cH, mx, my, block);
                        ForwardDct(block, dct);
                        Quantize(dct, qtC, quant);
                        prevCb = EncodeBlock(bitWriter, quant, prevCb, dcChr, acChr);

                        FillBlock(crPlane, cW, cH, mx, my, block);
                        ForwardDct(block, dct);
                        Quantize(dct, qtC, quant);
                        prevCr = EncodeBlock(bitWriter, quant, prevCr, dcChr, acChr);
                    }
                    else
                    {
                        FillBlock(yPlane, width, height, mx, my, block);
                        ForwardDct(block, dct);
                        Quantize(dct, qtY, quant);
                        prevY = EncodeBlock(bitWriter, quant, prevY, dcLum, acLum);

                        FillBlock(cbPlane, cW, cH, mx, my, block);
                        ForwardDct(block, dct);
                        Quantize(dct, qtC, quant);
                        prevCb = EncodeBlock(bitWriter, quant, prevCb, dcChr, acChr);

                        FillBlock(crPlane, cW, cH, mx, my, block);
                        ForwardDct(block, dct);
                        Quantize(dct, qtC, quant);
                        prevCr = EncodeBlock(bitWriter, quant, prevCr, dcChr, acChr);
                    }
                }
            }

            bitWriter.Flush();
            WriteMarker(stream, 0xD9);
        }

        private static void WriteMarker(Stream stream, int marker)
        {
            stream.WriteByte(0xFF);
            stream.WriteByte((byte)marker);
        }

        private static void WriteU16(Stream stream, int value)
        {
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)(value & 0xFF));
        }

        private static void WriteJfifApp0(Stream stream)
        {
            WriteMarker(stream, 0xE0);
            WriteU16(stream, 16);
            stream.WriteByte((byte)'J');
            stream.WriteByte((byte)'F');
            stream.WriteByte((byte)'I');
            stream.WriteByte((byte)'F');
            stream.WriteByte(0);
            stream.WriteByte(1);
            stream.WriteByte(1);
            stream.WriteByte(0);
            WriteU16(stream, 1);
            WriteU16(stream, 1);
            stream.WriteByte(0);
            stream.WriteByte(0);
        }

        private static void WriteApp1(Stream stream, byte[] exifRaw)
        {
            int length = exifRaw.Length + 2;
            if (length > 0xFFFF) return;
            WriteMarker(stream, 0xE1);
            WriteU16(stream, length);
            stream.Write(exifRaw, 0, exifRaw.Length);
        }

        private static void WriteDqt(Stream stream, byte[] table, int tableId)
        {
            WriteMarker(stream, 0xDB);
            WriteU16(stream, 67);
            stream.WriteByte((byte)(tableId & 0x0F));
            for (int i = 0; i < 64; i++)
            {
                stream.WriteByte(table[ZigZag[i]]);
            }
        }

        private static void WriteSof0(Stream stream, int width, int height, int hY, int vY)
        {
            WriteMarker(stream, 0xC0);
            WriteU16(stream, 17);
            stream.WriteByte(8);
            WriteU16(stream, height);
            WriteU16(stream, width);
            stream.WriteByte(3);
            stream.WriteByte(1);
            stream.WriteByte((byte)((hY << 4) | vY));
            stream.WriteByte(0);
            stream.WriteByte(2);
            stream.WriteByte(0x11);
            stream.WriteByte(1);
            stream.WriteByte(3);
            stream.WriteByte(0x11);
            stream.WriteByte(1);
        }

        private static void WriteSos(Stream stream)
        {
            WriteMarker(stream, 0xDA);
            WriteU16(stream, 12);
            stream.WriteByte(3);
            stream.WriteByte(1);
            stream.WriteByte(0x00);
            stream.WriteByte(2);
            stream.WriteByte(0x11);
            stream.WriteByte(3);
            stream.WriteByte(0x11);
            stream.WriteByte(0);
            stream.WriteByte(63);
            stream.WriteByte(0);
        }

        private static void WriteDht(Stream stream, int tableClass, int tableId, byte[] bits, byte[] values)
        {
            int count = 0;
            for (int i = 0; i < 16; i++) count += bits[i];
            WriteMarker(stream, 0xC4);
            WriteU16(stream, 3 + 16 + count);
            stream.WriteByte((byte)((tableClass << 4) | (tableId & 0x0F)));
            stream.Write(bits, 0, 16);
            stream.Write(values, 0, count);
        }

        private static (byte[] qtY, byte[] qtC) BuildQuantTables(int quality)
        {
            int scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
            byte[] y = new byte[64];
            byte[] c = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int qy = (StdLuminanceQuant[i] * scale + 50) / 100;
                int qc = (StdChrominanceQuant[i] * scale + 50) / 100;
                y[i] = (byte)Math.Clamp(qy, 1, 255);
                c[i] = (byte)Math.Clamp(qc, 1, 255);
            }
            return (y, c);
        }

        private static void ConvertToYCbCr(byte[] rgb, int width, int height, bool subsample420, out byte[] y, out byte[] cb, out byte[] cr, out int cW, out int cH)
        {
            y = new byte[width * height];
            for (int py = 0; py < height; py++)
            {
                int row = py * width;
                int srcRow = py * width * 3;
                for (int px = 0; px < width; px++)
                {
                    int si = srcRow + (px * 3);
                    int r = rgb[si + 0];
                    int g = rgb[si + 1];
                    int b = rgb[si + 2];
                    int yy = (77 * r + 150 * g + 29 * b + 128) >> 8;
                    y[row + px] = (byte)Math.Clamp(yy, 0, 255);
                }
            }

            if (subsample420)
            {
                cW = (width + 1) >> 1;
                cH = (height + 1) >> 1;
                cb = new byte[cW * cH];
                cr = new byte[cW * cH];
                for (int py = 0; py < cH; py++)
                {
                    for (int px = 0; px < cW; px++)
                    {
                        int sumCb = 0;
                        int sumCr = 0;
                        int count = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            int sy = (py * 2) + dy;
                            if (sy >= height) continue;
                            int srcRow = sy * width * 3;
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = (px * 2) + dx;
                                if (sx >= width) continue;
                                int si = srcRow + (sx * 3);
                                int r = rgb[si + 0];
                                int g = rgb[si + 1];
                                int b = rgb[si + 2];
                                int cbv = ((-43 * r - 85 * g + 128 * b + 128) >> 8) + 128;
                                int crv = ((128 * r - 107 * g - 21 * b + 128) >> 8) + 128;
                                sumCb += Math.Clamp(cbv, 0, 255);
                                sumCr += Math.Clamp(crv, 0, 255);
                                count++;
                            }
                        }
                        int o = (py * cW) + px;
                        cb[o] = (byte)(sumCb / count);
                        cr[o] = (byte)(sumCr / count);
                    }
                }
            }
            else
            {
                cW = width;
                cH = height;
                cb = new byte[cW * cH];
                cr = new byte[cW * cH];
                for (int py = 0; py < height; py++)
                {
                    int row = py * width;
                    int srcRow = py * width * 3;
                    for (int px = 0; px < width; px++)
                    {
                        int si = srcRow + (px * 3);
                        int r = rgb[si + 0];
                        int g = rgb[si + 1];
                        int b = rgb[si + 2];
                        int cbv = ((-43 * r - 85 * g + 128 * b + 128) >> 8) + 128;
                        int crv = ((128 * r - 107 * g - 21 * b + 128) >> 8) + 128;
                        cb[row + px] = (byte)Math.Clamp(cbv, 0, 255);
                        cr[row + px] = (byte)Math.Clamp(crv, 0, 255);
                    }
                }
            }
        }

        private static void FillBlock(byte[] plane, int width, int height, int blockX, int blockY, byte[] block)
        {
            int startX = blockX * 8;
            int startY = blockY * 8;
            for (int y = 0; y < 8; y++)
            {
                int py = Math.Min(startY + y, height - 1);
                int row = py * width;
                int outRow = y * 8;
                for (int x = 0; x < 8; x++)
                {
                    int px = Math.Min(startX + x, width - 1);
                    block[outRow + x] = plane[row + px];
                }
            }
        }

        private static void ForwardDct(byte[] block, double[] output)
        {
            for (int v = 0; v < 8; v++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double sum = 0.0;
                    for (int y = 0; y < 8; y++)
                    {
                        double cy = CosTable[(y * 8) + v];
                        int row = y * 8;
                        for (int x = 0; x < 8; x++)
                        {
                            double cx = CosTable[(x * 8) + u];
                            double val = block[row + x] - 128.0;
                            sum += val * cx * cy;
                        }
                    }
                    double cu = u == 0 ? InvSqrt2 : 1.0;
                    double cv = v == 0 ? InvSqrt2 : 1.0;
                    output[(v * 8) + u] = 0.25 * cu * cv * sum;
                }
            }
        }

        private static void Quantize(double[] dct, byte[] qt, short[] output)
        {
            for (int i = 0; i < 64; i++)
            {
                output[i] = (short)Math.Round(dct[i] / qt[i]);
            }
        }

        private static int EncodeBlock(BitWriter writer, short[] block, int prevDc, HuffmanTable dc, HuffmanTable ac)
        {
            int dcVal = block[0];
            int diff = dcVal - prevDc;
            int size = BitsRequired(diff);
            writer.WriteHuffman(dc, size);
            if (size > 0)
            {
                writer.WriteBits(EncodeValue(diff, size), size);
            }

            int zeroRun = 0;
            for (int k = 1; k < 64; k++)
            {
                int val = block[ZigZag[k]];
                if (val == 0)
                {
                    zeroRun++;
                    continue;
                }

                while (zeroRun >= 16)
                {
                    writer.WriteHuffman(ac, 0xF0);
                    zeroRun -= 16;
                }

                int acSize = BitsRequired(val);
                int symbol = (zeroRun << 4) | acSize;
                writer.WriteHuffman(ac, symbol);
                writer.WriteBits(EncodeValue(val, acSize), acSize);
                zeroRun = 0;
            }

            if (zeroRun > 0)
            {
                writer.WriteHuffman(ac, 0x00);
            }

            return dcVal;
        }

        private static int BitsRequired(int value)
        {
            int v = Math.Abs(value);
            int bits = 0;
            while (v != 0)
            {
                bits++;
                v >>= 1;
            }
            return bits;
        }

        private static int EncodeValue(int value, int size)
        {
            if (value >= 0) return value;
            int mask = (1 << size) - 1;
            return (value + mask) & mask;
        }

        private sealed class BitWriter
        {
            private readonly Stream stream;
            private ulong bitBuffer;
            private int bitCount;

            public BitWriter(Stream stream)
            {
                this.stream = stream;
            }

            public void WriteHuffman(HuffmanTable table, int symbol)
            {
                int code = table.Code[symbol];
                int len = table.Length[symbol];
                WriteBits(code, len);
            }

            public void WriteBits(int value, int length)
            {
                bitBuffer = (bitBuffer << length) | (uint)(value & ((1 << length) - 1));
                bitCount += length;
                while (bitCount >= 8)
                {
                    int shift = bitCount - 8;
                    byte b = (byte)((bitBuffer >> shift) & 0xFF);
                    stream.WriteByte(b);
                    if (b == 0xFF) stream.WriteByte(0x00);
                    bitCount -= 8;
                    bitBuffer &= (1u << bitCount) - 1;
                }
            }

            public void Flush()
            {
                if (bitCount == 0) return;
                int pad = 8 - bitCount;
                int value = (int)(bitBuffer << pad);
                byte b = (byte)(value & 0xFF);
                stream.WriteByte(b);
                if (b == 0xFF) stream.WriteByte(0x00);
                bitBuffer = 0;
                bitCount = 0;
            }
        }

        private sealed class HuffmanTable
        {
            public int[] Code { get; } = new int[256];
            public int[] Length { get; } = new int[256];
        }

        private static HuffmanTable BuildHuffmanTable(byte[] bits, byte[] values)
        {
            if (bits.Length != 16) throw new ArgumentException(nameof(bits));
            var table = new HuffmanTable();
            int code = 0;
            int k = 0;
            for (int i = 0; i < 16; i++)
            {
                int len = i + 1;
                int count = bits[i];
                for (int j = 0; j < count; j++)
                {
                    int symbol = values[k++];
                    table.Code[symbol] = code;
                    table.Length[symbol] = len;
                    code++;
                }
                code <<= 1;
            }
            return table;
        }

        private static readonly double InvSqrt2 = 1.0 / Math.Sqrt(2.0);

        private static readonly double[] CosTable = BuildCosTable();

        private static double[] BuildCosTable()
        {
            var t = new double[64];
            for (int x = 0; x < 8; x++)
            {
                for (int u = 0; u < 8; u++)
                {
                    t[(x * 8) + u] = Math.Cos(((2 * x + 1) * u * Math.PI) / 16.0);
                }
            }
            return t;
        }

        private static readonly int[] ZigZag =
        [
            0, 1, 8, 16, 9, 2, 3, 10,
            17, 24, 32, 25, 18, 11, 4, 5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63
        ];

        private static readonly byte[] StdLuminanceQuant =
        [
            16, 11, 10, 16, 24, 40, 51, 61,
            12, 12, 14, 19, 26, 58, 60, 55,
            14, 13, 16, 24, 40, 57, 69, 56,
            14, 17, 22, 29, 51, 87, 80, 62,
            18, 22, 37, 56, 68, 109, 103, 77,
            24, 35, 55, 64, 81, 104, 113, 92,
            49, 64, 78, 87, 103, 121, 120, 101,
            72, 92, 95, 98, 112, 100, 103, 99
        ];

        private static readonly byte[] StdChrominanceQuant =
        [
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99
        ];

        private static readonly byte[] StdDcLuminanceBits = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
        private static readonly byte[] StdDcLuminanceValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        private static readonly byte[] StdDcChrominanceBits = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
        private static readonly byte[] StdDcChrominanceValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

        private static readonly byte[] StdAcLuminanceBits = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
        private static readonly byte[] StdAcLuminanceValues =
        [
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA
        ];

        private static readonly byte[] StdAcChrominanceBits = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
        private static readonly byte[] StdAcChrominanceValues =
        [
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA
        ];
    }
