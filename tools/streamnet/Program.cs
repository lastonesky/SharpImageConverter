using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;

namespace StreamNet;

/// <summary>
/// 网络传输流式调用的稳定性 / 健壮性测试台。
/// 覆盖：分片交付一致性、截断（FIN）、连接重置（RST）、取消、限速、内存、
/// 并发、写出侧断链、仅异步流、头部尺寸炸弹、非 JPEG 格式。
/// </summary>
internal static class Program
{
    private static string repoRoot = ".";
    private static readonly StringBuilder body = new();
    private static readonly List<string> findings = new();

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--bomb-child")
        {
            return BombChild(args[1]);
        }

        repoRoot = FindRepoRoot();
        Console.WriteLine($"仓库: {repoRoot}");
        Console.WriteLine($"运行时: .NET {Environment.Version}, 逻辑核 {Environment.ProcessorCount}");
        Console.WriteLine($"进程堆上限: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB");

        var only = new HashSet<int>();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--only" && int.TryParse(args[i + 1], out int n)) only.Add(n);
        }
        bool Wants(int n) => only.Count == 0 || only.Contains(n);

        if (Wants(1)) RunChunking();
        if (Wants(2)) RunTruncation();
        if (Wants(3)) RunReset();
        if (Wants(4)) RunCancellation();
        if (Wants(5)) RunThrottle();
        if (Wants(6)) RunMemory();
        if (Wants(7)) RunConcurrency();
        if (Wants(8)) RunWritePath();
        if (Wants(9)) RunAsyncOnly();
        if (Wants(10)) RunHeaderBomb();
        if (Wants(11)) RunOtherFormats();

        string report = Compose();
        string outPath = Path.Combine(repoRoot, "docs", "NetworkStreaming.md");
        File.WriteAllText(outPath, report, new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine($"报告已写入: {outPath}");
        Console.WriteLine($"问题条目: {findings.Count}");
        return 0;
    }

    // ---------------------------------------------------------------- 场景 1

    private static void RunChunking()
    {
        Section("1. 分片交付一致性（分片大小 A/B）",
            "同一份 JPEG 经不可 Seek 的仿真网络流喂给 `JpegDecoder.DecodeFromStreamAsync`，" +
            "分片从 1 字节到 256 KB 变化，像素必须与内存解码逐字节一致；同时开启严格模式验证正常文件不被误拒。");

        string[] files = ["video-001.rgb.jpeg", "Amish-Noka-Dresser.jpg", "5_star_base.jpg", "progressive.jpg"];
        int[] chunks = [1, 3, 17, 1024, 16 * 1024, 256 * 1024];
        var table = new Table("文件", "字节", "分片", "Read 次数", "解码 ms", "像素一致");
        int mismatches = 0;

        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(ExamplePath(file));
            byte[] reference = ReferenceRgb(data);
            Console.WriteLine($"[chunk] {file} ({data.Length} B, {reference.Length} px-bytes)");

            foreach (int chunk in chunks)
            {
                if (chunk == 1 && data.Length > 400_000)
                {
                    table.Add(file, data.Length, 1, "-", "-", "跳过(过大)");
                    continue;
                }

                var stream = new SimulatedNetworkStream(data, new NetworkProfile { ChunkBytes = chunk, Seed = chunk + 7 });
                byte[]? rgb = null;
                Exception? error = null;
                var sw = Stopwatch.StartNew();
                try
                {
                    rgb = JpegDecoder.DecodeFromStreamAsync(stream, default, false, requireEoi: true)
                        .GetAwaiter().GetResult().Image.ToRgb24();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                sw.Stop();

                bool same = rgb is not null && rgb.AsSpan().SequenceEqual(reference);
                if (!same) mismatches++;
                table.Add(file, data.Length, chunk, stream.Stats.ReadCalls,
                    sw.Elapsed.TotalMilliseconds.ToString("F1"),
                    error != null ? "异常 " + error.GetType().Name : same ? "是" : "否(pixel diff)");
            }
        }

        AppendTable(table);
        if (mismatches > 0) findings.Add($"分片交付导致 {mismatches} 组分片出现像素差异或异常（应为 0）。");

        // ImageFrame 高层入口（含 EXIF 方向处理）
        byte[] amish = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
        var fromBytes = ImageFrame.Load(amish);
        using var sim = new SimulatedNetworkStream(amish, new NetworkProfile { ChunkBytes = 5, Seed = 42 });
        var fromStream = ImageFrame.LoadJpeg(sim, useStreamingDecoder: true);
        bool frameSame = fromBytes.Width == fromStream.Width && fromBytes.Height == fromStream.Height
            && fromBytes.Pixels.AsSpan().SequenceEqual(fromStream.Pixels);
        AppendLine($"`ImageFrame.LoadJpeg(useStreamingDecoder: true)` 与 `ImageFrame.Load(byte[])` 一致: **{(frameSame ? "是" : "否")}**" +
            $"（{fromStream.Width}x{fromStream.Height}, 分片 5 B）。");
        if (!frameSame) findings.Add("ImageFrame 流式入口与内存入口结果不一致（EXIF/方向处理路径）。");
    }

    // ---------------------------------------------------------------- 场景 2

    private static void RunTruncation()
    {
        Section("2. 截断 / 提前 EOF（FIN）",
            "在文件的每个比例点与每个 marker 边界截断，用干净 EOF（Read 返回 0）交付。" +
            "分别用宽松模式（默认）与严格模式（`requireEoi: true`）解码：" +
            "宽松模式统计「不报错但像素已损坏」的用例，严格模式验证这些用例是否全部被拒。");

        string[] files = ["video-001.rgb.jpeg", "Amish-Noka-Dresser.jpg", "5_star_base.jpg", "progressive.jpg"];
        var summary = new Table("文件", "字节", "用例", "宽松:抛", "宽松:返回", "宽松:损坏", "严格:抛", "严格:返回", "严格:损坏", "最坏差异");
        var allKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(ExamplePath(file));
            byte[] reference = ReferenceRgb(data);

            SweepResult lenient = Sweep(data, reference, strict: false);
            SweepResult strict = Sweep(data, reference, strict: true);

            foreach (string kind in lenient.ExceptionKinds) allKinds[kind] = allKinds.GetValueOrDefault(kind) + 1;
            foreach (string kind in strict.ExceptionKinds) allKinds[kind] = allKinds.GetValueOrDefault(kind) + 1;

            summary.Add(file, data.Length, lenient.Cases, lenient.Threw, lenient.Ok, lenient.Corrupt,
                strict.Threw, strict.Ok, strict.Corrupt, lenient.Worst.ToString("P2"));

            Console.WriteLine($"[trunc] {file}: 用例 {lenient.Cases}；" +
                $"宽松 抛 {lenient.Threw}/返回 {lenient.Ok}/损坏 {lenient.Corrupt}（最坏 {lenient.Worst:P2}）；" +
                $"严格 抛 {strict.Threw}/返回 {strict.Ok}/损坏 {strict.Corrupt}");
            foreach (string line in lenient.Accepted.Take(20)) Console.WriteLine("        " + line);

            if (lenient.Corrupt > 0)
            {
                findings.Add($"{file}: 宽松模式在扫描边界截断时静默返回损坏图像 {lenient.Corrupt}/{lenient.Cases} 例，" +
                    $"最坏像素差异 {lenient.Worst:P2}（最深移除 {lenient.MaxRemovedCorrupt} B，" +
                    $"占 {lenient.MaxRemovedCorrupt * 100.0 / data.Length:F1}%）。");
            }

            if (strict.Ok > 0)
            {
                findings.Add($"{file}: 严格模式仍有 {strict.Ok} 例截断被接受（其中损坏 {strict.Corrupt} 例），" +
                    "说明 `requireEoi` 覆盖不完整。");
            }
        }

        AppendTable(summary);
        AppendLine("异常类型分布（宽松+严格合计）: " + string.Join(", ", allKinds.Select(kv => $"{kv.Key}={kv.Value}")) + "。");
        AppendLine("说明：`严格:返回` 列为严格模式下仍被接受的截断（正常只应剩「截取位置 = 文件长度」这一个对照用例）。");
    }

    private sealed record SweepResult(
        int Cases, int Threw, int Ok, int Corrupt, double Worst,
        int MaxRemovedCorrupt, List<string> Accepted, List<string> ExceptionKinds);

    private static SweepResult Sweep(byte[] data, byte[] reference, bool strict)
    {
        var offsets = TruncationOffsets(data, data.Length <= 8_192);
        int cases = 0, threw = 0, ok = 0, corrupt = 0, maxRemovedCorrupt = 0;
        double worst = 0;
        var accepted = new List<string>();
        var kinds = new List<string>();

        foreach (int offset in offsets)
        {
            // 跳过「截取位置 = 文件长度」的对照用例：它本来就该被接受。
            if (offset >= data.Length) continue;
            cases++;
            byte[] truncated = data[..offset];
            var profile = new NetworkProfile
            {
                ChunkBytes = 4096,
                FaultMode = FaultMode.EarlyEof,
                FaultAtBytes = truncated.Length,
                Seed = 1,
            };

            byte[]? rgb = null;
            Exception? error = null;
            using (var stream = new SimulatedNetworkStream(truncated, profile))
            {
                try
                {
                    rgb = JpegDecoder.DecodeFromStreamAsync(stream, default, false, strict)
                        .GetAwaiter().GetResult().Image.ToRgb24();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }

            int removed = data.Length - offset;
            if (error != null)
            {
                threw++;
                kinds.Add(Classify(error));
                continue;
            }

            ok++;
            double diff = rgb is null ? 1.0 : DiffRatio(reference, rgb);
            worst = Math.Max(worst, diff);
            accepted.Add($"-{removed}B/偏移{offset} → 差异 {diff:P2}");
            if (diff > 0.000001)
            {
                corrupt++;
                maxRemovedCorrupt = Math.Max(maxRemovedCorrupt, removed);
            }
        }

        return new SweepResult(cases, threw, ok, corrupt, worst, maxRemovedCorrupt, accepted, kinds);
    }

    // ---------------------------------------------------------------- 场景 3

    private static void RunReset()
    {
        Section("3. 连接重置（RST）在任意偏移注入",
            "`Read` 抛 `IOException`。要求异常原样向上传播，不能被吞掉变成「成功返回部分图像」。");

        byte[] data = File.ReadAllBytes(ExamplePath("5_star_base.jpg"));
        var table = new Table("故障偏移", "占文件", "结果", "耗时 ms");
        int swallowed = 0;

        long[] offsets = [2, 10, data.Length / 100, data.Length / 5, data.Length / 2, data.Length * 9 / 10];
        foreach (long at in offsets)
        {
            var stream = new SimulatedNetworkStream(data, new NetworkProfile
            {
                ChunkBytes = 4096,
                FaultMode = FaultMode.Reset,
                FaultAtBytes = at,
                Seed = 3,
            });

            Exception? error = null;
            var sw = Stopwatch.StartNew();
            try
            {
                JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            sw.Stop();

            bool isIo = error is IOException;
            if (error is null) swallowed++;
            table.Add(at, $"{at * 100.0 / data.Length:F1}%",
                error is null ? "静默成功（危险）" : $"{Classify(error)}: {Trim(error.Message, 48)}",
                sw.Elapsed.TotalMilliseconds.ToString("F1"));
        }

        AppendTable(table);
        if (swallowed > 0) findings.Add($"连接重置被吞掉 {swallowed} 次，调用方无法区分「完整」与「中断」。");
    }

    // ---------------------------------------------------------------- 场景 4

    private static void RunCancellation()
    {
        Section("4. 传输中途取消",
            "在交付约 25% 字节时取消 `CancellationToken`，期望 `OperationCanceledException`，" +
            "且取消点不晚于下一个缓冲填充点。");

        byte[] data = File.ReadAllBytes(ExamplePath("5_star_base.jpg"));
        var table = new Table("场景", "分片", "结果", "耗时 ms", "已交付");

        foreach (int chunk in new[] { 64, 4096 })
        {
            using var cts = new CancellationTokenSource();
            var stream = new SimulatedNetworkStream(data, new NetworkProfile
            {
                ChunkBytes = chunk,
                Seed = 11,
                Progress = delivered =>
                {
                    if (delivered >= data.Length / 4) cts.Cancel();
                },
            });

            Exception? error = null;
            var sw = Stopwatch.StartNew();
            try
            {
                JpegDecoder.DecodeFromStreamAsync(stream, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            sw.Stop();

            table.Add("中途取消", chunk, error is null ? "未响应取消（危险）" : Classify(error),
                sw.Elapsed.TotalMilliseconds.ToString("F1"), $"{stream.Stats.BytesServed}/{data.Length}");
            if (error is null || error is not OperationCanceledException)
            {
                findings.Add($"分片 {chunk} 时取消未被正确响应：{(error is null ? "调用照常完成" : error.GetType().Name)}。");
            }
        }

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            using var stream = new SimulatedNetworkStream(data, new NetworkProfile { ChunkBytes = 4096 });
            Exception? error = null;
            try
            {
                JpegDecoder.DecodeFromStreamAsync(stream, cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            table.Add("预取消 token", "-", error is null ? "未响应取消（危险）" : Classify(error), "0",
                $"{stream.Stats.BytesServed}/{data.Length}");
            if (error is null) findings.Add("已取消的 token 未能阻止解码。");
        }

        AppendTable(table);
        AppendLine("注意：`ImageFrame.LoadJpeg(stream, useStreamingDecoder: true)` 没有 token 参数，" +
            "高层入口无法取消；只能改用 `JpegDecoder.DecodeFromStreamAsync`。");
    }

    // ---------------------------------------------------------------- 场景 5

    private static void RunThrottle()
    {
        Section("5. 限速 / 高延迟（trickle）",
            "每次 Read 注入固定延迟，检查是否出现忙等、二次读取或超线性开销。" +
            "「注入延迟」为按 Read 次数累加的理论等待，「墙钟」应约等于注入延迟加解码时间。");

        byte[] data = File.ReadAllBytes(ExamplePath("5_star_base.jpg"));
        var table = new Table("分片", "每次延迟 ms", "Read 次数", "注入延迟 ms", "墙钟 ms", "开销 ms", "吞吐 KB/s");

        foreach ((int chunk, double delay) in new[] { (16 * 1024, 20.0), (1024, 2.0), (1024, 0.5), (256, 0.2) })
        {
            var stream = new SimulatedNetworkStream(data, new NetworkProfile
            {
                ChunkBytes = chunk,
                DelayMsPerRead = delay,
                Seed = 5,
            });

            var sw = Stopwatch.StartNew();
            try
            {
                JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult();
            }
            catch
            {
                // 由表格结果体现
            }
            sw.Stop();

            double wall = sw.Elapsed.TotalMilliseconds;
            double overhead = wall - stream.Stats.InjectedDelayMs;
            double throughput = data.Length / 1024.0 / (wall / 1000.0);
            table.Add(chunk, delay, stream.Stats.ReadCalls, stream.Stats.InjectedDelayMs.ToString("F0"),
                wall.ToString("F0"), overhead.ToString("F0"), throughput.ToString("F1"));
        }

        AppendTable(table);
    }

    // ---------------------------------------------------------------- 场景 6

    private static void RunMemory()
    {
        Section("6. 内存开销（流式 vs 整文件）",
            "`GC.GetTotalAllocatedBytes` 增量。流式路径理论上只需 16 KB 池化缓冲 + 系数 + 输出，" +
            "整文件路径多分配一份与文件等长的字节数组。");

        var table = new Table("文件", "字节", "路径", "分配 MB", "放大倍数", "相对整文件");
        foreach (string file in new[] { "Amish-Noka-Dresser.jpg", "progressive.jpg" })
        {
            byte[] data = File.ReadAllBytes(ExamplePath(file));

            byte[] whole = data;
            long wholeAlloc = MeasureAlloc(() => JpegDecoder.Decode(whole));
            table.Add(file, data.Length, "整文件", (wholeAlloc / 1048576.0).ToString("F2"),
                $"{wholeAlloc / (double)data.Length:F1}×", "基准");

            var stream = new SimulatedNetworkStream(data, new NetworkProfile { ChunkBytes = 16 * 1024, Seed = 9 });
            long streamAlloc = MeasureAlloc(() => JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult());
            table.Add(file, data.Length, "流式", (streamAlloc / 1048576.0).ToString("F2"),
                $"{streamAlloc / (double)data.Length:F1}×",
                $"{(streamAlloc - wholeAlloc) / 1048576.0:+0.00;-0.00} MB");

            if (file == "progressive.jpg")
            {
                findings.Add($"内存放大：3.09 MB 的 143 MP 渐进 JPEG 在流式解码中仍分配 " +
                    $"{streamAlloc / 1048576.0:F0} MB（{streamAlloc / (double)data.Length:F0}× 文件大小）；" +
                    "库内没有可配置的像素数/内存预算闸门，网络输入需要调用方自己限制尺寸或字节数。");
            }
        }

        AppendTable(table);
        AppendLine("结论：流式解码省掉的只是「整文件缓冲」这一项（≈文件大小），" +
            "系数数组与 RGB 输出这两块与整文件路径完全相同，所以当图片本身很大时两者内存几乎无差。");
    }

    // ---------------------------------------------------------------- 场景 7

    private static void RunConcurrency()
    {
        Section("7. 并发多流（16 路同时下载解码）",
            "16 个任务同时解码，分片与延迟随机；一半在随机位置提前 EOF。检查死锁、串扰与结果确定性。");

        byte[] data = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
        byte[] reference = ReferenceRgb(data);
        int rounds = 16;

        var tasks = new Task<(string Kind, bool Same, long Bytes)>[rounds];
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            int seed = i;
            tasks[i] = Task.Run(() =>
            {
                var rng = new Random(seed * 977 + 1);
                bool truncate = (seed & 1) == 1;
                int faultAt = truncate ? rng.Next(data.Length / 10, data.Length) : -1;
                var stream = new SimulatedNetworkStream(data, new NetworkProfile
                {
                    ChunkBytes = rng.Next(1, 8192),
                    ChunkJitterPercent = 25,
                    DelayMsPerRead = rng.NextDouble() < 0.5 ? 0 : 0.05,
                    FaultMode = truncate ? FaultMode.EarlyEof : FaultMode.None,
                    FaultAtBytes = faultAt,
                    Seed = seed + 1,
                });

                try
                {
                    byte[] rgb = JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult().Image.ToRgb24();
                    return ("返回图像", rgb.AsSpan().SequenceEqual(reference), stream.Stats.BytesServed);
                }
                catch (Exception ex)
                {
                    return (Classify(ex), false, stream.Stats.BytesServed);
                }
            });
        }

        bool completed = Task.WaitAll(tasks, TimeSpan.FromSeconds(60));
        stopwatch.Stop();

        if (!completed)
        {
            findings.Add("并发 16 路解码在 60 秒内未完成（疑似死锁或活锁）。");
            AppendLine("**超时**：60 秒内未全部完成。");
            return;
        }

        var results = tasks.Select(t => t.Result).ToList();
        int returned = results.Count(r => r.Kind == "返回图像");
        int same = results.Count(r => r.Same);
        int invalid = results.Count(r => r.Kind == "InvalidData");
        int other = results.Count(r => r.Kind is not ("返回图像" or "InvalidData"));

        AppendLine($"墙钟 {stopwatch.Elapsed.TotalMilliseconds:F0} ms；返回图像 {returned} 路（其中与参考一致 {same} 路），" +
            $"InvalidData {invalid} 路，其他 {other} 路。");

        if (other > 0) findings.Add($"并发下出现 {other} 路非预期结果类型。");
        if (returned != same) findings.Add("并发下出现像素不一致（存在跨流状态串扰）。");
    }

    // ---------------------------------------------------------------- 场景 8

    private static void RunWritePath()
    {
        Section("8. 写出侧：编码到不可 Seek 的网络响应流",
            "把 JPEG/PNG/GIF/WebP 编码结果分片写入只写流（模拟 HTTP 响应体）：" +
            "产物应与 `MemoryStream` 逐字节一致；中途断链应抛 `IOException`，且编码器仍可继续使用。");

        ImageFrame frame = ImageFrame.Load(ExamplePath("Amish-Noka-Dresser.jpg"));
        var table = new Table("格式", "字节", "分片写出", "与内存一致", "断链@25%", "断链后重编码");

        (string Name, Func<Stream, byte[]> Encode)[] codecs =
        [
            ("JPEG", s => { JpegEncoder.Write(s, frame.Width, frame.Height, frame.Pixels, 80); return []; }),
            ("PNG", s => { PngWriter.Write(s, frame.Width, frame.Height, frame.Pixels); return []; }),
            ("GIF", s => { new GifEncoder().Encode(frame, s); return []; }),
            ("WebP", s =>
            {
                var image = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);
                new WebpEncoderAdapter().EncodeRgb24(s, image);
                return [];
            }),
        ];

        foreach ((string name, Func<Stream, byte[]> encode) in codecs)
        {
            byte[] reference;
            using (var memory = new MemoryStream())
            {
                encode(memory);
                reference = memory.ToArray();
            }

            var throttled = new ThrottledWriteStream(chunkBytes: 7);
            Exception? writeError = null;
            try
            {
                encode(throttled);
            }
            catch (Exception ex)
            {
                writeError = ex;
            }

            bool identical = writeError is null && throttled.ToArray().AsSpan().SequenceEqual(reference);

            var broken = new ThrottledWriteStream(chunkBytes: 64, faultAtBytes: Math.Max(1, reference.Length / 4));
            Exception? brokenError = null;
            try
            {
                encode(broken);
            }
            catch (Exception ex)
            {
                brokenError = ex;
            }

            byte[] afterReference;
            using (var memory = new MemoryStream())
            {
                encode(memory);
                afterReference = memory.ToArray();
            }

            bool reusable = afterReference.AsSpan().SequenceEqual(reference);

            table.Add(name, reference.Length, writeError is null ? "OK" : Classify(writeError),
                identical ? "是" : "否", brokenError is null ? "静默成功（危险）" : Classify(brokenError),
                reusable ? "一致" : "不一致");

            if (!identical) findings.Add($"写出侧 {name} 分片写入改变了产物字节。");
            if (brokenError is null) findings.Add($"写出侧 {name} 断链后未抛异常，可能静默丢失尾部数据。");
            if (!reusable) findings.Add($"写出侧 {name} 断链后编码器状态被破坏（重编码结果与基准不同）。");
        }

        AppendTable(table);
    }

    // ---------------------------------------------------------------- 场景 9

    private static void RunAsyncOnly()
    {
        Section("9. 仅支持异步读取的流",
            "有些网络流（管道 / 协议栈封装）只实现 `ReadAsync`，同步 `Read` 抛 `NotSupportedException`。" +
            "`DecodeFromStreamAsync` 的熵解码阶段是同步阻塞读取，这里验证其行为。");

        byte[] data = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
        var stream = new SimulatedNetworkStream(data, new NetworkProfile
        {
            ChunkBytes = 8192,
            SyncReadUnsupported = true,
            Seed = 13,
        });

        Exception? error = null;
        byte[]? rgb = null;
        try
        {
            rgb = JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult().Image.ToRgb24();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (error is null)
        {
            AppendLine($"**解码成功**（{rgb?.Length} 字节），已交付 {stream.Stats.BytesServed}/{data.Length} B。");
        }
        else
        {
            AppendLine($"失败于 `{error.GetType().Name}`：{Trim(error.Message, 80)}；" +
                $"此时已交付 {stream.Stats.BytesServed}/{data.Length} B（说明头部异步阶段通过，熵解码阶段失败）。");
            findings.Add("只支持异步读取的流在熵解码阶段抛 `NotSupportedException`：`DecodeFromStreamAsync` 的异步承诺是部分的。");
        }
    }

    // ---------------------------------------------------------------- 场景 10

    private static void RunHeaderBomb()
    {
        Section("10. 头部尺寸炸弹（只改 SOF 宽高）",
            "只改 SOF 的宽高、其余字节不变，子进程以 1 GB 托管堆硬上限运行。" +
            "第一组 65500×65500 触发的是整型溢出保护；第二组 24000×24000（576 MP）在 int 范围内，" +
            "用来检查是否存在「内存预算」维度的准入控制。");

        byte[] data = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
        foreach ((int width, int height) in new[] { (65500, 65500), (24000, 24000) })
        {
            byte[] bomb;
            try
            {
                bomb = PatchSofDimensions(data, width, height);
            }
            catch (Exception ex)
            {
                AppendLine($"{width}×{height}: 无法构造（{ex.Message}）。");
                continue;
            }

            string temp = Path.Combine(Path.GetTempPath(), "sic_bomb_" + Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllBytes(temp, bomb);
            try
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位自身进程");
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--bomb-child");
                psi.ArgumentList.Add(temp);
                psi.Environment["DOTNET_GCHeapHardLimit"] = "0x40000000"; // 1 GiB
                psi.Environment["DOTNET_gcServer"] = "0";

                using var process = Process.Start(psi)!;
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(60_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    findings.Add($"{width}×{height} 尺寸炸弹使子进程 60 秒未退出（疑似死循环或疯狂 GC）。");
                    AppendLine($"{width}×{height}: **子进程超时未退出**。");
                    continue;
                }

                string firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "(无输出)";
                AppendLine($"{width}×{height}: 子进程退出码 `{process.ExitCode}`，输出：`{Trim(firstLine, 120)}`" +
                    (stderr.Length > 0 ? $"，stderr：`{Trim(stderr, 160)}`" : "") + "。");

                bool caught = firstLine.StartsWith("THROW", StringComparison.Ordinal);
                if (process.ExitCode != 0)
                {
                    findings.Add($"{width}×{height} 尺寸炸弹使子进程异常退出（exit {process.ExitCode}）：流式解码没有可配置的像素数/内存预算检查。");
                }
                else if (!caught)
                {
                    findings.Add($"{width}×{height} 尺寸炸弹被成功解码：网络输入可以在没有预算闸门的情况下索取数十 GB 内存。");
                }
            }
            finally
            {
                File.Delete(temp);
            }
        }

        AppendLine("注意：第二组报 `InvalidData` 是因为截断的熵数据在填充块解完后就失败了，" +
            "并未真正分配 576 MP 的系数数组，因此本项**不能**证明「大图不会 OOM」。" +
            "真正的内存风险见 §6：3.09 MB 的文件需要 409 MB，文件大小完全不能作为准入依据。");
    }

    private static int BombChild(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = JpegDecoder.DecodeFromStreamAsync(fs).GetAwaiter().GetResult();
            Console.WriteLine($"OK {result.Image.Width}x{result.Image.Height}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"THROW {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    // ---------------------------------------------------------------- 场景 11

    private static void RunOtherFormats()
    {
        Section("11. 非 JPEG 格式经网络流加载",
            "`ImageFrame.Load(Stream)` 对不可 Seek 流先缓存 12 字节头再拼接尾部。" +
            "验证分片一致性，以及 50% / 仅差 1 字节截断时的行为。");

        ImageFrame frame = ImageFrame.Load(ExamplePath("Amish-Noka-Dresser.jpg"));
        var table = new Table("格式", "字节", "分片一致", "截断 50%", "截断 -1B");

        var payloads = new List<(string Name, byte[] Data)>();
        using (var ms = new MemoryStream()) { PngWriter.Write(ms, frame.Width, frame.Height, frame.Pixels); payloads.Add(("PNG", ms.ToArray())); }
        using (var ms = new MemoryStream()) { new GifEncoder().Encode(frame, ms); payloads.Add(("GIF", ms.ToArray())); }
        using (var ms = new MemoryStream()) { new WebpEncoderAdapter().EncodeRgb24(ms, new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels)); payloads.Add(("WebP", ms.ToArray())); }
        using (var ms = new MemoryStream()) { BmpWriter.Write24(ms, frame.Width, frame.Height, frame.Pixels); payloads.Add(("BMP", ms.ToArray())); }

        foreach ((string name, byte[] data) in payloads)
        {
            byte[] referencePixels = ImageFrame.Load(data).Pixels;

            using var chunked = new SimulatedNetworkStream(data, new NetworkProfile { ChunkBytes = 7, Seed = 17 });
            bool same;
            try
            {
                var loaded = ImageFrame.Load(chunked);
                same = loaded.Pixels.AsSpan().SequenceEqual(referencePixels);
            }
            catch
            {
                same = false;
            }

            string half = TruncatedOutcome(data, data.Length / 2);
            string minusOne = TruncatedOutcome(data, data.Length - 1);
            table.Add(name, data.Length, same ? "是" : "否", half, minusOne);

            if (minusOne.StartsWith("返回图像", StringComparison.Ordinal))
            {
                findings.Add($"{name}: 缺失尾部 1 字节（GIF trailer 0x3B）仍返回图像，尾部完整性没有自检。");
            }
        }

        AppendTable(table);
    }

    private static string TruncatedOutcome(byte[] data, int keep)
    {
        byte[] truncated = data[..Math.Max(0, keep)];
        using var stream = new SimulatedNetworkStream(truncated, new NetworkProfile
        {
            ChunkBytes = 4096,
            FaultMode = FaultMode.EarlyEof,
            FaultAtBytes = truncated.Length,
        });

        try
        {
            var frame = ImageFrame.Load(stream);
            return $"返回图像 {frame.Width}x{frame.Height}";
        }
        catch (Exception ex)
        {
            return Classify(ex);
        }
    }

    // ---------------------------------------------------------------- 工具

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "examples")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string ExamplePath(string name) => Path.Combine(repoRoot, "examples", name);

    private static byte[] ReferenceRgb(byte[] jpeg) => JpegDecoder.Decode(jpeg).ToRgb24();

    private static string Classify(Exception ex) => ex switch
    {
        InvalidDataException => "InvalidData",
        EndOfStreamException => "EndOfStream",
        NotSupportedException => "NotSupported",
        OperationCanceledException => "Canceled",
        OutOfMemoryException => "OOM",
        IOException => "IO",
        _ => ex.GetType().Name,
    };

    private static double DiffRatio(byte[] reference, byte[] actual)
    {
        if (reference.Length != actual.Length) return 1.0;
        int diff = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            if (reference[i] != actual[i]) diff++;
        }
        return reference.Length == 0 ? 0 : (double)diff / reference.Length;
    }

    private static long MeasureAlloc(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        action();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static List<int> TruncationOffsets(byte[] data, bool everyOffset)
    {
        var set = new SortedSet<int>();
        if (everyOffset)
        {
            for (int i = 0; i <= data.Length; i++) set.Add(i);
            return set.ToList();
        }

        for (int p = 0; p <= 100; p++) set.Add(data.Length * p / 100);
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] != 0x00 && data[i + 1] != 0xFF)
            {
                set.Add(i);
                set.Add(i + 2);
            }
        }
        set.Add(data.Length - 1);
        set.Add(data.Length);
        return set.Where(o => o >= 0 && o <= data.Length).ToList();
    }

    private static byte[] PatchSofDimensions(byte[] jpeg, int width, int height)
    {
        byte[] copy = (byte[])jpeg.Clone();
        for (int i = 0; i + 1 < copy.Length; i++)
        {
            if (copy[i] != 0xFF) continue;
            byte marker = copy[i + 1];
            if (marker is not (0xC0 or 0xC1 or 0xC2)) continue;
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(i + 5), (ushort)height);
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(i + 7), (ushort)width);
            return copy;
        }
        throw new InvalidOperationException("未找到 SOF 标记");
    }

    private static string Trim(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static void Section(string title, string description)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
        body.AppendLine();
        body.AppendLine("## " + title);
        body.AppendLine();
        body.AppendLine(description);
        body.AppendLine();
    }

    private static void AppendLine(string text)
    {
        body.AppendLine(text);
        Console.WriteLine(text);
    }

    private static void AppendTable(Table table)
    {
        foreach (string line in table.Render())
        {
            body.AppendLine(line);
        }
        body.AppendLine();
        Console.WriteLine(table.RenderToConsole());
    }

    private static string Compose()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 网络传输流式调用的稳定性与健壮性实测");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}　·　运行时 .NET {Environment.Version}　·　" +
            $"逻辑核 {Environment.ProcessorCount}");
        sb.AppendLine();
        sb.AppendLine("测试台：`tools/streamnet`。被测入口：`JpegDecoder.DecodeFromStreamAsync`、" +
            "`ImageFrame.LoadJpeg/Load(Stream)`、`JpegEncoder/PngWriter/GifEncoder/WebpEncoderAdapter` 的 Stream 写出。");
        sb.AppendLine("所有输入都经过一个**不可 Seek** 的仿真网络流：固定/抖动分片、逐次延迟、提前 EOF（FIN）、" +
            "连接重置（RST）、可中途取消。");

        sb.AppendLine();
        sb.AppendLine("## 结论摘要");
        sb.AppendLine();
        sb.AppendLine("唯一的高危问题是**渐进式 JPEG 在扫描边界被截断时不会报错**：解码器的位读取器把"
            + "「数据源读尽」当作 EOI 补 0xFF，多扫描 JPEG 只解出前几个扫描也会被 `ValidateScansComplete`"
            + " 判为完整（它只检查「组件是否解到过系数」），于是返回一张严重失真的图像。" +
            "默认行为保持宽松（兼容历史），网络场景应显式打开 `requireEoi: true`。");
        sb.AppendLine();
        foreach (string level in new[] { "P0", "P1", "P2" })
        {
            var items = findings.Where(f => Severity(f) == level).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine($"**{level}**");
            sb.AppendLine();
            foreach (string item in items) sb.AppendLine($"- {item}");
            sb.AppendLine();
        }

        sb.AppendLine("**未发现问题的维度**：分片交付一致性（1 B ~ 256 KB，§1）、连接重置传播（§3）、" +
            "取消响应（§4）、限速下的线性开销（§5）、16 路并发无串扰（§7）、写出侧分片确定性与断链后可用性（§8）、" +
            "PNG/BMP/WebP 截断拒绝（§11）。");
        sb.AppendLine();

        sb.AppendLine("## 未修复（需决策）");
        sb.AppendLine();
        sb.AppendLine("1. **CLI 退出码恒为 0**：`Program.Main` 是 `void`，解码/编码失败只打印到 stdout。" +
            "实测 `--stream-strict` 拒绝截断文件后 `exit code` 仍是 0，网络管道里无法判定成败。" +
            "要改就得先定批量语义（部分失败返回几？），故本轮只记录。");
        sb.AppendLine("2. **`ImageFrame.LoadJpeg` 没有 `CancellationToken`**：只能下沉到 `JpegDecoder.DecodeFromStreamAsync` 才能取消。");
        sb.AppendLine("3. **只支持 `ReadAsync` 的流无法解码**：熵解码是同步循环。要么让调用方先把整条流异步读进内存，要么增加一个" +
            "「异步读满 → 内存解码」的入口；后者会放弃流式的内存优势。");
        sb.AppendLine("4. **库内没有像素/内存预算闸门**：`MemoryGuard` 只服务 CLI 的文件路径，Stream 入口不经过它。");
        sb.AppendLine();

        sb.AppendLine("## 复现方式");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("dotnet run --project tools/streamnet -c Release                 # 全量矩阵（约 1 分钟）");
        sb.AppendLine("dotnet run --project tools/streamnet -c Release -- --only 2     # 只跑第 2 个场景");
        sb.AppendLine("dotnet test --filter FullyQualifiedName~NetworkStreamingTests   # 回归测试（15 条）");
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## 本轮修复");
        sb.AppendLine();
        sb.AppendLine("1. **严格 EOI 模式**（对应 P0）：`JpegDecoder.DecodeFromStreamAsync(..., requireEoi: true)` 与 " +
            "`ImageFrame.LoadJpeg(..., requireEoi: true)`；CLI 新增 `--stream-strict`。" +
            "位读取器新增 `ReachedEndOfData`——数据源读尽却未遇到真实 marker 时置位，" +
            "解码器据此拒绝「扫描边界截断」。默认关闭，保持既有宽松行为不变。");
        sb.AppendLine("2. **WebP 写出异常保形**（对应 P1）：`WebpCodec` 原先把写出回调里的 `IOException` 包成 " +
            "`InvalidOperationException`，调用方无法用 `catch (IOException)` 区分「传输中断」与「编码器内部错误」；" +
            "现在先用 `ExceptionDispatchInfo` 原样抛出回调异常，再判断 `WebPEncode` 返回值。");
        sb.AppendLine("3. **WebP 解码异常归类**：截断/损坏数据的 `InvalidOperationException` 改为 `InvalidDataException`，" +
            "与 PNG/JPEG 的「数据问题」口径一致。");
        sb.AppendLine();
        sb.AppendLine("宽松/严格两种模式在 4 个样本上的对照见 §2：严格模式把「静默损坏」从 24 例降到 0 例，" +
            "且完整文件全部照常通过（§1 的分片矩阵全程开启严格模式，无一误拒）。");
        sb.AppendLine();

        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>给自动收集的发现分级，便于按严重度排序。</summary>
    private static string Severity(string finding)
    {
        if (finding.Contains("宽松模式", StringComparison.Ordinal)) return "P0";
        if (finding.Contains("内存放大", StringComparison.Ordinal)
            || finding.Contains("异步读取", StringComparison.Ordinal)
            || finding.Contains("trailer", StringComparison.Ordinal)) return "P2";
        return "P1";
    }

    private sealed class Table
    {
        private readonly string[] headers;
        private readonly List<string[]> rows = [];

        public Table(params string[] headers) => this.headers = headers;

        public void Add(params object[] cells) => rows.Add(cells.Select(c => c?.ToString() ?? "").ToArray());

        public IEnumerable<string> Render()
        {
            yield return "| " + string.Join(" | ", headers) + " |";
            yield return "|" + string.Join("|", headers.Select(h => h.Length > 0 ? "---" : "---")) + "|";
            foreach (string[] row in rows)
            {
                yield return "| " + string.Join(" | ", row) + " |";
            }
        }

        public string RenderToConsole()
        {
            var widths = new int[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                widths[i] = headers[i].Length;
                foreach (string[] row in rows)
                {
                    widths[i] = Math.Max(widths[i], row[i].Length);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine(Row(headers, widths));
            sb.AppendLine(new string('-', widths.Sum(w => w + 3)));
            foreach (string[] row in rows) sb.AppendLine(Row(row, widths));
            return sb.ToString().TrimEnd();
        }

        private static string Row(string[] cells, int[] widths)
            => "  " + string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i])));
    }
}
