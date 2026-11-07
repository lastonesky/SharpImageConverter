namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// 布尔编码的位成本表：给定 VP8 概率（0..255 表示写 1 的概率），查写 0 / 写 1 各需多少 bit。
///
/// 单位是 **1/256 bit**，这样整个 RD 评分可以用整数算完（浮点会让同一输入在不同平台上
/// 出现极小的分叉，进而改变宏块决策与位流）。
///
/// 与 <see cref="Vp8BoolEncoder"/> 的真实输出字节数有一定偏差（布尔编码的重归一化粒度、
/// 末字节补齐都会引入几个 bit 的误差），但用于比较「同一宏块的两种编码路径」时是准确的：
/// 两条路径的编码器状态与尾部开销几乎相同，差值是可靠的。
/// </summary>
internal static class Vp8CostTable
{
    /// <summary>
    /// costFlat[(prob &lt;&lt; 1) | bit]，单位 1/256 bit。
    /// 一维表：trellis 内层每次候选都要查若干次，二维索引的双界检查与乘法是热路径负担。
    /// </summary>
    internal static readonly int[] CostFlat = Build();

    /// <summary>按概率与位值取写位成本（1/256 bit）。</summary>
    internal static int Cost(int prob, int bit) => CostFlat[(prob << 1) | bit];

    private static int[] Build()
    {
        var table = new int[256 * 2];
        for (int p = 0; p < 256; p++)
        {
            // 注意 VP8 的 prob 是 **P(bit == 0)**，不是 P(bit == 1)：
            // Vp8BoolEncoder 里 bit=0 取 [0, split) 这一半，split 正比于 prob。
            // 反过来建表会让「高频事件」被算成昂贵（实测整体成本高估 4.75 倍，
            // 且高估倍数随内容漂移，足以让 i4x4 与 16×16 的相对比较完全失真）。
            double p0 = Math.Max(p, 1) / 256.0;
            double p1 = Math.Max(256 - p, 1) / 256.0;
            table[p * 2] = Math.Max(1, (int)Math.Round(-Math.Log2(p0) * 256));
            table[p * 2 + 1] = Math.Max(1, (int)Math.Round(-Math.Log2(p1) * 256));
        }
        return table;
    }
}
