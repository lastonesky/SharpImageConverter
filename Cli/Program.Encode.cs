using System.Diagnostics;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

sealed partial class Program
{
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
}
