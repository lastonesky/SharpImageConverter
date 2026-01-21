using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: dotnet run -- <输入文件路径> [输出文件路径] [操作] [--quality N]");
            Console.WriteLine("支持输入: .jpg/.jpeg/.png/.bmp/.webp/.gif");
            Console.WriteLine("支持输出: .jpg/.jpeg/.png/.bmp/.webp/.gif");
            Console.WriteLine("操作: resize:WxH | resizebilinear:WxH | resizefit:WxH | grayscale");
            Console.WriteLine("参数: --quality N | --subsample 420/444 | --keep-metadata | --fdct int/float | --idct int/float | --jpeg-debug | --gif-frames");
            return;
        }
        string inputPath = args[0];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"输入文件不存在: {inputPath}");
            return;
        }
        string? outputPath = null;
        int? jpegQuality = null;
        bool? subsample420 = null;
        bool keepMetadata = false;
        bool gifFrames = false;
        bool useFloatIdct = false;
        var ops = new List<Action<ImageProcessingContext>>();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--gif-frames", StringComparison.OrdinalIgnoreCase))
            {
                gifFrames = true;
                continue;
            }
            if (string.Equals(a, "--jpeg-debug", StringComparison.OrdinalIgnoreCase))
            {
                JpegEncoder.DebugPrintConfig = true;
                continue;
            }
            if (string.Equals(a, "--quality", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-q", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out int q))
                {
                    jpegQuality = q;
                    i++;
                }
                continue;
            }
            if (a.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a["--quality=".Length..];
                if (int.TryParse(v, out int q2)) jpegQuality = q2;
                continue;
            }
            if (string.Equals(a, "--subsample", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    string v = args[i + 1].Trim();
                    if (string.Equals(v, "420", StringComparison.OrdinalIgnoreCase)) subsample420 = true;
                    else if (string.Equals(v, "444", StringComparison.OrdinalIgnoreCase)) subsample420 = false;
                    i++;
                }
                continue;
            }
            if (string.Equals(a, "--keep-metadata", StringComparison.OrdinalIgnoreCase))
            {
                keepMetadata = true;
                continue;
            }
            if (string.Equals(a, "--idct", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    string v = args[i + 1].Trim();
                    if (string.Equals(v, "float", StringComparison.OrdinalIgnoreCase)) useFloatIdct = true;
                    else if (string.Equals(v, "int", StringComparison.OrdinalIgnoreCase)) useFloatIdct = false;
                    i++;
                }
                continue;
            }
            if (a.StartsWith("--idct=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a["--idct=".Length..].Trim();
                if (string.Equals(v, "float", StringComparison.OrdinalIgnoreCase)) useFloatIdct = true;
                else if (string.Equals(v, "int", StringComparison.OrdinalIgnoreCase)) useFloatIdct = false;
                continue;
            }
            if (a.StartsWith("--subsample=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a["--subsample=".Length..].Trim();
                if (string.Equals(v, "420", StringComparison.OrdinalIgnoreCase)) subsample420 = true;
                else if (string.Equals(v, "444", StringComparison.OrdinalIgnoreCase)) subsample420 = false;
                continue;
            }
            string op = a.ToLowerInvariant();
            if (op.StartsWith("resize:"))
            {
                var sizePart = op.Substring(7);
                var parts = sizePart.Split(['x', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    ops.Add(ctx => ctx.Resize(w, h));
                }
                continue;
            }
            if (op.StartsWith("resizebilinear:"))
            {
                var sizePart = op.Substring("resizebilinear:".Length);
                var parts = sizePart.Split(['x', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    ops.Add(ctx => ctx.ResizeBilinear(w, h));
                }
                continue;
            }
            if (op.StartsWith("resizefit:"))
            {
                var sizePart = op[10..];
                var parts = sizePart.Split(['x', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int w2) && int.TryParse(parts[1], out int h2))
                {
                    ops.Add(ctx => ctx.ResizeToFit(w2, h2));
                }
                continue;
            }
            if (op == "grayscale")
            {
                ops.Add(ctx => ctx.Grayscale());
                continue;
            }

            if (outputPath == null && !a.StartsWith("-", StringComparison.Ordinal))
            {
                outputPath = a;
            }
        }
        var swTotal = Stopwatch.StartNew();
        string inExt = Path.GetExtension(inputPath).ToLowerInvariant();
        if (gifFrames && inExt == ".gif")
        {
            string baseOut = outputPath ?? Path.ChangeExtension(inputPath, ".png");
            string dir = Path.GetDirectoryName(baseOut) ?? ".";
            string nameNoExt = Path.GetFileNameWithoutExtension(baseOut);
            string ext = Path.GetExtension(baseOut).ToLowerInvariant();
            if (ext != ".bmp" && ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp")
            {
                ext = ".png";
            }
            var gifDec = new GifDecoder();
            var frames = gifDec.DecodeAllFrames(inputPath);
            int digits = Math.Max(3, frames.Count.ToString().Length);
            for (int i = 0; i < frames.Count; i++)
            {
                string idx = i.ToString().PadLeft(digits, '0');
                string path = Path.Combine(dir, $"{nameNoExt}_{idx}{ext}");
                if (ext is ".jpg" or ".jpeg")
                {
                    int q = jpegQuality ?? 75;
                    var frame = new ImageFrame(frames[i].Width, frames[i].Height, frames[i].Buffer);
                    bool effectiveSubsample420 = subsample420 ?? true;
                    frame.SaveAsJpeg(path, q, effectiveSubsample420);
                }
                else
                {
                    Image.Save(frames[i], path);
                }
            }
            swTotal.Stop();
            Console.WriteLine($"✅ 导出 GIF 动画帧: 共 {frames.Count} 帧");
            Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
            return;
        }
        bool noOps = ops.Count == 0;
        if (noOps)
        {
            if (outputPath == null)
            {
                string defExt = ".png";
                outputPath = ResolveOutputPath(inputPath, outputPath, defExt);
            }
            string outExt2 = Path.GetExtension(outputPath).ToLowerInvariant();
            if (inExt == ".gif" && outExt2 == ".webp")
            {
                var gifDec = new GifDecoder();
                GifAnimation anim;
                using (var fs = File.OpenRead(inputPath))
                {
                    anim = gifDec.DecodeAnimationRgb24(fs);
                }

                if (anim.Frames.Count == 0)
                {
                    var rgbaImage = Image.LoadRgba32(inputPath);
                    Image.Save(rgbaImage, outputPath);
                }
                else if (anim.Frames.Count == 1)
                {
                    var rgbaImage = ToRgba32(anim.Frames[0]);
                    WebpEncoderAdapterRgba.DefaultQuality = (jpegQuality ?? 75);
                    Image.Save(rgbaImage, outputPath);
                }
                else
                {
                    int loop = anim.LoopCount;
                    float qWebp = jpegQuality.HasValue ? jpegQuality.Value : 75f;
                    WebpAnimationEncoder.EncodeRgb24(outputPath, anim.Frames, anim.FrameDurationsMs, loop, qWebp);
                }
            }
            else
            {
                if (outputPath == null)
                {
                    string defExt = ".png";
                    outputPath = ResolveOutputPath(inputPath, outputPath, defExt);
                }
                string outExt2b = Path.GetExtension(outputPath).ToLowerInvariant();
                if (inExt is ".jpg" or ".jpeg" && outExt2b == ".bmp")
                {
                    var swDecode = Stopwatch.StartNew();
                    var image = LoadRgb24(inputPath, useFloatIdct);
                    swDecode.Stop();
                    Console.WriteLine($"解码耗时: {swDecode.ElapsedMilliseconds} ms");
                    Image.Save(image, outputPath);
                }
                else if (inExt is ".jpg" or ".jpeg" && (outExt2b is ".jpg" or ".jpeg"))
                {
                    var swDecode = Stopwatch.StartNew();
                    var image = LoadRgb24(inputPath, useFloatIdct);
                    swDecode.Stop();
                    Console.WriteLine($"解码耗时: {swDecode.ElapsedMilliseconds} ms");
                    int q = jpegQuality ?? 75;
                    var frame = new ImageFrame(image.Width, image.Height, image.Buffer, image.Metadata);
                    bool effectiveSubsample420 = subsample420 ?? true;
                    frame.SaveAsJpeg(outputPath, q, effectiveSubsample420, keepMetadata);
                }
                else
                {
                    var rgbaImage = LoadRgba32(inputPath, useFloatIdct);
                    if (outExt2b is ".jpg" or ".jpeg")
                    {
                        int q = jpegQuality ?? 75;
                        var frame = new ImageFrame(rgbaImage.Width, rgbaImage.Height, RgbaToRgb(rgbaImage.Buffer));
                        bool effectiveSubsample420 = subsample420 ?? true;
                        frame.SaveAsJpeg(outputPath, q, effectiveSubsample420);
                    }
                    else
                    {
                        if (outExt2b == ".webp")
                        {
                            WebpEncoderAdapterRgba.DefaultQuality = (jpegQuality ?? 75);
                        }
                        Image.Save(rgbaImage, outputPath);
                    }
                }
            }
            swTotal.Stop();
            Console.WriteLine($"✅ 写入完成: {outputPath}");
            Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
            return;
        }
        else
        {
            if (outputPath == null)
            {
                string defExt = ".bmp";
                outputPath = ResolveOutputPath(inputPath, outputPath, defExt);
            }
            string outExt = Path.GetExtension(outputPath).ToLowerInvariant();
                if (inExt == ".gif" && outExt == ".webp")
            {
                var gifDec = new GifDecoder();
                GifAnimation anim;
                using (var fs = File.OpenRead(inputPath))
                {
                    anim = gifDec.DecodeAnimationRgb24(fs);
                }

                var processed = new List<Image<Rgb24>>(anim.Frames.Count);
                for (int i = 0; i < anim.Frames.Count; i++)
                {
                    var frame = anim.Frames[i];
                    ImageExtensions.Mutate(frame, ctx =>
                    {
                        foreach (var a in ops) a(ctx);
                    });
                    processed.Add(frame);
                }

                float qWebpOps = jpegQuality.HasValue ? jpegQuality.Value : 75f;
                WebpAnimationEncoder.EncodeRgb24(outputPath, processed, anim.FrameDurationsMs, anim.LoopCount, qWebpOps);
            }
            else
            {
                var image = LoadRgb24(inputPath, useFloatIdct);
                ImageExtensions.Mutate(image, ctx =>
                {
                    foreach (var a in ops) a(ctx);
                });
                if (outExt is ".jpg" or ".jpeg")
                {
                    int q = jpegQuality ?? 75;
                    var frame = new ImageFrame(image.Width, image.Height, image.Buffer, image.Metadata);
                    bool effectiveSubsample420 = subsample420 ?? true;
                    frame.SaveAsJpeg(outputPath, q, effectiveSubsample420, keepMetadata);
                }
                else
                {
                    if (outExt == ".webp")
                    {
                        WebpEncoderAdapter.Quality = (jpegQuality ?? 75);
                    }
                    Image.Save(image, outputPath);
                }
            }
            swTotal.Stop();
            Console.WriteLine($"✅ 写入完成: {outputPath}");
            Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
        }
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

    static Image<Rgb24> LoadRgb24(string path, bool useFloatIdct)
    {
        if (!useFloatIdct) return Image.Load(path);
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return Image.Load(path);
        }
        using var fs = File.OpenRead(path);
        var decoder = new JpegDecoder { UseFloatingPointIdct = true };
        var img = decoder.Decode(fs);
        if (decoder.ExifOrientation != 1)
        {
            var frame = new ImageFrame(img.Width, img.Height, img.Buffer, img.Metadata);
            frame = frame.ApplyExifOrientation(decoder.ExifOrientation);
            img.Update(frame.Width, frame.Height, frame.Pixels);
            img.Metadata.Orientation = 1;
        }
        return img;
    }

    static Image<Rgba32> LoadRgba32(string path, bool useFloatIdct)
    {
        if (!useFloatIdct) return Image.LoadRgba32(path);
        string ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return Image.LoadRgba32(path);
        }
        var rgb = LoadRgb24(path, true);
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
}
