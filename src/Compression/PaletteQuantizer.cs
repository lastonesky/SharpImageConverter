using System;
using System.Collections.Generic;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 调色板量化选项。
    /// </summary>
    public sealed class QuantizeOptions
    {
        /// <summary>
        /// 调色板颜色数上限（2-256）。默认 256。
        /// </summary>
        public int MaxColors { get; set; } = 256;

        /// <summary>
        /// 是否启用 Floyd–Steinberg 误差扩散抖动。默认开启。
        /// </summary>
        public bool EnableDithering { get; set; } = true;

        /// <summary>
        /// 抖动强度（0-1）。默认 1，即标准 Floyd–Steinberg 权重。
        /// </summary>
        public double DitherStrength { get; set; } = 1.0;

        /// <summary>
        /// 透明阈值（0-255）。大于 0 时，alpha 低于该值的像素直接映射到调色板索引 0（GIF 用），
        /// 且不参与量化，把调色板预算全部留给不透明像素。默认 0（关闭，alpha 正常参与量化）。
        /// </summary>
        public int TransparentAlphaThreshold { get; set; }
    }

    /// <summary>
    /// 调色板量化结果。
    /// </summary>
    public sealed class QuantizeResult
    {
        /// <summary>
        /// 调色板 RGB 部分，长度 3 * <see cref="ColorCount"/>。
        /// </summary>
        public byte[] PaletteRgb { get; init; } = [];

        /// <summary>
        /// 调色板 alpha 部分；不含任何半透明色时为 null（PNG 无需写 tRNS）。
        /// </summary>
        public byte[]? PaletteAlpha { get; init; }

        /// <summary>
        /// 每像素 1 字节的调色板索引，长度 width * height。
        /// </summary>
        public byte[] Indices { get; init; } = [];

        /// <summary>
        /// 调色板实际颜色数（含保留的透明槽）。
        /// </summary>
        public int ColorCount { get; init; }
    }

    /// <summary>
    /// RGBA 调色板量化器：先尝试精确取色（无损），否则用中位切分聚类 + 误差扩散抖动。
    /// </summary>
    /// <remarks>
    /// 实例内部持有可复用的直方图 / 查找表缓冲。评估多个候选方案时请复用同一实例，
    /// 避免反复分配数 MB 的临时数组。
    /// </remarks>
    public sealed class PaletteQuantizer
    {
        private const int RgbBins = 32768;   // 每通道 5 bit
        private const int AlphaBins = 32;    // alpha 5 bit
        private const int TotalBins = RgbBins * AlphaBins;
        private const int MaxHistogramSamples = 4 << 20;
        private const int ShortlistSize = 16;

        private struct Bin
        {
            public int Count;
            public byte R;
            public byte G;
            public byte B;
            public byte A;
        }

        private struct Box
        {
            public int Start;
            public int End;
            public int Count;
            public int SumR;
            public int SumG;
            public int SumB;
            public int SumA;
            public byte MinR;
            public byte MaxR;
            public byte MinG;
            public byte MaxG;
            public byte MinB;
            public byte MaxB;
            public byte MinA;
            public byte MaxA;
        }

        private sealed class BinComparer : IComparer<Bin>
        {
            public int Channel;

            public int Compare(Bin x, Bin y) => Channel switch
            {
                0 => x.R - y.R,
                1 => x.G - y.G,
                2 => x.B - y.B,
                _ => x.A - y.A,
            };
        }

        private readonly BinComparer _comparer = new();

        private int[]? _counts;
        private int[]? _sumR;
        private int[]? _sumG;
        private int[]? _sumB;
        private int[]? _sumA;
        private int[]? _lut;
        private int[]? _rgbLut;
        private int[]? _shortlist;
        private int[]? _usedBins;
        private Bin[]? _binArray;
        private Box[]? _boxArray;
        private byte[]? _palR;
        private byte[]? _palG;
        private byte[]? _palB;
        private byte[]? _palA;

        private Dictionary<uint, int>? _exactMap;
        private int _exactOffset;
        private bool _hasAlpha;
        private int _capacity;
        private int _binCount;
        private int _usedBinCount;
        private int _boxCount;
        private int _palCount;

        /// <summary>
        /// 对 RGBA32 像素做调色板量化（一次性使用的便捷入口）。
        /// </summary>
        /// <param name="rgba">RGBA32 像素数据</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="options">量化选项</param>
        public static QuantizeResult Quantize(ReadOnlySpan<byte> rgba, int width, int height, QuantizeOptions? options = null)
            => new PaletteQuantizer().QuantizeCore(rgba, width, height, options ?? new QuantizeOptions());

        /// <summary>
        /// 统计不同颜色的数量；超过 <paramref name="max"/> 时提前返回 <paramref name="max"/> + 1。
        /// </summary>
        /// <param name="rgba">RGBA32 像素数据</param>
        /// <param name="max">关心的上限</param>
        public static int CountUniqueColors(ReadOnlySpan<byte> rgba, int max = 256)
        {
            int pixels = rgba.Length / 4;
            var seen = new HashSet<uint>(Math.Min(max + 1, 1024));
            for (int p = 0; p < pixels; p++)
            {
                int i = p * 4;
                uint key = ((uint)rgba[i] << 24) | ((uint)rgba[i + 1] << 16) | ((uint)rgba[i + 2] << 8) | rgba[i + 3];
                if (seen.Add(key) && seen.Count > max) return max + 1;
            }
            return seen.Count;
        }

        /// <summary>
        /// 对 RGBA32 像素做调色板量化（复用本实例的缓冲区）。
        /// </summary>
        /// <param name="rgba">RGBA32 像素数据</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="options">量化选项</param>
        public QuantizeResult QuantizeCore(ReadOnlySpan<byte> rgba, int width, int height, QuantizeOptions options)
        {
            Prepare(rgba, width, height, options);
            byte[] indices = Map(rgba, width, height, options);
            return AssemblePalette(indices, options.TransparentAlphaThreshold > 0);
        }

        /// <summary>
        /// 第一阶段：只建立调色板（不做映射）。多帧共享同一调色板时用它。
        /// </summary>
        /// <param name="rgba">用于建调色板的 RGBA32 像素（可以是多帧拼接的大图）</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="options">量化选项</param>
        public void Prepare(ReadOnlySpan<byte> rgba, int width, int height, QuantizeOptions options)
        {
            int maxColors = Math.Clamp(options.MaxColors, 2, 256);
            int pixels = width * height;
            if (pixels <= 0) throw new ArgumentException("图像尺寸非法", nameof(width));
            if (rgba.Length < pixels * 4) throw new ArgumentException("像素缓冲区长度与宽高不匹配", nameof(rgba));

            bool reserveTransparent = options.TransparentAlphaThreshold > 0;
            int opaqueBudget = Math.Max(1, reserveTransparent ? maxColors - 1 : maxColors);
            rgba = rgba.Slice(0, pixels * 4);
            ScanAlpha(rgba);

            _exactMap = null;
            if (TryExactPalette(rgba, pixels, opaqueBudget, reserveTransparent, options)) return;

            BuildHistogram(rgba, pixels, reserveTransparent ? options.TransparentAlphaThreshold : 0);
            BuildBins();
            BuildPalette(opaqueBudget);
            BuildSearchStructures();
        }

        /// <summary>
        /// 第二阶段：用 <see cref="Prepare"/> 得到的调色板把一帧映射为索引。
        /// </summary>
        /// <param name="rgba">待映射的 RGBA32 像素</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="options">量化选项（抖动开关需与建调色板时一致）</param>
        /// <returns>每像素 1 字节的索引</returns>
        public byte[] Map(ReadOnlySpan<byte> rgba, int width, int height, QuantizeOptions options)
        {
            int pixels = width * height;
            if (rgba.Length < pixels * 4) throw new ArgumentException("像素缓冲区长度与宽高不匹配", nameof(rgba));
            bool reserveTransparent = options.TransparentAlphaThreshold > 0;
            byte[] indices = new byte[pixels];

            if (_exactMap != null)
            {
                MapExact(rgba, indices, pixels, reserveTransparent, options);
                return indices;
            }
            if (_lut == null) throw new InvalidOperationException("请先调用 Prepare 建立调色板");

            if (options.EnableDithering) MapWithDithering(rgba, indices, width, height, reserveTransparent, options);
            else MapNearest(rgba, indices, pixels, reserveTransparent, options);
            return indices;
        }

        /// <summary>
        /// 输出当前调色板与给定索引构成的结果。
        /// </summary>
        /// <param name="indices">调色板索引</param>
        /// <param name="reserveTransparent">是否保留了 0 号透明槽</param>
        public QuantizeResult AssemblePalette(byte[] indices, bool reserveTransparent)
            => Assemble(indices, reserveTransparent);

        private void ScanAlpha(ReadOnlySpan<byte> rgba)
        {
            bool hasAlpha = false;
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255)
                {
                    hasAlpha = true;
                    break;
                }
            }
            _hasAlpha = hasAlpha;
        }

        /// <summary>
        /// 精确取色：颜色数不超过预算时直接建立无损调色板，写入内部调色板数组。
        /// </summary>
        /// <returns>命中精确取色返回 true；颜色数超预算返回 false</returns>
        private bool TryExactPalette(
            ReadOnlySpan<byte> rgba,
            int pixels,
            int opaqueBudget,
            bool reserveTransparent,
            QuantizeOptions options)
        {
            var map = new Dictionary<uint, int>(Math.Min(opaqueBudget + 1, 97));
            var order = new List<uint>(Math.Min(opaqueBudget + 1, 97));
            int threshold = reserveTransparent ? options.TransparentAlphaThreshold : 0;

            for (int p = 0; p < pixels; p++)
            {
                int i = p * 4;
                byte a = rgba[i + 3];
                if (reserveTransparent && a < threshold) continue;
                uint key = ((uint)rgba[i] << 24) | ((uint)rgba[i + 1] << 16) | ((uint)rgba[i + 2] << 8) | a;
                if (map.ContainsKey(key)) continue;
                if (map.Count >= opaqueBudget) return false;
                map[key] = map.Count;
                order.Add(key);
            }

            _palR ??= new byte[256];
            _palG ??= new byte[256];
            _palB ??= new byte[256];
            _palA ??= new byte[256];

            int offset = reserveTransparent ? 1 : 0;
            _palCount = order.Count;
            _exactOffset = offset;
            if (reserveTransparent)
            {
                _palR[0] = 0;
                _palG[0] = 0;
                _palB[0] = 0;
                _palA[0] = 0;
            }
            for (int k = 0; k < order.Count; k++)
            {
                uint key = order[k];
                int dst = k + offset;
                _palR[dst] = (byte)(key >> 24);
                _palG[dst] = (byte)(key >> 16);
                _palB[dst] = (byte)(key >> 8);
                _palA[dst] = (byte)key;
            }

            _exactMap = map;
            return true;
        }

        private void MapExact(ReadOnlySpan<byte> rgba, byte[] indices, int pixels, bool reserveTransparent, QuantizeOptions options)
        {
            var map = _exactMap!;
            int threshold = reserveTransparent ? options.TransparentAlphaThreshold : 0;
            int offset = _exactOffset;
            for (int p = 0; p < pixels; p++)
            {
                int i = p * 4;
                byte a = rgba[i + 3];
                if (reserveTransparent && a < threshold)
                {
                    indices[p] = 0;
                    continue;
                }
                uint key = ((uint)rgba[i] << 24) | ((uint)rgba[i + 1] << 16) | ((uint)rgba[i + 2] << 8) | a;
                indices[p] = (byte)(map[key] + offset);
            }
        }

        private void BuildHistogram(ReadOnlySpan<byte> rgba, int pixels, int transparentThreshold)
        {
            _capacity = _hasAlpha ? TotalBins : RgbBins;
            if (_counts == null || _counts.Length < _capacity)
            {
                _counts = new int[_capacity];
                _sumR = new int[_capacity];
                _sumG = new int[_capacity];
                _sumB = new int[_capacity];
                _sumA = new int[_capacity];
                _lut = new int[_capacity];
            }

            int[] counts = _counts!;
            int[] sr = _sumR!, sg = _sumG!, sb = _sumB!, sa = _sumA!;

            // 只清零上一轮用过的桶，避免每轮对整个大表做 memset
            if (_usedBins != null)
            {
                for (int i = 0; i < _usedBinCount; i++)
                {
                    int bin = _usedBins[i];
                    counts[bin] = 0;
                    sr[bin] = 0;
                    sg[bin] = 0;
                    sb[bin] = 0;
                    sa[bin] = 0;
                }
            }
            _usedBinCount = 0;
            int initialBins = Math.Min(_capacity, 1 << 18);
            if (_usedBins == null || _usedBins.Length < initialBins) _usedBins = new int[initialBins];

            Array.Fill(_lut!, -1);

            int step = pixels > MaxHistogramSamples ? (pixels + MaxHistogramSamples - 1) / MaxHistogramSamples : 1;
            bool hasAlpha = _hasAlpha;
            for (int p = 0; p < pixels; p += step)
            {
                int i = p * 4;
                byte a = rgba[i + 3];
                if (transparentThreshold > 0 && a < transparentThreshold) continue;
                int rgbKey = ((rgba[i] >> 3) << 10) | ((rgba[i + 1] >> 3) << 5) | (rgba[i + 2] >> 3);
                int bin = hasAlpha ? rgbKey * AlphaBins + (a >> 3) : rgbKey;
                if (counts[bin] == 0)
                {
                    if (_usedBinCount == _usedBins!.Length) Array.Resize(ref _usedBins, _usedBins.Length * 2);
                    _usedBins[_usedBinCount++] = bin;
                }
                counts[bin]++;
                sr[bin] += rgba[i];
                sg[bin] += rgba[i + 1];
                sb[bin] += rgba[i + 2];
                sa[bin] += a;
            }
        }

        private void BuildBins()
        {
            if (_binArray == null || _binArray.Length < _usedBinCount) _binArray = new Bin[Math.Max(_usedBinCount, 16)];
            int[] counts = _counts!, sr = _sumR!, sg = _sumG!, sb = _sumB!, sa = _sumA!;
            for (int k = 0; k < _usedBinCount; k++)
            {
                int bin = _usedBins![k];
                int c = counts[bin];
                ref var b = ref _binArray[k];
                b.Count = c;
                b.R = (byte)((sr[bin] + (c >> 1)) / c);
                b.G = (byte)((sg[bin] + (c >> 1)) / c);
                b.B = (byte)((sb[bin] + (c >> 1)) / c);
                b.A = (byte)((sa[bin] + (c >> 1)) / c);
            }
            _binCount = _usedBinCount;
        }

        /// <summary>
        /// 中位切分：反复切分「像素数 × 最大通道跨度」最大的盒子，直到达到颜色预算。
        /// </summary>
        private void BuildPalette(int maxColors)
        {
            int capacity = maxColors * 2 + 4;
            if (_boxArray == null || _boxArray.Length < capacity) _boxArray = new Box[capacity];
            if (_palR == null)
            {
                _palR = new byte[256];
                _palG = new byte[256];
                _palB = new byte[256];
                _palA = new byte[256];
            }

            _boxCount = 0;
            AddBox(MakeBox(0, _binCount));

            while (_boxCount < maxColors)
            {
                int best = -1;
                long bestScore = 0;
                for (int i = 0; i < _boxCount; i++)
                {
                    var b = _boxArray[i];
                    if (b.End - b.Start < 2 || b.Count < 2) continue;
                    long score = ScoreOf(in b);
                    if (score <= 0) continue;
                    if (best < 0 || score > bestScore)
                    {
                        best = i;
                        bestScore = score;
                    }
                }
                if (best < 0) break;

                int start = _boxArray[best].Start;
                int end = _boxArray[best].End;
                int count = _boxArray[best].Count;
                _comparer.Channel = PickChannel(in _boxArray[best]);

                Array.Sort(_binArray!, start, end - start, _comparer);

                int half = (count + 1) >> 1;
                int acc = 0;
                int mid = start + 1;
                for (int k = start; k < end - 1; k++)
                {
                    acc += _binArray![k].Count;
                    mid = k + 1;
                    if (acc >= half) break;
                }

                _boxArray[best] = MakeBox(start, mid);
                AddBox(MakeBox(mid, end));
            }

            _palCount = _boxCount;
            for (int i = 0; i < _boxCount; i++)
            {
                var b = _boxArray[i];
                int c = b.Count > 0 ? b.Count : 1;
                _palR![i] = (byte)((b.SumR + (c >> 1)) / c);
                _palG![i] = (byte)((b.SumG + (c >> 1)) / c);
                _palB![i] = (byte)((b.SumB + (c >> 1)) / c);
                _palA![i] = (byte)((b.SumA + (c >> 1)) / c);
            }
        }

        private void AddBox(Box box)
        {
            if (_boxCount == _boxArray!.Length) Array.Resize(ref _boxArray, _boxArray.Length * 2);
            _boxArray[_boxCount++] = box;
        }

        private Box MakeBox(int start, int end)
        {
            var box = new Box
            {
                Start = start,
                End = end,
                MinR = 255,
                MinG = 255,
                MinB = 255,
                MinA = 255,
            };
            for (int k = start; k < end; k++)
            {
                ref var b = ref _binArray![k];
                int c = b.Count;
                box.Count += c;
                box.SumR += b.R * c;
                box.SumG += b.G * c;
                box.SumB += b.B * c;
                box.SumA += b.A * c;
                if (b.R < box.MinR) box.MinR = b.R;
                if (b.R > box.MaxR) box.MaxR = b.R;
                if (b.G < box.MinG) box.MinG = b.G;
                if (b.G > box.MaxG) box.MaxG = b.G;
                if (b.B < box.MinB) box.MinB = b.B;
                if (b.B > box.MaxB) box.MaxB = b.B;
                if (b.A < box.MinA) box.MinA = b.A;
                if (b.A > box.MaxA) box.MaxA = b.A;
            }
            return box;
        }

        private static long ScoreOf(in Box b)
        {
            long range = Math.Max(
                Math.Max((long)(b.MaxR - b.MinR) * 299, (long)(b.MaxG - b.MinG) * 587),
                Math.Max((long)(b.MaxB - b.MinB) * 114, (long)(b.MaxA - b.MinA) * 400));
            return range == 0 ? 0 : range * b.Count;
        }

        private static int PickChannel(in Box b)
        {
            long rr = (long)(b.MaxR - b.MinR) * 299;
            long gg = (long)(b.MaxG - b.MinG) * 587;
            long bb = (long)(b.MaxB - b.MinB) * 114;
            long aa = (long)(b.MaxA - b.MinA) * 400;
            if (gg >= rr && gg >= bb && gg >= aa) return 1;
            if (aa >= rr && aa >= bb) return 3;
            return rr >= bb ? 0 : 2;
        }

        /// <summary>
        /// 建立最近色查找结构：5bit RGB → 种子索引 + 每色的 K 近邻短表。
        /// 逐像素线性扫描 256 色太慢，「粗查表定种子 + 短表精修」把单次查询降到常数级。
        /// </summary>
        private void BuildSearchStructures()
        {
            int count = _palCount;
            _rgbLut ??= new int[RgbBins];
            _shortlist ??= new int[256 * ShortlistSize];

            byte[] pr = _palR!, pg = _palG!, pb = _palB!;
            int[] lut = _rgbLut;
            for (int cell = 0; cell < RgbBins; cell++)
            {
                int r = (((cell >> 10) & 31) << 3) | 4;
                int g = (((cell >> 5) & 31) << 3) | 4;
                int b = ((cell & 31) << 3) | 4;
                int best = 0;
                int bestD = int.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    int dr = r - pr[i], dg = g - pg[i], db = b - pb[i];
                    int d = 3 * dr * dr + 5 * dg * dg + 2 * db * db;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                lut[cell] = best;
            }

            for (int i = 0; i < count; i++)
            {
                int off = i * ShortlistSize;
                for (int k = 0; k < ShortlistSize; k++) _shortlist[off + k] = -1;
                for (int j = 0; j < count; j++)
                {
                    if (j == i) continue;
                    int d = PaletteDistance(j, i);
                    int pos = 0;
                    while (pos < ShortlistSize)
                    {
                        int at = _shortlist[off + pos];
                        if (at < 0 || d < PaletteDistance(at, i)) break;
                        pos++;
                    }
                    if (pos >= ShortlistSize) continue;
                    for (int t = ShortlistSize - 1; t > pos; t--) _shortlist[off + t] = _shortlist[off + t - 1];
                    _shortlist[off + pos] = j;
                }
            }
        }

        private int PaletteDistance(int i, int j)
        {
            int dr = _palR![i] - _palR[j];
            int dg = _palG![i] - _palG[j];
            int db = _palB![i] - _palB[j];
            int da = _palA![i] - _palA[j];
            return 3 * dr * dr + 5 * dg * dg + 2 * db * db + 4 * da * da;
        }

        private int FindNearest(int r, int g, int b, int a)
        {
            int seed = _rgbLut![((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)];
            int best = seed;
            int bestD = DistanceTo(r, g, b, a, seed);
            int off = seed * ShortlistSize;
            for (int k = 0; k < ShortlistSize; k++)
            {
                int cand = _shortlist![off + k];
                if (cand < 0) break;
                int d = DistanceTo(r, g, b, a, cand);
                if (d < bestD)
                {
                    bestD = d;
                    best = cand;
                }
            }
            return best;
        }

        private int DistanceTo(int r, int g, int b, int a, int idx)
        {
            int dr = r - _palR![idx];
            int dg = g - _palG![idx];
            int db = b - _palB![idx];
            int da = a - _palA![idx];
            return 3 * dr * dr + 5 * dg * dg + 2 * db * db + 4 * da * da;
        }

        private int BinOf(int r, int g, int b, int a)
        {
            int rgbKey = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            return _hasAlpha ? rgbKey * AlphaBins + (a >> 3) : rgbKey;
        }

        /// <summary>
        /// 无抖动映射：先为每个直方图桶求最近色，再逐像素 O(1) 查表。
        /// </summary>
        private void MapNearest(ReadOnlySpan<byte> rgba, byte[] indices, int pixels, bool reserveTransparent, QuantizeOptions options)
        {
            int[] lut = _lut!;
            int threshold = reserveTransparent ? options.TransparentAlphaThreshold : 0;
            int offset = reserveTransparent ? 1 : 0;

            for (int k = 0; k < _usedBinCount; k++)
            {
                int bin = _usedBins![k];
                if (lut[bin] >= 0) continue;
                ref var b = ref _binArray![k];
                lut[bin] = FindNearest(b.R, b.G, b.B, b.A);
            }

            for (int p = 0; p < pixels; p++)
            {
                int i = p * 4;
                byte a = rgba[i + 3];
                if (reserveTransparent && a < threshold)
                {
                    indices[p] = 0;
                    continue;
                }
                int bin = BinOf(rgba[i], rgba[i + 1], rgba[i + 2], a);
                int idx = lut[bin];
                if (idx < 0)
                {
                    idx = FindNearest(rgba[i], rgba[i + 1], rgba[i + 2], a);
                    lut[bin] = idx;
                }
                indices[p] = (byte)(idx + offset);
            }
        }

        /// <summary>
        /// Floyd–Steinberg 误差扩散映射：只扩散 RGB，alpha 保持原值（alpha 抖动会产生可见噪点）。
        /// </summary>
        private void MapWithDithering(
            ReadOnlySpan<byte> rgba,
            byte[] indices,
            int width,
            int height,
            bool reserveTransparent,
            QuantizeOptions options)
        {
            int[] lut = _lut!;
            int threshold = reserveTransparent ? options.TransparentAlphaThreshold : 0;
            int offset = reserveTransparent ? 1 : 0;
            double strength = Math.Clamp(options.DitherStrength, 0.0, 1.0);
            int w7 = (int)Math.Round(7 * strength);
            int w3 = (int)Math.Round(3 * strength);
            int w5 = (int)Math.Round(5 * strength);
            int w1 = (int)Math.Round(1 * strength);

            int[] cur = new int[(width + 2) * 3];
            int[] next = new int[(width + 2) * 3];

            for (int y = 0; y < height; y++)
            {
                int rowOff = y * width * 4;
                int idxOff = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = rowOff + x * 4;
                    byte a = rgba[i + 3];
                    if (reserveTransparent && a < threshold)
                    {
                        indices[idxOff + x] = 0;
                        continue;
                    }

                    int e = (x + 1) * 3;
                    int r = Clamp255(rgba[i] + ((cur[e] + 8) >> 4));
                    int g = Clamp255(rgba[i + 1] + ((cur[e + 1] + 8) >> 4));
                    int b = Clamp255(rgba[i + 2] + ((cur[e + 2] + 8) >> 4));

                    int key = BinOf(r, g, b, a);
                    int idx = lut[key];
                    if (idx < 0)
                    {
                        idx = FindNearest(r, g, b, a);
                        lut[key] = idx;
                    }
                    indices[idxOff + x] = (byte)(idx + offset);

                    int dr = r - _palR![idx];
                    int dg = g - _palG![idx];
                    int db = b - _palB![idx];

                    int right = (x + 2) * 3;
                    cur[right] += dr * w7;
                    cur[right + 1] += dg * w7;
                    cur[right + 2] += db * w7;

                    int down = x * 3;
                    next[down] += dr * w3;
                    next[down + 1] += dg * w3;
                    next[down + 2] += db * w3;
                    next[down + 3] += dr * w5;
                    next[down + 4] += dg * w5;
                    next[down + 5] += db * w5;
                    next[down + 6] += dr * w1;
                    next[down + 7] += dg * w1;
                    next[down + 8] += db * w1;
                }

                (cur, next) = (next, cur);
                Array.Clear(next);
            }
        }

        private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        private QuantizeResult Assemble(byte[] indices, bool reserveTransparent)
        {
            int offset = reserveTransparent ? 1 : 0;
            int count = _palCount + offset;
            byte[] rgb = new byte[count * 3];
            byte[] alpha = new byte[count];
            bool opaque = !reserveTransparent;

            if (reserveTransparent) alpha[0] = 0;
            for (int i = 0; i < _palCount; i++)
            {
                int dst = i + offset;
                rgb[dst * 3] = _palR![i];
                rgb[dst * 3 + 1] = _palG![i];
                rgb[dst * 3 + 2] = _palB![i];
                alpha[dst] = _palA![i];
                if (_palA![i] != 255) opaque = false;
            }

            return new QuantizeResult
            {
                PaletteRgb = rgb,
                PaletteAlpha = opaque ? null : alpha,
                Indices = indices,
                ColorCount = count,
            };
        }
    }
}
