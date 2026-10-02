using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace SicBench;

internal sealed class Options
{
    public string Mode = "bench";          // gen | bench
    public string Corpus = "";             // corpus directory
    public string Examples = "";           // examples directory (gen only)
    public string Out = "";                // csv output path (bench only)
    public string Tag = "";                // logical name of the build under test
    public string Filter = "";             // only run tests whose name contains this
    public double IterScale = 1.0;
    public bool IncludeHugeEncode = true;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        var opt = ParseArgs(args);

        if (opt.Mode == "gen")
        {
            Corpus.Generate(opt.Examples, opt.Corpus);
            return 0;
        }
        if (opt.Mode == "probe")
        {
            Probe(opt);
            return 0;
        }
        if (opt.Mode == "encodetest")
        {
            EncodeTest(opt);
            return 0;
        }
        if (opt.Mode == "rawdiff")
        {
            RawDiff(opt);
            return 0;
        }

        PrintEnvironment(opt);
        var results = Bench.Run(opt);
        WriteCsv(opt.Out, results);
        return 0;
    }

    private static Options ParseArgs(string[] args)
    {
        var opt = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => (i + 1 < args.Length) ? args[++i] : "";
            switch (a)
            {
                case "--mode": opt.Mode = Next(); break;
                case "--corpus": opt.Corpus = Next(); break;
                case "--examples": opt.Examples = Next(); break;
                case "--out": opt.Out = Next(); break;
                case "--tag": opt.Tag = Next(); break;
                case "--filter": opt.Filter = Next(); break;
                case "--iter-scale": opt.IterScale = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-huge-encode": opt.IncludeHugeEncode = false; break;
            }
        }
        if (string.IsNullOrEmpty(opt.Corpus))
            opt.Corpus = Path.Combine(LocateRepoRoot(), ".perf", "corpus");
        if (string.IsNullOrEmpty(opt.Examples))
            opt.Examples = Path.Combine(LocateRepoRoot(), "examples");
        return opt;
    }

    private static string LocateRepoRoot()
    {
        // tools/perf/SicBench  ->  repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (dir.GetFiles("SharpImageConvert.slnx").Length > 0 ||
                dir.GetFiles("SharpImageConvert.sln").Length > 0 ||
                (dir.GetDirectories("src").Length > 0 && dir.GetDirectories("examples").Length > 0))
                return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Decode the huge corpus jpeg, report pixel statistics and dump the raw RGB
    /// so both builds can be handed the exact same pixels.
    /// </summary>
    private static void Probe(Options opt)
    {
        string src = Path.Combine(opt.Corpus, "huge.jpg");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = SharpImageConverter.ImageFrame.LoadJpeg(src);
        sw.Stop();
        byte[] p = f.Pixels;

        long sum = 0, grad = 0;
        int equalNeighbours = 0;
        var hist = new long[256];
        for (int i = 0; i < p.Length; i++) { sum += p[i]; hist[p[i]]++; }
        for (int i = 1; i < p.Length; i++)
        {
            int d = p[i] - p[i - 1];
            grad += Math.Abs(d);
            if (d == 0) equalNeighbours++;
        }
        double entropy = 0;
        foreach (long c in hist) { if (c > 0) { double q = c / (double)p.Length; entropy -= q * Math.Log2(q); } }

        Console.WriteLine($"tag              : {opt.Tag}");
        Console.WriteLine($"size             : {f.Width}x{f.Height}, pixels.Length={p.Length} (w*h*3={f.Width * f.Height * 3})");
        Console.WriteLine($"decode           : {sw.Elapsed.TotalMilliseconds:F1} ms");
        Console.WriteLine($"sha256           : {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p))}");
        Console.WriteLine($"mean             : {sum / (double)p.Length:F3}");
        Console.WriteLine($"mean |gradient|  : {grad / (double)p.Length:F4}");
        Console.WriteLine($"equal-neighbours : {equalNeighbours / (double)p.Length:P2}");
        Console.WriteLine($"byte entropy     : {entropy:F4} bits");

        string dir = Path.GetDirectoryName(Path.GetFullPath(opt.Out))!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(opt.Out, p);
        File.WriteAllText(Path.ChangeExtension(opt.Out, ".dims.txt"), $"{f.Width} {f.Height}");
        Console.WriteLine($"raw dumped       : {opt.Out}");
    }

    /// <summary>
    /// Encode a dumped raw RGB file (same bytes for both builds) to WebP, N times.
    /// </summary>
    private static void EncodeTest(Options opt)
    {
        var dims = File.ReadAllText(Path.ChangeExtension(opt.Out, ".dims.txt")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int w = int.Parse(dims[0]), h = int.Parse(dims[1]);
        byte[] rgb = File.ReadAllBytes(opt.Out);
        Console.WriteLine($"tag={opt.Tag} raw={opt.Out} {w}x{h} bytes={rgb.Length}");

        int n = (int)Math.Max(1, Math.Round(3 * opt.IterScale));
        var ms = new MemoryStream(1 << 26);
        for (int i = 0; i < n; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ms.SetLength(0);
            new SharpImageConverter.Formats.Webp.WebpEncoderAdapter()
                .EncodeRgb24(ms, new SharpImageConverter.Core.Image<SharpImageConverter.Core.Rgb24>(w, h, rgb));
            sw.Stop();
            Console.WriteLine($"  webp encode #{i + 1}: {sw.Elapsed.TotalMilliseconds:F1} ms  -> {ms.Length} bytes");
        }
    }

    /// <summary>Compare two dumped raw RGB files byte by byte (opt.Out = a;b).</summary>
    private static void RawDiff(Options opt)
    {
        var parts = opt.Out.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var a = File.ReadAllBytes(parts[0]);
        var b = File.ReadAllBytes(parts[1]);
        Console.WriteLine($"a={parts[0]} ({a.Length})  b={parts[1]} ({b.Length})");
        if (a.Length != b.Length) { Console.WriteLine("length mismatch"); return; }

        long sum = 0, diffCount = 0, max = 0;
        var chSum = new long[3];
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            chSum[i % 3] += d;
            if (d != 0) diffCount++;
            if (d > max) max = d;
        }
        Console.WriteLine($"mean |diff|      : {sum / (double)a.Length:F4}");
        Console.WriteLine($"per-channel      : R={chSum[0] / (double)(a.Length / 3):F4} G={chSum[1] / (double)(a.Length / 3):F4} B={chSum[2] / (double)(a.Length / 3):F4}");
        Console.WriteLine($"differing bytes  : {diffCount / (double)a.Length:P2}");
        Console.WriteLine($"max |diff|       : {max}");
    }

    private static void PrintEnvironment(Options opt)
    {
        var sicAsm = typeof(SharpImageConverter.ImageFrame).Assembly;
        string ver = sicAsm.GetName().Version?.ToString() ?? "?";
        string info = sicAsm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "?";

        Console.WriteLine("=== SharpImageConverter performance harness ===");
        Console.WriteLine($"build-tag        : {opt.Tag}");
        Console.WriteLine($"library-version  : {ver} (informational {info})");
        Console.WriteLine($"library-location : {sicAsm.Location}");
        Console.WriteLine($"framework        : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os               : {RuntimeInformation.OSDescription.Trim()}");
        Console.WriteLine($"cpu              : {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
        Console.WriteLine($"cores            : {Environment.ProcessorCount}");
        Console.WriteLine($"arch             : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"simd             : SSE2={Sse2.IsSupported} SSSE3={Ssse3.IsSupported} SSE41={Sse41.IsSupported} " +
                          $"AVX={Avx.IsSupported} AVX2={Avx2.IsSupported} AVX512F={Avx512F.IsSupported} " +
                          $"AVXVNNI={AvxVnni.IsSupported}");
        Console.WriteLine($"gc               : server={System.Runtime.GCSettings.IsServerGC} latency={System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"corpus           : {opt.Corpus}");
        Console.WriteLine($"timestamp        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();
    }

    private static void WriteCsv(string path, IReadOnlyList<BenchResult> results)
    {
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var sb = new StringBuilder();
        sb.Append("category,name,format,tag,width,height,pixels,iterations,median_ms,min_ms,mean_ms,max_ms,mpx_s,out_bytes,hash,stable\n");
        foreach (var r in results)
        {
            sb.Append(string.Join(",", new[]
            {
                Esc(r.Category), Esc(r.Name), Esc(r.Format), Esc(r.Tag),
                r.Width.ToString(CultureInfo.InvariantCulture),
                r.Height.ToString(CultureInfo.InvariantCulture),
                r.Pixels.ToString(CultureInfo.InvariantCulture),
                r.Iterations.ToString(CultureInfo.InvariantCulture),
                F(r.MedianMs), F(r.MinMs), F(r.MeanMs), F(r.MaxMs), F(r.MpxPerSec),
                r.OutBytes.ToString(CultureInfo.InvariantCulture),
                r.Hash.ToString("x8", CultureInfo.InvariantCulture),
                r.Stable ? "1" : "0"
            }));
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"\nCSV written: {path}");
    }

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
