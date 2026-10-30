using System.Diagnostics;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter;

sealed partial class Program
{
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
}
