using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: dotnet run -- <输入JPEG路径> [输出BMP路径]");
            Console.WriteLine("当未提供输出路径时，默认写入到与输入同目录同名的 .bmp，且不覆盖已存在文件。");
            return;
        }
        string inputPath = args[0];
        string? outputPath = args.Length >= 2 ? args[1] : null;

        // Auto-detect format by extension or content
        string ext = Path.GetExtension(inputPath).ToLower();
        
        if (ext == ".jpg" || ext == ".jpeg")
        {
            ProcessJpeg(inputPath, outputPath);
        }
        else if (ext == ".png")
        {
            ProcessPng(inputPath, outputPath);
        }
        else if (ext == ".bmp")
        {
            ProcessBmp(inputPath, outputPath);
        }
        else
        {
            Console.WriteLine("不支持的输入文件格式。仅支持 .jpg, .png, .bmp");
        }
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
        
        // 根据 EXIF 方向进行旋转/翻转
        int outW = parser.Width, outH = parser.Height;
        if (parser.ExifOrientation != 1)
        {
            Console.WriteLine($"✅ 检测到 EXIF 方向: {parser.ExifOrientation}，将应用对应的图像变换。");
            var transformed = ApplyExifOrientation(rgb, parser.Width, parser.Height, parser.ExifOrientation);
            rgb = transformed.pixels;
            outW = transformed.width;
            outH = transformed.height;
        }

        string outPath = ResolveOutputPath(inputPath, outputPath, ".bmp");
        string outExt = Path.GetExtension(outPath).ToLower();
        var swWrite = System.Diagnostics.Stopwatch.StartNew();

        if (outExt == ".bmp")
        {
            BmpWriter.Write24(outPath, outW, outH, rgb);
            Console.WriteLine($"✅ BMP 写入完成: {outPath}");
        }
        else if (outExt == ".png")
        {
            PngWriter.Write(outPath, outW, outH, rgb);
            Console.WriteLine($"✅ PNG 写入完成: {outPath}");
        }
        else
        {
             Console.WriteLine($"不支持的输出格式: {outExt}，默认写入 BMP");
             outPath = Path.ChangeExtension(outPath, ".bmp");
             BmpWriter.Write24(outPath, outW, outH, rgb);
        }

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

        string outPath = ResolveOutputPath(inputPath, outputPath, ".bmp");
        string outExt = Path.GetExtension(outPath).ToLower();

        if (outExt == ".bmp")
        {
            BmpWriter.Write24(outPath, decoder.Width, decoder.Height, rgb);
            Console.WriteLine($"✅ BMP 写入完成: {outPath}");
        }
        else if (outExt == ".png")
        {
            // Re-encode
             PngWriter.Write(outPath, decoder.Width, decoder.Height, rgb);
             Console.WriteLine($"✅ PNG 重编码完成: {outPath}");
        }
        
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

        string outPath = ResolveOutputPath(inputPath, outputPath, ".png");
        string outExt = Path.GetExtension(outPath).ToLower();

        if (outExt == ".png")
        {
            PngWriter.Write(outPath, width, height, rgb);
            Console.WriteLine($"✅ PNG 写入完成: {outPath}");
        }
        else if (outExt == ".bmp")
        {
             BmpWriter.Write24(outPath, width, height, rgb);
             Console.WriteLine($"✅ BMP 重写完成: {outPath}");
        }

        swTotal.Stop();
        Console.WriteLine($"⏱️ 总耗时: {swTotal.ElapsedMilliseconds} ms");
    }

    private static string ResolveOutputPath(string inputPath, string? outputPath, string defaultExtension)
    {
        string desired = outputPath;
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

    private static (byte[] pixels, int width, int height) ApplyExifOrientation(byte[] src, int width, int height, int orientation)
    {
        int newW = width;
        int newH = height;
        switch (orientation)
        {
            case 1: // 正常
                return (src, width, height);
            case 2: // 水平镜像
                newW = width; newH = height; break;
            case 3: // 旋转180
                newW = width; newH = height; break;
            case 4: // 垂直镜像
                newW = width; newH = height; break;
            case 5: // 水平镜像 + 旋转270（Transpose）
            case 6: // 旋转90 CW
            case 7: // 水平镜像 + 旋转90（Transverse）
            case 8: // 旋转270 CW（90 CCW）
                newW = height; newH = width; break;
            default:
                return (src, width, height);
        }

        byte[] dst = new byte[newW * newH * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx, dy;
                switch (orientation)
                {
                    case 2: // 水平镜像
                        dx = (width - 1 - x); dy = y; break;
                    case 3: // 旋转180
                        dx = (width - 1 - x); dy = (height - 1 - y); break;
                    case 4: // 垂直镜像
                        dx = x; dy = (height - 1 - y); break;
                    case 5: // Transpose（主对角线翻转）
                        dx = y; dy = x; break;
                    case 6: // Rotate 90 CW
                        dx = (height - 1 - y); dy = x; break;
                    case 7: // Transverse（副对角线翻转）
                        dx = (height - 1 - y); dy = (width - 1 - x); break;
                    case 8: // Rotate 270 CW (90 CCW)
                        dx = y; dy = (width - 1 - x); break;
                    default: // 1
                        dx = x; dy = y; break;
                }
                int srcIdx = (y * width + x) * 3;
                int dstIdx = (dy * newW + dx) * 3;
                dst[dstIdx + 0] = src[srcIdx + 0];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
            }
        }
        return (dst, newW, newH);
    }
}
