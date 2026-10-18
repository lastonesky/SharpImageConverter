using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 基线 JPEG 的无损 Huffman 表重排（jpegtran -optimize 等价）：
    /// 复用解码器已解出的量化系数，重新统计 DC/AC 符号频率、生成最优 Huffman 表，
    /// 再按原 MCU 顺序重写熵编码段。系数完全不变，因此像素逐字节一致。
    /// </summary>
    /// <remarks>
    /// 渐进式 JPEG、多次扫描或解析失败时返回 null，由调用方回退到「只删元数据」。
    /// </remarks>
    internal static class JpegHuffmanOptimizer
    {
        /// <summary>
        /// 尝试对基线 JPEG 做无损 Huffman 重排；失败或不支持时返回 null。
        /// </summary>
        internal static byte[]? TryReencodeBaseline(byte[] jpeg)
        {
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
            if (CountSosSegments(jpeg) != 1) return null;

            JpegFrameState? state = null;
            try
            {
                state = JpegDecoder.ParseForTranscode(jpeg, out ScanHeader scan);
                if (state.IsProgressive || state.Components.Length != scan.Components.Length) return null;

                var components = new ComponentState[scan.Components.Length];
                for (int i = 0; i < components.Length; i++)
                {
                    components[i] = FindComponent(state, scan.Components[i].ComponentId);
                }

                var dcFreq = NewFreqTables();
                var acFreq = NewFreqTables();
                Walk(state, scan, components, new CountSink(dcFreq, acFreq), writer: null);

                var dcTables = new (byte[] Counts, byte[] Symbols)?[4];
                var acTables = new (byte[] Counts, byte[] Symbols)?[4];
                var dcCodes = new HuffCode[4][];
                var acCodes = new HuffCode[4][];
                bool anyTable = false;
                for (int id = 0; id < 4; id++)
                {
                    if (HasAny(dcFreq[id]))
                    {
                        var t = JpegEncoder.BuildOptimalHuffmanTable(dcFreq[id]);
                        dcTables[id] = t;
                        dcCodes[id] = BuildCodes(t.Counts, t.Symbols);
                        anyTable = true;
                    }
                    if (HasAny(acFreq[id]))
                    {
                        var t = JpegEncoder.BuildOptimalHuffmanTable(acFreq[id]);
                        acTables[id] = t;
                        acCodes[id] = BuildCodes(t.Counts, t.Symbols);
                        anyTable = true;
                    }
                }
                if (!anyTable) return null;
                foreach (var sc in scan.Components)
                {
                    if (dcTables[sc.DcTableId] == null || acTables[sc.AcTableId] == null) return null;
                }

                byte[] dht = BuildDhtSegment(dcTables, acTables);
                if (dht.Length == 0) return null;

                byte[] entropy;
                using (var ms = new MemoryStream(jpeg.Length / 2))
                {
                    using var writer = new EntropyWriter(ms);
                    Walk(state, scan, components, new EncodeSink(dcCodes, acCodes, writer), writer);
                    writer.AlignToByte();
                    entropy = ms.ToArray();
                }

                return BuildReencodedJpeg(jpeg, dht, entropy, KeepApp1(jpeg));
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

        private static long[][] NewFreqTables()
        {
            var tables = new long[4][];
            for (int i = 0; i < 4; i++) tables[i] = new long[256];
            return tables;
        }

        private static bool HasAny(long[] freq)
        {
            foreach (long f in freq) if (f > 0) return true;
            return false;
        }

        /// <summary>按 MCU / 块顺序遍历基线扫描，并在 restart 边界复位 DC 预测器。</summary>
        private static void Walk(
            JpegFrameState state, ScanHeader scan, ComponentState[] components,
            BlockSink sink, EntropyWriter? writer)
        {
            int restartInterval = state.RestartInterval;
            int units = restartInterval;
            int rst = 0;
            bool interleaved = components.Length > 1;
            var frame = state.Frame;

            void Tick()
            {
                if (restartInterval == 0 || --units != 0) return;
                if (writer != null) writer.WriteMarker((byte)(0xD0 + rst));
                rst = (rst + 1) & 7;
                sink.ResetPredictors();
                units = restartInterval;
            }

            if (interleaved)
            {
                for (int my = 0; my < frame.McuY; my++)
                {
                    for (int mx = 0; mx < frame.McuX; mx++)
                    {
                        for (int ci = 0; ci < components.Length; ci++)
                        {
                            var comp = components[ci];
                            var sc = scan.Components[ci];
                            for (int v = 0; v < comp.V; v++)
                            {
                                for (int h = 0; h < comp.H; h++)
                                {
                                    int bx = (mx * comp.H) + h;
                                    int by = (my * comp.V) + v;
                                    sink.Process(ci, comp.GetBlockSpan(by * comp.BlocksX + bx), sc.DcTableId, sc.AcTableId);
                                }
                            }
                        }
                        Tick();
                    }
                }
            }
            else
            {
                var comp = components[0];
                var sc = scan.Components[0];
                int compWidth = (frame.Width * comp.H + frame.MaxH - 1) / frame.MaxH;
                int compHeight = (frame.Height * comp.V + frame.MaxV - 1) / frame.MaxV;
                int blocksX = (compWidth + 7) / 8;
                int blocksY = (compHeight + 7) / 8;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        sink.Process(0, comp.GetBlockSpan(by * comp.BlocksX + bx), sc.DcTableId, sc.AcTableId);
                        Tick();
                    }
                }
            }
        }

        private abstract class BlockSink
        {
            protected readonly int[] Predictors = new int[4];
            public virtual void ResetPredictors() => Array.Clear(Predictors);
            public abstract void Process(int ci, Span<short> block, int dcTable, int acTable);
        }

        private sealed class CountSink(long[][] dcFreq, long[][] acFreq) : BlockSink
        {
            public override void Process(int ci, Span<short> block, int dcTable, int acTable)
            {
                int dc = block[0];
                int diff = dc - Predictors[ci];
                Predictors[ci] = dc;
                dcFreq[dcTable][MagnitudeCategory(diff)]++;

                int lastNz = LastNonZero(block);
                if (lastNz == 0)
                {
                    acFreq[acTable][0x00]++;
                    return;
                }

                int run = 0;
                ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;
                for (int k = 1; k <= lastNz; k++)
                {
                    int v = block[zigzag[k]];
                    if (v == 0) { run++; continue; }
                    while (run >= 16)
                    {
                        acFreq[acTable][0xF0]++;
                        run -= 16;
                    }
                    int cat = MagnitudeCategory(v);
                    acFreq[acTable][(run << 4) | cat]++;
                    run = 0;
                }
                if (lastNz != 63) acFreq[acTable][0x00]++;
            }
        }

        private sealed class EncodeSink(HuffCode[][] dcCodes, HuffCode[][] acCodes, EntropyWriter writer) : BlockSink
        {
            public override void Process(int ci, Span<short> block, int dcTable, int acTable)
            {
                int dc = block[0];
                int diff = dc - Predictors[ci];
                Predictors[ci] = dc;
                int dcCat = MagnitudeCategory(diff);
                writer.WriteHuffAndBits(dcCodes[dcTable][dcCat], EncodeMagnitudeBits(diff, dcCat), dcCat);

                int lastNz = LastNonZero(block);
                if (lastNz == 0)
                {
                    writer.WriteHuff(acCodes[acTable][0x00]);
                    return;
                }

                int run = 0;
                ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;
                for (int k = 1; k <= lastNz; k++)
                {
                    int v = block[zigzag[k]];
                    if (v == 0) { run++; continue; }
                    while (run >= 16)
                    {
                        writer.WriteHuff(acCodes[acTable][0xF0]);
                        run -= 16;
                    }
                    int cat = MagnitudeCategory(v);
                    writer.WriteHuffAndBits(acCodes[acTable][(run << 4) | cat], EncodeMagnitudeBits(v, cat), cat);
                    run = 0;
                }
                if (lastNz != 63) writer.WriteHuff(acCodes[acTable][0x00]);
            }
        }

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

        private static byte[] BuildDhtSegment(
            (byte[] Counts, byte[] Symbols)?[] dcTables,
            (byte[] Counts, byte[] Symbols)?[] acTables)
        {
            using var ms = new MemoryStream();
            for (int id = 0; id < 4; id++)
            {
                if (dcTables[id] is { } dc) WriteDhtEntry(ms, (byte)id, dc.Counts, dc.Symbols);
                if (acTables[id] is { } ac) WriteDhtEntry(ms, (byte)(0x10 | id), ac.Counts, ac.Symbols);
            }
            if (ms.Length == 0) return [];
            int len = (int)ms.Length + 2;
            if (len > 65535) return [];
            var result = new byte[4 + ms.Length];
            result[0] = 0xFF; result[1] = 0xC4;
            result[2] = (byte)(len >> 8); result[3] = (byte)len;
            ms.Position = 0;
            ms.ReadExactly(result.AsSpan(4));
            return result;
        }

        private static void WriteDhtEntry(Stream stream, byte tcTh, byte[] counts, byte[] symbols)
        {
            stream.WriteByte(tcTh);
            stream.Write(counts, 0, 16);
            stream.Write(symbols, 0, symbols.Length);
        }

        private static byte[] BuildReencodedJpeg(byte[] jpeg, byte[] dht, byte[] entropy, bool keepApp1)
        {
            using var output = new MemoryStream(jpeg.Length);
            output.WriteByte(0xFF); output.WriteByte(0xD8);
            bool dhtWritten = false;
            bool wroteEoi = false;
            int pos = 2;
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF) { pos++; continue; }
                int markerPos = pos;
                int m = pos + 1;
                while (m < jpeg.Length && jpeg[m] == 0xFF) m++;
                if (m >= jpeg.Length) break;
                byte marker = jpeg[m];

                if (marker == 0xD8) { pos = m + 1; continue; }
                if (marker == 0xD9)
                {
                    output.WriteByte(0xFF); output.WriteByte(0xD9);
                    wroteEoi = true;
                    break;
                }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    output.Write(jpeg, markerPos, m - markerPos + 1);
                    pos = m + 1;
                    continue;
                }
                if (m + 2 >= jpeg.Length) break;
                int length = (jpeg[m + 1] << 8) | jpeg[m + 2];
                int end = m + 1 + length;
                if (length < 2 || end > jpeg.Length) break;

                if (marker == 0xC4) // DHT：丢弃，统一在第一个 SOS 前写新表
                {
                    pos = end;
                    continue;
                }
                if (marker == 0xDA) // SOS
                {
                    if (!dhtWritten)
                    {
                        output.Write(dht, 0, dht.Length);
                        dhtWritten = true;
                    }
                    output.Write(jpeg, markerPos, end - markerPos);
                    output.Write(entropy, 0, entropy.Length);

                    // 跳过原熵编码数据
                    pos = end;
                    while (pos + 1 < jpeg.Length)
                    {
                        if (jpeg[pos] != 0xFF) { pos++; continue; }
                        byte next = jpeg[pos + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) { pos += 2; continue; }
                        if (next == 0xFF) { pos++; continue; }
                        break;
                    }
                    continue;
                }

                bool drop = marker == 0xFE || marker == 0xED || (marker == 0xE1 && !keepApp1);
                if (!drop) output.Write(jpeg, markerPos, end - markerPos);
                pos = end;
            }
            if (!wroteEoi) { output.WriteByte(0xFF); output.WriteByte(0xD9); }
            return output.ToArray();
        }

        private static int CountSosSegments(byte[] jpeg)
        {
            int count = 0;
            foreach (var (marker, _, _) in EnumerateSegments(jpeg))
            {
                if (marker == 0xDA) count++;
            }
            return count;
        }

        private static bool KeepApp1(byte[] jpeg)
        {
            int orientation = 1;
            byte[]? exifRaw = null;
            foreach (var (marker, dataStart, dataLength) in EnumerateSegments(jpeg))
            {
                if (marker != 0xE1) continue;
                JpegDecoder.TryStoreExifApp1(jpeg.AsSpan(dataStart, dataLength), ref exifRaw, ref orientation);
                if (orientation != 1) return true;
            }
            return false;
        }

        private static IEnumerable<(byte Marker, int DataStart, int DataLength)> EnumerateSegments(byte[] jpeg)
        {
            int pos = 2;
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF) { pos++; continue; }
                int m = pos + 1;
                while (m < jpeg.Length && jpeg[m] == 0xFF) m++;
                if (m >= jpeg.Length) yield break;
                byte marker = jpeg[m];
                if (marker == 0xD9) yield break;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    pos = m + 1;
                    continue;
                }
                if (m + 2 >= jpeg.Length) yield break;
                int length = (jpeg[m + 1] << 8) | jpeg[m + 2];
                int end = m + 1 + length;
                if (length < 2 || end > jpeg.Length) yield break;

                yield return (marker, m + 3, length - 2);
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

            public void WriteMarker(byte marker)
            {
                AlignToByte();
                stream.WriteByte(0xFF);
                stream.WriteByte(marker);
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
