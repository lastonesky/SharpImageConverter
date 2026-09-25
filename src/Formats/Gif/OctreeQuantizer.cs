using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// 八叉树颜色量化 + Bayer 有序抖动。
/// <para>
/// 与 <see cref="Quantizer"/>（Wu + Floyd–Steinberg 误差扩散）互为替代实现：
/// 本实现在 5-bit 直方图上把每个被占用的颜色立方喂入一棵深度为 5 的八叉树，
/// 再自底向上归约到 ≤256 片叶子得到调色板；像素映射走一张 5-bit → 调色板索引的 LUT，
/// 抖动则采用无依赖链的 Bayer 4×4 有序抖动（替代 Floyd–Steinberg 的误差扩散链）。
/// </para>
/// <para>
/// 速度主要来自两点：Bayer 抖动是逐像素独立的 O(1) 操作（无误差传播依赖链），
/// 且映射是一张只读 LUT 查表。代价是视觉风格与原实现不同，且输出**不**与原实现保持 MD5 一致
/// （这是有意为之：见项目约定「新方案不必和原实现保持输出 MD5 相同」）。
/// </para>
/// </summary>
public sealed class OctreeQuantizer
{
    private const int MaxColors = 256;
    private const int Bits = 5;                       // 八叉树深度 / 直方图精度（每通道 5-bit）
    private const int Size = 1 << Bits;               // 32
    private const int HistVolume = Size * Size * Size; // 32768

    /// <summary>
    /// 低于该像素数走单线程直方图（并行固定开销不划算）；阈值随核数抬高，与 <see cref="Quantizer"/> 同策略。
    /// </summary>
    private static readonly int HistParallelPixelThreshold =
        60_000 * Environment.ProcessorCount + 300_000;

    /// <summary>
    /// Bayer 4×4 有序抖动矩阵，归一化到 [-0.5, 0.5) 区间使用。
    /// </summary>
    private static readonly byte[,] Bayer4 =
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };

    /// <summary>
    /// Bayer 抖动幅度，单位与 0-255 通道值同量级。默认 8。
    /// <para>
    /// 本量化器的输出在 5-bit 立方上分段恒定，即每通道量化步长 <c>q = 8</c>。
    /// 有序抖动的正确幅度是「一个步长」（峰峰值 q，即 ±q/2）：此时相邻像素按比例落在
    /// 上下两个色阶上，块平均恰好还原原色，色带被打散且颗粒最小。
    /// </para>
    /// <para>
    /// 调大不会进一步减少色带（实测 S=48 与 S=8 的色阶跳变几乎相同），只会线性放大颗粒，
    /// 并让 4×4 抖动网格变成强周期图案 —— 缩放时用最近邻采样的看图软件会把它
    /// 走样成摩尔纹。若要更强的纹理感请改用非周期阈值矩阵，而不是加大这里的幅度。
    /// </para>
    /// </summary>
    public int DitherStrength { get; set; } = 8;

    // 5-bit 直方图（每立方累计像素数与 RGB 和，用于取该立方代表色 = 均值）
    private readonly int[] _histCount = new int[HistVolume];
    private readonly long[] _histR = new long[HistVolume];
    private readonly long[] _histG = new long[HistVolume];
    private readonly long[] _histB = new long[HistVolume];

    // 八叉树节点池
    private sealed class Node
    {
        public int[] Children = new int[8]; // 子节点在 _nodes 中的下标；-1 表示无
        public int Parent = -1;
        public int PixelCount;
        public long RSum, GSum, BSum;
        public int Level;
        public bool IsLeaf;
        public int PaletteIndex;
        public int LeafCount;        // 子树内的叶子数
        public bool InReducible;     // 是否已在可归约列表中
        public int NearestLeafIndex; // 安全回退：任一可达叶子的调色板索引（见 BuildMapLut 注释）
    }

    private readonly List<Node> _nodes = new();
    private readonly List<int>[] _reducible = new List<int>[Bits];
    private readonly byte[] _mapLut = new byte[HistVolume]; // 5-bit 立方 → 调色板索引
    private int _leafCount;

    public OctreeQuantizer()
    {
        for (int i = 0; i < Bits; i++)
            _reducible[i] = new List<int>();
    }

    /// <summary>
    /// 对 RGB24 像素进行八叉树量化并（可选）Bayer 抖动，生成 ≤256 色调色板与像素索引。
    /// </summary>
    public static (byte[] Palette, byte[] Indices) Quantize(byte[] pixels, int width, int height, bool enableDithering = true, int ditherStrength = 8)
    {
        var q = new OctreeQuantizer { DitherStrength = ditherStrength };
        return q.QuantizeInternal(pixels.AsSpan(), width, height, enableDithering);
    }

    /// <summary>
    /// 对 RGB24 像素（ReadOnlySpan，适合 NativeBufferOwner 等非托管源）进行量化。
    /// </summary>
    public static (byte[] Palette, byte[] Indices) Quantize(ReadOnlySpan<byte> pixels, int width, int height, bool enableDithering = true, int ditherStrength = 8)
    {
        var q = new OctreeQuantizer { DitherStrength = ditherStrength };
        return q.QuantizeInternal(pixels, width, height, enableDithering);
    }

    private unsafe (byte[] Palette, byte[] Indices) QuantizeInternal(ReadOnlySpan<byte> pixels, int width, int height, bool enableDithering)
    {
        if (pixels.IsEmpty)
            return (Array.Empty<byte>(), Array.Empty<byte>());
        fixed (byte* p = &MemoryMarshal.GetReference(pixels))
        {
            return QuantizeInternalPtr(p, pixels.Length, width, height, enableDithering);
        }
    }

    private unsafe (byte[] Palette, byte[] Indices) QuantizeInternalPtr(byte* pixels, int pixelLength, int width, int height, bool enableDithering)
    {
        BuildHistogram(pixels, pixelLength);

        // 构建八叉树：每个被占用的 5-bit 直方立方对应一个深度 Bits 的叶子
        _nodes.Clear();
        for (int i = 0; i < Bits; i++)
            _reducible[i].Clear();
        _leafCount = 0;

        var root = new Node { Level = 0, Parent = -1, Children = new int[8] };
        for (int k = 0; k < 8; k++) root.Children[k] = -1;
        _nodes.Add(root);

        for (int bin = 0; bin < HistVolume; bin++)
        {
            int count = _histCount[bin];
            if (count == 0) continue;
            int r = (int)(_histR[bin] / count);
            int g = (int)(_histG[bin] / count);
            int b = (int)(_histB[bin] / count);
            Insert(r, g, b, count);
        }

        // 归约到 ≤256 片叶子
        Reduce();

        // 生成调色板，并给每片【从根可达】的叶子编号。
        // 注意：归约（Prune）把某内部节点合并成叶子时，其原有的深度 5 子节点会变成
        // 孤儿节点（不再从根可达，但仍停留在 _nodes 里且 IsLeaf==true）。
        // 若按「遍历全部 _nodes」来数叶子，pIdx 会超过 _leafCount，导致 palette 越界；
        // 因此必须从根做可达性遍历，只给真正可达的叶子编号。
        byte[] palette = BuildPalette();

        // 预计算每个 5-bit 立方对应的调色板索引（供逐像素 O(1) 查表）
        BuildMapLut();

        byte[] indices = enableDithering
            ? MapWithBayer(pixels, width, height)
            : MapDirect(pixels, width, height);

        return (palette, indices);
    }

    /// <summary>
    /// 从根出发做可达性遍历，只为【可达】的叶子生成调色板项并编号。
    /// 这样 palette 的长度恒等于 <see cref="_leafCount"/>，且每个可达叶子的
    /// <see cref="Node.PaletteIndex"/> 在 <see cref="BuildMapLut"/> 之前已就绪。
    /// 孤儿节点（被剪枝节点的原深度 5 子节点）不会被访问，也不会污染调色板。
    /// </summary>
    private byte[] BuildPalette()
    {
        byte[] palette = new byte[_leafCount * 3];
        int pIdx = 0;
        var stack = new Stack<int>();
        stack.Push(0); // 根
        while (stack.Count > 0)
        {
            int ni = stack.Pop();
            var n = _nodes[ni];
            if (n.IsLeaf)
            {
                int r = (int)(n.RSum / n.PixelCount);
                int g = (int)(n.GSum / n.PixelCount);
                int b = (int)(n.BSum / n.PixelCount);
                palette[pIdx * 3] = (byte)r;
                palette[pIdx * 3 + 1] = (byte)g;
                palette[pIdx * 3 + 2] = (byte)b;
                n.PaletteIndex = pIdx;
                pIdx++;
            }
            else
            {
                for (int c = 0; c < 8; c++)
                {
                    int ch = n.Children[c];
                    if (ch != -1) stack.Push(ch);
                }
            }
        }
        // pIdx 应等于 _leafCount（可达叶子数）。若不一致说明叶子计数逻辑有 bug。
        System.Diagnostics.Debug.Assert(pIdx == _leafCount,
            $"BuildPalette: pIdx={pIdx} != _leafCount={_leafCount}");
        return palette;
    }

    private unsafe void BuildHistogram(byte* pixels, int pixelLength)
    {
        int len = pixelLength / 3;
        if (len < HistParallelPixelThreshold)
        {
            Array.Clear(_histCount, 0, HistVolume);
            Array.Clear(_histR, 0, HistVolume);
            Array.Clear(_histG, 0, HistVolume);
            Array.Clear(_histB, 0, HistVolume);
            for (int i = 0; i < len; i++)
            {
                int o = i * 3;
                int r = pixels[o], g = pixels[o + 1], b = pixels[o + 2];
                int bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                _histCount[bin]++;
                _histR[bin] += r;
                _histG[bin] += g;
                _histB[bin] += b;
            }
            return;
        }

        // 并行：每线程独立直方图，最后串行合并
        int threadCount = Math.Min(Environment.ProcessorCount, Math.Max(1, len));
        var tCount = new int[threadCount][];
        var tR = new long[threadCount][];
        var tG = new long[threadCount][];
        var tB = new long[threadCount][];
        for (int t = 0; t < threadCount; t++)
        {
            tCount[t] = new int[HistVolume];
            tR[t] = new long[HistVolume];
            tG[t] = new long[HistVolume];
            tB[t] = new long[HistVolume];
        }

        int block = len / threadCount;
        Parallel.For(0, threadCount, t =>
        {
            var cc = tCount[t];
            var rr = tR[t];
            var gg = tG[t];
            var bb = tB[t];
            int start = t * block;
            int end = (t == threadCount - 1) ? len : (t + 1) * block;
            for (int i = start; i < end; i++)
            {
                int o = i * 3;
                int r = pixels[o], g = pixels[o + 1], b = pixels[o + 2];
                int bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                cc[bin]++;
                rr[bin] += r;
                gg[bin] += g;
                bb[bin] += b;
            }
        });

        Array.Clear(_histCount, 0, HistVolume);
        Array.Clear(_histR, 0, HistVolume);
        Array.Clear(_histG, 0, HistVolume);
        Array.Clear(_histB, 0, HistVolume);
        for (int t = 0; t < threadCount; t++)
        {
            var cc = tCount[t];
            var rr = tR[t];
            var gg = tG[t];
            var bb = tB[t];
            for (int bin = 0; bin < HistVolume; bin++)
            {
                _histCount[bin] += cc[bin];
                _histR[bin] += rr[bin];
                _histG[bin] += gg[bin];
                _histB[bin] += bb[bin];
            }
        }
    }

    private void Insert(int r, int g, int b, int count)
    {
        int node = 0; // 根（下标 0）
        for (int level = 0; level < Bits; level++)
        {
            var n = _nodes[node];
            // 沿路径累加子树和（含当前像素）
            n.PixelCount += count;
            n.RSum += (long)r * count;
            n.GSum += (long)g * count;
            n.BSum += (long)b * count;

            int shift = 7 - level; // 取第 (7-level) 位（自高到低）
            int idx = (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);

            if (level == Bits - 1)
            {
                // 叶子：深度 Bits
                if (n.Children[idx] == -1)
                {
                    var leaf = new Node
                    {
                        Level = Bits,
                        Parent = node,
                        IsLeaf = true,
                        PixelCount = count,
                        RSum = (long)r * count,
                        GSum = (long)g * count,
                        BSum = (long)b * count,
                        LeafCount = 1,
                        Children = new int[8],
                    };
                    for (int k = 0; k < 8; k++) leaf.Children[k] = -1;
                    _nodes.Add(leaf);
                    n.Children[idx] = _nodes.Count - 1;
                    n.LeafCount += 1;
                    _leafCount++;
                    MarkReducible(node);
                    AddAncestorLeaf(node, 1);
                }
                else
                {
                    // 同一 5-bit 立方（理论上不会重复，防御性累加）
                    var leaf = _nodes[n.Children[idx]];
                    leaf.PixelCount += count;
                    leaf.RSum += (long)r * count;
                    leaf.GSum += (long)g * count;
                    leaf.BSum += (long)b * count;
                }
                return;
            }

            int next;
            if (n.Children[idx] == -1)
            {
                var child = new Node
                {
                    Level = level + 1,
                    Parent = node,
                    Children = new int[8],
                };
                for (int k = 0; k < 8; k++) child.Children[k] = -1;
                _nodes.Add(child);
                next = _nodes.Count - 1;
                n.Children[idx] = next;
                MarkReducible(node);
            }
            else
            {
                next = n.Children[idx];
            }
            node = next;
        }
    }

    private void MarkReducible(int node)
    {
        var n = _nodes[node];
        if (n.IsLeaf || n.InReducible) return;
        _reducible[n.Level].Add(node);
        n.InReducible = true;
    }

    private void AddAncestorLeaf(int node, int delta)
    {
        int p = _nodes[node].Parent;
        while (p != -1)
        {
            _nodes[p].LeafCount += delta;
            p = _nodes[p].Parent;
        }
    }

    /// <summary>
    /// 自底向上归约：反复把「最深一层里像素数最少」的可归约节点剪枝（合并其全部子树叶），
    /// 直到叶子数 ≤ <see cref="MaxColors"/>。每个内部节点已在插入时累计了整棵子树的
    /// 像素和与 RGB 和，故剪枝后其代表色（RSum/PixelCount）自然就是合并后的均值，无需重新累加。
    /// </summary>
    private void Reduce()
    {
        while (_leafCount > MaxColors)
        {
            int node = FindReducible();
            if (node < 0) break;
            Prune(node);
        }
    }

    private int FindReducible()
    {
        for (int level = Bits - 1; level >= 0; level--)
        {
            var list = _reducible[level];
            int best = -1;
            int bestCount = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                int idx = list[i];
                var n = _nodes[idx];
                if (n.IsLeaf) continue;           // 已剪枝成叶子，跳过
                if (n.PixelCount < bestCount)
                {
                    bestCount = n.PixelCount;
                    best = idx;
                }
            }
            if (best >= 0) return best;
        }
        return -1;
    }

    private void Prune(int nodeIdx)
    {
        var n = _nodes[nodeIdx];
        int removedLeaves = n.LeafCount;
        for (int c = 0; c < 8; c++)
            n.Children[c] = -1;
        n.IsLeaf = true;
        n.InReducible = false;
        n.LeafCount = 1;
        _leafCount += 1 - removedLeaves;

        // 祖先的叶子计数同步变化（removedLeaves 片 -> 1 片）
        int delta = 1 - removedLeaves;
        int p = n.Parent;
        while (p != -1)
        {
            _nodes[p].LeafCount += delta;
            p = _nodes[p].Parent;
        }
    }

    /// <summary>
    /// 为每个 5-bit 立方（共 32768 个）确定其归属的调色板索引，写入 <see cref="_mapLut"/>。
    /// 叶子直接取自身索引；内部节点取「第一个非空子节点」的叶子索引作为安全回退。
    /// <para>
    /// 注意：这个回退值并不是几何意义上最近的调色板色，只是子树内任意一片叶子。
    /// 它只用于未被任何像素占用的空立方（Bayer 偏移把像素推到空立方时才会命中，
    /// 143 MP 实测约 0.24% 像素）。若要更准，应改为按立方中心色在调色板里找最近色。
    /// </para>
    /// </summary>
    private void BuildMapLut()
    {
        // 反向遍历：子节点下标恒大于父节点，故先算好叶子/子节点再算父节点
        for (int i = _nodes.Count - 1; i >= 0; i--)
        {
            var n = _nodes[i];
            if (n.IsLeaf)
            {
                n.NearestLeafIndex = n.PaletteIndex;
            }
            else
            {
                for (int c = 0; c < 8; c++)
                {
                    int ch = n.Children[c];
                    if (ch != -1)
                    {
                        n.NearestLeafIndex = _nodes[ch].NearestLeafIndex;
                        break;
                    }
                }
            }
        }

        for (int r5 = 0; r5 < Size; r5++)
        {
            for (int g5 = 0; g5 < Size; g5++)
            {
                for (int b5 = 0; b5 < Size; b5++)
                {
                    _mapLut[(r5 << 10) | (g5 << 5) | b5] = (byte)Walk(r5, g5, b5);
                }
            }
        }
    }

    private int Walk(int cubeR5, int cubeG5, int cubeB5)
    {
        int node = 0;
        for (int level = 0; level < Bits; level++)
        {
            var n = _nodes[node];
            if (n.IsLeaf) return n.PaletteIndex;
            int shift = (Bits - 1) - level; // 4 - level
            int idx = (((cubeR5 >> shift) & 1) << 2) | (((cubeG5 >> shift) & 1) << 1) | ((cubeB5 >> shift) & 1);
            int child = n.Children[idx];
            if (child == -1) return n.NearestLeafIndex;
            node = child;
        }
        return _nodes[node].NearestLeafIndex;
    }

    private static int CubeIndex(int r, int g, int b) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

    private unsafe byte[] MapDirect(byte* pixels, int width, int height)
    {
        byte[] indices = new byte[width * height];
        int len = width * height;
        for (int i = 0; i < len; i++)
        {
            int o = i * 3;
            indices[i] = _mapLut[CubeIndex(pixels[o], pixels[o + 1], pixels[o + 2])];
        }
        return indices;
    }

    private unsafe byte[] MapWithBayer(byte* pixels, int width, int height)
    {
        byte[] indices = new byte[width * height];
        if (Ssse3.IsSupported && Sse2.IsSupported)
            return MapWithBayerSimd(pixels, width, height, indices);
        return MapWithBayerScalar(pixels, width, height, indices);
    }

    /// <summary>
    /// 标量回退路径（无 SSSE3 时使用）。把「浮点偏移 + clamp + (int)截断 + &gt;&gt;3」
    /// 预计算成 16 相位 × 256 项的一张 5-bit 表，逐像素只做 3 次查表 + 移位 + 1 次 LUT 查表。
    /// <para>
    /// 与原先的浮点写法逐位等价：t*strength = 3k-22.5（k 为 Bayer 矩阵值、strength=48），
    /// 故 (int)clamp(v + t*48, 0, 255) ≡ clamp(v + 3k - 23, 0, 255)。实测产物 md5 一致。
    /// 相对浮点版：143 MP 图 map 阶段 553 → 215 ms（quantize 中位）。
    /// </para>
    /// </summary>
    private unsafe byte[] MapWithBayerScalar(byte* pixels, int width, int height, byte[] indices)
    {
        byte[] d5 = BuildDither5Table();
        Span<int> pbase = stackalloc int[16];
        for (int ym = 0; ym < 4; ym++)
            for (int xm = 0; xm < 4; xm++)
                pbase[ym * 4 + xm] = Bayer4[ym, xm] * 256;

        int w4 = width & ~3;
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            int y4 = (y & 3) * 4;
            int b0 = pbase[y4], b1 = pbase[y4 + 1], b2 = pbase[y4 + 2], b3 = pbase[y4 + 3];
            int o = rowBase * 3;
            int x = 0;
            for (; x < w4; x += 4)
            {
                indices[rowBase + x] = _mapLut[(d5[b0 + pixels[o]] << 10) | (d5[b0 + pixels[o + 1]] << 5) | d5[b0 + pixels[o + 2]]];
                indices[rowBase + x + 1] = _mapLut[(d5[b1 + pixels[o + 3]] << 10) | (d5[b1 + pixels[o + 4]] << 5) | d5[b1 + pixels[o + 5]]];
                indices[rowBase + x + 2] = _mapLut[(d5[b2 + pixels[o + 6]] << 10) | (d5[b2 + pixels[o + 7]] << 5) | d5[b2 + pixels[o + 8]]];
                indices[rowBase + x + 3] = _mapLut[(d5[b3 + pixels[o + 9]] << 10) | (d5[b3 + pixels[o + 10]] << 5) | d5[b3 + pixels[o + 11]]];
                o += 12;
            }
            for (; x < width; x++)
            {
                int pb = pbase[y4 + (x & 3)];
                indices[rowBase + x] = _mapLut[(d5[pb + pixels[o]] << 10) | (d5[pb + pixels[o + 1]] << 5) | d5[pb + pixels[o + 2]]];
                o += 3;
            }
        }
        return indices;
    }

    /// <summary>
    /// 第 k 个 Bayer 相位对应的整数偏移。浮点原式为 (int)clamp(v + t*strength, 0, 255)，
    /// t = (k-7.5)/16；因 v 为整数，floor(v + x) = v + floor(x)，故等价于
    /// clamp(v + floor(strength*(2k-15)/32), 0, 255)。strength=48 时即 3k-23。
    /// </summary>
    private int DitherOffset(int k) => (int)Math.Floor(DitherStrength * (2 * k - 15) / 32.0);

    /// <summary>
    /// 相位表：d5[k*256 + v] = clamp(v + DitherOffset(k), 0, 255) &gt;&gt; 3。
    /// </summary>
    private byte[] BuildDither5Table()
    {
        byte[] d5 = new byte[16 * 256];
        for (int k = 0; k < 16; k++)
        {
            int off = DitherOffset(k);
            int src = k * 256;
            for (int v = 0; v < 256; v++)
            {
                int w = v + off;
                if (w < 0) w = 0; else if (w > 255) w = 255;
                d5[src + v] = (byte)(w >> 3);
            }
        }
        return d5;
    }

    // RGB24 → 三个 16 字节平面的解交织掩码（0x80 位置置零）
    private static readonly Vector128<byte> ShufR0 = Vector128.Create((byte)0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> ShufR1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> ShufR2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1, 4, 7, 10, 13);
    private static readonly Vector128<byte> ShufG0 = Vector128.Create((byte)1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> ShufG1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> ShufG2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 5, 8, 11, 14);
    private static readonly Vector128<byte> ShufB0 = Vector128.Create((byte)2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> ShufB1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> ShufB2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 3, 6, 9, 12, 15);

    /// <summary>
    /// SSSE3 路径：一次处理 16 像素（48 字节）。pshufb 解交织出 R/G/B 三个平面，
    /// 在 16 位通道内做「加偏移 + pminsw/pmaxsw 饱和 + psrlw 3」得到 5-bit 分量，
    /// 合成 16 个 cube 下标后仍用标量查 <see cref="_mapLut"/>——
    /// 字节表 + 15 位下标的 gather 在 AVX2 下没有可用的向量指令（见 docs/PerfReport.md）。
    /// <para>
    /// Bayer 抖动逐像素独立、无跨像素依赖链，因此这里是吞吐受限而非依赖链受限，
    /// SIMD 有效；这与 Floyd–Steinberg（串行误差扩散链）的结论相反。
    /// 相对标量整数版：143 MP 图 quantize 中位 215 → 111 ms。
    /// </para>
    /// </summary>
    private unsafe byte[] MapWithBayerSimd(byte* pixels, int width, int height, byte[] indices)
    {
        var offTab = new short[4][];
        for (int ym = 0; ym < 4; ym++)
        {
            var a = new short[16];
            for (int j = 0; j < 16; j++) a[j] = (short)DitherOffset(Bayer4[ym, j & 3]);
            offTab[ym] = a;
        }
        Vector128<byte> zero = Vector128<byte>.Zero;
        Vector128<short> c255 = Vector128.Create((short)255);
        Vector128<short> c0 = Vector128.Create((short)0);
        ushort* buf = stackalloc ushort[16];

        int w16 = width & ~15;
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            int o = rowBase * 3;
            fixed (short* op = offTab[y & 3])
            {
                Vector128<short> offLo = Sse2.LoadVector128(op);
                Vector128<short> offHi = Sse2.LoadVector128(op + 8);
                int x = 0;
                for (; x < w16; x += 16)
                {
                    var v0 = Sse2.LoadVector128(pixels + o);
                    var v1 = Sse2.LoadVector128(pixels + o + 16);
                    var v2 = Sse2.LoadVector128(pixels + o + 32);
                    var R = Sse2.Or(Sse2.Or(Ssse3.Shuffle(v0, ShufR0), Ssse3.Shuffle(v1, ShufR1)), Ssse3.Shuffle(v2, ShufR2));
                    var G = Sse2.Or(Sse2.Or(Ssse3.Shuffle(v0, ShufG0), Ssse3.Shuffle(v1, ShufG1)), Ssse3.Shuffle(v2, ShufG2));
                    var B = Sse2.Or(Sse2.Or(Ssse3.Shuffle(v0, ShufB0), Ssse3.Shuffle(v1, ShufB1)), Ssse3.Shuffle(v2, ShufB2));

                    var rl = Sse2.ShiftRightLogical(Sse2.Max(Sse2.Min(Sse2.Add(Sse2.UnpackLow(R, zero).AsInt16(), offLo), c255), c0), 3);
                    var rh = Sse2.ShiftRightLogical(Sse2.Max(Sse2.Min(Sse2.Add(Sse2.UnpackHigh(R, zero).AsInt16(), offHi), c255), c0), 3);
                    var gl = Sse2.ShiftRightLogical(Sse2.Max(Sse2.Min(Sse2.Add(Sse2.UnpackLow(G, zero).AsInt16(), offLo), c255), c0), 3);
                    var gh = Sse2.ShiftRightLogical(Sse2.Max(Sse2.Min(Sse2.Add(Sse2.UnpackHigh(G, zero).AsInt16(), offHi), c255), c0), 3);
                    var bl = Sse2.ShiftRightLogical(Sse2.Max(Sse2.Min(Sse2.Add(Sse2.UnpackLow(B, zero).AsInt16(), offLo), c255), c0), 3);
                    var bh = Sse2.ShiftRightLogical(Sse2.Max(Sse2.Min(Sse2.Add(Sse2.UnpackHigh(B, zero).AsInt16(), offHi), c255), c0), 3);

                    var clo = Sse2.Or(Sse2.Or(Sse2.ShiftLeftLogical(rl, 10), Sse2.ShiftLeftLogical(gl, 5)), bl);
                    var chi = Sse2.Or(Sse2.Or(Sse2.ShiftLeftLogical(rh, 10), Sse2.ShiftLeftLogical(gh, 5)), bh);
                    Sse2.Store((byte*)buf, clo.AsByte());
                    Sse2.Store((byte*)(buf + 8), chi.AsByte());

                    for (int j = 0; j < 16; j++) indices[rowBase + x + j] = _mapLut[buf[j]];
                    o += 48;
                }
                for (; x < width; x++)
                {
                    int off = DitherOffset(Bayer4[y & 3, x & 3]);
                    int r = Math.Clamp(pixels[o] + off, 0, 255) >> 3;
                    int g = Math.Clamp(pixels[o + 1] + off, 0, 255) >> 3;
                    int b = Math.Clamp(pixels[o + 2] + off, 0, 255) >> 3;
                    indices[rowBase + x] = _mapLut[(r << 10) | (g << 5) | b];
                    o += 3;
                }
            }
        }
        return indices;
    }
}
