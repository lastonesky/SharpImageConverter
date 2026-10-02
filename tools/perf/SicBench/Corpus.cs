using System.Globalization;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Processing;

namespace SicBench;

internal sealed class CorpusEntry
{
    public string Tag = "";
    public int Width;
    public int Height;
    public bool HasRaw;

    public long Pixels => (long)Width * Height;

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dir;

    public CorpusEntry(string dir) => _dir = dir;

    public bool Has(string file) => File.Exists(Path.Combine(_dir, file));

    public byte[] File_(string file)
    {
        if (_files.TryGetValue(file, out var cached)) return cached;
        var bytes = File.ReadAllBytes(Path.Combine(_dir, file));
        _files[file] = bytes;
        return bytes;
    }
}

internal static class Corpus
{
    public const string ManifestName = "manifest.csv";

    // tag, width, height
    private static readonly (string Tag, int W, int H)[] Sizes =
    {
        ("tiny",   320,  240),
        ("small",  800,  600),
        ("medium", 1600, 1200),
        ("large",  4096, 3072),
    };

    public static void Generate(string examplesDir, string corpusDir)
    {
        Directory.CreateDirectory(corpusDir);
        var manifest = new List<string> { "tag,width,height,hasRaw" };

        string progressive = Path.Combine(examplesDir, "progressive.jpg");
        Console.WriteLine($"loading {progressive} ...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var huge = ImageFrame.LoadJpeg(progressive);
        sw.Stop();
        Console.WriteLine($"  progressive.jpg = {huge.Width}x{huge.Height} ({(huge.Width / 1000.0) * (huge.Height / 1000.0):F1} MP), decode {sw.Elapsed.TotalSeconds:F1}s");

        // huge tier: use the original file as-is (only decode / processing / encode tests)
        string hugeDst = Path.Combine(corpusDir, "huge.jpg");
        if (!File.Exists(hugeDst) || new FileInfo(hugeDst).Length != new FileInfo(progressive).Length)
            File.Copy(progressive, hugeDst, true);
        // also dump the decoded pixels: every non-decode test in the huge tier uses this
        // fixed buffer, so both builds are fed bit-identical input (native webp/png/jpeg
        // encoders are extremely sensitive to +/-1 pixel differences).
        File.WriteAllBytes(Path.Combine(corpusDir, "huge.rgb"), huge.Pixels);
        Console.WriteLine($"  huge.jpg  <- progressive.jpg ({new FileInfo(hugeDst).Length / 1024} KB)");
        Console.WriteLine($"  huge.rgb  <- decoded pixels ({huge.Pixels.Length / 1024} KB)");

        var src = new Image<Rgb24>(huge.Width, huge.Height, huge.Pixels);
        foreach (var s in Sizes)
        {
            Console.WriteLine($"  building {s.Tag} {s.W}x{s.H} ...");
            var img = src.Clone(c => c.Resize(s.W, s.H));
            byte[] rgb = img.Buffer;
            byte[] gray = ToGray8(rgb);

            File.WriteAllBytes(Path.Combine(corpusDir, $"{s.Tag}.rgb"), rgb);
            File.WriteAllBytes(Path.Combine(corpusDir, $"{s.Tag}.gray"), gray);

            WriteColor(corpusDir, s.Tag, s.W, s.H, rgb);
            WriteGray(corpusDir, s.Tag, s.W, s.H, gray);

            manifest.Add($"{s.Tag},{s.W},{s.H},1");
            Console.WriteLine($"    {s.Tag}: rgb={rgb.Length / 1024}KB  jpg={new FileInfo(Path.Combine(corpusDir, s.Tag + ".jpg")).Length / 1024}KB" +
                              $"  png={new FileInfo(Path.Combine(corpusDir, s.Tag + ".png")).Length / 1024}KB" +
                              $"  gif={new FileInfo(Path.Combine(corpusDir, s.Tag + ".gif")).Length / 1024}KB" +
                              $"  bmp={new FileInfo(Path.Combine(corpusDir, s.Tag + ".bmp")).Length / 1024}KB" +
                              $"  webp={new FileInfo(Path.Combine(corpusDir, s.Tag + ".webp")).Length / 1024}KB");
            GC.Collect();
        }

        // huge last: it allocates a ~430 MB frame, and running the small tiers afterwards
        // in a multi-GB heap adds a lot of GC noise to sub-millisecond measurements
        manifest.Add($"huge,{huge.Width},{huge.Height},1");

        File.WriteAllLines(Path.Combine(corpusDir, ManifestName), manifest);
        Console.WriteLine($"corpus generated at {corpusDir}");
    }

    private static void WriteColor(string dir, string tag, int w, int h, byte[] rgb)
    {
        JpegEncoder.Write(Path.Combine(dir, tag + ".jpg"), w, h, rgb, 75);
        PngWriter.Write(Path.Combine(dir, tag + ".png"), w, h, rgb);
        BmpWriter.Write24(Path.Combine(dir, tag + ".bmp"), w, h, rgb);
        using (var fs = File.Create(Path.Combine(dir, tag + ".gif")))
            new GifEncoder().Encode(new ImageFrame(w, h, rgb), fs);
        using (var fs = File.Create(Path.Combine(dir, tag + ".webp")))
            new WebpEncoderAdapter().EncodeRgb24(fs, new Image<Rgb24>(w, h, rgb));
    }

    private static void WriteGray(string dir, string tag, int w, int h, byte[] gray)
    {
        string suffix = "_g";
        PngWriter.WriteGray(Path.Combine(dir, tag + suffix + ".png"), w, h, gray);
        BmpWriter.Write8(Path.Combine(dir, tag + suffix + ".bmp"), w, h, gray);
        JpegEncoder.WriteGray8(Path.Combine(dir, tag + suffix + ".jpg"), w, h, gray, 75);
        byte[] rgb = ExpandGrayToRgb(gray);
        using (var fs = File.Create(Path.Combine(dir, tag + suffix + ".gif")))
            new GifEncoder().Encode(new ImageFrame(w, h, rgb), fs);
        using (var fs = File.Create(Path.Combine(dir, tag + suffix + ".webp")))
            new WebpEncoderAdapter().EncodeRgb24(fs, new Image<Rgb24>(w, h, rgb));
    }

    public static byte[] ToGray8(byte[] rgb)
    {
        int n = rgb.Length / 3;
        var gray = new byte[n];
        for (int i = 0, o = 0; i < n; i++, o += 3)
            gray[i] = (byte)((rgb[o] * 77 + rgb[o + 1] * 150 + rgb[o + 2] * 29) >> 8);
        return gray;
    }

    public static byte[] ExpandGrayToRgb(byte[] gray)
    {
        var rgb = new byte[gray.Length * 3];
        for (int i = 0, o = 0; i < gray.Length; i++, o += 3)
            rgb[o] = rgb[o + 1] = rgb[o + 2] = gray[i];
        return rgb;
    }

    public static List<CorpusEntry> Load(string corpusDir)
    {
        var list = new List<CorpusEntry>();
        foreach (var line in File.ReadAllLines(Path.Combine(corpusDir, ManifestName)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            list.Add(new CorpusEntry(corpusDir)
            {
                Tag = p[0],
                Width = int.Parse(p[1], CultureInfo.InvariantCulture),
                Height = int.Parse(p[2], CultureInfo.InvariantCulture),
                HasRaw = p[3] == "1",
            });
        }
        return list;
    }
}
