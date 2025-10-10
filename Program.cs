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
            Console.WriteLine($"Scan: NbChannels={scan.NbChannels}, DataOffset={scan.DataOffset}, DataLength={scan.DataLength}");
            for (int i = 0; i < scan.NbChannels; i++)
            {
                var c = scan.Components[i];
                Console.WriteLine($"  Channel {c.channelId}: DC={c.dcTableId}, AC={c.acTableId}");
            }
        }
        Console.WriteLine("Step 7 OK: SOS 段解析完成");

        Console.WriteLine("Step 8: 基线JPEG解码到RGB并写BMP...");
        var decoder = new JpegDecoder(parser);
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var swDecode = System.Diagnostics.Stopwatch.StartNew();
        byte[] rgb = decoder.DecodeToRGB(inputPath);
        swDecode.Stop();
        string outPath = ResolveOutputPath(inputPath, outputPath);
        var swWrite = System.Diagnostics.Stopwatch.StartNew();
        BmpWriter.Write24(outPath, parser.Width, parser.Height, rgb);
        swWrite.Stop();
        swTotal.Stop();
        Console.WriteLine($"✅ BMP 写入完成: {outPath}");
        Console.WriteLine($"⏱️ 解码耗时: {swDecode.ElapsedMilliseconds} ms, 写入耗时: {swWrite.ElapsedMilliseconds} ms, 总耗时: {swTotal.ElapsedMilliseconds} ms");

    }

    private static string ResolveOutputPath(string inputPath, string? outputPath)
    {
        string desired = outputPath ?? Path.Combine(
            Path.GetDirectoryName(inputPath) ?? ".",
            Path.GetFileNameWithoutExtension(inputPath) + ".bmp");

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
