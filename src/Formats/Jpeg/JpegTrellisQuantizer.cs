using System;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// trellis 量化（mozjpeg <c>trellis_quant</c> 思路）的简化实现：在每个 8×8 块内用动态规划
/// 选择量化系数，使「块失真 + λ×码率」最小。
/// </summary>
/// <remarks>
/// 与最近值量化的差别：
/// <list type="bullet">
/// <item>候选值不是唯一的 <c>round(v/Q)</c>，而是 <c>{0, 最近值, 最近值±1}</c>；</item>
/// <item>码率按 zigzag 的 (run, size) 符号查标准 Huffman 表码长（含 ZRL 与 EOB）；</item>
/// <item>失真用系数域平方误差。本库 FDCT 输出按 8 倍缩放，Parseval 下
///   <c>像素 SSE = Σ(Δcoef/8)² = ΣΔ²/64</c>，因此目标函数乘以 64 后为
///   <c>ΣΔ² + 64λ×bits</c>，可直接用整数累加。</item>
/// </list>
/// 这是**离线评估/显式 opt-in** 的实现：每块 64×64 状态转移，比 SIMD 最近值量化慢一个数量级；
/// 默认关闭，不进入正常 optimize 或普通转换路径。
/// </remarks>
internal static class JpegTrellisQuantizer
{
    private const int N = 64;
    private const long Infinity = long.MaxValue / 4;

    [ThreadStatic] private static long[]? t_dp;
    [ThreadStatic] private static long[]? t_next;
    [ThreadStatic] private static int[]? t_parent;
    [ThreadStatic] private static int[]? t_value;

    /// <summary>按质量推导 RD 拉格朗日乘子。</summary>
    /// <remarks>
    /// 可用 <c>SIC_JPEG_TRELLIS_LAMBDA</c> 覆盖（离线评估用）。
    /// 默认随质量升高而减小：高质量档更重视失真，低质量档更愿意丢系数。
    /// </remarks>
    internal static double LambdaForQuality(int quality)
    {
        string? env = Environment.GetEnvironmentVariable("SIC_JPEG_TRELLIS_LAMBDA");
        if (!string.IsNullOrEmpty(env)
            && double.TryParse(env, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double custom)
            && custom >= 0)
        {
            return custom;
        }

        // λ 必须与量化步长同量纲：失真项是 (v - k·8Q)²，v ~ 8Q·u，
        // 同一「相对量化误差」对应的失真按 Q² 增长，而码率节省基本不变。
        // 用 JPEG 标准量化表的缩放因子（与 JpegEncoder.BuildQuantTable 同式）近似 Q：
        //   q=60 → scale 80 → λ≈38；q=40 → 125 → λ≈94；q=95 → 10 → λ≈0.6。
        // 基准值 60 来自 1.57 MP 照片的离线标定（q60 固定质量下 λ≈30~50 换 10%~20% 体积）。
        double q = Math.Clamp(quality, 1, 100);
        double scale = q < 50 ? 5000.0 / q : 200.0 - 2.0 * q;
        double normalized = scale / 100.0;
        return Math.Clamp(60.0 * normalized * normalized, 0.1, 500.0);
    }

    /// <summary>
    /// 原地把 DCT 系数（自然顺序、8 倍缩放）替换为 trellis 量化结果。
    /// </summary>
    /// <param name="block">DCT 系数，原地改写为量化值（自然顺序）</param>
    /// <param name="recip">量化倒数（与本库最近值量化同一套 2^20 定点）</param>
    /// <param name="quant">量化表（自然顺序，1~255）</param>
    /// <param name="acCodeLengths">标准 AC Huffman 表码长，按符号下标（0x00..0xFF）</param>
    /// <param name="lambda">RD 乘子</param>
    /// <returns>zigzag 序最后一个非零系数的下标（0 表示 AC 全零）</returns>
    internal static int QuantizeInPlace(
        Span<int> block,
        ReadOnlySpan<int> recip,
        ReadOnlySpan<byte> quant,
        ReadOnlySpan<byte> acCodeLengths,
        double lambda)
    {
        // DC 保持最近值：DC 差分对整体亮度/码率的影响与 AC 的 run 模型不同，收益也小。
        block[0] = QuantizeNearest(block[0], recip[0]);

        long lambdaScaled = (long)Math.Round(lambda * 64.0);
        long[] dp = t_dp ??= new long[N];
        long[] next = t_next ??= new long[N];
        int[] parent = t_parent ??= new int[N * N];
        int[] value = t_value ??= new int[N * N];

        Array.Fill(dp, Infinity);
        dp[0] = 0;

        ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

        for (int p = 1; p < N; p++)
        {
            Array.Fill(next, Infinity);
            int index = zigzag[p];
            int coefficient = block[index];
            int q = quant[index];
            int q8 = q * 8;
            int nearest = QuantizeNearest(coefficient, recip[index]);

            Span<int> candidates = stackalloc int[4];
            int count = 0;
            candidates[count++] = 0;
            AddCandidate(candidates, ref count, nearest);
            AddCandidate(candidates, ref count, nearest - 1);
            AddCandidate(candidates, ref count, nearest + 1);

            for (int run = 0; run < N; run++)
            {
                long baseCost = dp[run];
                if (baseCost >= Infinity) continue;

                // 归零：失真为该系数本身的能量，不付码率（真正编码时由后续符号或 EOB 承担）。
                long zeroCost = baseCost + (long)coefficient * coefficient;
                if (run + 1 < N && zeroCost < next[run + 1])
                {
                    next[run + 1] = zeroCost;
                    parent[p * N + run + 1] = run;
                    value[p * N + run + 1] = 0;
                }

                for (int c = 0; c < count; c++)
                {
                    int k = candidates[c];
                    if (k == 0) continue;

                    int category = MagnitudeCategory(k);
                    int bits = RunBits(run, category, acCodeLengths) + category;
                    long error = coefficient - (long)k * q8;
                    long cost = baseCost + error * error + lambdaScaled * bits;
                    if (cost < next[0])
                    {
                        next[0] = cost;
                        parent[p * N] = run;
                        value[p * N] = k;
                    }
                }
            }

            (dp, next) = (next, dp);
        }

        int eobBits = Math.Max(1, (int)acCodeLengths[0x00]);
        long bestCost = Infinity;
        int bestRun = 0;
        for (int run = 0; run < N; run++)
        {
            if (dp[run] >= Infinity) continue;
            long cost = dp[run] + (run > 0 ? eobBits * lambdaScaled : 0);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestRun = run;
            }
        }

        if (bestCost >= Infinity)
        {
            // 理论上不可达（全零路径总是存在）；兜底回最近值量化，避免读到未写入的回溯表。
            for (int p = 1; p < N; p++)
            {
                int index = zigzag[p];
                block[index] = QuantizeNearest(block[index], recip[index]);
            }
            for (int k = N - 1; k >= 1; k--)
            {
                if (block[zigzag[k]] != 0) return k;
            }
            return 0;
        }

        // 回溯：value[p, run] 是位置 p 处理完后的状态 run 对应的量化值。
        int state = bestRun;
        for (int p = N - 1; p >= 1; p--)
        {
            int key = p * N + state;
            block[zigzag[p]] = value[key];
            state = parent[key];
        }

        for (int k = N - 1; k >= 1; k--)
        {
            if (block[zigzag[k]] != 0) return k;
        }
        return 0;
    }

    private static void AddCandidate(Span<int> candidates, ref int count, int k)
    {
        if (k < -1023) k = -1023;
        if (k > 1023) k = 1023;
        for (int i = 0; i < count; i++)
        {
            if (candidates[i] == k) return;
        }
        if (count < candidates.Length) candidates[count++] = k;
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

    private static int MagnitudeCategory(int v)
    {
        if (v < 0) v = -v;
        int category = 0;
        while (v > 0)
        {
            category++;
            v >>= 1;
        }
        return category;
    }

    private static int RunBits(int run, int category, ReadOnlySpan<byte> acCodeLengths)
    {
        int bits = 0;
        int zrl = Math.Max(1, (int)acCodeLengths[0xF0]);
        while (run >= 16)
        {
            bits += zrl;
            run -= 16;
        }
        int symbol = (run << 4) | category;
        int length = acCodeLengths[symbol];
        bits += length > 0 ? length : 16;
        return bits;
    }
}
