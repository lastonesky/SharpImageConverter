using System;
using System.IO;
using System.Numerics;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 把基线 JPEG 无损转成 progressive JPEG：复用基线文件的量化系数，
    /// 只改变熵编码组织方式（DC 扫描 + 每个分量一条 AC 扫描），不重新量化。
    /// </summary>
    /// <remarks>
    /// 当前只做 spectral selection（Ss/Se），不做 successive approximation（Ah/Al=0），
    /// 因此每个系数只编码一次，解码像素与基线完全一致。渐进式最终是否更小由调用方按体积取小。
    /// </remarks>
    internal static class JpegProgressiveTranscoder
    {
        internal static byte[]? TryTranscode(byte[] baseline)
        {
            if (baseline.Length < 4 || baseline[0] != 0xFF || baseline[1] != 0xD8) return null;
            if (CountSosSegments(baseline) != 1) return null;

            JpegFrameState? state = null;
            try
            {
                state = JpegDecoder.ParseForTranscode(baseline, out ScanHeader scan);
                if (state.IsProgressive || state.Components.Length != scan.Components.Length) return null;

                var components = new ComponentState[scan.Components.Length];
                for (int i = 0; i < components.Length; i++)
                {
                    components[i] = FindComponent(state, scan.Components[i].ComponentId);
                }

                // DC 扫描：所有分量共用一张 DC 表（id 0）。
                var dcFreq = new long[256];
                CountDc(state, scan, components, dcFreq);
                var dcTable = JpegEncoder.BuildOptimalHuffmanTable(dcFreq);
                var dcCodes = BuildCodes(dcTable.Counts, dcTable.Symbols);

                // AC 扫描：每个分量单独一条，各自一张 AC 表（id 0，扫描前重新定义）。
                var acFreqs = new long[components.Length][];
                var acTables = new (byte[] Counts, byte[] Symbols)[components.Length];
                var acCodes = new HuffCode[components.Length][];
                for (int ci = 0; ci < components.Length; ci++)
                {
                    acFreqs[ci] = new long[256];
                    CountAc(state.Frame, components[ci], acFreqs[ci]);
                    acTables[ci] = JpegEncoder.BuildOptimalHuffmanTable(acFreqs[ci]);
                    acCodes[ci] = BuildCodes(acTables[ci].Counts, acTables[ci].Symbols);
                }

                return BuildProgressive(baseline, state, scan, components, dcTable, dcCodes, acTables, acCodes);
            }
            catch
            {
                return null;
            }
            finally
            {
                state?.DisposeResources();
            }
        }

        private static ComponentState FindComponent(JpegFrameState state, byte id)
        {
            foreach (var comp in state.Components)
            {
                if (comp.Id == id) return comp;
            }
            throw new InvalidDataException("JPEG 分量不存在");
        }

        // ---------- DC 扫描 ----------

        private static void CountDc(JpegFrameState state, ScanHeader scan, ComponentState[] components, long[] freq)
        {
            var predictors = new int[components.Length];
            WalkInterleaved(state, scan, components, (ci, block) =>
            {
                int dc = block[0];
                int diff = dc - predictors[ci];
                predictors[ci] = dc;
                freq[MagnitudeCategory(diff)]++;
            });
        }

        private static void EncodeDc(EntropyWriter writer, JpegFrameState state, ScanHeader scan,
            ComponentState[] components, HuffCode[] codes)
        {
            var predictors = new int[components.Length];
            WalkInterleaved(state, scan, components, (ci, block) =>
            {
                int dc = block[0];
                int diff = dc - predictors[ci];
                predictors[ci] = dc;
                int cat = MagnitudeCategory(diff);
                writer.WriteHuffAndBits(codes[cat], EncodeMagnitudeBits(diff, cat), cat);
            });
        }

        private delegate void BlockVisitor(int ci, Span<short> block);

        private static void WalkInterleaved(JpegFrameState state, ScanHeader scan, ComponentState[] components,
            BlockVisitor process)
        {
            var frame = state.Frame;
            for (int my = 0; my < frame.McuY; my++)
            {
                for (int mx = 0; mx < frame.McuX; mx++)
                {
                    for (int ci = 0; ci < components.Length; ci++)
                    {
                        var comp = components[ci];
                        for (int v = 0; v < comp.V; v++)
                        {
                            for (int h = 0; h < comp.H; h++)
                            {
                                int bx = (mx * comp.H) + h;
                                int by = (my * comp.V) + v;
                                process(ci, comp.GetBlockSpan(by * comp.BlocksX + bx));
                            }
                        }
                    }
                }
            }
        }

        // ---------- AC 扫描（单分量，带 EOBRUN） ----------

        private static void CountAc(FrameHeader frame, ComponentState comp, long[] freq)
        {
            int eobRun = 0;
            WalkComponent(frame, comp, block =>
            {
                int lastNz = LastNonZero(block);
                if (lastNz == 0)
                {
                    if (++eobRun == 0x7FFF)
                    {
                        CountEob(freq, eobRun);
                        eobRun = 0;
                    }
                    return;
                }

                if (eobRun > 0)
                {
                    CountEob(freq, eobRun);
                    eobRun = 0;
                }

                int run = 0;
                ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;
                for (int k = 1; k <= lastNz; k++)
                {
                    int v = block[zigzag[k]];
                    if (v == 0) { run++; continue; }
                    while (run >= 16)
                    {
                        freq[0xF0]++;
                        run -= 16;
                    }
                    int cat = MagnitudeCategory(v);
                    freq[(run << 4) | cat]++;
                    run = 0;
                }
                if (lastNz != 63) eobRun = 1;
            });

            if (eobRun > 0) CountEob(freq, eobRun);
        }

        private static void EncodeAc(EntropyWriter writer, FrameHeader frame, ComponentState comp, HuffCode[] codes)
        {
            int eobRun = 0;
            WalkComponent(frame, comp, block =>
            {
                int lastNz = LastNonZero(block);
                if (lastNz == 0)
                {
                    if (++eobRun == 0x7FFF)
                    {
                        WriteEob(writer, codes, eobRun);
                        eobRun = 0;
                    }
                    return;
                }

                if (eobRun > 0)
                {
                    WriteEob(writer, codes, eobRun);
                    eobRun = 0;
                }

                int run = 0;
                ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;
                for (int k = 1; k <= lastNz; k++)
                {
                    int v = block[zigzag[k]];
                    if (v == 0) { run++; continue; }
                    while (run >= 16)
                    {
                        writer.WriteHuff(codes[0xF0]);
                        run -= 16;
                    }
                    int cat = MagnitudeCategory(v);
                    writer.WriteHuffAndBits(codes[(run << 4) | cat], EncodeMagnitudeBits(v, cat), cat);
                    run = 0;
                }
                if (lastNz != 63) eobRun = 1;
            });

            if (eobRun > 0) WriteEob(writer, codes, eobRun);
        }

        private delegate void SingleBlockVisitor(Span<short> block);

        private static void WalkComponent(FrameHeader frame, ComponentState comp, SingleBlockVisitor process)
        {
            // 与解码器的非交错扫描完全一致：只遍历分量有效区域，不碰右侧/底部的 MCU 填充块。
            int compWidth = (frame.Width * comp.H + frame.MaxH - 1) / frame.MaxH;
            int compHeight = (frame.Height * comp.V + frame.MaxV - 1) / frame.MaxV;
            int blocksX = (compWidth + 7) / 8;
            int blocksY = (compHeight + 7) / 8;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    process(comp.GetBlockSpan(by * comp.BlocksX + bx));
                }
            }
        }

        private static void CountEob(long[] freq, int runBlocks)
        {
            int v = runBlocks - 1;
            int r = 0;
            while (v > (1 << (r + 1)) - 2) r++;
            freq[r << 4]++;
        }

        private static void WriteEob(EntropyWriter writer, HuffCode[] codes, int runBlocks)
        {
            int v = runBlocks - 1;
            int r = 0;
            while (v > (1 << (r + 1)) - 2) r++;
            int symbol = r << 4;
            writer.WriteHuff(codes[symbol]);
            if (r > 0)
            {
                int baseValue = (1 << r) - 1;
                writer.WriteBits((uint)(v - baseValue), r);
            }
        }

        // ---------- 输出 ----------

        private static byte[]? BuildProgressive(
            byte[] baseline, JpegFrameState state, ScanHeader scan, ComponentState[] components,
            (byte[] Counts, byte[] Symbols) dcTable, HuffCode[] dcCodes,
            (byte[] Counts, byte[] Symbols)[] acTables, HuffCode[][] acCodes)
        {
            using var output = new MemoryStream(baseline.Length);
            output.WriteByte(0xFF); output.WriteByte(0xD8);

            int pos = 2;
            bool sawSof = false;
            while (pos + 1 < baseline.Length)
            {
                if (baseline[pos] != 0xFF) { pos++; continue; }
                int markerPos = pos;
                int m = pos + 1;
                while (m < baseline.Length && baseline[m] == 0xFF) m++;
                if (m >= baseline.Length) return null;
                byte marker = baseline[m];
                if (marker == 0xD8) { pos = m + 1; continue; }
                if (marker == 0xD9) break;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    pos = m + 1;
                    continue;
                }
                if (m + 2 >= baseline.Length) return null;
                int length = (baseline[m + 1] << 8) | baseline[m + 2];
                int end = m + 1 + length;
                if (length < 2 || end > baseline.Length) return null;

                if (marker == 0xDA) break; // 到第一个 SOS，后面全部由我们重写
                if (marker == 0xC4 || marker == 0xDD) { pos = end; continue; } // DHT / DRI
                if (marker == 0xC0)
                {
                    // SOF0 -> SOF2，段数据完全不变
                    output.WriteByte(0xFF); output.WriteByte(0xC2);
                    output.Write(baseline, m + 1, end - (m + 1));
                    sawSof = true;
                }
                else
                {
                    output.Write(baseline, markerPos, end - markerPos);
                }
                pos = end;
            }
            if (!sawSof) return null;

            // DC 扫描
            WriteDht(output, 0x00, dcTable.Counts, dcTable.Symbols);
            WriteSos(output, components, ss: 0, se: 0);
            using (var entropy = new MemoryStream())
            {
                using (var writer = new EntropyWriter(entropy))
                {
                    EncodeDc(writer, state, scan, components, dcCodes);
                    writer.AlignToByte();
                }
                output.Write(entropy.GetBuffer(), 0, (int)entropy.Length);
            }

            // 每个分量一条 AC 扫描（1..63，Ah=Al=0）
            for (int ci = 0; ci < components.Length; ci++)
            {
                WriteDht(output, 0x10, acTables[ci].Counts, acTables[ci].Symbols);
                WriteSos(output, [components[ci]], ss: 1, se: 63);
                using var entropy = new MemoryStream();
                using (var writer = new EntropyWriter(entropy))
                {
                    EncodeAc(writer, state.Frame, components[ci], acCodes[ci]);
                    writer.AlignToByte();
                }
                output.Write(entropy.GetBuffer(), 0, (int)entropy.Length);
            }

            output.WriteByte(0xFF); output.WriteByte(0xD9);
            return output.ToArray();
        }

        private static void WriteDht(Stream output, byte tcTh, byte[] counts, byte[] symbols)
        {
            int length = 2 + 1 + 16 + symbols.Length;
            output.WriteByte(0xFF); output.WriteByte(0xC4);
            output.WriteByte((byte)(length >> 8)); output.WriteByte((byte)length);
            output.WriteByte(tcTh);
            output.Write(counts, 0, 16);
            output.Write(symbols, 0, symbols.Length);
        }

        private static void WriteSos(
            Stream output, ComponentState[] components,
            byte ss, byte se)
        {
            int n = components.Length;
            int length = 2 + 1 + (2 * n) + 3;
            output.WriteByte(0xFF); output.WriteByte(0xDA);
            output.WriteByte((byte)(length >> 8)); output.WriteByte((byte)length);
            output.WriteByte((byte)n);
            for (int i = 0; i < n; i++)
            {
                output.WriteByte(components[i].Id);
                // DC 用表 0；AC 每条扫描都用表 0（扫描前重新定义）
                output.WriteByte(0x00);
            }
            output.WriteByte(ss);
            output.WriteByte(se);
            output.WriteByte(0x00); // Ah/Al = 0
        }

        // ---------- 通用 ----------

        private static int LastNonZero(Span<short> block)
        {
            ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;
            for (int k = 63; k >= 1; k--)
            {
                if (block[zigzag[k]] != 0) return k;
            }
            return 0;
        }

        private static int MagnitudeCategory(int v)
        {
            if (v == 0) return 0;
            uint a = (uint)(v < 0 ? -v : v);
            return 32 - BitOperations.LeadingZeroCount(a);
        }

        private static uint EncodeMagnitudeBits(int v, int cat)
        {
            if (cat == 0) return 0;
            return v >= 0 ? (uint)v : (uint)(v + ((1u << cat) - 1));
        }

        private static HuffCode[] BuildCodes(byte[] counts, byte[] symbols)
        {
            var table = new HuffCode[256];
            int code = 0;
            int idx = 0;
            for (int len = 1; len <= 16; len++)
            {
                int count = counts[len - 1];
                for (int i = 0; i < count; i++)
                {
                    byte symbol = symbols[idx++];
                    table[symbol] = new HuffCode((ushort)code, (byte)len);
                    code++;
                }
                code <<= 1;
            }
            return table;
        }

        private static int CountSosSegments(byte[] jpeg)
        {
            int count = 0;
            int pos = 2;
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF) { pos++; continue; }
                int m = pos + 1;
                while (m < jpeg.Length && jpeg[m] == 0xFF) m++;
                if (m >= jpeg.Length) break;
                byte marker = jpeg[m];
                if (marker == 0xD9) break;
                if (marker == 0xDA) count++;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos = m + 1; continue; }
                if (m + 2 >= jpeg.Length) break;
                int length = (jpeg[m + 1] << 8) | jpeg[m + 2];
                int end = m + 1 + length;
                if (length < 2 || end > jpeg.Length) break;
                pos = end;
                if (marker == 0xDA)
                {
                    while (pos + 1 < jpeg.Length)
                    {
                        if (jpeg[pos] != 0xFF) { pos++; continue; }
                        byte next = jpeg[pos + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) { pos += 2; continue; }
                        if (next == 0xFF) { pos++; continue; }
                        break;
                    }
                }
            }
            return count;
        }

        private readonly record struct HuffCode(ushort Code, byte Length);

        private sealed class EntropyWriter : IDisposable
        {
            private readonly Stream stream;
            private uint bits;
            private int bitCount;

            public EntropyWriter(Stream stream) { this.stream = stream; }

            private void WriteStuffedByte(byte b)
            {
                stream.WriteByte(b);
                if (b == 0xFF) stream.WriteByte(0x00);
            }

            public void WriteBits(uint value, int count)
            {
                if (count == 0) return;
                bits = (bits << count) | (value & ((1u << count) - 1));
                bitCount += count;
                while (bitCount >= 8)
                {
                    int shift = bitCount - 8;
                    WriteStuffedByte((byte)((bits >> shift) & 0xFF));
                    bitCount -= 8;
                    bits &= bitCount == 0 ? 0u : ((1u << bitCount) - 1);
                }
            }

            public void WriteHuff(HuffCode hc) => WriteBits(hc.Code, hc.Length);

            public void WriteHuffAndBits(HuffCode hc, uint value, int count)
            {
                WriteBits(hc.Code, hc.Length);
                if (count > 0) WriteBits(value, count);
            }

            public void AlignToByte()
            {
                if (bitCount > 0)
                {
                    WriteBits((1u << (8 - bitCount)) - 1, 8 - bitCount);
                }
            }

            public void Dispose() => AlignToByte();
        }
    }
}
