using System.Diagnostics;
using System.Globalization;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Processing;

namespace SicBench;

internal sealed record BenchResult(
    string Category, string Name, string Format, string Tag,
    int Width, int Height, long Pixels, int Iterations,
    double MedianMs, double MinMs, double MeanMs, double MaxMs,
    long OutBytes, uint Hash, bool Stable)
{
    public double MpxPerSec => MedianMs > 0 ? (Pixels / 1_000_000.0) / (MedianMs / 1000.0) : 0.0;
}

internal static class Bench
{
    private static readonly List<BenchResult> Results = new();
    private static Options _opt = new();

    public static List<BenchResult> Run(Options opt)
    {
        _opt = opt;
        var entries = Corpus.Load(opt.Corpus);
        Console.WriteLine($"corpus entries: {string.Join(", ", entries.Select(e => $"{e.Tag}({e.Width}x{e.Height})"))}");
        Console.WriteLine();

        foreach (var e in entries)
        {
            if (e.Tag == "huge") RunHuge(e);
            else RunTier(e);
        }

        PrintSummary();
        return Results;
    }

    // ---------------------------------------------------------------- tier

    private static void RunTier(CorpusEntry e)
    {
        Console.WriteLine($"--- tier {e.Tag} {e.Width}x{e.Height} ({(e.Pixels / 1_000_000.0):F2} MP) ---");

        byte[] rgb = e.File_(e.Tag + ".rgb");
        byte[] gray = e.File_(e.Tag + ".gray");
        int w = e.Width, h = e.Height;
        int iter = Iterations(e.Pixels);

        var ms = new MemoryStream(1 << 22);
        var frame = new ImageFrame(w, h, rgb);

        // ---- decode (color) ----
        Decode($"decode", "jpeg", e, iter, e.File_(e.Tag + ".jpg"), st => ImageFrame.LoadJpeg(st));
        Decode($"decode", "png", e, iter, e.File_(e.Tag + ".png"), st => ImageFrame.LoadPng(st));
        Decode($"decode", "bmp", e, iter, e.File_(e.Tag + ".bmp"), st => ImageFrame.LoadBmp(st));
        Decode($"decode", "gif", e, iter, e.File_(e.Tag + ".gif"), st => ImageFrame.LoadGif(st));
        Decode($"decode", "webp", e, iter, e.File_(e.Tag + ".webp"), st => ImageFrame.LoadWebp(st));

        // ---- decode (grayscale sources) ----
        Decode("decode-gray", "png8", e, iter, e.File_(e.Tag + "_g.png"), st => ImageFrame.LoadPng(st));
        Decode("decode-gray", "bmp8", e, iter, e.File_(e.Tag + "_g.bmp"), st => ImageFrame.LoadBmp(st));
        Decode("decode-gray", "jpeg-gray", e, iter, e.File_(e.Tag + "_g.jpg"), st => ImageFrame.LoadJpeg(st));
        Decode("decode-gray", "gif", e, iter, e.File_(e.Tag + "_g.gif"), st => ImageFrame.LoadGif(st));
        Decode("decode-gray", "webp", e, iter, e.File_(e.Tag + "_g.webp"), st => ImageFrame.LoadWebp(st));

        // ---- encode (color) ----
        Measure("encode", "jpeg-q75", "jpeg", e, iter, () => { ms.SetLength(0); JpegEncoder.Write(ms, w, h, rgb, 75); return Snap(ms); });
        Measure("encode", "jpeg-q90", "jpeg", e, iter, () => { ms.SetLength(0); JpegEncoder.Write(ms, w, h, rgb, 90); return Snap(ms); });
        Measure("encode", "png", "png", e, iter, () => { ms.SetLength(0); PngWriter.Write(ms, w, h, rgb); return Snap(ms); });
        Measure("encode", "gif", "gif", e, iter, () => { ms.SetLength(0); new GifEncoder().Encode(frame, ms); return Snap(ms); });
        Measure("encode", "bmp", "bmp", e, iter, () => { ms.SetLength(0); BmpWriter.Write24(ms, w, h, rgb); return Snap(ms); });
        Measure("encode", "webp-q75", "webp", e, iter, () => WebpEncode(ms, rgb, w, h));

        // ---- encode (grayscale sources) ----
        Measure("encode-gray", "png8", "png", e, iter, () => { ms.SetLength(0); PngWriter.WriteGray(ms, w, h, gray); return Snap(ms); });
        Measure("encode-gray", "bmp8", "bmp", e, iter, () => { ms.SetLength(0); BmpWriter.Write8(ms, w, h, gray); return Snap(ms); });
        Measure("encode-gray", "jpeg-gray", "jpeg", e, iter, () => { ms.SetLength(0); JpegEncoder.WriteGray8(ms, w, h, gray, 75); return Snap(ms); });
        byte[] grayRgb = Corpus.ExpandGrayToRgb(gray);
        var grayFrame = new ImageFrame(w, h, grayRgb);
        Measure("encode-gray", "gif", "gif", e, iter, () => { ms.SetLength(0); new GifEncoder().Encode(grayFrame, ms); return Snap(ms); });
        Measure("encode-gray", "webp-q75", "webp", e, iter, () => WebpEncode(ms, grayRgb, w, h));

        // ---- color -> grayscale ----
        Measure("grayscale", "rgb24->gray", "gray", e, iter, () =>
        {
            var img = new Image<Rgb24>(w, h, rgb);
            var g = img.Clone(c => c.Grayscale());
            return ((long)g.Buffer.Length, Hash(g.Buffer));
        });

        // ---- resize ----
        Measure("resize", "downscale-50%-auto", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w / 2, h / 2, Mode.Auto));
        Measure("resize", "downscale-25%-auto", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w / 4, h / 4, Mode.Auto));
        if (e.Pixels < 6_000_000)
            Measure("resize", "upscale-200%-auto", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w * 2, h * 2, Mode.Auto));
        if (e.Tag is "small" or "medium")
        {
            Measure("resize", "upscale-200%-bilinear", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w * 2, h * 2, Mode.Bilinear));
            Measure("resize", "upscale-200%-bicubic", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w * 2, h * 2, Mode.Bicubic));
            Measure("resize", "downscale-50%-bilinear", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w / 2, h / 2, Mode.Bilinear));
        }
        if (e.Pixels > 1_000_000)
            Measure("resize", "resizefit-1024", "rgb24", e, iter, () =>
            {
                var img = new Image<Rgb24>(w, h, rgb);
                var r = img.Clone(c => c.ResizeToFit(1024, 1024));
                return ((long)r.Buffer.Length, Hash(r.Buffer));
            });

        // ---- grayscale resize (expand is not timed) ----
        Measure("gray-resize", "downscale-50%", "gray8", e, iter, () => ResizeOp(grayRgb, w, h, w / 2, h / 2, Mode.Auto));
        if (e.Pixels < 6_000_000)
            Measure("gray-resize", "upscale-200%", "gray8", e, iter, () => ResizeOp(grayRgb, w, h, w * 2, h * 2, Mode.Auto));

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private enum Mode { Auto, Bilinear, Bicubic }

    private static (long, uint) WebpEncode(MemoryStream ms, byte[] rgb, int w, int h)
    {
        ms.SetLength(0);
        new WebpEncoderAdapter().EncodeRgb24(ms, new Image<Rgb24>(w, h, rgb));
        return Snap(ms);
    }

    private static (long, uint) ResizeOp(byte[] buf, int w, int h, int nw, int nh, Mode mode)
    {
        var img = new Image<Rgb24>(w, h, buf);
        var r = mode switch
        {
            Mode.Bilinear => img.Clone(c => c.ResizeBilinear(nw, nh)),
            Mode.Bicubic => img.Clone(c => c.ResizeBicubicOptimized(nw, nh)),
            _ => img.Clone(c => c.Resize(nw, nh)),
        };
        return ((long)r.Buffer.Length, Hash(r.Buffer));
    }

    // ---------------------------------------------------------------- huge

    private static void RunHuge(CorpusEntry e)
    {
        Console.WriteLine($"--- tier huge {e.Width}x{e.Height} ({(e.Pixels / 1_000_000.0):F1} MP) ---");
        int iter = Math.Max(1, (int)Math.Round(2 * _opt.IterScale));
        byte[] bytes = e.File_("huge.jpg");

        // the huge frame is the source for every other huge test, so it is always
        // decoded - the decode itself is only timed when it passes the filter.
        var st0 = new MemoryStream(bytes, 0, bytes.Length, false, false);
        ImageFrame? decoded = null;
        bool timeDecode = Matches("decode/jpeg-progressive/jpeg@huge");
        if (timeDecode)
        {
            Measure("decode", "jpeg-progressive", "jpeg", e, iter, () =>
            {
                st0.Position = 0;
                var f = ImageFrame.LoadJpeg(st0);
                var snap = ((long)f.Pixels.Length, Hash(f.Pixels));
                decoded = f;
                return snap;
            });
        }
        if (decoded == null)
        {
            st0.Position = 0;
            decoded = ImageFrame.LoadJpeg(st0);
        }

        if (decoded == null)
        {
            Console.WriteLine("  !! huge decode failed, skipping huge processing tests");
            return;
        }

        // use the fixed raw dump (identical bytes for both builds) for everything except
        // the decode test itself
        byte[] rgb = e.HasRaw ? e.File_("huge.rgb") : decoded.Pixels;
        int w = decoded.Width, h = decoded.Height;
        var ms = new MemoryStream(1 << 26);

        Measure("grayscale", "rgb24->gray", "gray", e, iter, () =>
        {
            var img = new Image<Rgb24>(w, h, rgb);
            var g = img.Clone(c => c.Grayscale());
            return ((long)g.Buffer.Length, Hash(g.Buffer));
        });

        Measure("resize", "downscale-25%-auto", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w / 4, h / 4, Mode.Auto));
        Measure("resize", "downscale-50%-auto", "rgb24", e, iter, () => ResizeOp(rgb, w, h, w / 2, h / 2, Mode.Auto));

        if (!_opt.IncludeHugeEncode)
        {
            GC.Collect();
            return;
        }

        var frame = new ImageFrame(w, h, rgb);
        Measure("encode", "jpeg-q75", "jpeg", e, iter, () => { ms.SetLength(0); JpegEncoder.Write(ms, w, h, rgb, 75); return Snap(ms); });
        Measure("encode", "png", "png", e, iter, () => { ms.SetLength(0); PngWriter.Write(ms, w, h, rgb); return Snap(ms); });
        Measure("encode", "bmp", "bmp", e, iter, () => { ms.SetLength(0); BmpWriter.Write24(ms, w, h, rgb); return Snap(ms); });
        Measure("encode", "webp-q75", "webp", e, iter, () => WebpEncode(ms, rgb, w, h));
        Measure("encode", "gif", "gif", e, iter, () => { ms.SetLength(0); new GifEncoder().Encode(frame, ms); return Snap(ms); });

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    // ---------------------------------------------------------------- plumbing

    private static void Decode(string category, string format, CorpusEntry e, int iter, byte[] fileBytes, Func<Stream, ImageFrame> loader)
    {
        var st = new MemoryStream(fileBytes, 0, fileBytes.Length, false, false);
        Measure(category, format, format, e, iter, () =>
        {
            st.Position = 0;
            var f = loader(st);
            return ((long)f.Pixels.Length, Hash(f.Pixels));
        });
    }

    private static int Iterations(long pixels)
    {
        int n = pixels switch
        {
            < 200_000 => 12,
            < 1_000_000 => 10,
            < 5_000_000 => 8,
            < 30_000_000 => 5,
            _ => 2,
        };
        return Math.Max(1, (int)Math.Round(n * _opt.IterScale));
    }

    private static bool Matches(string key)
        => _opt.Filter.Length == 0 || key.Contains(_opt.Filter, StringComparison.OrdinalIgnoreCase);

    private static void Measure(string category, string name, string format, CorpusEntry e, int iterations, Func<(long bytes, uint hash)> body)
    {
        string key = $"{category}/{name}/{format}@{e.Tag}";
        if (!Matches(key))
            return;

        Console.Write($"  {key,-46}");
        Console.Out.Flush();

        // warm-up (also JIT / tiered-compilation promotion)
        (long bytes, uint hash) first;
        try
        {
            first = body();
        }
        catch (Exception ex)
        {
            Console.WriteLine($" FAILED: {ex.GetType().Name}: {Trim(ex.Message)}");
            Results.Add(new BenchResult(category, name, format, e.Tag, e.Width, e.Height, e.Pixels, 0,
                -1, -1, -1, -1, 0, 0, false));
            return;
        }

        var times = new List<double>(iterations);
        bool stable = true;
        for (int i = 0; i < iterations; i++)
        {
            CollectGarbage();
            var sw = Stopwatch.StartNew();
            var r = body();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (r.hash != first.hash || r.bytes != first.bytes) stable = false;
        }

        var sorted = times.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        if (sorted.Count % 2 == 0) median = (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        var result = new BenchResult(category, name, format, e.Tag, e.Width, e.Height, e.Pixels, iterations,
            median, sorted[0], times.Average(), sorted[^1], first.bytes, first.hash, stable);
        Results.Add(result);

        Console.WriteLine($"{median,10:F2} ms   {result.MpxPerSec,8:F2} Mpx/s   {(first.bytes / 1024.0),10:F0} KB   it={iterations}{(stable ? "" : "  [UNSTABLE]")}");
    }

    private static void CollectGarbage()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static (long, uint) Snap(MemoryStream ms)
    {
        int len = (int)ms.Length;
        return (len, Hash(new ReadOnlySpan<byte>(ms.GetBuffer(), 0, len)));
    }

    private static uint Hash(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            uint h = 2166136261u;
            int step = data.Length / 8192;
            if (step < 1) step = 1;
            for (int i = 0; i < data.Length; i += step) { h ^= data[i]; h *= 16777619u; }
            h ^= (uint)data.Length;
            h *= 16777619u;
            return h;
        }
    }

    private static string Trim(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 90 ? s : s.Substring(0, 90) + "...";
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("=== summary ===");
        foreach (var g in Results.GroupBy(r => r.Category))
        {
            Console.WriteLine($"[{g.Key}] {g.Count()} tests, total median {g.Sum(r => Math.Max(0, r.MedianMs)) / 1000.0:F2} s");
        }
        int unstable = Results.Count(r => !r.Stable);
        int failed = Results.Count(r => r.Iterations == 0);
        Console.WriteLine($"total {Results.Count()} tests, failed {failed}, unstable-output {unstable}");
    }
}
