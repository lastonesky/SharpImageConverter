using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpImageConverter;

/// <summary>
/// CLI 的输出与结果收集：把「人看的进度行」与「脚本看的 JSON」收在同一个地方，
/// 避免每个命令路径各自判断 --quiet / --json。
/// </summary>
/// <remarks>
/// 约定：
/// <list type="bullet">
/// <item><see cref="Info"/>：进度/汇总信息。<c>--quiet</c> 或 <c>--json</c> 时静默（JSON 模式下
/// 这些行会污染 stdout，脚本解析不了）。</item>
/// <item><see cref="Error"/>：错误。始终写 stderr（<c>--quiet</c> 也一样），
/// 退出码才是对外的唯一判定接口。</item>
/// <item><see cref="AddFile"/>：逐文件结果。仅 <c>--json</c> 时收集（并行调用，内部加锁）。</item>
/// </list>
/// </remarks>
internal sealed class CliReporter
{
    private readonly bool _json;
    private readonly bool _quiet;
    private readonly bool _infoToStderr;
    private readonly object _locker = new();
    private readonly List<CliFileResult> _files = [];

    /// <param name="json">是否输出 JSON 报告</param>
    /// <param name="quiet">是否静默（只留错误）</param>
    /// <param name="infoToStderr">
    /// 人读信息写到 stderr 而不是 stdout。结果本身就是二进制（<c>-</c> 输出）时必须打开，
    /// 否则进度行会插进图片字节流里。
    /// </param>
    public CliReporter(bool json, bool quiet, bool infoToStderr = false)
    {
        _json = json;
        _quiet = quiet;
        _infoToStderr = infoToStderr;
    }

    /// <summary>是否正在收集 JSON 结果（决定逐文件是否要记账）。</summary>
    public bool Collecting => _json;

    /// <summary>
    /// 打印进度/汇总信息。<c>--quiet</c> 或 <c>--json</c> 时丢弃。
    /// </summary>
    public void Info(string message)
    {
        if (_quiet || _json) return;
        if (_infoToStderr)
        {
            Console.Error.WriteLine(message);
            return;
        }
        Console.WriteLine(message);
    }

    /// <summary>
    /// 打印错误到 stderr。<c>--quiet</c> 不吞错误——否则脚本只知道「失败了」而不知道为什么。
    /// </summary>
    public void Error(string message) => Console.Error.WriteLine(message);

    /// <summary>
    /// 记录一个文件的结果（仅 JSON 模式）。
    /// </summary>
    public void AddFile(CliFileResult result)
    {
        if (!_json) return;
        lock (_locker) _files.Add(result);
    }

    /// <summary>
    /// 打印最终 JSON。只在 <c>--json</c> 时调用；<c>--quiet</c> 时不打印（要的就是安静）。
    /// </summary>
    /// <param name="mode">本次运行模式（convert / optimize / convert-optimize）</param>
    /// <param name="input">输入路径</param>
    /// <param name="output">输出路径或目录（可能为 null）</param>
    /// <param name="elapsedMs">总耗时（毫秒）</param>
    /// <param name="failed">失败文件数</param>
    public void WriteJson(string mode, string input, string? output, long elapsedMs, int failed)
    {
        if (!_json || _quiet) return;

        long before = 0, after = 0;
        int kept = 0;
        lock (_locker)
        {
            foreach (var f in _files)
            {
                before += f.OriginalSize;
                after += f.OptimizedSize;
                if (f.KeptOriginal) kept++;
            }
        }

        var payload = new CliRunReport
        {
            Mode = mode,
            Input = input,
            Output = output,
            ElapsedMs = elapsedMs,
            TotalFiles = _files.Count,
            Failed = failed,
            KeptOriginal = kept,
            OriginalBytes = before,
            ResultBytes = after,
            Files = _files,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload, CliJson.Options));
    }
}

/// <summary>
/// 一次运行的汇总（<c>--json</c> 的顶层对象）。
/// </summary>
internal sealed class CliRunReport
{
    /// <summary>运行模式：<c>convert</c> / <c>optimize</c> / <c>convert-optimize</c>。</summary>
    public string Mode { get; set; } = "convert";

    /// <summary>输入文件或目录。</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>输出文件或目录。</summary>
    public string? Output { get; set; }

    /// <summary>总耗时（毫秒）。</summary>
    public long ElapsedMs { get; set; }

    /// <summary>处理的文件数（不含失败的——失败见 <see cref="Failed"/>）。</summary>
    public int TotalFiles { get; set; }

    /// <summary>失败文件数。大于 0 时退出码为 1。</summary>
    public int Failed { get; set; }

    /// <summary>因无收益而保留原图的文件数。</summary>
    public int KeptOriginal { get; set; }

    /// <summary>原始总字节数。</summary>
    public long OriginalBytes { get; set; }

    /// <summary>结果总字节数。</summary>
    public long ResultBytes { get; set; }

    /// <summary>节省的字节数。</summary>
    public long SavedBytes => OriginalBytes - ResultBytes;

    /// <summary>逐文件结果。</summary>
    public List<CliFileResult> Files { get; set; } = [];
}

/// <summary>
/// 单个文件的结果（<c>--json</c> 的 <c>files[]</c> 元素）。
/// </summary>
internal sealed class CliFileResult
{
    /// <summary>输入文件。</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>输出文件。</summary>
    public string? Output { get; set; }

    /// <summary>是否成功。</summary>
    public bool Ok { get; set; }

    /// <summary>原始字节数。</summary>
    public long OriginalSize { get; set; }

    /// <summary>结果字节数。</summary>
    public long OptimizedSize { get; set; }

    /// <summary>是否因无收益而保留原图。</summary>
    public bool KeptOriginal { get; set; }

    /// <summary>是否发生了格式转换。</summary>
    public bool Converted { get; set; }

    /// <summary>optimize 未执行时的原因（成功执行时为 null）。</summary>
    public string? Reason { get; set; }

    /// <summary>采用的方案（质量/方法描述）。</summary>
    public string? Method { get; set; }

    /// <summary>画质摘要（与人类可读输出口径一致的字符串）。</summary>
    public string? Quality { get; set; }

    /// <summary>逐像素 PSNR（dB）。未做画质评估时（如跳过/失败）为 null。</summary>
    public double? Psnr { get; set; }

    /// <summary>4x4 块平均后的感知 PSNR（dB）。optimize 的判定依据。</summary>
    public double? PerceptualPsnr { get; set; }

    /// <summary>Y 通道 SSIM 均值（0-1）。</summary>
    public double? Ssim { get; set; }

    /// <summary>最差 1% 块的 SSIM（0-1）。</summary>
    public double? WorstSsim { get; set; }

    /// <summary>目标格式建议（跨格式时可能给出）。</summary>
    public string? Suggestion { get; set; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; set; }
}

/// <summary>
/// JSON 序列化设置：文件名里常有中文与反斜杠，用宽松编码器避免把路径转义成 <c>\uXXXX</c>。
/// </summary>
internal static class CliJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
