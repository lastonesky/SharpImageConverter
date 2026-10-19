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
/// 全格式流式矩阵：把 §1~§8 的网络条件对 PNG / GIF / BMP / WebP / JPEG 各跑一遍。
/// JPEG 走 <see cref="JpegDecoder.DecodeFromStreamAsync"/>（真正的边读边解），
/// 其余格式走 <see cref="ImageFrame.Load(Stream)">ImageFrame.Load(Stream)</see>。
/// </summary>
internal static partial class Program
{
    private sealed record FormatProbe(
        string Name,
        byte[] Payload,
        byte[] Reference,
        Func<Stream, byte[]> Decode,
        string PathKind,
        Func<Stream, byte[]>? DecodeStrict);

    private static List<FormatProbe> BuildProbes()
    {
        ImageFrame frame = ImageFrame.Load(ExamplePath("Amish-Noka-Dresser.jpg"));
        var image = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);

        static byte[] Jpeg(Stream s, bool strict)
            => JpegDecoder.DecodeFromStreamAsync(s, default, false, requireEoi: strict)
                .GetAwaiter().GetResult().Image.ToRgb24();

        var payloads = new List<(string Name, byte[] Data, Func<Stream, byte[]> Decode, string PathKind, Func<Stream, byte[]>? Strict)>
        {
            ("JPEG", File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg")),
                s => Jpeg(s, strict: false), "真流式", s => Jpeg(s, strict: true)),
            ("PNG", EncodeToArray(s => PngWriter.Write(s, frame.Width, frame.Height, frame.Pixels)),
                s => new PngDecoder().DecodeToRGB(s), "真流式", null),
            ("GIF", EncodeToArray(s => new GifEncoder().Encode(frame, s)),
                s => new GifDecoder().DecodeRgb24(s).Buffer, "真流式",
                s => new GifDecoder().DecodeRgb24(s, requireComplete: true).Buffer),
            ("BMP", EncodeToArray(s => BmpWriter.Write24(s, frame.Width, frame.Height, frame.Pixels)),
                s => BmpReader.Read(s, out _, out _, out _, out _), "真流式", null),
            ("WebP", EncodeToArray(s => new WebpEncoderAdapter().EncodeRgb24(s, image)),
                s => new WebpDecoderAdapter().DecodeRgb24(s).Buffer, "整读缓冲", null),
        };

        var probes = new List<FormatProbe>();
        foreach ((string name, byte[] data, Func<Stream, byte[]> decode, string pathKind, Func<Stream, byte[]>? strict) in payloads)
        {
            byte[] reference = ImageFrame.Load(data).Pixels;
            probes.Add(new FormatProbe(name, data, reference, decode, pathKind, strict));
        }

        return probes;
    }

    private static byte[] EncodeToArray(Action<Stream> encode)
    {
        using var ms = new MemoryStream();
        encode(ms);
        return ms.ToArray();
    }

    private static void RunAllFormats()
    {
        Section("11. 全格式流式矩阵（PNG / GIF / BMP / WebP / JPEG 对照）",
            "`ImageFrame.Load(Stream)` 对不可 Seek 流只缓存 12 字节头，随后交给各格式自己的解码器。" +
            "这里把 §1~§8 的网络条件对五种格式各跑一遍：分片一致性、截断扫描、连接重置/取消/限速、内存、" +
            "并发、写出侧。样本统一由同一张 1000×1000 图重新编码，参照像素取整文件解码结果。");

        List<FormatProbe> probes = BuildProbes();

        var profile = new Table("格式", "字节", "解码路径", "严格开关", "像素长度");
        foreach (FormatProbe probe in probes)
        {
            profile.Add(probe.Name, probe.Payload.Length, probe.PathKind,
                probe.DecodeStrict is null ? "无（始终拒绝截断）" : "requireComplete", probe.Reference.Length);
        }

        AppendTable(profile);

        RunChunkMatrix(probes);
        RunTruncationSweep(probes);
        RunFaultMatrix(probes);
        RunCostMatrix(probes);
        RunWriteMatrix(probes);
    }

    // ---------------------------------------------------------------- 分片

    private static void RunChunkMatrix(List<FormatProbe> probes)
    {
        SubSection("11.1 分片交付一致性",
            "分片大小 1 B ~ 256 KB。不可 Seek 流会把「一次 Read 只给 1 字节」放大成几十万次调用，" +
            "用于暴露依赖单次读满的假设。");

        int[] chunks = [1, 7, 64, 1024, 16 * 1024, 256 * 1024];
        var table = new Table(["格式", .. chunks.Select(c => FormatBytes(c)), "全部一致"]);

        foreach (FormatProbe probe in probes)
        {
            var cells = new List<object> { probe.Name };
            bool allSame = true;
            foreach (int chunk in chunks)
            {
                string outcome;
                try
                {
                    using var stream = new SimulatedNetworkStream(probe.Payload,
                        new NetworkProfile { ChunkBytes = chunk, ChunkJitterPercent = chunk > 64 ? 20 : 0, Seed = 21 });
                    byte[] pixels = probe.Decode(stream);
                    outcome = pixels.AsSpan().SequenceEqual(probe.Reference) ? "一致" : "像素不同";
                    if (outcome != "一致") allSame = false;
                }
                catch (Exception ex)
                {
                    outcome = Classify(ex);
                    allSame = false;
                }

                cells.Add(outcome);
            }

            cells.Add(allSame ? "是" : "否");
            table.Add(cells.ToArray());
            if (!allSame) findings.Add($"{probe.Name}: 分片大小改变了解码结果（说明解码器对单次 Read 的交付量有隐含假设）。");
        }

        AppendTable(table);
    }

    // ---------------------------------------------------------------- 截断

    private static void RunTruncationSweep(List<FormatProbe> probes)
    {
        SubSection("11.2 截断扫描（FIN，取整文件比例 + 各格式结构边界，宽松 vs 严格）",
            "每个格式在 50~70 个位置截断（含 chunk / block / row 边界，最敏感的位置就在这里）。" +
            "结果分三类：报错、接受且像素与完整文件一致（缺的只是尾部元数据）、" +
            "**接受但像素不同**（静默损坏）。JPEG 与 GIF 各跑宽松/严格两遍，其余格式的入口本身就会拒绝截断。");

        var table = new Table("格式", "字节", "用例", "宽松:抛", "宽松:接受一致", "宽松:静默损坏",
            "严格:静默损坏", "严格:抛", "最深静默截断", "最坏差异", "异常类型");
        var detail = new Table("格式", "静默损坏样例（宽松模式）");

        foreach (FormatProbe probe in probes)
        {
            List<int> offsets = StructuralOffsets(probe).Where(o => o > 0 && o < probe.Payload.Length)
                .Distinct().OrderBy(o => o).ToList();
            offsets = Thin(offsets, 70);

            TruncSweep lenient = SweepOffsets(probe, offsets, probe.Decode);
            TruncSweep? strict = probe.DecodeStrict is null ? null : SweepOffsets(probe, offsets, probe.DecodeStrict);

            table.Add(probe.Name, probe.Payload.Length, offsets.Count,
                lenient.Threw, lenient.Same, lenient.Corrupt,
                strict is null ? "—（入口本身拒绝）" : strict.Corrupt.ToString(),
                strict is null ? "—" : strict.Threw.ToString(),
                lenient.Deepest == 0 ? "—" : $"-{lenient.Deepest} B（{lenient.Deepest * 100.0 / probe.Payload.Length:F0}%）",
                lenient.Worst == 0 ? "—" : lenient.Worst.ToString("P2"),
                lenient.Kinds.Count == 0 ? "—" : string.Join(" ", lenient.Kinds.Select(kv => $"{kv.Key}×{kv.Value}")));

            detail.Add(probe.Name, lenient.Points.Count == 0 ? "无" : string.Join("；", lenient.Points));

            if (lenient.Corrupt > 0)
            {
                findings.Add($"{probe.Name}: 截断后有 {lenient.Corrupt}/{offsets.Count} 例**静默返回像素不同的图像**" +
                    $"（最坏差异 {lenient.Worst:P1}，最深截掉 {lenient.Deepest * 100.0 / probe.Payload.Length:F0}% 字节）。" +
                    (strict is null
                        ? "该格式没有严格开关。"
                        : $"严格模式（requireComplete）下静默损坏 {strict.Corrupt} 例。"));
            }
        }

        AppendTable(table);
        AppendLine("静默损坏样例：");
        AppendLine("");
        foreach (string line in detail.Render()) body.AppendLine(line);
        body.AppendLine();
        AppendLine("说明：`接受一致` 表示数据虽被截断，但缺失的只是尾部元数据（PNG 的 IEND、GIF 的 trailer），" +
            "像素仍然完整——不构成画质风险，只是「无法区分完整传输与被截断的传输」。");
    }

    private sealed record TruncSweep(int Threw, int Same, int Corrupt, int Deepest, double Worst,
        SortedDictionary<string, int> Kinds, List<string> Points);

    private static TruncSweep SweepOffsets(FormatProbe probe, List<int> offsets, Func<Stream, byte[]> decode)
    {
        int threw = 0, same = 0, corrupt = 0, deepest = 0;
        double worst = 0;
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var points = new List<string>();

        foreach (int offset in offsets)
        {
            byte[] truncated = probe.Payload[..offset];
            try
            {
                using var stream = new SimulatedNetworkStream(truncated,
                    new NetworkProfile { ChunkBytes = 4096, FaultMode = FaultMode.EarlyEof, FaultAtBytes = truncated.Length, Seed = 3 });
                byte[] pixels = decode(stream);
                double diff = DiffRatio(probe.Reference, pixels);
                if (diff <= 1e-6)
                {
                    same++;
                }
                else
                {
                    corrupt++;
                    deepest = Math.Max(deepest, probe.Payload.Length - offset);
                    worst = Math.Max(worst, diff);
                    if (points.Count < 6) points.Add($"-{probe.Payload.Length - offset}B → {diff:P1}");
                }
            }
            catch (Exception ex)
            {
                threw++;
                string kind = Classify(ex);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            }
        }

        return new TruncSweep(threw, same, corrupt, deepest, worst, kinds, points);
    }

    private static IEnumerable<int> StructuralOffsets(FormatProbe probe)
    {
        byte[] d = probe.Payload;
        var set = new SortedSet<int>();
        for (int p = 0; p <= 100; p++) set.Add(d.Length * p / 100);

        switch (probe.Name)
        {
            case "JPEG":
                for (int i = 0; i + 1 < d.Length; i++)
                {
                    if (d[i] != 0xFF || d[i + 1] == 0x00 || d[i + 1] == 0xFF) continue;
                    set.Add(i);
                    set.Add(i + 2);
                    if (d[i + 1] == 0xDA)
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(i + 2));
                        set.Add(i + 2 + len);
                    }
                }
                break;

            case "PNG":
            {
                int p = 8;
                while (p + 12 <= d.Length)
                {
                    int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(p));
                    if (len < 0 || p + 12 + len > d.Length) { set.Add(p); set.Add(d.Length); break; }
                    set.Add(p);
                    set.Add(p + 8 + len);            // 数据结束（CRC 前）
                    set.Add(p + 12 + len);           // CRC 结束
                    p += 12 + len;
                }
                break;
            }

            case "GIF":
            {
                int flags = d[10];
                int p = 13;
                if ((flags & 0x80) != 0) p += 3 * (1 << ((flags & 7) + 1));
                while (p < d.Length)
                {
                    byte block = d[p];
                    set.Add(p);
                    if (block == 0x21)
                    {
                        p += 2;
                        while (p < d.Length && d[p] != 0) { set.Add(p + 1 + d[p]); p += 1 + d[p]; }
                        p++;
                    }
                    else if (block == 0x2C)
                    {
                        if (p + 10 > d.Length) break;
                        int lct = d[p + 9];
                        p += 10;
                        if ((lct & 0x80) != 0) p += 3 * (1 << ((lct & 7) + 1));
                        p++; // LZW minimum code size
                        while (p < d.Length && d[p] != 0) { set.Add(p + 1 + d[p]); p += 1 + d[p]; }
                        p++;
                    }
                    else
                    {
                        break; // 0x3B trailer 或损坏
                    }
                }
                break;
            }

            case "BMP":
            {
                int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(10));
                int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
                int height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22)));
                int rowSize = ((width * 3 + 3) / 4) * 4;
                set.Add(dataOffset);
                foreach (int row in new[] { 1, 2, height / 2, height - 1 })
                {
                    if (row > 0 && row < height) set.Add(dataOffset + row * rowSize);
                }
                break;
            }

            case "WebP":
            {
                int p = 12;
                while (p + 8 <= d.Length)
                {
                    int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
                    if (size < 0 || p + 8 + size > d.Length) { set.Add(p); set.Add(d.Length); break; }
                    set.Add(p);
                    set.Add(p + 8 + size);
                    p += 8 + size + (size & 1);
                }
                break;
            }
        }

        return set;
    }

    private static List<int> Thin(List<int> offsets, int max)
    {
        if (offsets.Count <= max) return offsets;
        var result = new List<int>();
        double step = offsets.Count / (double)max;
        for (int i = 0; i < offsets.Count; i += Math.Max(1, (int)Math.Round(step)))
        {
            result.Add(offsets[i]);
        }

        if (result[^1] != offsets[^1]) result.Add(offsets[^1]);
        return result;
    }

    // ---------------------------------------------------------------- 故障

    private static void RunFaultMatrix(List<FormatProbe> probes)
    {
        SubSection("11.3 连接重置 / 中途取消 / 限速",
            "RST 在 10%、50%、90% 字节处注入；取消由流自身在 50% 处抛 `OperationCanceledException`" +
            "（非 JPEG 的入口没有 `CancellationToken`，HTTP 客户端断开时正是这种形态）；限速用 4 KB / 2 ms。" +
            "关注点：传输错误能否以可捕获的异常原样到达调用方，而不是被吞掉或改写成别的类型。");

        var table = new Table("格式", "RST@10%", "RST@50%", "RST@90%", "取消@50%", "限速墙钟/注入");

        foreach (FormatProbe probe in probes)
        {
            string[] reset = new string[3];
            double[] fractions = [0.10, 0.50, 0.90];
            for (int i = 0; i < fractions.Length; i++)
            {
                long at = (long)(probe.Payload.Length * fractions[i]);
                try
                {
                    using var stream = new SimulatedNetworkStream(probe.Payload,
                        new NetworkProfile { ChunkBytes = 4096, FaultMode = FaultMode.Reset, FaultAtBytes = at, Seed = 5 });
                    probe.Decode(stream);
                    reset[i] = "静默成功";
                }
                catch (Exception ex)
                {
                    reset[i] = Classify(ex);
                }
            }

            string cancel;
            {
                long half = probe.Payload.Length / 2;
                var profile = new NetworkProfile
                {
                    ChunkBytes = 4096,
                    Seed = 6,
                    Progress = served =>
                    {
                        if (served >= half) throw new OperationCanceledException("客户端取消");
                    },
                };
                try
                {
                    using var stream = new SimulatedNetworkStream(probe.Payload, profile);
                    probe.Decode(stream);
                    cancel = "未生效";
                }
                catch (Exception ex)
                {
                    cancel = Classify(ex);
                }

                if (cancel == "未生效") findings.Add($"{probe.Name}: 传输中途取消没有生效，解码继续跑完。");
            }

            string throttle;
            {
                var profile = new NetworkProfile { ChunkBytes = 4096, DelayMsPerRead = 2.0, Seed = 7 };
                using var stream = new SimulatedNetworkStream(probe.Payload, profile);
                var sw = Stopwatch.StartNew();
                try
                {
                    probe.Decode(stream);
                }
                catch
                {
                    // 限速本身不应引发异常；异常在下面通过读数体现
                }

                sw.Stop();
                throttle = $"{sw.ElapsedMilliseconds} / {stream.Stats.InjectedDelayMs:F0} ms（{stream.Stats.ReadCalls} 次读）";
            }

            table.Add(probe.Name, reset[0], reset[1], reset[2], cancel, throttle);
        }

        AppendTable(table);
    }

    // ---------------------------------------------------------------- 成本

    private static void RunCostMatrix(List<FormatProbe> probes)
    {
        SubSection("11.4 内存与并发",
            "内存：`ImageFrame.Load(byte[])`（整文件在内存）对照 `Load(Stream)`（不可 Seek 网络流）。" +
            "并发：5 种格式各 3 路同时从随机分片的网络流解码，像素必须各自与参照一致。");

        var table = new Table("格式", "文件字节", "整文件分配 MB", "流式分配 MB", "流式/整文件", "流式放大倍数");

        foreach (FormatProbe probe in probes)
        {
            long whole = MeasureAlloc(() => ImageFrame.Load(probe.Payload));
            long streamed = MeasureAlloc(() =>
            {
                using var stream = new SimulatedNetworkStream(probe.Payload, new NetworkProfile { ChunkBytes = 16 * 1024, Seed = 9 });
                probe.Decode(stream);
            });

            table.Add(probe.Name, probe.Payload.Length,
                (whole / 1048576.0).ToString("F2"),
                (streamed / 1048576.0).ToString("F2"),
                $"{streamed / (double)Math.Max(1, whole):F2}×",
                $"{streamed / (double)probe.Payload.Length:F1}×");

            if (probe.Name == "BMP" && streamed > whole * 1.05)
            {
                findings.Add($"BMP: 流式解码比整文件多分配 {(streamed - whole) / 1048576.0:F1} MB，" +
                    "`BmpReader` 逐行读取但仍整帧缓存输出。");
            }
        }

        AppendTable(table);

        var results = new string[probes.Count * 3];
        var tasks = new List<Task>();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < probes.Count; i++)
        {
            for (int copy = 0; copy < 3; copy++)
            {
                int index = i;
                int slot = i * 3 + copy;
                int seed = 100 + slot;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        using var stream = new SimulatedNetworkStream(probes[index].Payload,
                            new NetworkProfile { ChunkBytes = 1024, ChunkJitterPercent = 40, Seed = seed });
                        byte[] pixels = probes[index].Decode(stream);
                        results[slot] = pixels.AsSpan().SequenceEqual(probes[index].Reference) ? "一致" : "像素不同";
                    }
                    catch (Exception ex)
                    {
                        results[slot] = Classify(ex);
                    }
                }));
            }
        }

        Task.WaitAll(tasks.ToArray());
        sw.Stop();

        var concurrent = new Table("格式", "路 1", "路 2", "路 3");
        int bad = 0;
        for (int i = 0; i < probes.Count; i++)
        {
            concurrent.Add(probes[i].Name, results[i * 3], results[i * 3 + 1], results[i * 3 + 2]);
            for (int copy = 0; copy < 3; copy++)
            {
                if (results[i * 3 + copy] != "一致") bad++;
            }
        }

        AppendTable(concurrent);
        AppendLine($"15 路并发（5 格式 × 3 副本，随机分片）总耗时 {sw.ElapsedMilliseconds} ms，" +
            $"异常/错误 {bad} 路。");
        if (bad > 0) findings.Add($"全格式并发下有 {bad} 路结果不一致（跨格式共享状态或缓冲复用出错）。");
    }

    // ---------------------------------------------------------------- 写出

    private static void RunWriteMatrix(List<FormatProbe> probes)
    {
        SubSection("11.5 写出侧（分片写 + 断链）",
            "五种格式各编码到只写流：7 字节分片写入必须与 `MemoryStream` 逐字节一致；" +
            "写入到 25% 处断链必须抛 `IOException`，且编码器之后仍能产出同样字节。");

        ImageFrame frame = ImageFrame.Load(ExamplePath("Amish-Noka-Dresser.jpg"));
        var image = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);

        var table = new Table("格式", "字节", "7 B 分片", "与内存一致", "断链@25%", "断链后重编码");

        (string Name, Action<Stream> Encode)[] codecs =
        [
            ("JPEG", s => JpegEncoder.Write(s, frame.Width, frame.Height, frame.Pixels, 80)),
            ("PNG", s => PngWriter.Write(s, frame.Width, frame.Height, frame.Pixels)),
            ("GIF", s => new GifEncoder().Encode(frame, s)),
            ("BMP", s => BmpWriter.Write24(s, frame.Width, frame.Height, frame.Pixels)),
            ("WebP", s => new WebpEncoderAdapter().EncodeRgb24(s, image)),
        ];

        foreach ((string name, Action<Stream> encode) in codecs)
        {
            byte[] reference = EncodeToArray(encode);

            var throttled = new ThrottledWriteStream(chunkBytes: 7);
            string chunkedOutcome;
            try
            {
                encode(throttled);
                chunkedOutcome = "OK";
            }
            catch (Exception ex)
            {
                chunkedOutcome = Classify(ex);
            }

            bool identical = chunkedOutcome == "OK" && throttled.ToArray().AsSpan().SequenceEqual(reference);

            var broken = new ThrottledWriteStream(chunkBytes: 64, faultAtBytes: Math.Max(1, reference.Length / 4));
            string brokenOutcome;
            try
            {
                encode(broken);
                brokenOutcome = "静默成功";
            }
            catch (Exception ex)
            {
                brokenOutcome = Classify(ex);
            }

            byte[] after = EncodeToArray(encode);
            bool reusable = after.AsSpan().SequenceEqual(reference);

            table.Add(name, reference.Length, chunkedOutcome, identical ? "是" : "否", brokenOutcome,
                reusable ? "一致" : "不一致");

            if (!identical) findings.Add($"写出侧 {name}: 7 字节分片写入改变了产物字节。");
            if (brokenOutcome == "静默成功") findings.Add($"写出侧 {name}: 断链后没有抛异常，尾部数据被静默丢弃。");
            if (!reusable) findings.Add($"写出侧 {name}: 断链后编码器状态被破坏（重编码字节与基准不同）。");
        }

        AppendTable(table);
    }

    /// <summary>
    /// 诊断：同一进程内重复编码同一帧，检查编码器是否确定性。
    /// 用法：`dotnet run --project tools/streamnet -- --gif-repeat 3`
    /// </summary>
    private static int EncodeRepeatChild(int times)
    {
        ImageFrame frame = ImageFrame.Load(ExamplePath("Amish-Noka-Dresser.jpg"));
        var hashes = new List<string>();
        byte[]? first = null;
        for (int i = 0; i < times; i++)
        {
            byte[] bytes = EncodeToArray(s => new GifEncoder().Encode(frame, s));
            hashes.Add($"{i}: {bytes.Length} B {Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes))[..12]}");
            if (first is null)
            {
                first = bytes;
            }
            else if (!first.AsSpan().SequenceEqual(bytes))
            {
                int at = first.AsSpan().CommonPrefixLength(bytes);
                hashes.Add($"   ↳ 与第 0 次不同，首个差异偏移 {at}");
            }
        }

        foreach (string line in hashes) Console.WriteLine(line);
        return 0;
    }

    private static void SubSection(string title, string description)
    {
        body.AppendLine();
        body.AppendLine("### " + title);
        body.AppendLine();
        body.AppendLine(description);
        body.AppendLine();
        Console.WriteLine();
        Console.WriteLine("-- " + title);
    }

    private static string FormatBytes(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024 * 1024)} MB",
    };
}
