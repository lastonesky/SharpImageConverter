using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// 耗时统计方向：编码或解码。
/// </summary>
public enum GifTimingKind
{
    /// <summary>编码统计。</summary>
    Encode,

    /// <summary>解码统计。</summary>
    Decode,
}

/// <summary>
/// GIF 编解码的分阶段耗时统计。
/// 仅在 <c>EnableDiagnostics</c> 打开时采集，未涉及的阶段保持 0 且不出现在报表中。
/// 所有耗时以 <see cref="Stopwatch"/> 时间戳记录，避免高频调用时的委托与对象开销。
/// </summary>
public sealed class GifTiming
{
    private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// 创建一份统计实例。
    /// </summary>
    /// <param name="kind">编码或解码。</param>
    /// <param name="pixelCount">像素总数（单帧为 宽×高，动画为 宽×高×帧数）。</param>
    /// <param name="frameCount">帧数。</param>
    public GifTiming(GifTimingKind kind, int pixelCount = 0, int frameCount = 1)
    {
        Kind = kind;
        PixelCount = pixelCount;
        FrameCount = frameCount;
    }

    /// <summary>统计方向。</summary>
    public GifTimingKind Kind { get; }

    /// <summary>附加说明，例如 rgb24 / rgba32(alpha) / animation。</summary>
    public string? Label { get; set; }

    /// <summary>像素总数。</summary>
    public int PixelCount { get; set; }

    /// <summary>帧数。</summary>
    public int FrameCount { get; set; }

    /// <summary>调色板颜色数（编码时为实际生成的颜色数）。</summary>
    public int PaletteColors { get; set; }

    /// <summary>总耗时（tick）。</summary>
    public long TotalTicks { get; set; }

    /// <summary>前置转换耗时（tick）：RGBA 拆分、透明通道扫描、索引重映射。</summary>
    public long PrepareTicks { get; set; }

    /// <summary>颜色量化耗时（tick）：直方图构建 + 中位切分 + 调色板映射 + 抖动。</summary>
    public long QuantizeTicks { get; set; }

    /// <summary>容器读写耗时（tick）：头信息、全局/局部调色板、扩展块、图像描述符与尾块。</summary>
    public long HeaderTicks { get; set; }

    /// <summary>LZW 耗时（tick）：编码为压缩，解码为解压。</summary>
    public long LzwTicks { get; set; }

    /// <summary>像素渲染耗时（tick）：解码时把索引展开为 RGB/RGBA。</summary>
    public long RenderTicks { get; set; }

    /// <summary>画布维护耗时（tick）：背景填充、索引范围校验、disposal 恢复。</summary>
    public long BackgroundTicks { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public double TotalMs => ToMs(TotalTicks);

    /// <summary>前置转换耗时（毫秒）。</summary>
    public double PrepareMs => ToMs(PrepareTicks);

    /// <summary>颜色量化耗时（毫秒）。</summary>
    public double QuantizeMs => ToMs(QuantizeTicks);

    /// <summary>容器读写耗时（毫秒）。</summary>
    public double HeaderMs => ToMs(HeaderTicks);

    /// <summary>LZW 耗时（毫秒）。</summary>
    public double LzwMs => ToMs(LzwTicks);

    /// <summary>像素渲染耗时（毫秒）。</summary>
    public double RenderMs => ToMs(RenderTicks);

    /// <summary>画布维护耗时（毫秒）。</summary>
    public double BackgroundMs => ToMs(BackgroundTicks);

    /// <summary>未归入上述阶段的时间（毫秒），主要是解析循环、缓冲分配与文件 IO。</summary>
    public double OtherMs
    {
        get
        {
            double accounted = PrepareMs + QuantizeMs + HeaderMs + LzwMs + RenderMs + BackgroundMs;
            double rest = TotalMs - accounted;
            return rest > 0 ? rest : 0;
        }
    }

    /// <summary>吞吐率，单位百万像素/秒；总耗时为 0 时返回 0。</summary>
    public double MegapixelsPerSecond
    {
        get
        {
            double ms = TotalMs;
            if (ms <= 0 || PixelCount <= 0) return 0;
            return PixelCount / (ms * 1000.0);
        }
    }

    /// <summary>
    /// 把时间戳差值换算为毫秒。
    /// </summary>
    /// <param name="ticks">时间戳差值。</param>
    /// <returns>毫秒。</returns>
    public static double ToMs(long ticks) => ticks * TickToMs;

    /// <summary>
    /// 生成可读的多行耗时报表：首行为总计与吞吐，随后按占比从大到小列出各阶段。
    /// </summary>
    /// <returns>报表字符串。</returns>
    public string Format()
    {
        var sb = new StringBuilder(160);
        sb.Append("[gif-timing] ").Append(Kind == GifTimingKind.Encode ? "encode" : "decode");
        if (!string.IsNullOrEmpty(Label)) sb.Append('/').Append(Label);
        sb.Append(" total=").Append(Fmt(TotalMs)).Append("ms");

        double mpx = MegapixelsPerSecond;
        if (mpx > 0) sb.Append(" (").Append(Fmt(mpx)).Append(" Mpx/s)");
        if (PixelCount > 0) sb.Append(" pixels=").Append(PixelCount);
        if (FrameCount > 1) sb.Append(" frames=").Append(FrameCount);
        if (PaletteColors > 0) sb.Append(" colors=").Append(PaletteColors);

        AppendPhase(sb, "prepare", "预处理", PrepareMs);
        AppendPhase(sb, "quantize", "量化抖动", QuantizeMs);
        AppendPhase(sb, "header", "头/调色板", HeaderMs);
        AppendPhase(sb, "lzw", "LZW", LzwMs);
        AppendPhase(sb, "render", "像素展开", RenderMs);
        AppendPhase(sb, "background", "背景/校验", BackgroundMs);
        AppendPhase(sb, "other", "其余", OtherMs);
        return sb.ToString();
    }

    /// <summary>
    /// 输出统计报表：优先使用调用方提供的日志委托，否则回落到 <see cref="Trace"/>。
    /// </summary>
    /// <param name="timing">统计实例。</param>
    /// <param name="log">日志委托，为 null 时写入 Trace。</param>
    public static void Emit(GifTiming timing, Action<string>? log)
    {
        string report = timing.Format();
        if (log is not null) log(report);
        else Trace.WriteLine(report);
    }

    private void AppendPhase(StringBuilder sb, string name, string description, double ms)
    {
        // 低于 1 微秒的阶段只可能是计时噪声，不进报表
        if (ms < 0.001) return;
        double pct = TotalMs > 0 ? ms * 100.0 / TotalMs : 0;
        sb.Append("\n    ")
          .Append(name.PadRight(11))
          .Append(Fmt(ms).PadLeft(9))
          .Append("ms  ")
          .Append(Fmt(pct).PadLeft(5))
          .Append("%  ")
          .Append(description);
    }

    private static string Fmt(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
