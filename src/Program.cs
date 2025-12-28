using System;
using System.Collections.Generic;
using System.IO;

using PictureSharp.Core;
using PictureSharp.Processing;

namespace PictureSharp;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: dotnet run -- <输入文件路径> [输出文件路径] [操作] [--quality N]");
            Console.WriteLine("支持输入: .jpg/.jpeg/.png/.bmp");
            Console.WriteLine("支持输出: .jpg/.jpeg/.png/.bmp/.webp");
            Console.WriteLine("操作: resize:WxH | resizefit:WxH | grayscale");
            Console.WriteLine("参数: --quality N | --subsample 420/444 | --fdct int/float | --jpeg-debug");
            return;
        }
        string inputPath = args[0];
        string? outputPath = null;
        int? jpegQuality = null;
        bool? subsample420 = null;
        bool? useIntFdct = null;
        bool jpegDebug = false;
        var ops = new List<Action<Processing.ImageProcessingContext>>();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--jpeg-debug", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--debug-jpeg", StringComparison.OrdinalIgnoreCase))
            {
                jpegDebug = true;
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
                if (int.TryParse(v, out int q)) jpegQuality = q;
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
            if (a.StartsWith("--subsample=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a["--subsample=".Length..].Trim();
                if (string.Equals(v, "420", StringComparison.OrdinalIgnoreCase)) subsample420 = true;
                else if (string.Equals(v, "444", StringComparison.OrdinalIgnoreCase)) subsample420 = false;
                continue;
            }
            if (string.Equals(a, "--fdct", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    string v = args[i + 1].Trim();
                    if (string.Equals(v, "int", StringComparison.OrdinalIgnoreCase)) useIntFdct = true;
                    else if (string.Equals(v, "float", StringComparison.OrdinalIgnoreCase)) useIntFdct = false;
                    i++;
                }
                continue;
            }
            if (a.StartsWith("--fdct=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a["--fdct=".Length..].Trim();
                if (string.Equals(v, "int", StringComparison.OrdinalIgnoreCase)) useIntFdct = true;
                else if (string.Equals(v, "float", StringComparison.OrdinalIgnoreCase)) useIntFdct = false;
                continue;
            }
            string op = a.ToLowerInvariant();
            if (op.StartsWith("resize:"))
            {
                var parts = op.Substring(7).Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    ops.Add(ctx => ctx.Resize(w, h));
                }
                continue;
            }
            if (op.StartsWith("resizefit:"))
            {
                var parts = op[10..].Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    ops.Add(ctx => ctx.ResizeToFit(w, h));
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
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var image = Image.Load(inputPath);
        if (ops.Count > 0)
        {
            ImageExtensions.Mutate(image, ctx =>
            {
                foreach (var a in ops) a(ctx);
            });
        }
        if (outputPath == null)
        {
            string defExt = ".bmp";
            outputPath = Path.ChangeExtension(inputPath, defExt);
        }
        string outExt = Path.GetExtension(outputPath).ToLowerInvariant();
        if (outExt is ".jpg" or ".jpeg")
        {
            int q = jpegQuality ?? 75;
            var frame = new ImageFrame(image.Width, image.Height, image.Buffer);
            JpegEncoder.DebugPrintConfig = jpegDebug;
            bool effectiveSubsample420 = subsample420 ?? true;
            bool effectiveUseIntFdct = useIntFdct ?? true;
            frame.SaveAsJpeg(outputPath, q, effectiveSubsample420, effectiveUseIntFdct);
        }
        else
        {
            Image.Save(image, outputPath);
        }
        swTotal.Stop();
        Console.WriteLine($"✅ 写入完成: {outputPath}");
        Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
    }

    static void ProcessJpeg(string inputPath, string? outputPath)
    {
        Console.WriteLine("Step 5: 解析 JPEG 量化表...");

        string path = inputPath;
        JpegParser parser = new JpegParser();
        parser.Parse(path);

        Console.WriteLine($"解析到 {parser.Segments.Count} 个段。");
        Console.WriteLine($"✅ 图像尺寸: {parser.Width} x {parser.Height}");
        Console.WriteLine($"✅ 量化表数量: {parser.QuantTables.Count}");

        foreach (var kv in parser.QuantTables)
        {
            kv.Value.Print();
            Console.WriteLine();
        }

        Console.WriteLine("Step 5 OK: DQT 段解析完成");
        Console.WriteLine($"✅ Huffman 表数量: {parser.HuffmanTables.Count}");
        foreach (var kv in parser.HuffmanTables)
        {
            kv.Value.Print();
            Console.WriteLine();
        }
        Console.WriteLine("Step 6 OK: DHT 段解析完成");
        Console.WriteLine($"✅ 扫描段数量: {parser.Scans.Count}");
        foreach (var scan in parser.Scans)
        {
            Console.WriteLine($"Scan: NbChannels={scan.NbChannels}, Ss={scan.Ss}, Se={scan.Se}, Ah={scan.Ah}, Al={scan.Al}, DataOffset={scan.DataOffset}, DataLength={scan.DataLength}");
            for (int i = 0; i < scan.NbChannels; i++)
            {
                var c = scan.Components[i];
                Console.WriteLine($"  Channel {c.channelId}: DC={c.dcTableId}, AC={c.acTableId}");
            }
        }
        Console.WriteLine("Step 7 OK: SOS 段解析完成");

        Console.WriteLine(parser.IsProgressive ? "Step 8: 渐进式JPEG解码到RGB..." : "Step 8: 基线JPEG解码到RGB...");
        var decoder = new JpegDecoder(parser);
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var swDecode = System.Diagnostics.Stopwatch.StartNew();
        byte[] rgb = decoder.DecodeToRGB(inputPath);
        swDecode.Stop();
        
        ImageFrame frame = new ImageFrame(parser.Width, parser.Height, rgb);
        if (parser.ExifOrientation != 1)
        {
            Console.WriteLine($"✅ 检测到 EXIF 方向: {parser.ExifOrientation}，将应用对应的图像变换。");
            frame = frame.ApplyExifOrientation(parser.ExifOrientation);
        }

        string outPath = ResolveOutputPath(inputPath, outputPath, ".bmp");
        string outExt = Path.GetExtension(outPath).ToLower();
        var swWrite = System.Diagnostics.Stopwatch.StartNew();

        if (outExt != ".bmp" && outExt != ".png" && outExt != ".jpg" && outExt != ".jpeg")
        {
            Console.WriteLine($"不支持的输出格式: {outExt}，默认写入 BMP");
            outPath = Path.ChangeExtension(outPath, ".bmp");
        }
        frame.Save(outPath);
        Console.WriteLine($"✅ 写入完成: {outPath}");

        swWrite.Stop();
        swTotal.Stop();
        Console.WriteLine($"⏱️ 解码耗时: {swDecode.ElapsedMilliseconds} ms, 写入耗时: {swWrite.ElapsedMilliseconds} ms, 总耗时: {swTotal.ElapsedMilliseconds} ms");
    }

    static void ProcessPng(string inputPath, string? outputPath)
    {
        Console.WriteLine($"正在处理 PNG: {inputPath}");
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        
        var decoder = new PngDecoder();
        byte[] rgb = decoder.DecodeToRGB(inputPath);
        
        Console.WriteLine($"✅ 图像尺寸: {decoder.Width} x {decoder.Height}");
        Console.WriteLine($"✅ 色深: {decoder.BitDepth}, 颜色类型: {decoder.ColorType}");
        Console.WriteLine($"✅ 隔行扫描: {decoder.InterlaceMethod == 1}");

        ImageFrame frame = new ImageFrame(decoder.Width, decoder.Height, rgb);
        string outPath = ResolveOutputPath(inputPath, outputPath, ".bmp");
        string outExt = Path.GetExtension(outPath).ToLower();

        if (outExt != ".bmp" && outExt != ".png" && outExt != ".jpg" && outExt != ".jpeg")
        {
            Console.WriteLine($"不支持的输出格式: {outExt}，默认写入 BMP");
            outPath = Path.ChangeExtension(outPath, ".bmp");
        }
        frame.Save(outPath);
        Console.WriteLine($"✅ 写入完成: {outPath}");
        
        swTotal.Stop();
        Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
    }

    static void ProcessBmp(string inputPath, string? outputPath)
    {
        Console.WriteLine($"正在处理 BMP: {inputPath}");
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        
        int width, height;
        byte[] rgb = BmpReader.Read(inputPath, out width, out height);
        
        Console.WriteLine($"✅ 图像尺寸: {width} x {height}");

        ImageFrame frame = new ImageFrame(width, height, rgb);
        string outPath = ResolveOutputPath(inputPath, outputPath, ".png");
        string outExt = Path.GetExtension(outPath).ToLower();

        if (outExt != ".bmp" && outExt != ".png" && outExt != ".jpg" && outExt != ".jpeg")
        {
            Console.WriteLine($"不支持的输出格式: {outExt}，默认写入 PNG");
            outPath = Path.ChangeExtension(outPath, ".png");
        }
        frame.Save(outPath);
        Console.WriteLine($"✅ 写入完成: {outPath}");

        swTotal.Stop();
        Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
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
