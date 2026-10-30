using SharpImageConverter.Processing;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

sealed partial class Program
{
    static CliOptions ParseOptions(string[] args, string inputPath)
    {
        var options = new CliOptions(inputPath);
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == CliStdio.Dash)
            {
                // 单独的 "-" 是位置参数（代表 stdout），不是未知开关：
                // 它以满足 StartsWith('-')，不特判就会被当成未知参数报错。
                if (options.OutputPath == null) options.OutputPath = a;
                else options.UnknownArguments.Add(a);
                continue;
            }
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
                // 1.3.0 已移除：语料实测只有 0~1.3% 体积收益，却要多花 2~4 倍搜索时间。
                // 这里不再"警告后继续"——静默忽略正是本次整改要消灭的那类行为（旧脚本
                // 以为参数生效、实际什么都没变），改为报用法错误并说明原因。
                options.UnknownArguments.Add(a);
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

    static string NormalizeOutputExtension(string ext)
    {
        string lower = ext.ToLowerInvariant();
        if (lower is ".bmp" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif") return lower;
        return ".png";
    }
}
