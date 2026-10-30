using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

sealed partial class Program
{
    /// <summary>
    /// 目录批量处理的默认并行度（可被 <c>--parallel N</c> 覆盖）。
    /// <para>
    /// 编解码器内部本身已是多阶段并行流水线：JPEG 编码有 produce/dct/huffman 三段
    /// <c>Task.Run</c> 串联，<c>JpegImage</c>/<c>JpegReconstruct</c> 里还有成排按 MCU 行带的
    /// <c>Parallel.For</c>。外层若再按 <c>Environment.ProcessorCount</c>（本机 12）铺开文件，
    /// 就是 12 × 内部并行度 的超订，线程争抢反而使总吞吐崩掉。
    /// </para>
    /// <para>
    /// 实测（8 张 2048×2048 / 4.19MP 图，<c>--optimize</c>，墙钟）：
    /// <c>--parallel 1</c> 5567 ms、<b>2 → 3874 ms</b>、4 → 11515 ms、8 → 29360 ms、
    /// 12 → 25245 ms。即默认值比最优慢 6.5~7.7 倍，故取 2。
    /// </para>
    /// </summary>
    const int DefaultDirectoryParallelism = 2;

    /// <summary>
    /// 批次里不会出现 JPEG 编码时的目录默认并行度。
    /// </summary>
    /// <remarks>
    /// JPEG 编码器内部是并行流水线，对超订极敏感；PNG/GIF/WebP 没有这个悬崖。
    /// 8 张 2048²/4.19MP 图 <c>--optimize</c> 单次实测（墙钟，同一份二进制）：
    /// <code>
    /// 格式   p1      p2      p4      p8
    /// jpg    4705    3026   12548   13863     ← p4 起 4.1x 悬崖，故 JPEG 走 2
    /// png    3871    3365    3069    3136
    /// gif    5991    3551    2182    1511
    /// webp   7598    7093    7004    6926
    /// </code>
    /// 故非 JPEG 批次放宽到 4（PNG 在 4 最优，GIF 在 4 明显优于 2；GIF 在 8 还更快，
    /// 但那属于需要 <c>--parallel</c> 显式声明的激进档）。
    /// </remarks>
    const int DefaultDirectoryParallelismNoJpeg = 4;

    /// <summary>成功。</summary>
    const int ExitSuccess = 0;

    /// <summary>处理失败（解码/编码/IO 出错，或目录批量里有文件失败）。</summary>
    const int ExitFailure = 1;

    /// <summary>用法错误（未知参数、缺少输入路径、输入不存在）。</summary>
    const int ExitUsage = 2;

    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            PrintUsage();
            return ExitUsage;
        }

        // --help / --version 必须在「把 args[0] 当输入路径」之前处理，
        // 否则 sic --help 会报「输入文件不存在: --help」。
        if (HasAnyFlag(args, "--help", "-h", "-?"))
        {
            PrintUsage();
            return ExitSuccess;
        }
        if (HasAnyFlag(args, "--version"))
        {
            Console.WriteLine(GetVersionString());
            return ExitSuccess;
        }

        // "-" 表示标准输入：落成临时文件后，后续管线与处理真实文件完全一致
        using var stdio = new CliStdio();
        try
        {
            if (CliStdio.IsDash(args[0]))
            {
                args[0] = stdio.StageInput();
            }
            return Run(args, stdio);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"读取标准输入失败: {ex.GetType().Name}: {ex.Message}");
            return ExitFailure;
        }
    }

    /// <summary>
    /// 主流程。参数里的 <c>-</c> 已由 <see cref="Main(string[])"/> 换成真实的临时路径；
    /// <paramref name="stdio"/> 负责「输出到标准输出」的暂存与收尾。
    /// </summary>
    static int Run(string[] args, CliStdio stdio)
    {
        string inputPath = args[0];
        bool isDir = Directory.Exists(inputPath);
        bool isFile = File.Exists(inputPath);
        if (!isDir && !isFile)
        {
            Console.Error.WriteLine($"输入文件不存在: {inputPath}");
            Console.Error.WriteLine("用 --help 查看用法。");
            return ExitUsage;
        }
        // 参数解析与行为选项汇总
        var options = ParseOptions(args, inputPath);
        if (options.UnknownArguments.Count > 0)
        {
            // 未知参数不再被静默吞掉：此前 `--foo bar` 会把 bar 当成输出路径，
            // 于是文件被写到意料之外的位置，而命令行看起来"成功"了。
            foreach (string unknown in options.UnknownArguments)
            {
                Console.Error.WriteLine(DescribeUnknownArgument(unknown));
            }
            Console.Error.WriteLine("用 --help 查看支持的选项。");
            return ExitUsage;
        }
        options.IsDirectoryInput = isDir;
        // 输出 "→" 标准输出的校验与暂存（格式只能靠 --to 指定）
        if (!stdio.TryPrepareOutput(options, out int usageExit)) return usageExit;
        // 输出口径统一走 reporter：--quiet 只留错误，--json 只留一份结构化结果；
        // 结果写 stdout 时人读信息改走 stderr，避免插进二进制流
        if (options.Json)
        {
            // JSON 必须按 UTF-8 落盘：Windows 控制台默认代码页是 GBK，
            // 中文路径与画质摘要会被写成解析不了的字节（实测 JSONDecodeError）。
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        var reporter = new CliReporter(options.Json, options.Quiet, stdio.WritingStdout);
        string reportedInput = stdio.ReadingStdin ? CliStdio.Dash : options.InputPath;
        EnableGifDiagnostics(options);
        // JPEG 诊断走 Trace.WriteLine，默认无监听器时输出全部丢失，这里补一个控制台监听器
        if (options.JpegDebug)
        {
            Trace.Listeners.Add(new ConsoleTraceListener());
        }

        // GIF 耗时基准：只测量不转换（目录输入时该选项无效）
        if (options.GifBench.HasValue)
        {
            if (isDir)
            {
                Console.WriteLine("⚠️ --gif-bench 仅支持单文件输入，已忽略");
            }
            else
            {
                RunGifBench(options, options.GifBench.Value);
                return ExitSuccess;
            }
        }

        // JPEG 耗时基准：只测量不转换（目录输入时该选项无效）
        if (options.JpegBench.HasValue)
        {
            if (isDir)
            {
                Console.WriteLine("⚠️ --jpeg-bench 仅支持单文件输入，已忽略");
            }
            else
            {
                RunJpegBench(options, options.JpegBench.Value);
                return ExitSuccess;
            }
        }

        var swTotal = Stopwatch.StartNew();
        int failures = 0;
        string mode = "convert";
        try
        {
            if (options.Optimize)
            {
                // --optimize 与 --to / 目标扩展名同时给出 → 一步完成「转换 + 智能压缩」
                bool converted = TryResolveConvertTarget(options, isDir, out string targetExt);
                mode = converted ? "convert-optimize" : "optimize";
                failures = converted
                    ? RunConvertOptimize(options, isDir, targetExt, reporter)
                    : RunOptimize(options, isDir, reporter);
                swTotal.Stop();
                reporter.Info($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
                reporter.WriteJson(mode, reportedInput, reportedOutput(options), swTotal.ElapsedMilliseconds, failures);
                return Finish(failures);
            }

            bool wroteFile;
            if (isDir)
            {
                failures = ProcessDirectory(options, reporter);
                wroteFile = true;
            }
            else
            {
                wroteFile = Process(options, reporter);
                if (wroteFile && reporter.Collecting)
                {
                    // 目录路径由 ProcessDirectory 逐文件记账；单文件在这里补一条
                    reporter.AddFile(new CliFileResult
                    {
                        Input = reportedInput,
                        Output = reportedOutput(options),
                        Ok = true,
                        OriginalSize = new FileInfo(options.InputPath).Length,
                        OptimizedSize = options.OutputPath is { } p && File.Exists(p) ? new FileInfo(p).Length : 0,
                    });
                }
            }
            swTotal.Stop();
            if (wroteFile)
            {
                reporter.Info($"✅ 写入完成: {reportedOutput(options)}");
            }
            reporter.Info($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
            reporter.WriteJson(mode, reportedInput, reportedOutput(options), swTotal.ElapsedMilliseconds, failures);
            return Finish(failures);
        }
        catch (Exception ex)
        {
            swTotal.Stop();
            string prefix = options.UseStreamingDecoder ? "流式解码失败" : "处理失败";
            reporter.Error($"{prefix}: {ex.GetType().Name}: {ex.Message}");
            reporter.AddFile(new CliFileResult
            {
                Input = options.InputPath,
                Output = reportedOutput(options),
                Ok = false,
                Error = ex.Message,
            });
            reporter.WriteJson(mode, reportedInput, reportedOutput(options), swTotal.ElapsedMilliseconds, 1);
            return ExitFailure;
        }

        // 退出码与「把暂存结果送到 stdout」绑定在同一个地方：
        // 只有全成功才输出字节，失败时管道里什么都不留（写半截图片比不写更糟）
        int Finish(int failed)
        {
            if (failed == 0) stdio.FlushStdout();
            return failed == 0 ? ExitSuccess : ExitFailure;
        }

        // 报告里显示的路径：管道端点显示 "-"，临时文件只对实现可见
        string? reportedOutput(CliOptions o) =>
            stdio.WritingStdout ? CliStdio.Dash : o.OutputPath;
    }

    /// <summary>
    /// 把「不认识的参数」翻成可操作的提示：已移除的选项要说清为什么，别只说"未知参数"。
    /// </summary>
    static string DescribeUnknownArgument(string argument) => argument.ToLowerInvariant() switch
    {
        "--opt-webp-params" =>
            "--opt-webp-params 已在 1.3.0 移除（实测只有 0~1.3% 体积收益，却要多花 2~4 倍搜索时间），请省略该参数",
        _ => $"未知参数: {argument}",
    };

    /// <summary>
    /// 命令行里是否出现了指定的任一开关（大小写不敏感）。用于 --help / --version 这类
    /// 与业务参数无关、按惯例可出现在任意位置的元开关。
    /// </summary>
    static bool HasAnyFlag(string[] args, params string[] names)
    {
        for (int i = 0; i < args.Length; i++)
        {
            for (int n = 0; n < names.Length; n++)
            {
                if (string.Equals(args[i], names[n], StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 版本号取自库程序集（含 SourceLink 的 +sha 后缀，这里截掉 commit 部分）。
    /// </summary>
    static string GetVersionString()
    {
        var asm = typeof(ImageOptimizer).Assembly;
        string? informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = informational ?? asm.GetName().Version?.ToString() ?? "unknown";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        return $"SharpImageConverter {version}";
    }

    static void PrintUsage()
    {
        Console.WriteLine("用法: sic <输入文件或文件夹> [输出文件或文件夹] [操作] [选项]");
        Console.WriteLine("      sic --help | --version");
        Console.WriteLine("支持输入: .jpg/.jpeg/.png/.bmp/.webp/.gif");
        Console.WriteLine("支持输出: .jpg/.jpeg/.png/.bmp/.webp/.gif");
        Console.WriteLine("操作: resize:WxH | resizebilinear:WxH | resizefit:WxH | grayscale");
        Console.WriteLine("参数: --quality N | --subsample 420/422/444 | --jpeg-progressive | --keep-metadata | --idct int/float | --stream | --stream-strict | --memory-limit N | --no-memory-guard | --jpeg-debug | --gif-frames | --gray | --dithering on/off");
        Console.WriteLine("WebP 档位: --webp-method 0~6（默认 3；q>=75 时 method 4 自动降为 3（>140 MP 有 3~4 倍悬崖），6 最省体积最慢）");
        Console.WriteLine("JPEG 编码: 默认 baseline；--jpeg-progressive 输出 progressive（SOF2）；--jpeg-trellis 启用 RD 优化量化（同画质省 5%~15% 体积，编码耗时约 3~10 倍，默认关闭）");
        Console.WriteLine("GIF 耗时统计: --gif-debug（分阶段耗时）| --gif-bench N（重复 N 次取最小/中位/平均，默认 5）");
        Console.WriteLine("JPEG 耗时统计: --jpeg-bench N（重复 N 次；配合环境变量 SIC_JPEG_STAGE_TIMING=1 输出分阶段耗时）");
        Console.WriteLine("GIF 量化器: --gif-quantizer octree(默认,八叉树+Bayer) | wu/legacy(原 Wu+Floyd–Steinberg)");
        Console.WriteLine("GIF 抖动幅度: --gif-dither N (默认 8 = 一个量化步长; 调大会放大颗粒与缩放摩尔纹)");
        Console.WriteLine($"文件夹选项: --recursive | --to bmp/png/jpg/webp/gif | --parallel N（默认 {DefaultDirectoryParallelism}） | --skip-existing");
        Console.WriteLine("输出控制: --quiet（只打印错误）| --json（结果以单个 JSON 对象输出到 stdout，逐文件进度行不再打印；bench 类诊断输出不受影响）");
        Console.WriteLine("智能压缩: --optimize [--opt-quality N] [--opt-webp-min N] [--opt-webp-large-min N] [--max-colors N] [--no-dither] [--opt-no-png-deflate9] [--opt-no-jpeg-progressive] [--opt-jpeg-subsample-compare] [--opt-jpeg-trellis] [--opt-verbose] [--min-saving 百分比] [--opt-lossy-min-gain 百分比]");
        Console.WriteLine("  说明: 在画质基本不变的前提下把 JPG/PNG/GIF/WEBP/BMP 压到最小，默认输出 <原名>.min.<原扩展名>");
        Console.WriteLine("转换+压缩: --to <格式> --optimize   转成目标格式并直接输出该格式下最小体积（无需再 optimize 一遍）");
        Console.WriteLine("  示例: sic photo.jpg --to webp --optimize   →  photo.webp（已是最小体积）");
        Console.WriteLine("管道: - 代表标准输入/输出（如 sic - out.png --to png、cat a.png | sic - out.jpg），输出到 stdout 时必须用 --to 指定格式");
        Console.WriteLine("退出码: 0 成功 | 1 处理失败（含批量中有文件失败） | 2 用法错误（未知参数/输入不存在）");
    }
}
