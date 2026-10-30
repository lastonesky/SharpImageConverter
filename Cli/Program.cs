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

sealed class Program
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
                Console.Error.WriteLine($"未知参数: {unknown}");
            }
            Console.Error.WriteLine("用 --help 查看支持的选项。");
            return ExitUsage;
        }
        options.IsDirectoryInput = isDir;
        // 输出口径统一走 reporter：--quiet 只留错误，--json 只留一份结构化结果
        if (options.Json)
        {
            // JSON 必须按 UTF-8 落盘：Windows 控制台默认代码页是 GBK，
            // 中文路径与画质摘要会被写成解析不了的字节（实测 JSONDecodeError）。
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        var reporter = new CliReporter(options.Json, options.Quiet);
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
                reporter.WriteJson(mode, options.InputPath, options.OutputPath, swTotal.ElapsedMilliseconds, failures);
                return failures == 0 ? ExitSuccess : ExitFailure;
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
                        Input = options.InputPath,
                        Output = options.OutputPath,
                        Ok = true,
                        OriginalSize = new FileInfo(options.InputPath).Length,
                        OptimizedSize = options.OutputPath is { } p && File.Exists(p) ? new FileInfo(p).Length : 0,
                    });
                }
            }
            swTotal.Stop();
            if (wroteFile)
            {
                reporter.Info($"✅ 写入完成: {options.OutputPath}");
            }
            reporter.Info($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
            reporter.WriteJson(mode, options.InputPath, options.OutputPath, swTotal.ElapsedMilliseconds, failures);
            return failures == 0 ? ExitSuccess : ExitFailure;
        }
        catch (Exception ex)
        {
            swTotal.Stop();
            string prefix = options.UseStreamingDecoder ? "流式解码失败" : "处理失败";
            reporter.Error($"{prefix}: {ex.GetType().Name}: {ex.Message}");
            reporter.AddFile(new CliFileResult
            {
                Input = options.InputPath,
                Output = options.OutputPath,
                Ok = false,
                Error = ex.Message,
            });
            reporter.WriteJson(mode, options.InputPath, options.OutputPath, swTotal.ElapsedMilliseconds, 1);
            return ExitFailure;
        }
    }

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
        Console.WriteLine("退出码: 0 成功 | 1 处理失败（含批量中有文件失败） | 2 用法错误（未知参数/输入不存在）");
    }

    /// <summary>
    /// 智能压缩模式：保持原格式输出体积最小的版本。
    /// </summary>
    /// <param name="options">CLI 选项</param>
    /// <param name="isDir">输入是否为目录</param>
    /// <param name="reporter">输出与结果收集（--quiet / --json 的落点）</param>
    /// <returns>失败的文件数（单文件模式为 0，失败时直接抛异常交给 Main）</returns>
    static int RunOptimize(CliOptions options, bool isDir, CliReporter reporter)
    {
        var opt = BuildOptimizeOptions(options);

        if (!isDir)
        {
            string output = options.OutputPath ?? ImageOptimizer.BuildOutputPath(options.InputPath);
            var result = ImageOptimizer.Optimize(options.InputPath, output, opt);
            reporter.AddFile(ToFileResult(result));
            PrintOptimizeResult(result, reporter);
            options.OutputPath = output;
            return 0;
        }

        string inputDir = options.InputPath;
        string outDir = options.OutputPath ?? inputDir;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif" };
        var files = Directory.EnumerateFiles(inputDir, "*.*", searchOption).Where(f => exts.Contains(Path.GetExtension(f))).ToList();
        int parallel = ResolveDirectoryParallelism(options, files, optimizing: true, targetExt: null);
        long savedTotal = 0, beforeTotal = 0, afterTotal = 0;
        int kept = 0;
        int failed = 0;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallel }, file =>
        {
            string? target = null;
            try
            {
                string rel = Path.GetRelativePath(inputDir, file);
                string targetDir = options.OutputPath != null
                    ? Path.Combine(outDir, Path.GetDirectoryName(rel) ?? ".")
                    : Path.GetDirectoryName(file) ?? ".";
                Directory.CreateDirectory(targetDir);
                string nameNoExt = Path.GetFileNameWithoutExtension(file);
                string ext = Path.GetExtension(file);
                // 指定输出目录时保持原文件名，否则在原目录生成 .min 副本
                target = Path.Combine(targetDir, options.OutputPath != null ? nameNoExt + ext : nameNoExt + ".min" + ext);
                if (options.SkipExisting && File.Exists(target)) return;

                var result = ImageOptimizer.Optimize(file, target, opt);
                lock (files)
                {
                    beforeTotal += result.OriginalSize;
                    afterTotal += result.OptimizedSize;
                    savedTotal += result.SavedBytes;
                    if (result.KeptOriginal) kept++;
                }
                reporter.AddFile(ToFileResult(result));
                PrintOptimizeResult(result, reporter);
            }
            catch (Exception ex)
            {
                lock (files) failed++;
                reporter.Error($"❌ {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
                reporter.AddFile(new CliFileResult { Input = file, Output = target, Ok = false, Error = ex.Message });
            }
        });

        double ratio = beforeTotal > 0 ? (double)savedTotal / beforeTotal : 0;
        reporter.Info($"✅ 智能压缩完成: {files.Count} 个文件，{FormatSize(beforeTotal)} → {FormatSize(afterTotal)}（省 {ratio:P1}）{(kept > 0 ? $"，{kept} 个无收益已保留原图" : "")}{(failed > 0 ? $"，失败 {failed} 个" : "")}");
        options.OutputPath = outDir;
        return failed;
    }

    /// <summary>
    /// 判断 <c>--optimize</c> 是否应走「转换 + 智能压缩」路径，并给出目标扩展名。
    /// 命中条件：显式给了 <c>--to</c>，或输出路径带一个与源格式不同的可识别扩展名。
    /// 目标与源同扩展名时视为「同格式压缩」，交给原本的 <see cref="RunOptimize"/>。
    /// </summary>
    static bool TryResolveConvertTarget(CliOptions options, bool isDir, out string targetExt)
    {
        targetExt = string.Empty;
        string sourceExt = Path.GetExtension(options.InputPath);

        if (!string.IsNullOrEmpty(options.OutputExtension))
        {
            string ext = options.OutputExtension!;
            if (!IsSupportedImageExtension(ext)) return false;
            // 单文件且目标扩展名与源一致：等价于同格式压缩
            if (!isDir && string.Equals(ext, sourceExt, StringComparison.OrdinalIgnoreCase)) return false;
            targetExt = ext;
            return true;
        }

        if (!isDir && !string.IsNullOrEmpty(options.OutputPath))
        {
            string ext = Path.GetExtension(options.OutputPath).ToLowerInvariant();
            if (IsSupportedImageExtension(ext)
                && !string.Equals(ext, sourceExt, StringComparison.OrdinalIgnoreCase))
            {
                targetExt = ext;
                return true;
            }
        }

        return false;
    }

    static bool IsSupportedImageExtension(string ext) =>
        ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif";

    /// <summary>
    /// 转换 + 智能压缩：把输入转成目标格式，并直接产出该格式下体积最小的版本，
    /// 无需再单独跑一遍 <c>--optimize</c>。
    /// </summary>
    /// <returns>失败的文件数</returns>
    static int RunConvertOptimize(CliOptions options, bool isDir, string targetExt, CliReporter reporter)
    {
        var opt = BuildOptimizeOptions(options);

        if (!isDir)
        {
            string output = !string.IsNullOrEmpty(options.OutputPath)
                ? options.OutputPath!
                : Path.ChangeExtension(options.InputPath, targetExt);
            var result = ImageOptimizer.Optimize(options.InputPath, output, opt);
            reporter.AddFile(ToFileResult(result));
            PrintOptimizeResult(result, reporter);
            options.OutputPath = result.OutputPath;
            return 0;
        }

        string inputDir = options.InputPath;
        string outDir = options.OutputPath ?? inputDir;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif" };
        var files = Directory.EnumerateFiles(inputDir, "*.*", searchOption).Where(f => exts.Contains(Path.GetExtension(f))).ToList();
        int parallel = ResolveDirectoryParallelism(options, files, optimizing: true, targetExt);
        long savedTotal = 0, beforeTotal = 0, afterTotal = 0;
        int converted = 0;
        int failed = 0;
        var locker = new object();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallel }, file =>
        {
            string? target = null;
            try
            {
                string rel = Path.GetRelativePath(inputDir, file);
                string targetDir = Path.Combine(outDir, Path.GetDirectoryName(rel) ?? ".");
                Directory.CreateDirectory(targetDir);
                string nameNoExt = Path.GetFileNameWithoutExtension(file);
                target = Path.Combine(targetDir, nameNoExt + targetExt);
                // --to 与源扩展名相同且输出到原目录时，改用 .min 避免覆盖源文件
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    target = Path.Combine(targetDir, nameNoExt + ".min" + targetExt);
                }
                if (options.SkipExisting && File.Exists(target)) return;

                var result = ImageOptimizer.Optimize(file, target, opt);
                lock (locker)
                {
                    beforeTotal += result.OriginalSize;
                    afterTotal += result.OptimizedSize;
                    savedTotal += result.SavedBytes;
                    converted++;
                }
                reporter.AddFile(ToFileResult(result));
                PrintOptimizeResult(result, reporter);
            }
            catch (Exception ex)
            {
                lock (locker) failed++;
                reporter.Error($"❌ {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
                reporter.AddFile(new CliFileResult { Input = file, Output = target, Ok = false, Error = ex.Message });
            }
        });

        reporter.Info($"✅ 转换+智能压缩完成: {converted} 个文件 → {targetExt}，{FormatSize(beforeTotal)} → {FormatSize(afterTotal)}{(failed > 0 ? $"，失败 {failed} 个" : "")}");
        options.OutputPath = outDir;
        return failed;
    }

    /// <summary>
    /// 把 CLI 选项映射为 <see cref="OptimizeOptions"/>。
    /// </summary>
    static OptimizeOptions BuildOptimizeOptions(CliOptions options)
    {
        var opt = new OptimizeOptions
        {
            StripMetadata = !options.KeepMetadata,
            EnableDithering = options.Dithering && !options.NoDither,
            PngMaxDeflateOnWinner = !options.NoPngDeflate9,
            JpegProgressive = !options.NoJpegProgressive,
            JpegCompareSubsampling = options.JpegSubsampleCompare && !options.NoJpegSubsampleCompare,
            JpegTrellisQuantization = options.JpegTrellis,
            MemoryLimitBytes = options.MemoryLimitMb is > 0 ? options.MemoryLimitMb.Value * 1024L * 1024L : 0,
            DisableMemoryGuard = options.NoMemoryGuard,
            // --quiet / --json 下连候选评估日志也要关：前者要安静，后者要 stdout 干净
            Log = options.OptimizeVerbose && !(options.Quiet || options.Json) ? Console.WriteLine : null,
        };
        if (options.OptimizeQuality.HasValue) opt.TargetQuality = options.OptimizeQuality.Value;
        if (options.OptimizeWebpMin.HasValue) opt.WebpMinQuality = options.OptimizeWebpMin.Value;
        if (options.OptimizeWebpLargeMin.HasValue) opt.WebpLargeImageMinQuality = options.OptimizeWebpLargeMin.Value;
        if (options.MaxColors.HasValue) opt.MaxColors = options.MaxColors.Value;
        if (options.MinSaving.HasValue) opt.MinSavingRatio = options.MinSaving.Value / 100.0;
        if (options.OptimizeLossyMinGain.HasValue) opt.MinLossyGainRatio = options.OptimizeLossyMinGain.Value / 100.0;
        if (options.OptimizeMinSsim.HasValue) opt.MinSsim = options.OptimizeMinSsim.Value / 100.0;
        if (options.OptimizeMinWorstSsim.HasValue) opt.MinWorstSsim = options.OptimizeMinWorstSsim.Value / 100.0;
        if (options.JpegQuality.HasValue) opt.JpegQuality = options.JpegQuality.Value;
        return OptimizeOptions.Normalize(opt);
    }

    /// <summary>
    /// 打印单个文件的压缩结果（<c>--quiet</c> / <c>--json</c> 时静默）。
    /// </summary>
    static void PrintOptimizeResult(OptimizationResult result, CliReporter reporter)
    {
        if (result.Reason != null)
        {
            reporter.Info($"⚠️ {Path.GetFileName(result.InputPath)}: 未压缩（{result.Reason}）");
            return;
        }
        if (result.KeptOriginal)
        {
            reporter.Info($"➖ {Path.GetFileName(result.InputPath)}: 无收益，保留原图（{FormatSize(result.OriginalSize)}）");
            return;
        }
        if (result.Converted)
        {
            reporter.Info($"✅ {Path.GetFileName(result.InputPath)} → {Path.GetFileName(result.OutputPath)}: {FormatSize(result.OriginalSize)} → {FormatSize(result.OptimizedSize)} | {result.Method} | {result.Quality}");
            if (!string.IsNullOrEmpty(result.Suggestion))
            {
                reporter.Info($"   💡 目标格式建议: {result.Suggestion}");
            }
            return;
        }
        reporter.Info($"✅ {Path.GetFileName(result.InputPath)}: {FormatSize(result.OriginalSize)} → {FormatSize(result.OptimizedSize)}（省 {result.SavedRatio:P1}）| {result.Method} | {result.Quality}");
    }

    /// <summary>
    /// 把压缩结果转成 <c>--json</c> 的逐文件记录。
    /// </summary>
    static CliFileResult ToFileResult(OptimizationResult result) => new()
    {
        Input = result.InputPath,
        Output = result.OutputPath,
        // 拿到 OptimizationResult 就算没出错：跳过（Reason）与「无收益保留原图」都不是失败，
        // 它们各自的口径写在 Reason / KeptOriginal 里。真正的失败走 catch 分支。
        Ok = true,
        OriginalSize = result.OriginalSize,
        OptimizedSize = result.OptimizedSize,
        KeptOriginal = result.KeptOriginal,
        Converted = result.Converted,
        Reason = result.Reason,
        Method = result.Method,
        Quality = result.Reason is null ? result.Quality.ToString() : null,
        Psnr = result.Reason is null ? result.Quality.Psnr : null,
        PerceptualPsnr = result.Reason is null ? result.Quality.PerceptualPsnr : null,
        Ssim = result.Reason is null ? result.Quality.Ssim : null,
        WorstSsim = result.Reason is null ? result.Quality.WorstSsim : null,
        Suggestion = result.Suggestion,
    };

    /// <summary>
    /// 人类可读的体积表示。
    /// </summary>
    static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.00} MB";
    }

    static CliOptions ParseOptions(string[] args, string inputPath)
    {
        var options = new CliOptions(inputPath);
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--gif-frames", StringComparison.OrdinalIgnoreCase))
            {
                options.GifFrames = true;
                continue;
            }
            if (string.Equals(a, "--jpeg-debug", StringComparison.OrdinalIgnoreCase))
            {
                options.JpegDebug = true;
                continue;
            }
            if (string.Equals(a, "--gif-debug", StringComparison.OrdinalIgnoreCase))
            {
                options.GifDebug = true;
                continue;
            }
            if (string.Equals(a, "--gif-bench", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int benchCount) && benchCount > 0)
                {
                    options.GifBench = benchCount;
                    i++;
                }
                else
                {
                    options.GifBench = 5;
                }
                continue;
            }
            if (string.Equals(a, "--jpeg-bench", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int jpegBenchCount) && jpegBenchCount > 0)
                {
                    options.JpegBench = jpegBenchCount;
                    i++;
                }
                else
                {
                    options.JpegBench = 5;
                }
                continue;
            }
            if (string.Equals(a, "--gif-quantizer", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    options.GifQuantizer = args[i + 1].Trim().ToLowerInvariant();
                    i++;
                }
                continue;
            }
            if (string.Equals(a, "--gif-dither", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int ds) && ds >= 0)
                {
                    options.GifDitherStrength = ds;
                    i++;
                }
                continue;
            }
            if (string.Equals(a, "--dithering", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    string v = args[i + 1].Trim().ToLowerInvariant();
                    if (v is "on" or "true" or "1") options.Dithering = true;
                    else if (v is "off" or "false" or "0") options.Dithering = false;
                    i++;
                }
                continue;
            }
            if (string.Equals(a, "--optimize", StringComparison.OrdinalIgnoreCase))
            {
                options.Optimize = true;
                continue;
            }
            if (string.Equals(a, "--opt-verbose", StringComparison.OrdinalIgnoreCase))
            {
                options.OptimizeVerbose = true;
                continue;
            }
            if (string.Equals(a, "--no-dither", StringComparison.OrdinalIgnoreCase))
            {
                options.NoDither = true;
                continue;
            }
            if (string.Equals(a, "--opt-no-png-deflate9", StringComparison.OrdinalIgnoreCase))
            {
                options.NoPngDeflate9 = true;
                continue;
            }
            if (string.Equals(a, "--jpeg-progressive", StringComparison.OrdinalIgnoreCase))
            {
                options.JpegProgressive = true;
                continue;
            }
            if (string.Equals(a, "--jpeg-trellis", StringComparison.OrdinalIgnoreCase))
            {
                // RD 优化量化：只在显式请求时启用（每块退回标量 FDCT + DP，编码耗时约为最近值的 3~10 倍）。
                options.JpegTrellis = true;
                continue;
            }
            if (string.Equals(a, "--opt-no-jpeg-progressive", StringComparison.OrdinalIgnoreCase))
            {
                options.NoJpegProgressive = true;
                continue;
            }
            if (string.Equals(a, "--opt-jpeg-subsample-compare", StringComparison.OrdinalIgnoreCase))
            {
                options.JpegSubsampleCompare = true;
                continue;
            }
            if (string.Equals(a, "--opt-no-jpeg-subsample-compare", StringComparison.OrdinalIgnoreCase))
            {
                options.NoJpegSubsampleCompare = true;
                continue;
            }
            if (string.Equals(a, "--no-memory-guard", StringComparison.OrdinalIgnoreCase))
            {
                options.NoMemoryGuard = true;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--memory-limit", out int? memoryLimit))
            {
                options.MemoryLimitMb = memoryLimit is > 0 ? memoryLimit : null;
                continue;
            }
            if (string.Equals(a, "--opt-webp-params", StringComparison.OrdinalIgnoreCase))
            {
                // 已移除：语料实测只有 0~1.3% 体积收益，却要多花 2~4 倍搜索时间。
                Console.Error.WriteLine("⚠️ --opt-webp-params 已移除（收益 0~1.3%、耗时 2~4 倍），本次忽略");
                continue;
            }
            if (string.Equals(a, "--opt-jpeg-trellis", StringComparison.OrdinalIgnoreCase))
            {
                options.JpegTrellis = true;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--opt-quality", out int? optQuality))
            {
                options.OptimizeQuality = optQuality;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--opt-webp-min", out int? optWebpMin))
            {
                options.OptimizeWebpMin = optWebpMin;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--opt-webp-large-min", out int? optWebpLargeMin))
            {
                options.OptimizeWebpLargeMin = optWebpLargeMin;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--opt-lossy-min-gain", out int? optLossyMinGain))
            {
                options.OptimizeLossyMinGain = optLossyMinGain;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--opt-min-ssim", out int? optMinSsim))
            {
                options.OptimizeMinSsim = optMinSsim;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--opt-min-worst-ssim", out int? optMinWorstSsim))
            {
                options.OptimizeMinWorstSsim = optMinWorstSsim;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--max-colors", out int? maxColors))
            {
                options.MaxColors = maxColors;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--min-saving", out int? minSaving))
            {
                options.MinSaving = minSaving;
                continue;
            }
            if (string.Equals(a, "--gray", StringComparison.OrdinalIgnoreCase))
            {
                options.Gray = true;
                continue;
            }
            if (string.Equals(a, "--stream", StringComparison.OrdinalIgnoreCase))
            {
                options.UseStreamingDecoder = true;
                continue;
            }
            if (string.Equals(a, "--stream-strict", StringComparison.OrdinalIgnoreCase))
            {
                options.UseStreamingDecoder = true;
                options.RequireEoi = true;
                continue;
            }
            if (string.Equals(a, "--keep-metadata", StringComparison.OrdinalIgnoreCase))
            {
                options.KeepMetadata = true;
                continue;
            }
            if (string.Equals(a, "--recursive", StringComparison.OrdinalIgnoreCase))
            {
                options.Recursive = true;
                continue;
            }
            if (string.Equals(a, "--skip-existing", StringComparison.OrdinalIgnoreCase))
            {
                options.SkipExisting = true;
                continue;
            }
            if (string.Equals(a, "--json", StringComparison.OrdinalIgnoreCase))
            {
                options.Json = true;
                continue;
            }
            if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
            {
                options.Quiet = true;
                continue;
            }
            if (string.Equals(a, "--parallel", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    options.Parallelism = p;
                    i++;
                }
                continue;
            }
            if (a.StartsWith("--to=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--out-ext=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a.Contains('=') ? a[(a.IndexOf('=') + 1)..].Trim() : string.Empty;
                if (!string.IsNullOrEmpty(v))
                {
                    options.OutputExtension = NormalizeOutputExtension(v.StartsWith('.') ? v : "." + v);
                }
                continue;
            }
            if (string.Equals(a, "--to", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--out-ext", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    string v2 = args[i + 1].Trim();
                    options.OutputExtension = NormalizeOutputExtension(v2.StartsWith('.') ? v2 : "." + v2);
                    i++;
                }
                continue;
            }
            if (TryParseQuality(args, ref i, out int? quality))
            {
                options.JpegQuality = quality;
                continue;
            }
            if (TryParseNamedInt(args, ref i, "--webp-method", out int? webpMethod))
            {
                if (webpMethod is >= 0 and <= 6) options.WebpMethod = webpMethod;
                else Console.Error.WriteLine($"⚠️ --webp-method 取值需为 0~6，已忽略: {webpMethod}");
                continue;
            }
            if (TryParseSubsample(args, ref i, out JpegChromaSubsampling? subsampling))
            {
                options.JpegSubsampling = subsampling;
                options.Subsample420 = subsampling == JpegChromaSubsampling.Yuv420;
                continue;
            }
            if (TryParseIdct(args, ref i, out bool? useFloatIdct))
            {
                if (useFloatIdct.HasValue) options.UseFloatIdct = useFloatIdct.Value;
                continue;
            }
            if (TryParseOperation(a, options.Operations))
            {
                continue;
            }
            if (!a.StartsWith('-'))
            {
                if (options.OutputPath == null)
                {
                    options.OutputPath = a;
                }
                else
                {
                    // 多余的第二个位置参数：以前被静默忽略，用户以为生效了
                    options.UnknownArguments.Add(a);
                }
                continue;
            }
            // 走到这里的开关没有任何分支认领：以前 `--foo bar` 会把 bar 当成输出路径，
            // 文件被写到意料之外的地方而命令行看起来"成功"。现在一律报用法错误。
            options.UnknownArguments.Add(a);
        }
        return options;
    }

    /// <summary>
    /// 解析 "--name N" 或 "--name=N" 形式的整数选项，返回是否命中。
    /// </summary>
    static bool TryParseNamedInt(string[] args, ref int i, string name, out int? value)
    {
        value = null;
        string a = args[i];
        if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            string raw = a[(name.Length + 1)..].Trim();
            if (int.TryParse(raw, out int parsed)) value = parsed;
            return true;
        }
        if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed))
            {
                value = parsed;
                i++;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 目录批量转换。
    /// </summary>
    /// <returns>失败的文件数</returns>
    static int ProcessDirectory(CliOptions options, CliReporter reporter)
    {
        string inputDir = options.InputPath;
        string outDir = options.OutputPath ?? inputDir;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif" };
        var files = Directory.EnumerateFiles(inputDir, "*.*", searchOption).Where(f => exts.Contains(Path.GetExtension(f))).ToList();
        string defaultExt = options.OutputExtension ?? (options.Operations.Count == 0 ? ".png" : ".bmp");
        int parallel = ResolveDirectoryParallelism(options, files, optimizing: false, defaultExt);
        // 纯转换路径没有候选并行可叠加，libwebp 自身也是单线程；但编码入口默认走 EncodeLock，
        // 会把外层并行整体串行化，故这条路径显式改用 Parallel。
        if (defaultExt.Equals(".webp", StringComparison.OrdinalIgnoreCase) && parallel > 1)
        {
            options.ParallelWebpEncode = true;
        }
        int success = 0, fail = 0;
        var locker = new object();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallel }, file =>
        {
            string? resolved = null;
            try
            {
                string rel = Path.GetRelativePath(inputDir, file);
                string targetDir = options.OutputPath != null ? Path.Combine(outDir, Path.GetDirectoryName(rel) ?? ".") : Path.GetDirectoryName(file) ?? ".";
                Directory.CreateDirectory(targetDir);
                string nameNoExt = Path.GetFileNameWithoutExtension(file);
                string desired = Path.Combine(targetDir, nameNoExt + defaultExt);
                if (options.SkipExisting && File.Exists(desired))
                {
                    lock (locker) success++;
                    return;
                }
                resolved = ResolveOutputPath(file, desired, defaultExt);
                var perFileOptions = CloneOptionsForFile(options, file, resolved);
                // Process 返回 false 只表示「没有单个输出文件」（GIF 拆帧），不是失败
                Process(perFileOptions, reporter);
                reporter.AddFile(new CliFileResult
                {
                    Input = file,
                    Output = resolved,
                    Ok = true,
                    OriginalSize = new FileInfo(file).Length,
                    OptimizedSize = File.Exists(resolved) ? new FileInfo(resolved).Length : 0,
                });
                lock (locker) success++;
            }
            catch (Exception ex)
            {
                // 此前这里是个裸 catch：失败只累加计数，连文件名都不打印，
                // 批量跑完只看到"失败 N"，无从定位。
                lock (locker) fail++;
                reporter.Error($"❌ {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
                reporter.AddFile(new CliFileResult { Input = file, Output = resolved, Ok = false, Error = ex.Message });
            }
        });
        reporter.Info($"✅ 转换完成: 成功 {success}，失败 {fail}");
        options.OutputPath = outDir;
        return fail;
    }

    /// <summary>
    /// 目录批量的外层并行度：显式 <c>--parallel N</c> 优先，否则按「本批次会不会与内层并行叠加 /
    /// 会不会踩 JPEG 的超订悬崖」来选。
    /// </summary>
    /// <param name="options">CLI 选项</param>
    /// <param name="files">本批次文件</param>
    /// <param name="optimizing">是否走 optimize（决定内层是否有并行）</param>
    /// <param name="targetExt">统一的目标扩展名；为 null 表示按各自格式原地处理</param>
    static int ResolveDirectoryParallelism(CliOptions options, IReadOnlyCollection<string> files, bool optimizing, string? targetExt)
    {
        if (options.Parallelism.HasValue && options.Parallelism.Value > 0) return options.Parallelism.Value;

        string? outExt = targetExt;
        bool anyJpeg = false, anyWebp = false;
        foreach (string f in files)
        {
            string e = Path.GetExtension(f);
            if (IsJpegExt(e)) anyJpeg = true;
            else if (e.Equals(".webp", StringComparison.OrdinalIgnoreCase)) anyWebp = true;
        }
        bool jpegOutput = outExt is not null ? IsJpegExt(outExt) : anyJpeg;
        bool webpOptimize = optimizing
            && (outExt is not null
                ? outExt.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                : anyWebp);

        // WebP 的 optimize 内部已按候选并行铺满（libwebp 编码是单线程，候选并行是它唯一的多核杠杆），
        // 外层再并行就会与内层叠加，故保持 1。
        if (webpOptimize) return 1;

        // 其余情况按「本批次是否可能出现 JPEG 编码」取保守/放宽值。
        return jpegOutput ? DefaultDirectoryParallelism : DefaultDirectoryParallelismNoJpeg;
    }

    static bool IsJpegExt(string ext) =>
        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    static CliOptions CloneOptionsForFile(CliOptions src, string inputFile, string outputFile)
    {
        var dst = new CliOptions(inputFile)
        {
            OutputPath = outputFile,
            JpegQuality = src.JpegQuality,
            Subsample420 = src.Subsample420,
            JpegSubsampling = src.JpegSubsampling,
            KeepMetadata = src.KeepMetadata,
            GifFrames = src.GifFrames,
            Dithering = src.Dithering,
            UseFloatIdct = src.UseFloatIdct,
            UseStreamingDecoder = src.UseStreamingDecoder,
            Gray = src.Gray,
            GifDebug = src.GifDebug,
            GifBench = src.GifBench,
            GifQuantizer = src.GifQuantizer,
            GifDitherStrength = src.GifDitherStrength,
            Optimize = src.Optimize,
            OptimizeQuality = src.OptimizeQuality,
            OptimizeWebpMin = src.OptimizeWebpMin,
            OptimizeWebpLargeMin = src.OptimizeWebpLargeMin,
            OptimizeLossyMinGain = src.OptimizeLossyMinGain,
            OptimizeMinSsim = src.OptimizeMinSsim,
            OptimizeMinWorstSsim = src.OptimizeMinWorstSsim,
            MaxColors = src.MaxColors,
            NoDither = src.NoDither,
            NoPngDeflate9 = src.NoPngDeflate9,
            JpegProgressive = src.JpegProgressive,
            NoJpegProgressive = src.NoJpegProgressive,
            JpegSubsampleCompare = src.JpegSubsampleCompare,
            NoJpegSubsampleCompare = src.NoJpegSubsampleCompare,
            JpegTrellis = src.JpegTrellis,
            MemoryLimitMb = src.MemoryLimitMb,
            NoMemoryGuard = src.NoMemoryGuard,
            ForceStreaming = src.ForceStreaming,
            OptimizeVerbose = src.OptimizeVerbose,
            MinSaving = src.MinSaving,
            ParallelWebpEncode = src.ParallelWebpEncode,
            WebpMethod = src.WebpMethod,
        };
        foreach (var op in src.Operations) dst.Operations.Add(op);
        return dst;
    }

    /// <summary>
    /// 创建 GIF 解码器，按 --gif-debug 决定是否输出分阶段耗时。
    /// </summary>
    static GifDecoder CreateGifDecoder(CliOptions options) => new()
    {
        EnableDiagnostics = options.GifDebug,
        DiagnosticsLog = options.GifDebug ? Console.WriteLine : null,
    };

    /// <summary>
    /// 开启 GIF 耗时统计：把带诊断开关的编解码器注册进默认配置，
    /// 这样经 Image.Load / Image.Save 走注册表实例的路径也会输出分阶段耗时。
    /// </summary>
    static void EnableGifDiagnostics(CliOptions options)
    {
        if (!options.GifDebug) return;
        Action<string> log = Console.WriteLine;
        Configuration.Default.RegisterDecoder<GifFormat>(new GifDecoder { EnableDiagnostics = true, DiagnosticsLog = log });
        Configuration.Default.RegisterDecoderRgba<GifFormat>(new GifDecoderRgbaAdapter { EnableDiagnostics = true, DiagnosticsLog = log });
        Configuration.Default.RegisterEncoder<GifFormat>(new GifEncoderAdapter { EnableDithering = options.Dithering, QuantizerKind = ParseGifQuantizer(options.GifQuantizer), DitherStrength = options.GifDitherStrength, EnableDiagnostics = true, DiagnosticsLog = log });
        Configuration.Default.RegisterEncoderRgba<GifFormat>(new GifEncoderAdapterRgba { EnableDithering = options.Dithering, QuantizerKind = ParseGifQuantizer(options.GifQuantizer), DitherStrength = options.GifDitherStrength, EnableDiagnostics = true, DiagnosticsLog = log });
    }

    /// <summary>
    /// 将 CLI 字符串映射为量化器枚举：wu/legacy/floyd/fs 走原 Wu + Floyd–Steinberg；
    /// 其余（含 octree/空）走默认八叉树 + Bayer。
    /// </summary>
    static GifQuantizerKind ParseGifQuantizer(string? v) =>
        string.Equals(v, "wu", StringComparison.OrdinalIgnoreCase)
        || string.Equals(v, "legacy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(v, "floyd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(v, "fs", StringComparison.OrdinalIgnoreCase)
            ? GifQuantizerKind.WuFloydSteinberg
            : GifQuantizerKind.OctreeBayer;

    /// <summary>
    /// GIF 编解码基准：重复 N 次并给出各阶段的最小/中位/平均值，便于对比优化前后的差异。
    /// </summary>
    static void RunGifBench(CliOptions options, int iterations)
    {
        string inExt = Path.GetExtension(options.InputPath).ToLowerInvariant();
        Console.WriteLine($"🏁 GIF 耗时基准: {Path.GetFileName(options.InputPath)}  重复 {iterations} 次  (抖动 {(options.Dithering ? "开" : "关")})");

        Image<Rgb24>? rgb = null;

        if (inExt == ".gif")
        {
            // 一次性读入内存，避免把磁盘 IO 计入解码耗时
            byte[] bytes = File.ReadAllBytes(options.InputPath);
            var decodeTimings = new List<GifTiming>(iterations);
            for (int i = 0; i < iterations; i++)
            {
                var dec = new GifDecoder { EnableDiagnostics = true };
                using var ms = new MemoryStream(bytes, false);
                var frame = dec.DecodeRgb24(ms);
                if (dec.LastTiming is not null) decodeTimings.Add(dec.LastTiming);
                rgb ??= frame;
            }
            PrintBenchSummary("decode", decodeTimings);
        }

        rgb ??= LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder, options.RequireEoi);

        var encodeTimings = new List<GifTiming>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            var enc = new GifEncoder { EnableDiagnostics = true, EnableDithering = options.Dithering, QuantizerKind = ParseGifQuantizer(options.GifQuantizer), DitherStrength = options.GifDitherStrength };
            using var ms = new MemoryStream();
            enc.Encode(new ImageFrame(rgb.Width, rgb.Height, rgb.Buffer), ms);
            if (enc.LastTiming is not null) encodeTimings.Add(enc.LastTiming);
        }
        PrintBenchSummary("encode", encodeTimings);
    }

    static void PrintBenchSummary(string kind, List<GifTiming> timings)
    {
        if (timings.Count == 0)
        {
            Console.WriteLine($"[{kind}] 无统计结果");
            return;
        }

        int pixels = timings[0].PixelCount;
        double medianTotal = Median(timings.Select(t => t.TotalMs));
        string throughput = medianTotal > 0 && pixels > 0
            ? $"  中位吞吐 {pixels / (medianTotal * 1000.0):F2} Mpx/s"
            : string.Empty;
        Console.WriteLine($"── [{kind}] {timings.Count} 次, {pixels} 像素{throughput}");

        PrintBenchStat(timings, "total", t => t.TotalMs);
        PrintBenchStat(timings, "prepare", t => t.PrepareMs);
        PrintBenchStat(timings, "quantize", t => t.QuantizeMs);
        PrintBenchStat(timings, "header", t => t.HeaderMs);
        PrintBenchStat(timings, "lzw", t => t.LzwMs);
        PrintBenchStat(timings, "render", t => t.RenderMs);
        PrintBenchStat(timings, "background", t => t.BackgroundMs);
        PrintBenchStat(timings, "other", t => t.OtherMs);
        Console.WriteLine();
    }

    static void PrintBenchStat(List<GifTiming> timings, string name, Func<GifTiming, double> selector)
    {
        var values = timings.Select(selector).OrderBy(v => v).ToArray();
        // 中位数低于 1 微秒的阶段只可能是计时噪声，不进报表
        if (Median(values) < 0.001) return;
        double avg = values.Average();
        Console.WriteLine($"    {name.PadRight(11)} min={values[0],8:F3}ms  median={Median(values),8:F3}ms  avg={avg,8:F3}ms  max={values[^1],8:F3}ms");
    }

    static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// JPEG 编解码基准：重复 N 次，输出总耗时 min/中位/avg/max。
    /// 设置环境变量 SIC_JPEG_STAGE_TIMING=1 时同时输出分阶段耗时：
    /// 编码侧 produce（RGB→YCbCr 色彩转换）、dct（FDCT+量化）、huffman（熵编码）及各自阻塞等待；
    /// 解码侧 entropy（熵解码）、reconstruct（重建）及其中的 idct-color（IDCT+上采样+YCbCr→RGB）。
    /// </summary>
    static void RunJpegBench(CliOptions options, int iterations)
    {
        string inExt = Path.GetExtension(options.InputPath).ToLowerInvariant();
        int quality = options.JpegQuality ?? 75;
        bool subsample420 = options.Subsample420 ?? true;
        Console.WriteLine($"🏁 JPEG 耗时基准: {Path.GetFileName(options.InputPath)}  重复 {iterations} 次  quality={quality} subsample={(subsample420 ? "420" : "444")}");
        if (!JpegPerfProbe.Enabled)
        {
            Console.WriteLine("（未开启分阶段计时：设置环境变量 SIC_JPEG_STAGE_TIMING=1 可查看各阶段占比）");
        }

        if (inExt is ".jpg" or ".jpeg")
        {
            // 一次性读入内存，避免把磁盘 IO 计入解码耗时
            byte[] jpegBytes = File.ReadAllBytes(options.InputPath);
            var runs = new List<double[]>();
            var totals = new List<double>();
            for (int i = 0; i < iterations; i++)
            {
                CollectGarbage();
                var sw = Stopwatch.StartNew();
                var img = JpegDecoder.Decode(jpegBytes, options.UseFloatIdct);
                sw.Stop();
                totals.Add(sw.Elapsed.TotalMilliseconds);
                runs.Add(JpegPerfProbe.TakeLastRun());
                GC.KeepAlive(img);
            }
            PrintJpegBenchSummary("decode", runs, totals, dctWorkers: 0);
        }

        Image<Rgb24> rgb = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder, options.RequireEoi);
        var encoderOptions = new JpegEncoderOptions(quality, subsample420, keepMetadata: false, enableDiagnostics: false);
        var encRuns = new List<double[]>();
        var encTotals = new List<double>();
        byte[]? firstOutput = null;
        bool outputsMatch = true;
        for (int i = 0; i < iterations; i++)
        {
            CollectGarbage();
            using var ms = new MemoryStream();
            var sw = Stopwatch.StartNew();
            JpegEncoder.Write(ms, rgb.Width, rgb.Height, rgb.Buffer, encoderOptions);
            sw.Stop();
            encTotals.Add(sw.Elapsed.TotalMilliseconds);
            encRuns.Add(JpegPerfProbe.TakeLastRun());
            byte[] bytes = ms.ToArray();
            if (firstOutput is null)
            {
                firstOutput = bytes;
            }
            else if (!bytes.AsSpan().SequenceEqual(firstOutput))
            {
                outputsMatch = false;
            }
        }
        PrintJpegBenchSummary("encode", encRuns, encTotals, JpegPerfProbe.LastDctWorkers);
        Console.WriteLine(outputsMatch ? "编码产物一致性: 各轮逐字节一致 ✅" : "编码产物一致性: 各轮不一致 ❌");
        Console.WriteLine();
    }

    static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    static void PrintJpegBenchSummary(string kind, List<double[]> runs, List<double> totals, int dctWorkers)
    {
        Console.WriteLine($"── [{kind}] {runs.Count} 次");
        PrintStat("total", totals);

        bool hasStages = runs.Count > 0 && runs[0].Length > 0;
        if (hasStages)
        {
            for (int s = 0; s < runs[0].Length; s++)
            {
                if (s == JpegPerfProbe.EncodeTotal || s == JpegPerfProbe.DecodeTotal)
                {
                    continue; // 与上面的 total 同义，避免重复
                }

                PrintStat(JpegPerfProbe.StageNames[s], runs.Select(r => r[s]).ToList());
            }

            if (kind == "encode")
            {
                PrintStat("produce-busy", Derive(runs, JpegPerfProbe.Produce, JpegPerfProbe.ProduceWait, (a, b) => a - b));
                PrintStat("dct-busy(sum)", Derive(runs, JpegPerfProbe.DctWall, JpegPerfProbe.DctWait, (a, b) => a - b));
                PrintStat("huffman-busy", Derive(runs, JpegPerfProbe.Huffman, JpegPerfProbe.HuffmanWait, (a, b) => a - b));
                if (dctWorkers > 1)
                {
                    PrintStat("dct/worker", runs.Select(r => r[JpegPerfProbe.DctWall] / dctWorkers).ToList());
                }
            }
        }

        Console.WriteLine();
    }

    static List<double> Derive(List<double[]> runs, int stageA, int stageB, Func<double, double, double> op)
        => runs.Select(r => op(r[stageA], r[stageB])).ToList();

    static void PrintStat(string name, IList<double> values)
    {
        if (values.Count == 0) return;
        var sorted = values.OrderBy(v => v).ToArray();
        // 阶段中位低于 0.01 ms 的只可能是计时噪声，不进报表
        if (Median(sorted) < 0.01) return;
        Console.WriteLine($"    {name.PadRight(15)} min={sorted[0],9:F3}ms  median={Median(sorted),9:F3}ms  avg={sorted.Average(),9:F3}ms  max={sorted[^1],9:F3}ms");
    }

    /// <summary>
    /// 非 optimize 路径的内存预检：预计会 OOM 时优先切到流式解码；
    /// 流式后仍不足（或源格式没有流式管线）则拒绝处理，而不是等分配失败。
    /// </summary>
    static void EnsurePlainMemory(CliOptions options, string inExt)
    {
        if (options.NoMemoryGuard) return;

        string targetExt = options.OutputExtension
            ?? (options.OutputPath is not null ? Path.GetExtension(options.OutputPath) : null)
            ?? (options.Operations.Count == 0 ? ".png" : ".bmp");
        var source = ImageOptimizer.DetectFormat(options.InputPath);
        var target = ImageOptimizer.DetectKindByExtension(targetExt);
        if (target == ImageFormatKind.Unknown) return;

        long explicitLimit = options.MemoryLimitMb is > 0 ? options.MemoryLimitMb.Value * 1024L * 1024L : 0;
        var verdict = MemoryGuard.CheckPlain(source, target, options.InputPath, explicitLimit);
        switch (verdict.Action)
        {
            case MemoryGuard.Action.Ok:
                return;
            case MemoryGuard.Action.UseStreaming:
                if (options.Operations.Count == 0
                    && !(options.Gray && target == ImageFormatKind.Jpeg)
                    && StreamingConversion.IsSupported(source, target, options.InputPath))
                {
                    options.ForceStreaming = true;
                    Console.WriteLine(
                        $"🧠 预计峰值 {verdict.EstimateBytes / 1048576.0:F1} MB，超过预算 " +
                        $"{verdict.BudgetBytes / 1048576.0:F1} MB；已切换到逐行流式转换（{source} → {target}）。");
                    return;
                }
                if (source == ImageFormatKind.Jpeg)
                {
                    // 目标不支持逐行编码时，至少切到流式输入解码。
                    options.UseStreamingDecoder = true;
                    Console.WriteLine($"🧠 {verdict.Reason} 已改用流式解码。");
                    return;
                }
                throw new InsufficientMemoryException(verdict.Reason);
            default:
                throw new InsufficientMemoryException(verdict.Reason);
        }
    }

    static bool Process(CliOptions options, CliReporter reporter)
    {
        string inExt = Path.GetExtension(options.InputPath).ToLowerInvariant();
        EnsurePlainMemory(options, inExt);

        if (options.ForceStreaming)
        {
            options.OutputPath = EnsureOutputPath(options.InputPath, options.OutputPath, options.OutputExtension ?? ".png");
            string output = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
            var target = ImageOptimizer.DetectKindByExtension(Path.GetExtension(output));
            try
            {
                StreamingConversion.Convert(
                    options.InputPath, output, target, options.Gray,
                    options.JpegQuality ?? 75, JpegSubsamplingOf(options), options.JpegProgressive,
                    options.UseFloatIdct);
            }
            catch (NotSupportedException ex) when (options.UseFloatIdct)
            {
                // 逐行解码的 4:2:0/4:2:2 只有「SIMD 定点 + fancy 上采样」或「浮点 + 双线性」两种组合，
                // 后者会牺牲色度清晰度，所以拒绝浮点 IDCT。这里退回整数 IDCT 并把原因说清楚，
                // 而不是让内存守卫自动触发的流式路径直接失败。
                reporter.Info($"⚠️ {ex.Message}（已回退整数 IDCT）");
                StreamingConversion.Convert(
                    options.InputPath, output, target, options.Gray,
                    options.JpegQuality ?? 75, JpegSubsamplingOf(options), options.JpegProgressive);
            }
            reporter.Info($"✅ 流式转换完成: {output}");
            return true;
        }

        // GIF 动画帧导出为单张图片序列
        if (options.GifFrames && inExt == ".gif")
        {
            ExportGifFrames(options, reporter);
            return false;
        }

        // 是否存在图像处理操作
        if (options.Operations.Count == 0)
        {
            ProcessNoOps(options, inExt, reporter);
            return true;
        }

        ProcessWithOps(options, inExt);
        return true;
    }

    static void ExportGifFrames(CliOptions options, CliReporter reporter)
    {
        // 根据输出路径推导帧输出目录与格式
        string baseOut = options.OutputPath ?? Path.ChangeExtension(options.InputPath, ".png");
        string dir = Path.GetDirectoryName(baseOut) ?? ".";
        string nameNoExt = Path.GetFileNameWithoutExtension(baseOut);
        string ext = NormalizeOutputExtension(Path.GetExtension(baseOut));
        var gifDec = CreateGifDecoder(options);
        var frames = gifDec.DecodeAllFrames(options.InputPath);
        int digits = Math.Max(3, frames.Count.ToString(CultureInfo.InvariantCulture).Length);

        for (int i = 0; i < frames.Count; i++)
        {
            string idx = i.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
            string path = Path.Combine(dir, $"{nameNoExt}_{idx}{ext}");
            SaveGifFrame(frames[i], path, ext, options);
        }

        reporter.Info($"✅ 导出 GIF 动画帧: 共 {frames.Count} 帧");
    }

    static void SaveGifFrame(Image<Rgb24> frame, string path, string ext, CliOptions options)
    {
        bool isInputGray = IsGrayRgb(frame);
        if (ext is ".jpg" or ".jpeg")
        {
            int q = options.JpegQuality ?? 75;
            var optionsJpeg = new JpegEncoderOptions(q, JpegSubsamplingOf(options), keepMetadata: false, enableDiagnostics: false, optimizeHuffman: false, trellisQuantization: options.JpegTrellis);
            EncodeJpegWithOptions(frame, path, optionsJpeg, options.JpegProgressive);
            return;
        }

        if ((ext is ".bmp" or ".png" or ".webp") && (options.Gray || isInputGray))
        {
            var grayImage = ToGray8(frame);
            Image.Save(grayImage, path);
            return;
        }

        Image.Save(frame, path);
    }

    static void ProcessNoOps(CliOptions options, string inExt, CliReporter reporter)
    {
        // 默认输出后缀：优先取 --to，否则 PNG
        options.OutputPath = EnsureOutputPath(options.InputPath, options.OutputPath, options.OutputExtension ?? ".png");
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        string outExt = Path.GetExtension(outputPath).ToLowerInvariant();

        // GIF -> WebP：保留动画信息
        if (inExt == ".gif" && outExt == ".webp")
        {
            ConvertGifToWebp(options);
            return;
        }

        if (inExt is ".jpg" or ".jpeg" && outExt == ".bmp")
        {
            var swDecode = Stopwatch.StartNew();
            var image = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder, options.RequireEoi);
            swDecode.Stop();
            reporter.Info($"解码耗时: {swDecode.ElapsedMilliseconds} ms");
            SaveRgbOrGray(image, outputPath, outExt, options.Gray);
            return;
        }

        if (inExt is ".jpg" or ".jpeg" && (outExt is ".jpg" or ".jpeg"))
        {
            var swDecode = Stopwatch.StartNew();
            var image = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder, options.RequireEoi);
            swDecode.Stop();
            reporter.Info($"解码耗时: {swDecode.ElapsedMilliseconds} ms");
            SaveJpegFromRgb(image, options, outputPath, keepMetadata: true);
            return;
        }

        var rgbaImage = LoadRgba32(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder, options.RequireEoi);
        if (outExt is ".jpg" or ".jpeg")
        {
            SaveJpegFromRgba(rgbaImage, options, outputPath);
            return;
        }

        bool isInputGray = IsGrayRgba(rgbaImage);
        if ((outExt == ".bmp" || outExt == ".png" || outExt == ".webp" || outExt == ".gif") && (options.Gray || isInputGray))
        {
            var rgb = new Image<Rgb24>(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer), rgbaImage.Metadata);
            var grayImage = ToGray8(rgb);
            SaveGrayWithOptions(grayImage, outputPath, options);
            return;
        }

        SaveRgbaWithOptions(rgbaImage, outputPath, options);
    }

    static void ProcessWithOps(CliOptions options, string inExt)
    {
        // 有操作时默认输出后缀：优先取 --to，否则 BMP
        options.OutputPath = EnsureOutputPath(options.InputPath, options.OutputPath, options.OutputExtension ?? ".bmp");
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        string outExt = Path.GetExtension(outputPath).ToLowerInvariant();

        // 带操作的 GIF -> WebP 动画
        if (inExt == ".gif" && outExt == ".webp")
        {
            ProcessGifToWebpWithOps(options);
            return;
        }

        var image = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder, options.RequireEoi);
        bool isInputGray = IsGrayRgb(image);

        ImageExtensions.Mutate(image, ctx =>
        {
            foreach (var a in options.Operations) a(ctx);
        });

        if (outExt is ".jpg" or ".jpeg")
        {
            SaveJpegFromRgb(image, options, outputPath, options.KeepMetadata);
            return;
        }

        if ((outExt == ".bmp" || outExt == ".png" || outExt == ".webp" || outExt == ".gif") && (options.Gray || isInputGray))
        {
            var grayImage = ToGray8(image);
            SaveGrayWithOptions(grayImage, outputPath, options);
            return;
        }

        SaveRgbWithOptions(image, outputPath, options);
    }

    static void ConvertGifToWebp(CliOptions options)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        var gifDec = CreateGifDecoder(options);
        GifAnimation anim;
        using (var fs = File.OpenRead(options.InputPath)) anim = gifDec.DecodeAnimationRgb24(fs);

        if (anim.Frames.Count == 0)
        {
            var rgbaImage = Image.LoadRgba32(options.InputPath);
            Image.Save(rgbaImage, outputPath);
            return;
        }

        if (anim.Frames.Count == 1)
        {
            var rgbaImage = ToRgba32(anim.Frames[0]);
            SaveRgbaWithOptions(rgbaImage, outputPath, options);
            return;
        }

        int loop = anim.LoopCount;
        float qWebp = options.JpegQuality.HasValue ? options.JpegQuality.Value : 75f;
        WebpAnimationEncoder.EncodeRgb24(outputPath, anim.Frames, anim.FrameDurationsMs, loop, qWebp);
    }

    static void ProcessGifToWebpWithOps(CliOptions options)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        var gifDec = CreateGifDecoder(options);
        GifAnimation anim;
        using (var fs = File.OpenRead(options.InputPath)) anim = gifDec.DecodeAnimationRgb24(fs);

        var processed = new List<Image<Rgb24>>(anim.Frames.Count);
        for (int i = 0; i < anim.Frames.Count; i++)
        {
            var frame = anim.Frames[i];
            ImageExtensions.Mutate(frame, ctx =>
            {
                foreach (var a in options.Operations) a(ctx);
            });
            processed.Add(frame);
        }

        float qWebpOps = options.JpegQuality.HasValue ? options.JpegQuality.Value : 75f;
        WebpAnimationEncoder.EncodeRgb24(outputPath, processed, anim.FrameDurationsMs, anim.LoopCount, qWebpOps);
    }

    static string EnsureOutputPath(string inputPath, string? outputPath, string defaultExtension)
    {
        return ResolveOutputPath(inputPath, outputPath, defaultExtension);
    }

    static void SaveRgbOrGray(Image<Rgb24> image, string outputPath, string outExt, bool grayFlag)
    {
        bool isInputGray = IsGrayRgb(image);
        if ((outExt == ".bmp" || outExt == ".png") && (grayFlag || isInputGray))
        {
            var grayImage = ToGray8(image);
            Image.Save(grayImage, outputPath);
            return;
        }

        Image.Save(image, outputPath);
    }

    static void SaveJpegFromRgb(Image<Rgb24> image, CliOptions options, string outputPath, bool keepMetadata)
    {
        int q = options.JpegQuality ?? 75;
        bool isInputGray = IsGrayRgb(image);
        var encoderOptions = new JpegEncoderOptions(q, JpegSubsamplingOf(options), keepMetadata, options.JpegDebug, optimizeHuffman: false, trellisQuantization: options.JpegTrellis);
        if (options.Gray || isInputGray)
        {
            var grayImage = ToGray8(image);
            EncodeJpegWithOptions(grayImage, outputPath, encoderOptions, options.JpegProgressive);
            return;
        }

        EncodeJpegWithOptions(image, outputPath, encoderOptions, options.JpegProgressive);
    }

    static void SaveJpegFromRgba(Image<Rgba32> rgbaImage, CliOptions options, string outputPath)
    {
        int q = options.JpegQuality ?? 75;
        bool isInputGray = IsGrayRgba(rgbaImage);
        var encoderOptions = new JpegEncoderOptions(q, JpegSubsamplingOf(options), keepMetadata: true, enableDiagnostics: options.JpegDebug, optimizeHuffman: false, trellisQuantization: options.JpegTrellis);
        if (options.Gray || isInputGray)
        {
            var rgb = new Image<Rgb24>(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer), rgbaImage.Metadata);
            var grayImage = ToGray8(rgb);
            EncodeJpegWithOptions(grayImage, outputPath, encoderOptions, options.JpegProgressive);
            return;
        }

        var rgbImage = new Image<Rgb24>(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer), rgbaImage.Metadata);
        EncodeJpegWithOptions(rgbImage, outputPath, encoderOptions, options.JpegProgressive);
    }

    static void EncodeJpegWithOptions<T>(Image<T> image, string outputPath, JpegEncoderOptions encoderOptions, bool progressive)
        where T : struct, IPixel
    {
        if (!progressive)
        {
            JpegEncoder.Encode(image, outputPath, encoderOptions);
            return;
        }

        using var ms = new MemoryStream();
        JpegEncoder.Encode(image, ms, encoderOptions);
        byte[] bytes = ms.ToArray();
        byte[]? p = JpegProgressiveTranscoder.TryTranscode(bytes);
        if (p != null) bytes = p;
        File.WriteAllBytes(outputPath, bytes);
    }

    static void SaveRgbWithOptions(Image<Rgb24> image, string outputPath, CliOptions options)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext == ".gif")
        {
            var encoder = new GifEncoderAdapter
            {
                EnableDithering = options.Dithering,
                QuantizerKind = ParseGifQuantizer(options.GifQuantizer),
                DitherStrength = options.GifDitherStrength,
                EnableDiagnostics = options.GifDebug,
                DiagnosticsLog = options.GifDebug ? Console.WriteLine : null,
            };
            encoder.EncodeRgb24(outputPath, image);
            return;
        }

        if (ext == ".webp")
        {
            var encoder = new WebpEncoderAdapter
            {
                Quality = options.JpegQuality ?? 75,
                ConcurrencyStrategy = options.ParallelWebpEncode ? WebpConcurrencyStrategy.Parallel : WebpConcurrencyStrategy.Auto,
            };
            // 只在用户显式指定时覆盖，默认档由 WebpEncoderOptions 决定（当前为 method 3）
            if (options.WebpMethod.HasValue) encoder.Method = options.WebpMethod.Value;
            encoder.EncodeRgb24(outputPath, image);
            return;
        }

        Image.Save(image, outputPath);
    }

    static void SaveGrayWithOptions(Image<Gray8> image, string outputPath, CliOptions options)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext is ".gif" or ".webp")
        {
            var rgb = new Image<Rgb24>(image.Width, image.Height, GrayToRgb(image.Buffer), image.Metadata);
            SaveRgbWithOptions(rgb, outputPath, options);
            return;
        }

        Image.Save(image, outputPath);
    }

    static void SaveRgbaWithOptions(Image<Rgba32> image, string outputPath, CliOptions options)
    {
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext == ".gif")
        {
            var encoder = new GifEncoderAdapterRgba
            {
                EnableDithering = options.Dithering,
                QuantizerKind = ParseGifQuantizer(options.GifQuantizer),
                DitherStrength = options.GifDitherStrength,
                EnableDiagnostics = options.GifDebug,
                DiagnosticsLog = options.GifDebug ? Console.WriteLine : null,
            };
            encoder.EncodeRgba32(outputPath, image);
            return;
        }

        if (ext == ".webp")
        {
            var encoder = new WebpEncoderAdapterRgba
            {
                Quality = options.JpegQuality ?? 75,
                ConcurrencyStrategy = options.ParallelWebpEncode ? WebpConcurrencyStrategy.Parallel : WebpConcurrencyStrategy.Auto,
            };
            if (options.WebpMethod.HasValue) encoder.Method = options.WebpMethod.Value;
            encoder.EncodeRgba32(outputPath, image);
            return;
        }

        Image.Save(image, outputPath);
    }

    static string NormalizeOutputExtension(string ext)
    {
        string lower = ext.ToLowerInvariant();
        if (lower is ".bmp" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif") return lower;
        return ".png";
    }

    static bool TryParseQuality(string[] args, ref int index, out int? quality)
    {
        quality = null;
        string a = args[index];
        if (string.Equals(a, "--quality", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-q", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Length && int.TryParse(args[index + 1], out int q))
            {
                quality = q;
                index++;
            }
            return true;
        }
        if (a.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase))
        {
            string v = a["--quality=".Length..];
            if (int.TryParse(v, out int q2)) quality = q2;
            return true;
        }
        return false;
    }

    static bool TryParseSubsample(string[] args, ref int index, out JpegChromaSubsampling? subsampling)
    {
        subsampling = null;
        string a = args[index];
        if (string.Equals(a, "--subsample", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Length)
            {
                string v = args[index + 1].Trim();
                if (v == "420") subsampling = JpegChromaSubsampling.Yuv420;
                else if (v == "422") subsampling = JpegChromaSubsampling.Yuv422;
                else if (v == "444") subsampling = JpegChromaSubsampling.Yuv444;
                index++;
            }
            return true;
        }
        if (a.StartsWith("--subsample=", StringComparison.OrdinalIgnoreCase))
        {
            string v = a["--subsample=".Length..].Trim();
            if (v == "420") subsampling = JpegChromaSubsampling.Yuv420;
            else if (v == "422") subsampling = JpegChromaSubsampling.Yuv422;
            else if (v == "444") subsampling = JpegChromaSubsampling.Yuv444;
            return true;
        }
        return false;
    }

    static JpegChromaSubsampling JpegSubsamplingOf(CliOptions options)
        => options.JpegSubsampling ?? (options.Subsample420 == false ? JpegChromaSubsampling.Yuv444 : JpegChromaSubsampling.Yuv420);

    static bool TryParseIdct(string[] args, ref int index, out bool? useFloatIdct)
    {
        useFloatIdct = null;
        string a = args[index];
        if (string.Equals(a, "--idct", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Length)
            {
                string v = args[index + 1].Trim();
                if (string.Equals(v, "float", StringComparison.OrdinalIgnoreCase)) useFloatIdct = true;
                else if (string.Equals(v, "int", StringComparison.OrdinalIgnoreCase)) useFloatIdct = false;
                index++;
            }
            return true;
        }
        if (a.StartsWith("--idct=", StringComparison.OrdinalIgnoreCase))
        {
            string v = a["--idct=".Length..].Trim();
            if (string.Equals(v, "float", StringComparison.OrdinalIgnoreCase)) useFloatIdct = true;
            else if (string.Equals(v, "int", StringComparison.OrdinalIgnoreCase)) useFloatIdct = false;
            return true;
        }
        return false;
    }

    static bool TryParseOperation(string arg, List<Action<ImageProcessingContext>> ops)
    {
        string op = arg.ToLowerInvariant();
        if (TryParseResizeOp(op, "resize:", (w, h) => ctx => ctx.Resize(w, h), ops)) return true;
        if (TryParseResizeOp(op, "resizebilinear:", (w, h) => ctx => ctx.ResizeBilinear(w, h), ops)) return true;
        if (TryParseResizeOp(op, "resizefit:", (w, h) => ctx => ctx.ResizeToFit(w, h), ops)) return true;
        if (op == "grayscale")
        {
            ops.Add(ctx => ctx.Grayscale());
            return true;
        }
        return false;
    }

    static bool TryParseResizeOp(string op, string prefix, Func<int, int, Action<ImageProcessingContext>> factory, List<Action<ImageProcessingContext>> ops)
    {
        if (!op.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string sizePart = op[prefix.Length..];
        var parts = sizePart.Split(['x', '*'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
        {
            ops.Add(factory(w, h));
        }
        return true;
    }

    static byte[] RgbaToRgb(byte[] rgba)
    {
        var rgb = new byte[(rgba.Length / 4) * 3];
        for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
        {
            rgb[j + 0] = rgba[i + 0];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }
        return rgb;
    }

    static byte[] GrayToRgb(byte[] gray)
    {
        var rgb = new byte[gray.Length * 3];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 3)
        {
            byte value = gray[i];
            rgb[j + 0] = value;
            rgb[j + 1] = value;
            rgb[j + 2] = value;
        }
        return rgb;
    }

    static Image<Rgba32> ToRgba32(Image<Rgb24> image)
    {
        var rgba = new byte[image.Width * image.Height * 4];
        for (int i = 0, j = 0; j < image.Buffer.Length; i += 4, j += 3)
        {
            rgba[i + 0] = image.Buffer[j + 0];
            rgba[i + 1] = image.Buffer[j + 1];
            rgba[i + 2] = image.Buffer[j + 2];
            rgba[i + 3] = 255;
        }
        return new Image<Rgba32>(image.Width, image.Height, rgba, image.Metadata);
    }

    static bool IsGrayRgb(Image<Rgb24> image)
    {
        var rgb = image.Buffer;
        for (int i = 0; i < rgb.Length; i += 3)
        {
            byte r = rgb[i + 0];
            if (r != rgb[i + 1] || r != rgb[i + 2]) return false;
        }
        return true;
    }

    static bool IsGrayRgba(Image<Rgba32> image)
    {
        var rgba = image.Buffer;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = rgba[i + 0];
            if (r != rgba[i + 1] || r != rgba[i + 2] || rgba[i + 3] != 255) return false;
        }
        return true;
    }

    static Image<Gray8> ToGray8(Image<Rgb24> image)
    {
        int count = image.Width * image.Height;
        var gray = new byte[count];
        var rgb = image.Buffer;
        for (int i = 0, j = 0; i < count; i++, j += 3)
        {
            int r = rgb[j + 0];
            int g = rgb[j + 1];
            int b = rgb[j + 2];
            int y = (77 * r + 150 * g + 29 * b) >> 8;
            gray[i] = (byte)y;
        }
        return new Image<Gray8>(image.Width, image.Height, gray, image.Metadata);
    }

    static Image<Rgb24> LoadRgb24(string path, bool useFloatIdct, bool useStreamingDecoder, bool requireEoi = false)
    {
        string ext = Path.GetExtension(path);
        bool isJpeg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        if (useStreamingDecoder)
        {
            if (!isJpeg)
            {
                Console.WriteLine("⚠️ --stream 当前仅对 JPEG 生效，已回退为常规解码");
                return Image.Load(path);
            }
            try
            {
                long fileLength = new FileInfo(path).Length;
                Console.WriteLine($"[stream] 开始解码: {Path.GetFileName(path)} ({fileLength} bytes)");
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                var sw = Stopwatch.StartNew();
                var frame = ImageFrame.LoadJpeg(fs, useStreamingDecoder: true, useFloatingPointIdct: useFloatIdct, requireEoi: requireEoi);
                sw.Stop();
                Console.WriteLine($"[stream] 解码完成: {sw.ElapsedMilliseconds} ms");
                return new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels, frame.Metadata);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"流式解码失败: {ex.Message}", ex);
            }
        }
        if (!useFloatIdct || !isJpeg) return Image.Load(path);
        using var fs2 = File.OpenRead(path);
        var frame2 = ImageFrame.LoadJpeg(fs2, useStreamingDecoder: false, useFloatingPointIdct: true);
        return new Image<Rgb24>(frame2.Width, frame2.Height, frame2.Pixels, frame2.Metadata);
    }

    static Image<Rgba32> LoadRgba32(string path, bool useFloatIdct, bool useStreamingDecoder, bool requireEoi = false)
    {
        string ext = Path.GetExtension(path);
        bool isJpeg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        if (useStreamingDecoder && isJpeg)
        {
            var rgb = LoadRgb24(path, useFloatIdct, true, requireEoi);
            return ToRgba32(rgb);
        }
        if (!useFloatIdct) return Image.LoadRgba32(path);
        if (!isJpeg) return Image.LoadRgba32(path);
        var rgb2 = LoadRgb24(path, true, useStreamingDecoder, requireEoi);
        return ToRgba32(rgb2);
    }

    private static string ResolveOutputPath(string inputPath, string? outputPath, string defaultExtension)
    {
        string? desired = outputPath;
        if (string.IsNullOrEmpty(desired))
        {
            desired = Path.Combine(
                Path.GetDirectoryName(inputPath) ?? ".",
                Path.GetFileNameWithoutExtension(inputPath) + defaultExtension);
        }

        if (!File.Exists(desired)) return desired;

        string dir = Path.GetDirectoryName(desired) ?? ".";
        string nameNoExt = Path.GetFileNameWithoutExtension(desired);
        string ext = Path.GetExtension(desired);
        int idx = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameNoExt} ({idx}){ext}");
            idx++;
        } while (File.Exists(candidate));
        return candidate;
    }
}
