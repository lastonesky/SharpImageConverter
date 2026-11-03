using System;

namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// Huffman 解码表：从码字长度列表构建，并支持查表解码。
///
/// 解码采用两级策略（对齐 libwebp 的 root table 思路）：
/// - 一级表 <c>_root</c> 用 8 bit 前缀直接定位，覆盖 VP8L 里绝大多数符号；
/// - 码长 &gt; 8 的少数符号走逐位慢路径。
/// 相比原先"每个符号逐位读 + 每层线性扫描"的实现，常见路径从 ~8 次位读取与数十次
/// 比较降到 1 次 peek + 1 次数组索引。
/// </summary>
internal sealed class HuffmanTable
{
    /// <summary>一级表位数。</summary>
    private const int RootBits = 8;
    private const int RootSize = 1 << RootBits;

    /// <summary>一级表项：高 16 位 = 码长（0 表示需走慢路径），低 16 位 = 符号。</summary>
    private readonly int[] _root = new int[RootSize];

    /// <summary>字母表总大小（含码长为 0 的未出现符号）。</summary>
    public int NumSymbols { get; }

    /// <summary>每个符号对应的码长（0 = 该符号未出现）。</summary>
    public int[] CodeLengths { get; }

    /// <summary>最大码长。</summary>
    public int MaxBitLength { get; }

    /// <summary>实际参与编码的符号数（码长 &gt; 0 的符号个数）。</summary>
    public int NumActiveSymbols { get; }

    /// <summary>仅当 NumActiveSymbols == 1 时，这里保存唯一的符号值，解码时无需读 bit。</summary>
    public int SingleActiveSymbol { get; }

    /// <summary>按码长升序排列的符号索引（前 NumActiveSymbols 个是码长&gt;0 的）。</summary>
    public int[] SortedSymbols { get; }

    /// <summary>按码长升序排列的码字值（前 NumActiveSymbols 个有效）。</summary>
    public int[] SortedCodes { get; }

    public HuffmanTable(int[] codeLengths)
    {
        ArgumentNullException.ThrowIfNull(codeLengths);
        if (codeLengths.Length == 0)
            throw new WebpBitstreamException("Huffman 表至少需要一个符号");

        CodeLengths = codeLengths;
        NumSymbols = codeLengths.Length;

        // 统计每个码长出现的符号数，以及激活符号数
        int maxLen = 0;
        int active = 0;
        int singleSym = -1;
        var blCount = new int[33]; // 码长 0~32
        for (int i = 0; i < codeLengths.Length; i++)
        {
            int l = codeLengths[i];
            if (l < 0) throw new WebpBitstreamException($"Huffman 码长 {l} 非法");
            if (l > 15) throw new WebpBitstreamException($"Huffman 码长 {l} 超出 VP8L 上限 15");
            if (l > 0)
            {
                active++;
                singleSym = i; // 最后一个激活符号（之后会判断如果只有 1 个，就是它）
            }
            if (l > maxLen) maxLen = l;
            blCount[l]++;
        }

        NumActiveSymbols = active;
        SingleActiveSymbol = active == 1 ? singleSym : -1;
        MaxBitLength = maxLen;

        // VP8L 要求码树完整（Kraft 和必须恰为 1）；只有 1 个激活符号时跳过。
        if (active > 1)
        {
            int left = 1;
            for (int len = 1; len <= 15; len++)
            {
                left = (left << 1) - blCount[len];
                if (left < 0)
                    throw new WebpBitstreamException("Huffman 码长溢出：码树不完整或过长");
            }
            if (left != 0)
                throw new WebpBitstreamException("Huffman 码树不完整（Kraft 和不为 1）");
        }

        // 构建排序：按码长从小到大，同码长的符号索引
        SortedSymbols = new int[NumSymbols];
        int pos = 0;
        for (int l = 1; l <= maxLen; l++)
        {
            for (int s = 0; s < NumSymbols; s++)
            {
                if (codeLengths[s] == l) SortedSymbols[pos++] = s;
            }
        }
        // 把码长为 0 的符号排在最后（不参与编码）
        for (int s = 0; s < NumSymbols; s++)
        {
            if (codeLengths[s] == 0) SortedSymbols[pos++] = s;
        }

        // 构建码字：从最短开始，按升序分配
        SortedCodes = new int[NumSymbols];
        int code = 0;
        pos = 0;
        for (int l = 1; l <= maxLen; l++)
        {
            for (int j = 0; j < blCount[l]; j++)
            {
                SortedCodes[pos] = code;
                code++;
                pos++;
            }
            code <<= 1;
        }

        // 保存 blCount 用于解码（避免每次循环重算）
        _blCount = blCount;
        // 保存 prefix 索引（blCountPrefix[l] = 码长 < l 的符号总数）
        _blPrefix = new int[maxLen + 2];
        int p = 0;
        for (int l = 1; l <= maxLen; l++)
        {
            _blPrefix[l] = p;
            p += blCount[l];
        }
        _blPrefix[maxLen + 1] = p;

        BuildRootTable();
    }

    // 码长计数表（内部使用）
    private readonly int[] _blCount;
    // _blPrefix[l] = 码长 < l 的符号数，用于快速定位 SortedSymbols 中的段
    private readonly int[] _blPrefix;

    /// <summary>
    /// 构建 8 bit 一级查找表。
    ///
    /// VP8L 的码字按 MSB-first 写出，而位流是 LSB-first 读取，所以长度为 L 的码字
    /// <c>c</c> 在 peek 到的 8 位值中表现为 <c>reverse(c, L)</c> 位于低 L 位；
    /// 高位是后续比特，可任意，故按 2^L 步长铺满。
    /// </summary>
    private void BuildRootTable()
    {
        if (NumActiveSymbols <= 1) return; // 单符号表不需要查表

        for (int j = 0; j < NumActiveSymbols; j++)
        {
            int sym = SortedSymbols[j];
            int len = CodeLengths[sym];
            if (len == 0 || len > RootBits) continue;

            int code = SortedCodes[j];
            int rev = ReverseBits(code, len);
            int entry = (len << 16) | sym;
            for (int i = rev; i < RootSize; i += 1 << len)
                _root[i] = entry;
        }
    }

    private static int ReverseBits(int value, int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }

    /// <summary>
    /// 从位流中解码一个符号。
    /// 若只有 1 个激活符号（libwebp 的"单符号优化"），直接返回、不读 bit。
    /// </summary>
    public int DecodeSymbol(ref LsbBitReader reader)
    {
        if (SingleActiveSymbol >= 0)
            return SingleActiveSymbol;

        if (MaxBitLength == 0)
            throw new WebpBitstreamException("空 Huffman 表无法解码");

        // 剩余位数不足 8 时 peek 会补 0，可能误命中；交给慢路径逐位做严格边界检查。
        if (reader.RemainingBits >= RootBits)
        {
            int entry = _root[reader.PeekBits(RootBits)];
            int len = entry >> 16;
            if (len != 0)
            {
                reader.SkipBits(len);
                return entry & 0xFFFF;
            }
        }

        return DecodeSymbolSlow(ref reader);
    }

    /// <summary>逐位解码（码长 &gt; 8 或数据尾部）。</summary>
    private int DecodeSymbolSlow(ref LsbBitReader reader)
    {
        int code = 0;
        for (int len = 1; len <= MaxBitLength; len++)
        {
            int bit = reader.ReadBit();
            code = (code << 1) | bit;

            int start = _blPrefix[len];
            int end = _blPrefix[len + 1];

            for (int j = start; j < end; j++)
            {
                if (SortedCodes[j] == code) return SortedSymbols[j];
            }
        }
        throw new WebpBitstreamException("Huffman 解码：找不到匹配的码字");
    }
}
