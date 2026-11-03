using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using SharpImageConverter;
using SharpImageConverter.Formats.Webp;

// Run from the repository root. Profile one operation with: --profile encode-lossy
string? profile = args.Length == 2 && args[0] == "--profile" ? args[1] : null;
string? baseline = args.Length == 2 && args[0] == "--compare" ? args[1] : null;
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; Vector128={Vector128.IsHardwareAccelerated}");
const int w = 512, h = 512;
var photo = ImageFrame.LoadJpeg("examples/5_star_base.jpg");
byte[] photoRgb = photo.Pixels;
var cases = new List<(string, byte[])>();
foreach (string kind in new[] { "photo", "gradient", "ui", "noise", "alpha" })
{
    byte[] rgba = new byte[w * h * 4];
    var random = new Random(1234);
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        int i = (y * w + x) * 4;
        if (kind == "photo")
        {
            int s = ((y * photo.Height / h) * photo.Width + x * photo.Width / w) * 3;
            photoRgb.AsSpan(s, 3).CopyTo(rgba.AsSpan(i, 3));
        }
        else if (kind == "noise") random.NextBytes(rgba.AsSpan(i, 3));
        else if (kind is "gradient" or "alpha")
        { rgba[i] = (byte)(x / 2); rgba[i + 1] = (byte)(y / 2); rgba[i + 2] = (byte)((x + y) / 4); }
        else
        { byte v = (byte)(((x / 32 + y / 24) % 4) * 64); rgba[i] = v; rgba[i + 1] = (byte)(v / 2); rgba[i + 2] = (byte)(255 - v); }
        rgba[i + 3] = kind == "alpha" ? (byte)(x ^ y) : (byte)255;
    }
    cases.Add((kind, rgba));
}
if (baseline != null)
{
    Comparison.Run(baseline, cases, w, h);
    return;
}
Console.WriteLine("case,operation,median_ms,min_ms,bytes,sha256");
foreach (var (name, rgba) in cases)
{
    byte[] lossy = profile == null || profile.EndsWith("-lossy", StringComparison.Ordinal) ? WebpEncoder.EncodeLossyRgba(rgba, w, h, 75) : [];
    byte[] lossless = profile == null || profile.EndsWith("-lossless", StringComparison.Ordinal) ? WebpEncoder.EncodeRgba(rgba, w, h) : [];
    var operations = new (string, Func<byte[]>)[] {
        ("encode-lossy", () => WebpEncoder.EncodeLossyRgba(rgba, w, h, 75)),
        ("decode-lossy", () => WebpDecoder.DecodeRgba(lossy, out _, out _)),
        ("encode-lossless", () => WebpEncoder.EncodeRgba(rgba, w, h)),
        ("decode-lossless", () => WebpDecoder.DecodeRgba(lossless, out _, out _)) };
    foreach (var (op, action) in operations)
    {
        if (profile != null && op != profile) continue;
        for (int i = 0; i < 3; i++) action();
        if (profile != null)
        {
            var until = Stopwatch.StartNew();
            while (until.Elapsed.TotalSeconds < 6) action();
            continue;
        }
        double[] times = new double[9];
        byte[]? result = null;
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            result = action();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(times);
        Console.WriteLine($"{name},{op},{times[times.Length / 2]:F4},{times[0]:F4},{result!.Length},{Convert.ToHexString(SHA256.HashData(result))}");
    }
}
