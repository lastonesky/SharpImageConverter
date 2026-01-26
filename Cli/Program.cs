using System.Diagnostics;
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            PrintUsage();
            return;
        }
        string inputPath = args[0];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"输入文件不存在: {inputPath}");
            return;
        }
        // 参数解析与行为选项汇总
        var options = ParseOptions(args, inputPath);
        var swTotal = Stopwatch.StartNew();
        try
        {
            bool wroteFile = Process(options);
            swTotal.Stop();
            if (wroteFile)
            {
                Console.WriteLine($"✅ 写入完成: {options.OutputPath}");
            }
            Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            swTotal.Stop();
            string prefix = options.UseStreamingDecoder ? "流式解码失败" : "处理失败";
            Console.Error.WriteLine($"{prefix}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("用法: dotnet run -- <输入文件路径> [输出文件路径] [操作] [--quality N]");
        Console.WriteLine("支持输入: .jpg/.jpeg/.png/.bmp/.webp/.gif");
        Console.WriteLine("支持输出: .jpg/.jpeg/.png/.bmp/.webp/.gif");
        Console.WriteLine("操作: resize:WxH | resizebilinear:WxH | resizefit:WxH | grayscale");
        Console.WriteLine("参数: --quality N | --subsample 420/444 | --keep-metadata | --fdct int/float | --stream | --jpeg-debug | --gif-frames | --gray");
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
                JpegEncoder.DebugPrintConfig = true;
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
            if (string.Equals(a, "--keep-metadata", StringComparison.OrdinalIgnoreCase))
            {
                options.KeepMetadata = true;
                continue;
            }
            if (TryParseQuality(args, ref i, out int? quality))
            {
                options.JpegQuality = quality;
                continue;
            }
            if (TryParseSubsample(args, ref i, out bool? subsample420))
            {
                options.Subsample420 = subsample420;
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
            if (options.OutputPath == null && !a.StartsWith("-", StringComparison.Ordinal))
            {
                options.OutputPath = a;
            }
        }
        return options;
    }

    static bool Process(CliOptions options)
    {
        string inExt = Path.GetExtension(options.InputPath).ToLowerInvariant();

        // GIF 动画帧导出为单张图片序列
        if (options.GifFrames && inExt == ".gif")
        {
            ExportGifFrames(options);
            return false;
        }

        // 是否存在图像处理操作
        if (options.Operations.Count == 0)
        {
            ProcessNoOps(options, inExt);
            return true;
        }

        ProcessWithOps(options, inExt);
        return true;
    }

    static void ExportGifFrames(CliOptions options)
    {
        // 根据输出路径推导帧输出目录与格式
        string baseOut = options.OutputPath ?? Path.ChangeExtension(options.InputPath, ".png");
        string dir = Path.GetDirectoryName(baseOut) ?? ".";
        string nameNoExt = Path.GetFileNameWithoutExtension(baseOut);
        string ext = NormalizeOutputExtension(Path.GetExtension(baseOut));
        var gifDec = new GifDecoder();
        var frames = gifDec.DecodeAllFrames(options.InputPath);
        int digits = Math.Max(3, frames.Count.ToString().Length);

        for (int i = 0; i < frames.Count; i++)
        {
            string idx = i.ToString().PadLeft(digits, '0');
            string path = Path.Combine(dir, $"{nameNoExt}_{idx}{ext}");
            SaveGifFrame(frames[i], path, ext, options);
        }

        Console.WriteLine($"✅ 导出 GIF 动画帧: 共 {frames.Count} 帧");
    }

    static void SaveGifFrame(Image<Rgb24> frame, string path, string ext, CliOptions options)
    {
        bool isInputGray = IsGrayRgb(frame);
        if (ext is ".jpg" or ".jpeg")
        {
            int q = options.JpegQuality ?? 75;
            bool effectiveSubsample420 = options.Subsample420 ?? true;
            new ImageFrame(frame.Width, frame.Height, frame.Buffer).SaveAsJpeg(path, q, effectiveSubsample420);
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

    static void ProcessNoOps(CliOptions options, string inExt)
    {
        // 默认输出后缀为 PNG
        options.OutputPath = EnsureOutputPath(options.InputPath, options.OutputPath, ".png");
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
            var image = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder);
            swDecode.Stop();
            Console.WriteLine($"解码耗时: {swDecode.ElapsedMilliseconds} ms");
            SaveRgbOrGray(image, outputPath, outExt, options.Gray);
            return;
        }

        if (inExt is ".jpg" or ".jpeg" && (outExt is ".jpg" or ".jpeg"))
        {
            var swDecode = Stopwatch.StartNew();
            var image = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder);
            swDecode.Stop();
            Console.WriteLine($"解码耗时: {swDecode.ElapsedMilliseconds} ms");
            SaveJpegFromRgb(image, options, outputPath, keepMetadata: true);
            return;
        }

        var rgbaImage = LoadRgba32(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder);
        if (outExt is ".jpg" or ".jpeg")
        {
            SaveJpegFromRgba(rgbaImage, options, outputPath);
            return;
        }

        if (outExt == ".webp")
        {
            WebpEncoderAdapterRgba.DefaultQuality = (options.JpegQuality ?? 75);
        }

        bool isInputGray = IsGrayRgba(rgbaImage);
        if ((outExt == ".bmp" || outExt == ".png" || outExt == ".webp") && (options.Gray || isInputGray))
        {
            var rgb = new Image<Rgb24>(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer), rgbaImage.Metadata);
            var grayImage = ToGray8(rgb);
            Image.Save(grayImage, outputPath);
            return;
        }

        Image.Save(rgbaImage, outputPath);
    }

    static void ProcessWithOps(CliOptions options, string inExt)
    {
        // 有操作时默认输出后缀为 BMP
        options.OutputPath = EnsureOutputPath(options.InputPath, options.OutputPath, ".bmp");
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        string outExt = Path.GetExtension(outputPath).ToLowerInvariant();

        // 带操作的 GIF -> WebP 动画
        if (inExt == ".gif" && outExt == ".webp")
        {
            ProcessGifToWebpWithOps(options);
            return;
        }

        var image = LoadRgb24(options.InputPath, options.UseFloatIdct, options.UseStreamingDecoder);
        bool isInputGray = IsGrayRgb(image);

        ImageExtensions.Mutate(image, ctx =>
        {
            foreach (var a in options.Operations) a(ctx);
        });

        if (outExt is ".jpg" or ".jpeg")
        {
            int q = options.JpegQuality ?? 75;
            bool effectiveSubsample420 = options.Subsample420 ?? true;
            new ImageFrame(image.Width, image.Height, image.Buffer, image.Metadata)
                .SaveAsJpeg(outputPath, q, effectiveSubsample420, options.KeepMetadata);
            return;
        }

        if (outExt == ".webp")
        {
            WebpEncoderAdapter.Quality = (options.JpegQuality ?? 75);
        }

        if ((outExt == ".bmp" || outExt == ".png" || outExt == ".webp") && (options.Gray || isInputGray))
        {
            var grayImage = ToGray8(image);
            Image.Save(grayImage, outputPath);
            return;
        }

        Image.Save(image, outputPath);
    }

    static void ConvertGifToWebp(CliOptions options)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        var gifDec = new GifDecoder();
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
            WebpEncoderAdapterRgba.DefaultQuality = (options.JpegQuality ?? 75);
            Image.Save(rgbaImage, outputPath);
            return;
        }

        int loop = anim.LoopCount;
        float qWebp = options.JpegQuality.HasValue ? options.JpegQuality.Value : 75f;
        WebpAnimationEncoder.EncodeRgb24(outputPath, anim.Frames, anim.FrameDurationsMs, loop, qWebp);
    }

    static void ProcessGifToWebpWithOps(CliOptions options)
    {
        string outputPath = options.OutputPath ?? throw new InvalidOperationException("输出路径为空");
        var gifDec = new GifDecoder();
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
        if (options.Gray || isInputGray)
        {
            var grayImage = ToGray8(image);
            JpegEncoder.Encode(grayImage, outputPath, q, true, keepMetadata);
            return;
        }

        bool effectiveSubsample420 = options.Subsample420 ?? true;
        JpegEncoder.Encode(image, outputPath, q, effectiveSubsample420, keepMetadata);
    }

    static void SaveJpegFromRgba(Image<Rgba32> rgbaImage, CliOptions options, string outputPath)
    {
        int q = options.JpegQuality ?? 75;
        bool isInputGray = IsGrayRgba(rgbaImage);
        if (options.Gray || isInputGray)
        {
            var rgb = new Image<Rgb24>(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer), rgbaImage.Metadata);
            var grayImage = ToGray8(rgb);
            JpegEncoder.Encode(grayImage, outputPath, q);
            return;
        }

        var rgbImage = new Image<Rgb24>(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer), rgbaImage.Metadata);
        bool effectiveSubsample420 = options.Subsample420 ?? true;
        JpegEncoder.Encode(rgbImage, outputPath, q, effectiveSubsample420);
    }

    static string NormalizeOutputExtension(string ext)
    {
        string lower = ext.ToLowerInvariant();
        if (lower is ".bmp" or ".png" or ".jpg" or ".jpeg" or ".webp") return lower;
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

    static bool TryParseSubsample(string[] args, ref int index, out bool? subsample420)
    {
        subsample420 = null;
        string a = args[index];
        if (string.Equals(a, "--subsample", StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Length)
            {
                string v = args[index + 1].Trim();
                if (string.Equals(v, "420", StringComparison.OrdinalIgnoreCase)) subsample420 = true;
                else if (string.Equals(v, "444", StringComparison.OrdinalIgnoreCase)) subsample420 = false;
                index++;
            }
            return true;
        }
        if (a.StartsWith("--subsample=", StringComparison.OrdinalIgnoreCase))
        {
            string v = a["--subsample=".Length..].Trim();
            if (string.Equals(v, "420", StringComparison.OrdinalIgnoreCase)) subsample420 = true;
            else if (string.Equals(v, "444", StringComparison.OrdinalIgnoreCase)) subsample420 = false;
            return true;
        }
        return false;
    }

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
        if (!op.StartsWith(prefix)) return false;
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
        return new Image<Rgba32>(image.Width, image.Height, rgba);
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

    static Image<Rgb24> LoadRgb24(string path, bool useFloatIdct, bool useStreamingDecoder)
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
                var decoder = new JpegDecoder { UseFloatingPointIdct = useFloatIdct };
                var sw = Stopwatch.StartNew();
                var img = decoder.Decode(fs);
                sw.Stop();
                Console.WriteLine($"[stream] 解码完成: {sw.ElapsedMilliseconds} ms");
                if (decoder.ExifOrientation != 1)
                {
                    var frame = new ImageFrame(img.Width, img.Height, img.Buffer, img.Metadata);
                    frame = frame.ApplyExifOrientation(decoder.ExifOrientation);
                    img.Update(frame.Width, frame.Height, frame.Pixels);
                    img.Metadata.Orientation = 1;
                }
                return img;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"流式解码失败: {ex.Message}", ex);
            }
        }
        if (!useFloatIdct || !isJpeg) return Image.Load(path);
        using var fs2 = File.OpenRead(path);
        var decoder2 = new JpegDecoder { UseFloatingPointIdct = true };
        var img2 = decoder2.Decode(fs2);
        if (decoder2.ExifOrientation != 1)
        {
            var frame = new ImageFrame(img2.Width, img2.Height, img2.Buffer, img2.Metadata);
            frame = frame.ApplyExifOrientation(decoder2.ExifOrientation);
            img2.Update(frame.Width, frame.Height, frame.Pixels);
            img2.Metadata.Orientation = 1;
        }
        return img2;
    }

    static Image<Rgba32> LoadRgba32(string path, bool useFloatIdct, bool useStreamingDecoder)
    {
        if (!useFloatIdct) return Image.LoadRgba32(path);
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return Image.LoadRgba32(path);
        }
        var rgb = LoadRgb24(path, true, useStreamingDecoder);
        return ToRgba32(rgb);
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

    private sealed class CliOptions(string inputPath)
    {
        public string InputPath { get; } = inputPath;
        public string? OutputPath { get; set; }
        public int? JpegQuality { get; set; }
        public bool? Subsample420 { get; set; }
        public bool KeepMetadata { get; set; }
        public bool GifFrames { get; set; }
        public bool UseFloatIdct { get; set; }
        public bool UseStreamingDecoder { get; set; }
        public bool Gray { get; set; }
        public List<Action<ImageProcessingContext>> Operations { get; } = [];
    }
}
