using System;
using System.Numerics;

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
/// 这是**离线评估/显式 opt-in** 的实现：默认关闭，不进入正常 optimize 或普通转换路径。
///
/// <para><b>DP 形态</b>：状态 = 自上一个非零系数以来的 run（0~63），位置 = zigzag 的 63 个 AC。
/// 旧实现每块做 63×64×(≤4) ≈ 1.6 万次状态转移，每转移还要现算 size 与 (run,size) 码长，
/// 143 MP 上 DP 占 trellis 量化耗时的 99%（~29 µs/块）。现在的快路径把它压成：</para>
/// <list type="number">
/// <item><b>零转移是确定的</b>：<c>next[r] = dp[r-1] + coef²</c>（r≥1），一次向量移位即可；
///   只有 <c>next[0]</c>（本系数取非零）需要取 min，所以回溯表从 64×64 缩到每位置一对
///   <c>(prevRun, value)</c>，32 KB/线程 → 384 B/线程。</item>
/// <item><b>码率可按 run = 16q + s 展开</b>：
///   <c>bits(run,cat) = q×ZRL + len[s,cat]</c>，于是
///   <c>min_r(dp[r] + λ×bits) = min_s( G[s] + λ×len[s,cat] )</c>，
///   其中 <c>G[s] = min_q(dp[16q+s] + q×λZRL)</c>。G 与 category 无关，每个位置只算一次
///   （16 lane 的向量 min），每个候选 category 只再做一次 16 lane 的向量 min。
///   这样每位置 ~60 条向量指令，替代原来的上万次标量迭代。</item>
/// <item><b>int32 定点</b>：最大代价 ≈ 6×10⁸ &lt; 2³⁰，用 <c>Vector&lt;int&gt;</c> 比 long 多一倍 lane；
///   加法一律带饱和（<c>Min(sum, Inf)</c>），Inf 取 2³⁰。λ 极端（环境变量覆盖）时仍回落旧 long 实现。</item>
/// </list>
/// <para>
/// 注：VNNI（<c>vpdpbusd</c> 等）在这里帮不上忙 —— DP 是 min-plus 递推，不是 int8/int16 点积，
/// 没有可融合的乘加；能用的只有 SSE2/AVX2/AVX-512/NEON 的整型 min/add。
/// </para>
/// </remarks>
internal static class JpegTrellisQuantizer
{
    private const int N = 64;
    private const long Infinity = long.MaxValue / 4;

    /// <summary>int32 快路径的饱和哨兵。</summary>
    private const int Inf32 = 1 << 30;

    /// <summary>
    /// int32 快路径能安全表示的最大 <c>λ×64</c>：63 × 80 bit/块 × λ ≤ Inf32。
    /// 默认 λ ≤ 500（→ λ×64 ≤ 32000）；只有 <c>SIC_JPEG_TRELLIS_LAMBDA</c> 极端覆盖时才会回落 long 路径。
    /// </summary>
    private const long MaxLambdaScaledForInt32 = 120_000;

    /// <summary>单个失真项的上限：留足余量，确保 int32 向量加法不溢出。</summary>
    private const int MaxTerm = 1 << 28;

    [ThreadStatic] private static long[]? t_dp;
    [ThreadStatic] private static long[]? t_next;
    [ThreadStatic] private static int[]? t_parent;
    [ThreadStatic] private static int[]? t_value;

    [ThreadStatic] private static int[]? t_dp32;
    [ThreadStatic] private static int[]? t_next32;
    [ThreadStatic] private static int[]? t_g;
    [ThreadStatic] private static int[]? t_rate;
    [ThreadStatic] private static byte[]? t_prevRun;
    [ThreadStatic] private static short[]? t_value16;

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
        if (lambdaScaled > MaxLambdaScaledForInt32 || lambdaScaled < 0)
        {
            return QuantizeInPlaceLegacy(block, recip, quant, acCodeLengths, lambdaScaled);
        }
        return QuantizeInPlaceVectorized(block, recip, quant, acCodeLengths, (int)lambdaScaled);
    }

    // ---------------------------------------------------------------------
    // 快路径：run 状态展开 + Vector<int> 整型 min/add
    // ---------------------------------------------------------------------

    private static int QuantizeInPlaceVectorized(
        Span<int> block,
        ReadOnlySpan<int> recip,
        ReadOnlySpan<byte> quant,
        ReadOnlySpan<byte> acCodeLengths,
        int lambdaScaled)
    {
        int[] dpArr = t_dp32 ??= new int[N];
        int[] nextArr = t_next32 ??= new int[N];
        int[] gArr = t_g ??= new int[16];
        int[] rateArr = t_rate ??= new int[11 * 16];
        byte[] prevRunArr = t_prevRun ??= new byte[N];
        short[] valueArr = t_value16 ??= new short[N];

        Span<int> dp = dpArr.AsSpan(0, N);
        Span<int> next = nextArr.AsSpan(0, N);
        Span<int> g = gArr.AsSpan(0, 16);
        Span<int> rate = rateArr.AsSpan(0, 11 * 16);

        int zrl = Math.Max(1, (int)acCodeLengths[0xF0]);
        int zrlScaled = zrl * lambdaScaled;

        // rate[cat, s] = λ64 × len(symbol = s<<4 | cat)，cat ∈ [1,10]、s ∈ [0,15]。
        for (int cat = 1; cat <= 10; cat++)
        {
            for (int s = 0; s < 16; s++)
            {
                int length = acCodeLengths[(s << 4) | cat];
                if (length <= 0) length = 16;
                rate[(cat << 4) | s] = length * lambdaScaled;
            }
        }

        ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

        for (int i = 1; i < N; i++) dp[i] = Inf32;
        dp[0] = 0;

        int lanes = Vector<int>.Count;
        var infVec = new Vector<int>(Inf32);
        var zrlVec = new Vector<int>(zrlScaled);
        var zrl2Vec = new Vector<int>(2 * zrlScaled);
        var zrl3Vec = new Vector<int>(3 * zrlScaled);

        // 每个位置固定 4 个候选槽；移出循环，避免 63 次 stackalloc。
        Span<int> candidates = stackalloc int[4];
        Span<int> candidateCats = stackalloc int[4];
        Span<int> candidateRates = stackalloc int[4];

        for (int p = 1; p < N; p++)
        {
            int index = zigzag[p];
            int coefficient = block[index];
            int q8 = quant[index] * 8;
            // 饱和到 2^28：保证 dp(≤2^30) + e0 不会让 int32 加法溢出；
            // 真实 8-bit JPEG 的系数能量远低于该值（|coef| ≤ ~8192 ⇒ e0 ≤ 6.7e7）。
            int e0 = (int)Math.Min((long)coefficient * coefficient, MaxTerm);
            int nearest = QuantizeNearest(coefficient, recip[index]);

            ref int dpRef = ref dp[0];
            ref int nextRef = ref next[0];
            ref int gRef = ref g[0];
            ref int rateRef = ref rate[0];

            // G[s] = min_q(dp[16q+s] + q×λZRL)。G 只跟 dp 有关，与候选值/category 无关。
            for (int i = 0; i < 16; i += lanes)
            {
                Vector<int> v = Vector.Min(Vector.LoadUnsafe(ref dpRef, (nuint)i), infVec);
                v = Vector.Min(v, Vector.Min(
                    Vector.LoadUnsafe(ref dpRef, (nuint)(16 + i)) + zrlVec, infVec));
                v = Vector.Min(v, Vector.Min(
                    Vector.LoadUnsafe(ref dpRef, (nuint)(32 + i)) + zrl2Vec, infVec));
                v = Vector.Min(v, Vector.Min(
                    Vector.LoadUnsafe(ref dpRef, (nuint)(48 + i)) + zrl3Vec, infVec));
                v.StoreUnsafe(ref gRef, (nuint)i);
            }

            // 候选顺序与旧实现完全一致（nearest, nearest-1, nearest+1；先夹取、再去重）。
            int candidateCount = 0;
            candidates[candidateCount++] = 0;
            AddCandidate(candidates, ref candidateCount, nearest);
            AddCandidate(candidates, ref candidateCount, nearest - 1);
            AddCandidate(candidates, ref candidateCount, nearest + 1);

            int bestCost = Inf32;
            int bestValue = 0;
            int bestCategory = 0;
            int bestRateMin = 0;
            int evaluated = 0;
            for (int c = 0; c < candidateCount; c++)
            {
                int k = candidates[c];
                if (k == 0) continue;

                int category = BitOperations.Log2((uint)(k < 0 ? -k : k)) + 1;

                // 同一位置里 nearest / nearest±1 常常落在同一 category，码率项可复用。
                int rateMin = -1;
                for (int j = 0; j < evaluated; j++)
                {
                    if (candidateCats[j] == category) { rateMin = candidateRates[j]; break; }
                }
                if (rateMin < 0)
                {
                    rateMin = MinRate(g, rate, category, lanes);
                    candidateCats[evaluated] = category;
                    candidateRates[evaluated] = rateMin;
                    evaluated++;
                }

                long error = coefficient - (long)k * q8;
                int err2 = (int)Math.Min(error * error, MaxTerm);
                long total = (long)rateMin + err2 + (long)category * lambdaScaled;
                int cost = Saturate(total);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestValue = k;
                    bestCategory = category;
                    bestRateMin = rateMin;
                }
            }

            if (bestValue != 0)
            {
                // 回溯只需要 (上一状态, 量化值)：run≥1 的转移是确定的，无需 64×64 父表。
                // 用向量按 r 升序找第一个等于最小代价的状态（保持旧实现的平局规则）。
                int bestRun = 0;
                var minVec = new Vector<int>(bestRateMin);
                int rateOffset = bestCategory << 4;
                bool found = false;
                for (int r = 0; r < N && !found; r += lanes)
                {
                    Vector<int> costVec = Vector.LoadUnsafe(ref dpRef, (nuint)r)
                        + new Vector<int>((r >> 4) * zrlScaled)
                        + Vector.LoadUnsafe(ref rateRef, (nuint)(rateOffset + (r & 15)));
                    costVec = Vector.Min(costVec, infVec);
                    if (Vector.Equals(costVec, minVec) != Vector<int>.Zero)
                    {
                        for (int lane = 0; lane < lanes; lane++)
                        {
                            if (costVec[lane] == bestRateMin)
                            {
                                bestRun = r + lane;
                                found = true;
                                break;
                            }
                        }
                    }
                }
                prevRunArr[p] = (byte)bestRun;
                valueArr[p] = (short)bestValue;
            }
            else
            {
                // 理论上不可达：p=1 时 dp[0]=0 必然可转移；兜底按最近值写回。
                prevRunArr[p] = (byte)(p - 1);
                valueArr[p] = (short)nearest;
                bestCost = e0;
            }

            // next[r] = dp[r-1] + coef²（r = 1..63），next[0] = 本位置取非零的最优代价。
            var e0Vec = new Vector<int>(e0);
            int shift = 0;
            for (; shift + lanes <= 63; shift += lanes)
            {
                Vector<int> shifted = Vector.Min(
                    Vector.LoadUnsafe(ref dpRef, (nuint)shift) + e0Vec, infVec);
                shifted.StoreUnsafe(ref nextRef, (nuint)(shift + 1));
            }
            for (; shift < 63; shift++)
            {
                next[shift + 1] = Math.Min(dp[shift] + e0, Inf32);
            }
            next[0] = bestCost;

            Span<int> swap = dp;
            dp = next;
            next = swap;
        }

        // 终止代价：run>0 还要补一个 EOB。
        int eobBits = Math.Max(1, (int)acCodeLengths[0x00]);
        int eobScaled = eobBits * lambdaScaled;
        int bestTotal = int.MaxValue;
        int bestState = 0;
        for (int r = 0; r < N; r++)
        {
            int cost = dp[r] + (r > 0 ? eobScaled : 0);
            if (cost < bestTotal)
            {
                bestTotal = cost;
                bestState = r;
            }
        }

        // 回溯：state>0 表示该位置被归零（值 0、上一状态 state-1）；
        // state==0 表示该位置是非零，值是当时记下的候选，上一状态是当时的 run。
        int state = bestState;
        for (int p = N - 1; p >= 1; p--)
        {
            if (state == 0)
            {
                block[zigzag[p]] = valueArr[p];
                state = prevRunArr[p];
            }
            else
            {
                block[zigzag[p]] = 0;
                state--;
            }
        }

        for (int k = N - 1; k >= 1; k--)
        {
            if (block[zigzag[k]] != 0) return k;
        }
        return 0;
    }

    /// <summary>min_s(g[s] + rate[cat, s])，16 lane 向量归约。</summary>
    private static int MinRate(Span<int> g, Span<int> rate, int category, int lanes)
    {
        ref int gRef = ref g[0];
        ref int rateRef = ref rate[category << 4];
        Vector<int> acc = Vector.LoadUnsafe(ref gRef, 0) + Vector.LoadUnsafe(ref rateRef, 0);
        for (int i = lanes; i < 16; i += lanes)
        {
            acc = Vector.Min(acc, Vector.LoadUnsafe(ref gRef, (nuint)i) + Vector.LoadUnsafe(ref rateRef, (nuint)i));
        }
        int best = acc[0];
        for (int lane = 1; lane < lanes; lane++)
        {
            int value = acc[lane];
            if (value < best) best = value;
        }
        return best;
    }

    private static int Saturate(long value)
        => value >= Inf32 ? Inf32 : (int)value;

    // ---------------------------------------------------------------------
    // 旧实现：long DP + 64×64 回溯表。仅作极端 λ 的兜底与测试参照。
    // ---------------------------------------------------------------------

    internal static int QuantizeInPlaceLegacy(
        Span<int> block,
        ReadOnlySpan<int> recip,
        ReadOnlySpan<byte> quant,
        ReadOnlySpan<byte> acCodeLengths,
        double lambda)
    {
        long lambdaScaled = (long)Math.Round(lambda * 64.0);
        return QuantizeInPlaceLegacy(block, recip, quant, acCodeLengths, lambdaScaled);
    }

    private static int QuantizeInPlaceLegacy(
        Span<int> block,
        ReadOnlySpan<int> recip,
        ReadOnlySpan<byte> quant,
        ReadOnlySpan<byte> acCodeLengths,
        long lambdaScaled)
    {
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
