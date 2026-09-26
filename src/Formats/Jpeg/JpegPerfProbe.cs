using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// JPEG 编解码分阶段计时探针。设置环境变量 <c>SIC_JPEG_STAGE_TIMING=1</c> 后，
/// 编码/解码各阶段的耗时会被记录进 <see cref="TakeLastRun"/> 返回的快照，
/// 供 CLI 的 <c>--jpeg-bench</c> 汇总成 min/中位/avg/max。
/// <para>
/// 设计要点（与 GIF 探针一致）：
/// ① 开关是 <c>static readonly</c>，同一份二进制内通过 env 切换，避免跨构建漂移；
/// ② 计时点全部放在「阶段级 / 批级」（每个 batch、每次扫描），不进像素级或块级热循环，
///    关闭时对热路径的影响可忽略；③ 关闭时不写任何字段，直接返回。
/// </para>
/// </summary>
internal static class JpegPerfProbe
{
    // ---- 阶段槽位 ----
    internal const int EncodeTotal = 0;   // 编码：WriteInternal 总耗时
    internal const int Produce = 1;       // 编码：采样生产（RGB→YCbCr 色彩转换）墙上时间
    internal const int ProduceWait = 2;   // 编码：生产者阻塞在入队（下游 DCT 背压）
    internal const int DctWall = 3;       // 编码：所有 DCT worker 墙上时间之和
    internal const int DctWait = 4;       // 编码：DCT worker 阻塞在取样/入队（上游慢/下游背压）
    internal const int Huffman = 5;       // 编码：Huffman 阶段墙上时间
    internal const int HuffmanWait = 6;   // 编码：Huffman 阻塞在取批
    internal const int DecodeTotal = 7;   // 解码：Parser.Decode 总耗时
    internal const int Entropy = 8;       // 解码：熵解码（DecodeScan）累计
    internal const int Reconstruct = 9;   // 解码：重建（IDCT+上采样+色彩转换+交织）累计
    internal const int IdctColor = 10;    // 解码：其中 SIMD 快路径（IDCT+YCbCr→RGB）部分
    internal const int StageCount = 11;

    internal static readonly string[] StageNames =
    [
        "encode-total", "produce", "produce-wait", "dct-wall(sum)", "dct-wait",
        "huffman", "huffman-wait", "decode-total", "entropy", "reconstruct", "idct-color"
    ];

    /// <summary>探针总开关。static readonly，调用点的 if 在关闭时由 JIT 消除。</summary>
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SIC_JPEG_STAGE_TIMING") == "1";

    private static readonly long[] ticks = new long[StageCount];
    private static readonly double[] lastRunMs = new double[StageCount];
    private static long beginTimestamp;
    private static int lastDctWorkers;

    /// <summary>DCT worker 数（编码），随快照一起返回，便于把 dct-wall(sum) 折算成单 worker 墙上时间。</summary>
    internal static int LastDctWorkers => lastDctWorkers;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Add(int stage, long deltaTicks)
    {
        if (!Enabled) return;
        Interlocked.Add(ref ticks[stage], deltaTicks);
    }

    internal static void SetDctWorkers(int workers)
    {
        if (!Enabled) return;
        lastDctWorkers = workers;
    }

    internal static void Begin()
    {
        if (!Enabled) return;
        Array.Clear(ticks);
        lastDctWorkers = 0;
        beginTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>结束一次编/解码：写入总耗时槽位并把累计 ticks 转成毫秒快照。</summary>
    internal static void End(int totalStage)
    {
        if (!Enabled) return;
        ticks[totalStage] += Stopwatch.GetTimestamp() - beginTimestamp;
        for (int i = 0; i < StageCount; i++)
        {
            lastRunMs[i] = ticks[i] * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>取走上一次编/解码的阶段快照（毫秒），并清零以便下一次记录。</summary>
    internal static double[] TakeLastRun()
    {
        if (!Enabled) return Array.Empty<double>();
        var result = (double[])lastRunMs.Clone();
        Array.Clear(ticks);
        Array.Clear(lastRunMs);
        return result;
    }

    internal static double ToMs(long ticksValue) => ticksValue * 1000.0 / Stopwatch.Frequency;
}
