using System.Diagnostics;
using System.Globalization;
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
            RequireEoi = src.RequireEoi,
            StrictFormat = src.StrictFormat,
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
        if (options.StrictFormat)
        {
            // 扩展名与文件头内容不符时报错，而不是「按内容解码、按扩展名写出」地隐式换格式。
            // 与 optimize 共用同一处判定（ImageOptimizer.EnsureFormatMatches），避免两套文案。
            ImageOptimizer.EnsureFormatMatches(options.InputPath, ImageOptimizer.DetectFormat(options.InputPath));
        }
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

        // 动图帧导出为单张图片序列（GIF 与 WebP 动图）
        if (options.GifFrames && (inExt == ".gif" || (inExt == ".webp" && IsAnimatedWebp(options.InputPath))))
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

        ProcessWithOps(options, inExt, reporter);
        return true;
    }

    static void ExportGifFrames(CliOptions options, CliReporter reporter)
    {
        // 根据输出路径推导帧输出目录与格式
        string baseOut = options.OutputPath ?? Path.ChangeExtension(options.InputPath, ".png");
        string dir = Path.GetDirectoryName(baseOut) ?? ".";
        string nameNoExt = Path.GetFileNameWithoutExtension(baseOut);
        string ext = NormalizeOutputExtension(Path.GetExtension(baseOut));

        string inExt = Path.GetExtension(options.InputPath).ToLowerInvariant();
        if (inExt == ".webp")
        {
            ExportWebpAnimationFrames(options, reporter, dir, nameNoExt, ext);
            return;
        }

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

    /// <summary>
    /// 把 WebP 动图拆成单帧序列导出。
    /// </summary>
    /// <remarks>
    /// WebP 动图的每帧都是整幅画布快照（已按 blend / dispose 合成），
    /// 所以导出的帧与播放器看到的画面一致。
    /// </remarks>
    static void ExportWebpAnimationFrames(CliOptions options, CliReporter reporter, string dir, string nameNoExt, string ext)
    {
        var anim = WebpDecoder.DecodeAnimation(File.ReadAllBytes(options.InputPath));
        if (anim.Frames.Length == 0)
            throw new InvalidDataException("WebP 动图没有可解码的帧");

        int digits = Math.Max(3, anim.Frames.Length.ToString(CultureInfo.InvariantCulture).Length);
        for (int i = 0; i < anim.Frames.Length; i++)
        {
            string idx = i.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
            string path = Path.Combine(dir, $"{nameNoExt}_{idx}{ext}");
            var frame = new Image<Rgba32>(anim.CanvasWidth, anim.CanvasHeight, anim.Frames[i].Rgba);
            SaveAnimationFrameRgba(frame, path, ext, options);
        }

        reporter.Info($"✅ 导出 WebP 动画帧: 共 {anim.Frames.Length} 帧");
    }

    /// <summary>
    /// 保存单帧动画画面。支持透明通道的格式（PNG / WebP）保留 alpha，JPEG/BMP 走 RGB。
    /// </summary>
    static void SaveAnimationFrameRgba(Image<Rgba32> frame, string path, string ext, CliOptions options)
    {
        if (ext is ".jpg" or ".jpeg")
        {
            int q = options.JpegQuality ?? 75;
            var rgb = new Image<Rgb24>(frame.Width, frame.Height, RgbaToRgb(frame.Buffer), frame.Metadata);
            var optionsJpeg = new JpegEncoderOptions(q, JpegSubsamplingOf(options), keepMetadata: false, enableDiagnostics: false, optimizeHuffman: false, trellisQuantization: options.JpegTrellis);
            EncodeJpegWithOptions(rgb, path, optionsJpeg, options.JpegProgressive);
            return;
        }

        if (ext == ".bmp")
        {
            var rgb = new Image<Rgb24>(frame.Width, frame.Height, RgbaToRgb(frame.Buffer), frame.Metadata);
            Image.Save(rgb, path);
            return;
        }

        // PNG / WebP：保留 alpha
        Image.Save(frame, path);
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

        // WebP 动图输入：按输出格式分派（动图 -> 动图 / 动图 -> 静帧）
        if (inExt == ".webp" && IsAnimatedWebp(options.InputPath))
        {
            ConvertWebpAnimation(options, outExt, reporter);
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

    static void ProcessWithOps(CliOptions options, string inExt, CliReporter reporter)
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

        // WebP 动图输入（带操作）：动图 -> 动图 / 动图 -> 静帧
        if (inExt == ".webp" && IsAnimatedWebp(options.InputPath))
        {
            ConvertWebpAnimation(options, outExt, reporter);
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

    /// <summary>
    /// 判断输入是否为 WebP 动图（VP8X 的 ANIMATION 标志）。
    /// </summary>
    /// <remarks>
    /// 只读 RIFF/WEBP 头与 VP8X 的 flags 字节（偏移 20，bit1 = ANIMATION），不解码像素。
    /// 头部不完整或不是 WebP 时按「非动图」处理，后续单图解码路径会给出更具体的错误。
    /// </remarks>
    static bool IsAnimatedWebp(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[21];
            if (fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (head[0] != (byte)'R' || head[1] != (byte)'I' || head[2] != (byte)'F' || head[3] != (byte)'F') return false;
            if (head[8] != (byte)'W' || head[9] != (byte)'E' || head[10] != (byte)'B' || head[11] != (byte)'P') return false;
            if (head[12] != (byte)'V' || head[13] != (byte)'P' || head[14] != (byte)'8' || head[15] != (byte)'X') return false;
            return (head[20] & 0x02) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// WebP 动图转换：
    /// 输出为 .webp / .gif 时保留全部帧、时长与循环次数；其他格式只取首帧并明确告知。
    /// </summary>
    static void ConvertWebpAnimation(CliOptions options, string outExt, CliReporter reporter)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");

        byte[] bytes = File.ReadAllBytes(options.InputPath);
        var anim = WebpDecoder.DecodeAnimation(bytes);

        if (anim.Frames.Length == 0)
            throw new InvalidDataException("WebP 动图没有可解码的帧");

        var durations = anim.Frames.Select(f => f.DurationMs).ToList();
        int loop = anim.LoopCount;
        float quality = options.JpegQuality.HasValue ? options.JpegQuality.Value : 75f;

        // 带操作时逐帧处理（alpha 一并保留，尺寸随之改变）
        List<Image<Rgba32>> frames;
        if (options.Operations.Count > 0)
        {
            frames = anim.Frames
                .Select(f => ApplyOpsToRgba(
                    new Image<Rgba32>(anim.CanvasWidth, anim.CanvasHeight, f.Rgba), options.Operations))
                .ToList();
        }
        else
        {
            frames = anim.Frames
                .Select(f => new Image<Rgba32>(anim.CanvasWidth, anim.CanvasHeight, f.Rgba))
                .ToList();
        }

        if (outExt == ".webp")
        {
            WebpAnimationEncoder.EncodeRgba32(outputPath, frames, durations, loop, quality);
            reporter.Info($"✅ WebP 动图: {frames.Count} 帧 {frames[0].Width}x{frames[0].Height}，循环 {loop} 次");
            return;
        }

        if (outExt == ".gif")
        {
            var gifEnc = new GifEncoder
            {
                EnableDithering = options.Dithering,
                QuantizerKind = ParseGifQuantizer(options.GifQuantizer),
                DitherStrength = options.GifDitherStrength,
            };
            var rgbaBuffers = frames.Select(f => f.Buffer).ToList();
            using (var fs = File.Create(outputPath))
            {
                gifEnc.EncodeAnimationRgba(frames[0].Width, frames[0].Height, rgbaBuffers, durations, loop, fs);
            }
            reporter.Info($"✅ GIF 动图: {frames.Count} 帧 {frames[0].Width}x{frames[0].Height}，循环 {loop} 次");
            return;
        }

        // 静态格式：只取首帧，并说明其余帧的去处
        var first = frames[0];
        reporter.Info($"⚠️ 输入是 {frames.Count} 帧的 WebP 动图，{outExt} 只能存一张，已取首帧；" +
                     "如需保留动画请用 --to webp 或 --to gif");
        var rgbaImage = new Image<Rgba32>(first.Width, first.Height, first.Buffer);
        SaveRgbaWithOptions(rgbaImage, outputPath, options);
    }

    static void ConvertGifToWebp(CliOptions options)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        var gifDec = CreateGifDecoder(options);
        GifAnimation anim;
        using (var fs = File.OpenRead(options.InputPath)) anim = gifDec.DecodeAnimationRgba32(fs);

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
        // 必须走 RGBA 路径：GIF 的透明索引只存在于 alpha 通道，走 Rgb24 会把透明背景变成不透明。
        WebpAnimationEncoder.EncodeRgba32(outputPath, anim.RgbaFrames!, anim.FrameDurationsMs, loop, qWebp);
    }
    static void ProcessGifToWebpWithOps(CliOptions options)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        var gifDec = CreateGifDecoder(options);
        GifAnimation anim;
        using (var fs = File.OpenRead(options.InputPath)) anim = gifDec.DecodeAnimationRgba32(fs);

        var processed = new List<Image<Rgba32>>(anim.RgbaFrames!.Count);
        for (int i = 0; i < anim.RgbaFrames.Count; i++)
        {
            processed.Add(ApplyOpsToRgba(anim.RgbaFrames[i], options.Operations));
        }

        float qWebpOps = options.JpegQuality.HasValue ? options.JpegQuality.Value : 75f;
        WebpAnimationEncoder.EncodeRgba32(outputPath, processed, anim.FrameDurationsMs, anim.LoopCount, qWebpOps);
    }

    /// <summary>
    /// 对 RGBA 帧应用 CLI 操作，同时保住 alpha 通道。
    /// </summary>
    /// <remarks>
    /// CLI 的操作管线只作用于 <see cref="Image{Rgb24}"/>（resize / grayscale）。这里把 alpha
    /// 单独复制成一张「灰度图」（R=G=B=A），与 RGB 一起走同一套操作——因为所有 resize 实现
    /// 都是逐通道独立的，alpha 用同一算法缩放后尺寸天然一致；grayscale 对 (A,A,A) 是恒等变换，
    /// 不会破坏 alpha。最后按新尺寸把两者合回去。
    /// </remarks>
    static Image<Rgba32> ApplyOpsToRgba(Image<Rgba32> frame, IReadOnlyList<Action<ImageProcessingContext>> ops)
    {
        if (ops.Count == 0) return frame;

        int width = frame.Width, height = frame.Height;
        var rgba = frame.Buffer;

        // RGB：丢弃 alpha 做操作
        var rgb = new Image<Rgb24>(width, height, RgbaToRgb(rgba), frame.Metadata);
        // Alpha：复制成灰度图走同样的操作
        var alphaGray = new Image<Rgb24>(width, height, new byte[width * height * 3], frame.Metadata);
        for (int p = 0; p < width * height; p++)
        {
            byte a = rgba[p * 4 + 3];
            alphaGray.Buffer[p * 3 + 0] = a;
            alphaGray.Buffer[p * 3 + 1] = a;
            alphaGray.Buffer[p * 3 + 2] = a;
        }

        foreach (var op in ops)
        {
            ImageExtensions.Mutate(rgb, op);
            ImageExtensions.Mutate(alphaGray, op);
        }

        int newWidth = rgb.Width, newHeight = rgb.Height;
        var outRgba = new byte[newWidth * newHeight * 4];
        for (int p = 0; p < newWidth * newHeight; p++)
        {
            outRgba[p * 4 + 0] = rgb.Buffer[p * 3 + 0];
            outRgba[p * 4 + 1] = rgb.Buffer[p * 3 + 1];
            outRgba[p * 4 + 2] = rgb.Buffer[p * 3 + 2];
            outRgba[p * 4 + 3] = alphaGray.Buffer[p * 3 + 0];
        }
        return new Image<Rgba32>(newWidth, newHeight, outRgba, frame.Metadata);
    }

    static string EnsureOutputPath(string inputPath, string? outputPath, string defaultExtension)
    {
        return ResolveOutputPath(inputPath, outputPath, defaultExtension);
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
