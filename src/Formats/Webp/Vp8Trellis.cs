namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 系数级 trellis 量化。
///
/// 对标量量化（每个系数独立四舍五入）的替代：在一个 4×4 块内，按
/// <c>D + λ·R</c> 在候选电平上做一次真正的动态规划。码率 <c>R</c> 直接使用
/// <see cref="Vp8CostTable"/>（与真实布尔编码同一张成本表，单位 1/256 bit），
/// 语法节点逐条对应 <see cref="Vp8Encoder"/> 的 <c>WriteCoeffs</c>：
/// <list type="bullet">
/// <item>node0：当前位置是否还有非零系数（EOB 前移可显著省码率）；</item>
/// <item>node1：零游程；</item>
/// <item>node2 与 <c>PutLargeValue</c> 的类别码：幅值分级；</item>
/// <item>符号位：固定概率 128。</item>
/// </list>
///
/// 状态设计：<c>(m, ctxAfter)</c>。<c>m</c> 是已确定的最后一个非零系数的扫描序号
/// （−1 表示尚无），<c>ctxAfter</c> 是该非零系数之后的节点上下文（1 或 2；无则初始
/// 上下文）。空档（<c>m</c> 与下一个非零之间）的零系数代价在选定下一个非零时一次性
/// 结算——这正是 VP8 语法「last 之后立即写 node0=0 并返回」的行为，因此终止位只需
/// 在 DP 结束时按 <c>m+1</c> 结算一次。
///
/// 失真在系数域计算：<c>Σ (c_i − level_i·dq_i)²</c>。Y2 经过 WHT 分发到 16 个亮度块，
/// 按 IDCT 增益折算权重 4；其余块权重 1。
/// 实例只使用预分配的 <see cref="Vp8TrellisScratch"/>，每个编码器持有一个，不产生
/// 逐块堆分配。
/// </summary>
internal static class Vp8Trellis
{
    /// <summary>状态数：m ∈ {−1, 0..15}，ctxAfter ∈ {0,1,2}。</summary>
    internal const int StateCount = 17 * 3;

    /// <summary>单个系数最多保留的候选电平数。</summary>
    private const int MaxCandidates = 5;

    /// <summary>VP8 系数幅值上限（Cat6 可表示的最大值）。</summary>
    internal const int MaxCoefficient = 2114;

    /// <summary>
    /// 对 4×4 块的系数做 RD trellis 量化。
    /// </summary>
    /// <param name="coefficients">光栅序变换系数（16 项）。</param>
    /// <param name="levels">输出：扫描序量化电平（16 项，调用方负责清零 first 之前的位置）。</param>
    /// <param name="slots">输出：反量化系数（光栅序，16 项）。</param>
    /// <param name="first">首个参与编码的扫描位置（Y1 AC 为 1，其余为 0）。</param>
    /// <param name="type">token 类型：0=Y1 AC，1=Y2，2=UV，3=i4x4。</param>
    /// <param name="ctx">块起始非零上下文（0..2）。</param>
    /// <param name="dcStep">DC 量化步长。</param>
    /// <param name="acStep">AC 量化步长。</param>
    /// <param name="probs">当前生效的 token 概率表。</param>
    /// <param name="lambdaQ8">λ（Q8），单位与 <paramref name="distortionWeight"/> 无关的位成本换算系数。</param>
    /// <param name="distortionWeight">失真权重（Y2 为 4，其余为 1）。</param>
    /// <param name="scratch">复用缓冲。</param>
    /// <returns>块内最后一个非零系数的扫描序号（−1 表示全零）。</returns>
    internal static int Quantize(
        ReadOnlySpan<int> coefficients,
        Span<int> levels,
        Span<short> slots,
        int first,
        int type,
        int ctx,
        int dcStep,
        int acStep,
        byte[,,,] probs,
        int lambdaQ8,
        int distortionWeight,
        Vp8TrellisScratch scratch)
    {
        // 单个系数候选 + 预存本块的步长，供空档结算复用。
        Span<int> dq = stackalloc int[16];
        for (int n = 0; n < 16; n++)
            dq[n] = n == 0 ? dcStep : acStep;

        Span<int> coef = stackalloc int[16];
        for (int n = 0; n < 16; n++)
            coef[n] = coefficients[Vp8Tables.Zigzag[n]];

        long[] current = scratch.Current;
        long[] next = scratch.Next;
        System.Array.Fill(current, long.MaxValue);
        System.Array.Fill(next, long.MaxValue);

        // 初始状态：m = −1（索引 0），ctx = 块初始上下文。
        current[0 * 3 + ctx] = 0;

        for (int n = first; n < 16; n++)
        {
            int band = Vp8Tables.Bands[n];

            for (int state = 0; state < StateCount; state++)
            {
                long cost = current[state];
                if (cost == long.MaxValue) continue;

                int m = state / 3 - 1;
                int ctxAfter = state % 3;
                int baseContext = m < 0 ? ctx : ctxAfter;

                int candidateCount = BuildCandidates(coef[n], dq[n], scratch.Candidates);
                for (int c = 0; c < candidateCount; c++)
                {
                    int level = scratch.Candidates[c];
                    int rate = 0;

                    if (level == 0)
                    {
                        // 零系数：只有当下一个非零出现时才需要补 node0/node1 代价，
                        // 这里不结算，状态保持（m/ctxAfter 不变）。
                        int newState = state;
                        long candidate = cost +
                            (long)distortionWeight * (long)coef[n] * coef[n] * 65536L;
                        if (candidate < next[newState])
                        {
                            next[newState] = candidate;
                            scratch.SetBackPointer(n, newState, state, 0);
                        }
                        continue;
                    }

                    // 非零系数：先把 m 与 n 之间的空档零系数代价补上。
                    // 与 WriteCoeffs 逐节点对应：每次「当前末尾 < 下一个非零」时，空档起点只写
                    // 一次 node0=1；随后每个零写 node1=0（第一个零沿用前一个非零的上下文，
                    // 其后 curCtx 被清 0）。无非零空档时 node0=1 就落在 n 本身。
                    int gapStart = m < 0 ? first : m + 1;
                    int gapCtx = m < 0 ? ctx : ctxAfter;
                    if (gapStart < n)
                    {
                        rate += BitCost(probs, type, Vp8Tables.Bands[gapStart], gapCtx, 0, 1);
                        rate += BitCost(probs, type, Vp8Tables.Bands[gapStart], gapCtx, 1, 0);
                        for (int j = gapStart + 1; j < n; j++)
                            rate += BitCost(probs, type, Vp8Tables.Bands[j], 0, 1, 0);
                        baseContext = 0;
                    }
                    else
                    {
                        rate += BitCost(probs, type, band, gapCtx, 0, 1);
                        baseContext = gapCtx;
                    }

                    rate += NonZeroCost(probs, type, band, baseContext, level);
                    int nextCtx = level == 1 || level == -1 ? 1 : 2;
                    int newStateIndex = (n + 1) * 3 + nextCtx;

                    long diff = coef[n] - level * dq[n];
                    long candidateCost = cost + (long)rate * lambdaQ8
                        + (long)distortionWeight * diff * diff * 65536L;
                    if (candidateCost < next[newStateIndex])
                    {
                        next[newStateIndex] = candidateCost;
                        scratch.SetBackPointer(n, newStateIndex, state, level);
                    }
                }
            }

            // 交换缓冲；next 清空前先保存到数组尾部（避免 Array.Fill 每次分配）。
            long[] swap = current; current = next; next = swap;
            System.Array.Fill(next, long.MaxValue);
        }

        // 结算终止位，选择全局最优。
        long best = long.MaxValue;
        int bestState = -1;
        for (int state = 0; state < StateCount; state++)
        {
            long cost = current[state];
            if (cost == long.MaxValue) continue;
            int m = state / 3 - 1;
            int ctxAfter = state % 3;
            long finalCost = cost;
            if (m < 0)
                finalCost += (long)lambdaQ8 * BitCost(probs, type, Vp8Tables.Bands[first], ctx, 0, 0);
            else if (m < 15)
                finalCost += (long)lambdaQ8 * BitCost(probs, type, Vp8Tables.Bands[m + 1], ctxAfter, 0, 0);
            if (finalCost < best)
            {
                best = finalCost;
                bestState = state;
                scratch.BestState = state;
            }
        }

        // 回溯：从最后的 state 开始，逐位置取回电平与前驱 state。
        int currentState = bestState;
        for (int n = 15; n >= first; n--)
        {
            int level = scratch.GetBackPointerLevel(n, currentState);
            levels[n] = level;
            int raster = Vp8Tables.Zigzag[n];
            slots[raster] = (short)(level * dq[n]);
            currentState = scratch.GetBackPointerState(n, currentState);
        }
        if (first == 1)
        {
            // Y1 AC 块：0 号位置是 Y2 分发的 DC，不由本量化器写入。
            levels[0] = 0;
        }

        int last = -1;
        for (int n = 15; n >= first; n--)
        {
            if (levels[n] != 0)
            {
                last = n;
                break;
            }
        }
        return last;
    }

    /// <summary>按四舍五入给出最近电平，并在其邻域生成候选。</summary>
    private static int BuildCandidates(int coefficient, int step, int[] candidates)
    {
        int count = 0;
        candidates[count++] = 0;
        if (coefficient == 0)
            return count;

        int q = Vp8EncoderQuantize(coefficient, step);
        AddCandidate(candidates, ref count, q);
        AddCandidate(candidates, ref count, q - 1);
        AddCandidate(candidates, ref count, q + 1);
        if (q >= 2 || q <= -2)
        {
            AddCandidate(candidates, ref count, q - 2);
            AddCandidate(candidates, ref count, q + 2);
        }
        return count;
    }

    private static void AddCandidate(int[] candidates, ref int count, int level)
    {
        if (level > MaxCoefficient) level = MaxCoefficient;
        if (level < -MaxCoefficient) level = -MaxCoefficient;
        for (int i = 0; i < count; i++)
            if (candidates[i] == level) return;
        if (count < MaxCandidates)
            candidates[count++] = level;
    }

    private static int Vp8EncoderQuantize(int coefficient, int dq)
    {
        if (coefficient == 0) return 0;
        int magnitude = coefficient < 0 ? -coefficient : coefficient;
        int quantized = (magnitude + (dq >> 1)) / dq;
        if (quantized > MaxCoefficient) quantized = MaxCoefficient;
        return coefficient < 0 ? -quantized : quantized;
    }

    private static int BitCost(byte[,,,] probs, int type, int band, int ctx, int node, int bit)
        => Vp8CostTable.Cost[probs[type, band, ctx, node], bit];

    private static int NonZeroCost(byte[,,,] probs, int type, int band, int ctx, int level)
    {
        int cost = BitCost(probs, type, band, ctx, 1, 1);
        int magnitude = Math.Abs(level);
        if (magnitude == 1)
        {
            cost += BitCost(probs, type, band, ctx, 2, 0);
        }
        else
        {
            cost += BitCost(probs, type, band, ctx, 2, 1);
            cost += LargeValueCost(probs, type, band, ctx, magnitude);
        }
        cost += Vp8CostTable.Cost[128, level < 0 ? 1 : 0];
        return cost;
    }

    /// <summary>对应 <c>Vp8Encoder.PutLargeValue</c>（幅值 ≥ 2）的位成本。</summary>
    private static int LargeValueCost(byte[,,,] probs, int type, int band, int ctx, int magnitude)
    {
        int cost = 0;
        if (magnitude <= 4)
        {
            cost += BitCost(probs, type, band, ctx, 3, 0);
            if (magnitude == 2)
            {
                cost += BitCost(probs, type, band, ctx, 4, 0);
            }
            else
            {
                cost += BitCost(probs, type, band, ctx, 4, 1);
                cost += BitCost(probs, type, band, ctx, 5, magnitude == 4 ? 1 : 0);
            }
            return cost;
        }

        cost += BitCost(probs, type, band, ctx, 3, 1);
        if (magnitude <= 10)
        {
            cost += BitCost(probs, type, band, ctx, 6, 0);
            if (magnitude <= 6)
            {
                cost += BitCost(probs, type, band, ctx, 7, 0);
                cost += Vp8CostTable.Cost[159, magnitude == 6 ? 1 : 0];
            }
            else
            {
                cost += BitCost(probs, type, band, ctx, 7, 1);
                if (magnitude <= 8)
                {
                    cost += Vp8CostTable.Cost[165, 0];
                    cost += Vp8CostTable.Cost[145, magnitude == 8 ? 1 : 0];
                }
                else
                {
                    cost += Vp8CostTable.Cost[165, 1];
                    cost += Vp8CostTable.Cost[145, magnitude == 10 ? 1 : 0];
                }
            }
            return cost;
        }

        cost += BitCost(probs, type, band, ctx, 6, 1);
        int category = magnitude <= 18 ? 0 : magnitude <= 34 ? 1 : magnitude <= 66 ? 2 : 3;
        int bit1 = category >= 2 ? 1 : 0;
        cost += BitCost(probs, type, band, ctx, 8, bit1);
        cost += BitCost(probs, type, band, ctx, 9 + bit1, category & 1);

        ReadOnlySpan<byte> table = category switch
        {
            0 => Vp8Tables.Cat3Probs,
            1 => Vp8Tables.Cat4Probs,
            2 => Vp8Tables.Cat5Probs,
            _ => Vp8Tables.Cat6Probs,
        };
        int extra = magnitude - (3 + (8 << category));
        for (int i = table.Length - 1, k = 0; i >= 0; i--, k++)
            cost += Vp8CostTable.Cost[table[k], (extra >> i) & 1];
        return cost;
    }
}

/// <summary>trellis 的预分配工作区（每个编码器实例一份，非线程共享）。</summary>
internal sealed class Vp8TrellisScratch
{
    internal readonly long[] Current = new long[Vp8Trellis.StateCount];
    internal readonly long[] Next = new long[Vp8Trellis.StateCount];

    /// <summary>[position, state] → 前驱 state / 选定电平。</summary>
    internal readonly int[] PreviousStates = new int[16 * Vp8Trellis.StateCount];
    internal readonly int[] PreviousLevels = new int[16 * Vp8Trellis.StateCount];

    internal readonly int[] Candidates = new int[5];

    internal int BestState;

    internal void SetBackPointer(int position, int state, int previousState, int level)
    {
        int index = position * Vp8Trellis.StateCount + state;
        PreviousStates[index] = previousState;
        PreviousLevels[index] = level;
    }

    internal int GetBackPointerState(int position, int state)
        => PreviousStates[position * Vp8Trellis.StateCount + state];

    internal int GetBackPointerLevel(int position, int state)
        => PreviousLevels[position * Vp8Trellis.StateCount + state];
}
