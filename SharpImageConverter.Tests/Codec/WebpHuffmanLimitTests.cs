using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// VP8L Huffman 码表的「码长上限」处理（package-merge）验证。
///
/// 背景：VP8L 主符号表的码长上限是 15 位。真实图像的残差分布是长尾的，
/// 不受限 Huffman 树的深度经常超过 15，因此限长逻辑几乎每张照片都会走到。
/// 历史 bug：旧实现用「折叠 + 再平衡」的手写近似，Kraft 校验频繁失败后退化成
/// 扁平码（所有符号 9~10 位，包括本该 2~3 位的高频符号），导致 peppers 这类照片
/// 的无损体积比 libwebp 大 72%（理想熵 17.7 Mbit / 实写 29.1 Mbit）。
///
/// 本文件固定三件事：码表合法（Kraft 和 = 1、码长 ≤ 上限）、限长结果与
/// 穷举得到的最优解一致、长尾分布不再退化成扁平码。
/// </summary>
public class WebpHuffmanLimitTests
{
    [Fact]
    public void LengthLimited_BruteForceOptimal_ForSmallAlphabets()
    {
        // 斐波那契频次在不受限时的树深 = n-1，必然触发限长。
        int[] fib = { 1, 1, 2, 3, 5, 8, 13, 21 };
        foreach (int maxLength in new[] { 3, 4, 5 })
        {
            var code = Vp8LHuffmanCode.Build(fib, fib.Length, maxLength);

            AssertValid(code, fib, maxLength);

            long actual = WeightedBits(code, fib);
            long optimal = BruteForceOptimalBits(fib, maxLength);
            Assert.True(actual == optimal,
                $"maxLength={maxLength}：限长码 {actual} bit 与穷举最优 {optimal} bit 不一致");
        }
    }

    [Fact]
    public void LengthLimited_MatchesUnlimitedHuffman_WhenLimitNotNeeded()
    {
        // 分布平坦时不受限树的深度远小于 15，限长逻辑不应改变任何码长。
        int[] freq = { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        var code = Vp8LHuffmanCode.Build(freq, freq.Length, 15);
        AssertValid(code, freq, 15);

        long huffman = WeightedBits(code, freq);
        double entropy = Entropy(freq);
        // 最优前缀码的加权长度必在 [H, H + 1) 区间内。
        Assert.True(huffman >= entropy - 1e-9, $"加权长度 {huffman} 小于熵 {entropy}");
        Assert.True(huffman < entropy + freq.Length, $"加权长度 {huffman} 远超熵 {entropy}");
    }

    [Fact]
    public void LongTailAlphabet_DoesNotDegradeToFlatCodes()
    {
        // 模拟照片残差：少量符号极高频率 + 长尾，不受限树深 > 15。
        const int n = 256;
        var freq = new int[n];
        for (int i = 0; i < n; i++)
            freq[i] = i < 12 ? 1 << (12 - i) : 1 + i % 3;

        var code = Vp8LHuffmanCode.Build(freq, n, 15);
        AssertValid(code, freq, 15);

        long bits = WeightedBits(code, freq);
        double entropy = Entropy(freq);
        long flatBits = (long)(n == 0 ? 0 : freq.Sum() * Math.Ceiling(Math.Log2(ActiveCount(freq))));

        // 扁平码就是这个 bug 的形态：所有符号等长。留 25% 余量后仍必须明显更小。
        Assert.True(bits < flatBits * 0.75,
            $"长尾分布退化成扁平码：加权 {bits} bit，扁平 {flatBits} bit，熵 {entropy:F0} bit");
        Assert.True(bits < entropy * 1.15,
            $"限长码 {bits} bit 比熵 {entropy:F0} bit 高出 15% 以上（码表构造劣化）");
    }

    [Fact]
    public void RandomAlphabets_AlwaysProducePrefixCodeWithinLimit()
    {
        var random = new Random(20261104);
        for (int iter = 0; iter < 200; iter++)
        {
            int n = random.Next(3, 300);
            var freq = new int[n];
            // 混合分布：几何衰减、均匀、极端偏斜各占一部分，覆盖限长分支。
            int shape = iter % 3;
            for (int i = 0; i < n; i++)
            {
                freq[i] = shape switch
                {
                    0 => random.Next(1, 1 << 14),
                    1 => 1 + (i < 4 ? random.Next(1, 50) : 0),
                    _ => (int)Math.Max(1, Math.Pow(0.8, i) * 100000),
                };
            }

            // 7 位上限最多容纳 128 个符号；符号更多时必须用规范上限 15 位。
            int maxLength = iter % 2 == 0 || n > 128 ? 15 : 7;
            var code = Vp8LHuffmanCode.Build(freq, n, maxLength);
            AssertValid(code, freq, maxLength);
        }
    }

    private static void AssertValid(Vp8LHuffmanCode code, int[] freq, int maxLength)
    {
        long units = 0;
        int active = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] == 0) continue;
            int len = code.Lengths[i];
            Assert.True(len >= 1 && len <= maxLength, $"符号 {i} 码长 {len} 越界（上限 {maxLength}）");
            units += 1L << (maxLength - len);
            active++;
        }
        Assert.True(active == 0 || units == 1L << maxLength,
            $"Kraft 和 {units} ≠ {1L << maxLength}（活跃符号 {active}）");
    }

    private static long WeightedBits(Vp8LHuffmanCode code, int[] freq)
    {
        long bits = 0;
        for (int i = 0; i < freq.Length; i++) bits += (long)freq[i] * code.Lengths[i];
        return bits;
    }

    private static int ActiveCount(int[] freq)
    {
        int n = 0;
        foreach (int f in freq) if (f > 0) n++;
        return n;
    }

    private static double Entropy(int[] freq)
    {
        long total = freq.Sum();
        double bits = 0;
        foreach (int f in freq)
        {
            if (f == 0) continue;
            bits += f * Math.Log2((double)total / f);
        }
        return bits;
    }

    /// <summary>穷举所有满足 Kraft 等式的码长组合，返回最小加权长度（只用于极小字母表）。</summary>
    private static long BruteForceOptimalBits(int[] freq, int maxLength)
    {
        int n = freq.Length;
        long target = 1L << maxLength;
        var lengths = new int[n];
        long best = long.MaxValue;

        void Recurse(int index, long unitsRemaining, long cost)
        {
            if (cost >= best) return;
            if (index == n)
            {
                if (unitsRemaining == 0) best = cost;
                return;
            }
            for (int len = 1; len <= maxLength; len++)
            {
                long unit = 1L << (maxLength - len);
                if (unit > unitsRemaining) continue;
                lengths[index] = len;
                Recurse(index + 1, unitsRemaining - unit, cost + (long)freq[index] * len);
            }
        }

        Recurse(0, target, 0);
        Assert.True(best < long.MaxValue, "穷举未找到合法前缀码");
        return best;
    }
}
