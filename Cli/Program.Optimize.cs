using SharpImageConverter.Compression;

namespace SharpImageConverter;

sealed partial class Program
{
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
            RequireFormatMatch = options.StrictFormat,
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
}
