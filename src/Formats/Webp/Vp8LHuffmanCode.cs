using System;
using System.Collections.Generic;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8L 的规范 Huffman 码表（编码端）。
/// 主符号表码长上限 15 位、码长码（meta）表上限 7 位；常规 Huffman 树若超限，
/// 用"折叠 + 再平衡"压到上限内，仍保证 Kraft 和恰为 1、位流合法，代价只是压缩率略降。
/// 码字按规范意义（MSB-first）保存在 <see cref="Codes"/>，由 <see cref="LsbBitWriter.WriteCode"/> 写出。
/// </summary>
internal sealed class Vp8LHuffmanCode
{
    private const int MaxCodeLength = 15;

    /// <summary>码长码（meta）表的码长在位流中以 3 bit 字段写出，因此上限为 7。</summary>
    private const int MaxCodeLengthCodeBits = 7;

    /// <summary>字母表大小。</summary>
    public int AlphabetSize { get; }

    /// <summary>每个符号的码长，0 表示该符号不参与本表。</summary>
    public int[] Lengths { get; }

    /// <summary>每个符号的规范码字（MSB-first），码长为 0 的符号值为 0。</summary>
    public int[] Codes { get; }

    /// <summary>表中唯一的启用符号；-1 表示多个符号（正常解码）。</summary>
    public int SingleSymbol { get; }

    private Vp8LHuffmanCode(int alphabetSize, int[] lengths, int[] codes, int singleSymbol)
    {
        AlphabetSize = alphabetSize;
        Lengths = lengths;
        Codes = codes;
        SingleSymbol = singleSymbol;
    }

    /// <summary>按符号频次构造码表；频次全 0 时退化为符号 0 的单符号表。</summary>
    public static Vp8LHuffmanCode Build(int[] frequencies, int alphabetSize)
        => Build(frequencies, alphabetSize, MaxCodeLength);

    /// <summary>
    /// 按符号频次构造码表，并把码长限制在 <paramref name="maxCodeLength"/> 以内。
    /// 主符号表的规范上限是 15 位；码长码（meta）表以 3 bit 字段写出，上限是 7 位。
    /// </summary>
    public static Vp8LHuffmanCode Build(int[] frequencies, int alphabetSize, int maxCodeLength)
    {
        if (frequencies.Length < alphabetSize)
            throw new ArgumentException("频次数组短于字母表大小", nameof(frequencies));

        var active = new List<int>();
        for (int i = 0; i < alphabetSize; i++)
        {
            if (frequencies[i] > 0) active.Add(i);
        }

        int[] lengths = new int[alphabetSize];
        if (active.Count == 0)
        {
            lengths[0] = 1;
            return new Vp8LHuffmanCode(alphabetSize, lengths, BuildCanonicalCodes(lengths, alphabetSize), 0);
        }
        if (active.Count == 1)
        {
            lengths[active[0]] = 1;
            return new Vp8LHuffmanCode(alphabetSize, lengths, BuildCanonicalCodes(lengths, alphabetSize), active[0]);
        }

        // 常规 Huffman 树（节点 id：0..n-1 为叶，之后为内部节点）。
        int n = active.Count;
        long[] weights = new long[2 * n];
        int[] parents = new int[2 * n];
        for (int i = 0; i < 2 * n; i++) parents[i] = -1;

        // 以 (权重, 节点 id) 作优先级，保证同权重时的次序稳定（不影响合法性，只影响长度分配）。
        var comparer = Comparer<(long Weight, int Id)>.Create(static (a, b) =>
        {
            int byWeight = a.Weight.CompareTo(b.Weight);
            return byWeight != 0 ? byWeight : a.Id.CompareTo(b.Id);
        });
        var queue = new PriorityQueue<int, (long, int)>(comparer);
        for (int i = 0; i < n; i++)
        {
            weights[i] = frequencies[active[i]];
            queue.Enqueue(i, (weights[i], i));
        }

        int nodeCount = n;
        while (queue.Count > 1)
        {
            int a = queue.Dequeue();
            int b = queue.Dequeue();
            int id = nodeCount++;
            weights[id] = weights[a] + weights[b];
            parents[a] = id;
            parents[b] = id;
            queue.Enqueue(id, (weights[id], id));
        }
        _ = queue.Dequeue();

        int maxDepth = 0;
        for (int i = 0; i < n; i++)
        {
            int depth = 0;
            for (int node = i; parents[node] >= 0; node = parents[node]) depth++;
            lengths[active[i]] = depth;
            if (depth > maxDepth) maxDepth = depth;
        }

        if (maxDepth > maxCodeLength)
        {
            LimitCodeLengths(frequencies, active, lengths, maxCodeLength);
        }

        return new Vp8LHuffmanCode(alphabetSize, lengths, BuildCanonicalCodes(lengths, alphabetSize), -1);
    }

    /// <summary>
    /// 把过深的 Huffman 码长压到 <paramref name="maxCodeLength"/> 以内。
    ///
    /// 用 **package-merge** 求「码长受限时的最优前缀码」（Larmore-Hirschberg / Katajainen），
    /// 在 n 个符号、上限 L 位时复杂度 O(n·L)，对 280/536 符号的字母表只有几十微秒。
    /// 结果是严格最优的（加权码长最小），且天然满足 Kraft 和 = 1。
    /// </summary>
    /// <remarks>
    /// 历史：此前这里是「折叠 + 再平衡」的手写近似，数值校验经常失败、退化成扁平码
    /// （所有符号 9~10 位），实测让照片类无损体积虚高 60%+（peppers：理想熵 17.7 Mbit，
    /// 实际写出 29.1 Mbit）。package-merge 直接消除这类退化。
    /// </remarks>
    private static void LimitCodeLengths(int[] frequencies, List<int> active, int[] lengths,
                                         int maxCodeLength)
    {
        int n = active.Count;
        if (n <= 2 || maxCodeLength < 1) { BuildFlatLengths(frequencies, active, lengths); return; }

        // 叶子按（频次，符号）升序，package-merge 要求输入有序。
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = active[i];
        Array.Sort(order, (a, b) =>
        {
            int byFreq = frequencies[a].CompareTo(frequencies[b]);
            return byFreq != 0 ? byFreq : a.CompareTo(b);
        });

        // 节点池：叶子 n 个，之后每一层最多再添 (n-1) 个打包节点，共 maxCodeLength 层。
        int capacity = n * (maxCodeLength + 1) + 8;
        var nodeWeight = new long[capacity];
        var nodeLeft = new int[capacity];
        var nodeRight = new int[capacity];
        var nodeSymbol = new int[capacity];
        int nodeCount = 0;

        int AddNode(long weight, int left, int right, int symbol)
        {
            if (nodeCount == capacity) throw new InvalidOperationException("package-merge 节点池溢出");
            int id = nodeCount++;
            nodeWeight[id] = weight;
            nodeLeft[id] = left;
            nodeRight[id] = right;
            nodeSymbol[id] = symbol;
            return id;
        }

        var current = new List<int>(n);
        for (int i = 0; i < n; i++)
            current.Add(AddNode(frequencies[order[i]], -1, -1, order[i]));

        int keep = 2 * n - 2;
        for (int level = 1; level < maxCodeLength; level++)
        {
            // 相邻两项打包：输入有序 ⇒ 打包结果也有序。
            var packages = new List<int>(current.Count / 2);
            for (int i = 0; i + 1 < current.Count; i += 2)
            {
                packages.Add(AddNode(nodeWeight[current[i]] + nodeWeight[current[i + 1]],
                    current[i], current[i + 1], -1));
            }

            var merged = new List<int>(keep);
            int leaf = 0, package = 0;
            while (merged.Count < keep && (leaf < n || package < packages.Count))
            {
                if (package >= packages.Count ||
                    (leaf < n && nodeWeight[leaf] <= nodeWeight[packages[package]]))
                    merged.Add(leaf++);
                else
                    merged.Add(packages[package++]);
            }
            current = merged;
        }

        // 取最小的 2n-2 项，某符号被覆盖多少次，码长就是多少。
        System.Array.Clear(lengths, 0, lengths.Length);
        int take = Math.Min(keep, current.Count);
        var stack = new Stack<int>();
        for (int i = 0; i < take; i++)
        {
            stack.Clear();
            stack.Push(current[i]);
            while (stack.Count > 0)
            {
                int id = stack.Pop();
                if (nodeSymbol[id] >= 0)
                {
                    lengths[nodeSymbol[id]]++;
                    continue;
                }
                stack.Push(nodeLeft[id]);
                stack.Push(nodeRight[id]);
            }
        }

        // 数值校验：Kraft 和必须恰为 1、叶子数不变、码长不超限，否则退回扁平码。
        long units = 0;
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            int len = lengths[order[i]];
            if (len <= 0 || len > maxCodeLength) { BuildFlatLengths(frequencies, active, lengths); return; }
            units += 1L << (maxCodeLength - len);
            total++;
        }
        if (units != 1L << maxCodeLength || total != n) BuildFlatLengths(frequencies, active, lengths);
    }

    /// <summary>
    /// 扁平完备码：深度 D = ceil(log2(活跃符号数))，其中 2^D - 活跃数 个最高频符号取 D-1 位，
    /// 其余取 D 位。Kraft 和恰为 1，仅在限长再平衡的数值校验失败时作为兜底。
    /// </summary>
    private static void BuildFlatLengths(int[] frequencies, List<int> active, int[] lengths)
    {
        active.Sort((a, b) =>
        {
            int byFreq = frequencies[b].CompareTo(frequencies[a]);
            return byFreq != 0 ? byFreq : a.CompareTo(b);
        });

        int depth = 1;
        while ((1 << depth) < active.Count) depth++;
        int shortCount = (1 << depth) - active.Count;

        for (int i = 0; i < active.Count; i++)
        {
            lengths[active[i]] = i < shortCount ? depth - 1 : depth;
        }
    }

    /// <summary>
    /// 规范码字分配：按码长递增、同长按符号升序依次分配，与解码端 <see cref="HuffmanTable"/> 一致。
    /// </summary>
    private static int[] BuildCanonicalCodes(int[] lengths, int alphabetSize)
    {
        var codes = new int[alphabetSize];
        int maxLength = 0;
        for (int i = 0; i < alphabetSize; i++)
        {
            if (lengths[i] > maxLength) maxLength = lengths[i];
        }

        int code = 0;
        for (int length = 1; length <= maxLength; length++)
        {
            for (int symbol = 0; symbol < alphabetSize; symbol++)
            {
                if (lengths[symbol] == length) codes[symbol] = code++;
            }
            code <<= 1;
        }
        return codes;
    }

    /// <summary>
    /// 把码表写入 VP8L 位流：活跃符号 ≤ 2 时用 simple code，否则用标准码长表 + RLE。
    /// </summary>
    public void WriteTo(LsbBitWriter writer)
    {
        var active = new List<int>(2);
        bool simpleCodeSupported = true;
        for (int i = 0; i < AlphabetSize && active.Count <= 2; i++)
        {
            if (Lengths[i] > 0)
            {
                active.Add(i);
                // 简单码的符号字段最多 8 bit（libwebp 也是这么读），≥256 的符号（如 green 的
                // 长度码/颜色缓存码）只能走标准码长表。
                if (i > 255) simpleCodeSupported = false;
            }
        }

        if (active.Count <= 2 && simpleCodeSupported)
        {
            writer.WriteBit(1);
            writer.WriteBits((uint)Math.Max(0, active.Count - 1), 1);
            if (active.Count > 0)
            {
                int first = active[0];
                int firstLenCode = first <= 1 ? 0 : 1;
                writer.WriteBit(firstLenCode);
                writer.WriteBits((uint)first, firstLenCode == 0 ? 1 : 8);
                if (active.Count == 2) writer.WriteBits((uint)active[1], 8);
            }
            return;
        }

        writer.WriteBit(0);
        WriteCodeLengths(writer);
    }

    private void WriteCodeLengths(LsbBitWriter writer)
    {
        // 1) 先按 RLE 规则把码长序列转成 meta 符号序列。
        var tokens = new List<MetaToken>();
        int i = 0;
        while (i < AlphabetSize)
        {
            int value = Lengths[i];
            int run = 1;
            while (i + run < AlphabetSize && Lengths[i + run] == value) run++;

            if (value == 0)
            {
                int remaining = run;
                while (remaining >= 11)
                {
                    int take = Math.Min(remaining, 138);
                    tokens.Add(new MetaToken(18, take - 11, 7));
                    remaining -= take;
                }
                if (remaining >= 3)
                {
                    tokens.Add(new MetaToken(17, remaining - 3, 3));
                    remaining = 0;
                }
                for (int k = 0; k < remaining; k++) tokens.Add(new MetaToken(0, 0, 0));
            }
            else
            {
                tokens.Add(new MetaToken(value, 0, 0));
                int remaining = run - 1;
                while (remaining >= 3)
                {
                    int take = Math.Min(remaining, 6);
                    tokens.Add(new MetaToken(16, take - 3, 2));
                    remaining -= take;
                }
                for (int k = 0; k < remaining; k++) tokens.Add(new MetaToken(value, 0, 0));
            }
            i += run;
        }

        // 2) 统计 meta 符号频次并构造码表（字母表固定 19）。
        var metaFreq = new int[Vp8LHuffmanAlphabet.Size];
        foreach (var token in tokens) metaFreq[token.Symbol]++;
        // meta 表的码长以 3 bit 字段写出，上限为 7 位。
        var metaCode = Build(metaFreq, Vp8LHuffmanAlphabet.Size, MaxCodeLengthCodeBits);

        // 3) 写 num_codes 与各码长（按规范顺序）。
        int numCodes = Vp8LHuffmanAlphabet.Size;
        while (numCodes > 4 && metaCode.Lengths[Vp8LHuffmanAlphabet.Order[numCodes - 1]] == 0) numCodes--;
        writer.WriteBits((uint)(numCodes - 4), 4);
        for (int c = 0; c < numCodes; c++)
        {
            writer.WriteBits((uint)metaCode.Lengths[Vp8LHuffmanAlphabet.Order[c]], 3);
        }

        // 4) 不写 max_symbol（用完整字母表）。
        writer.WriteBit(0);

        // 5) 输出码长序列。
        foreach (var token in tokens)
        {
            if (metaCode.SingleSymbol < 0)
                writer.WriteCode(metaCode.Codes[token.Symbol], metaCode.Lengths[token.Symbol]);
            if (token.ExtraBits > 0) writer.WriteBits((uint)token.ExtraValue, token.ExtraBits);
        }
    }

    private readonly struct MetaToken
    {
        public MetaToken(int symbol, int extraValue, int extraBits)
        {
            Symbol = symbol;
            ExtraValue = extraValue;
            ExtraBits = extraBits;
        }

        public int Symbol { get; }
        public int ExtraValue { get; }
        public int ExtraBits { get; }
    }
}

/// <summary>VP8L 码长字母表的固定顺序与大小（RFC 9649 §2.5）。</summary>
internal static class Vp8LHuffmanAlphabet
{
    public const int Size = 19;

    public static readonly int[] Order =
    {
        17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
    };
}
