using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;

// P3-2 离线参数消融：对每个参数单独扫网格，再按贪心组合，输出体积 / 画质 / 是否达标。
// 用法：dotnet run -c Release --project tools/perf/WebpParamProbe -- <image...> [quality]
int quality = 60;
var files = new List<string>();
foreach (string a in args)
{
    if (int.TryParse(a, out int q)) quality = q;
    else files.Add(a);
}
if (files.Count == 0) files.Add(".perf/corpus/small.png");

var options = new OptimizeOptions();
Console.WriteLine($"# WebP 参数消融（q={quality}，method=2，达标线 感知 {options.MinPerceptualPsnr:F2} dB / 像素 {options.MinRawPsnr:F1} dB / SSIM {options.MinSsim:F2} / 最差1% {options.MinWorstSsim:F2}）");

foreach (string file in files)
{
    var image = Configuration.Default.LoadRgba32(file);
    int w = image.Width, h = image.Height;
    byte[] rgba = image.Buffer;
    bool hasAlpha = false;
    for (int i = 3; i < rgba.Length; i += 4)
    {
        if (rgba[i] != 255) { hasAlpha = true; break; }
    }
    Console.WriteLine();
    Console.WriteLine($"## {Path.GetFileName(file)} ({w}×{h}, {w * (double)h / 1e6:F2} MP, alpha={hasAlpha})");

    var baseline = Encode(quality);
    Console.WriteLine($"baseline: {baseline.Bytes.Length} B | {baseline.Score} | accept={options.Accept(baseline.Score)}");
    Console.WriteLine("| 参数 | 取值 | 体积 | Δ | 感知 PSNR | 最差1% SSIM | 达标 |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|---|");

    void Row(string name, string value, Candidate c)
    {
        double delta = (double)(c.Bytes.Length - baseline.Bytes.Length) / baseline.Bytes.Length * 100;
        Console.WriteLine($"| {name} | {value} | {c.Bytes.Length} | {delta:+0.0;-0.0;0}% | {c.Score.PerceptualPsnr:F2} | {c.Score.WorstSsim:F3} | {options.Accept(c.Score)} |");
    }

    int bestSingle = baseline.Bytes.Length;
    var singles = new List<(string Name, Candidate C)>();
    void Sweep(string name, string[] values, Func<int, WebpEncoderOptions> make)
    {
        foreach (string v in values)
        {
            var c = Encode(quality, make(int.Parse(v)));
            Row(name, v, c);
            if (options.Accept(c.Score) && c.Bytes.Length < bestSingle)
            {
                bestSingle = c.Bytes.Length;
                singles.Add((name, c));
            }
        }
    }

    Sweep("filter", ["0", "20", "30", "40", "50", "80"], v => Tuned(quality, filterStrength: v));
    Sweep("sns", ["0", "25", "40", "60", "75", "100"], v => Tuned(quality, snsStrength: v));
    Sweep("segments", ["1", "2", "3"], v => Tuned(quality, segments: v));
    Sweep("preprocessing", ["1", "2"], v => Tuned(quality, preprocessing: v));
    if (hasAlpha) Sweep("alpha_quality", ["50", "75"], v => Tuned(quality, alphaQuality: v));

    // 贪心组合（与 WebpOptimizer 同顺序/同网格）
    int alphaQ = -1, filter = -1, sns = -1, segments = -1, pre = -1;
    int bestSize = baseline.Bytes.Length;
    for (int pass = 0; pass < 2; pass++)
    {
        bool changed = false;
        void Try(int value, ref int slot, Func<int, WebpEncoderOptions> make)
        {
            if (value == slot) return;
            var c = Encode(quality, make(value));
            if (options.Accept(c.Score) && c.Bytes.Length < bestSize)
            {
                bestSize = c.Bytes.Length;
                slot = value;
                changed = true;
            }
        }
        if (hasAlpha)
        {
            foreach (int v in new[] { 50, 75 }) Try(v, ref alphaQ, x => Tuned(quality, x, filter, sns, segments, pre));
        }
        foreach (int v in new[] { 0, 20, 30, 40, 50, 80 }) Try(v, ref filter, x => Tuned(quality, alphaQ, x, sns, segments, pre));
        foreach (int v in new[] { 0, 25, 40, 60, 75, 100 }) Try(v, ref sns, x => Tuned(quality, alphaQ, filter, x, segments, pre));
        foreach (int v in new[] { 1, 2, 3 }) Try(v, ref segments, x => Tuned(quality, alphaQ, filter, sns, x, pre));
        foreach (int v in new[] { 1, 2 }) Try(v, ref pre, x => Tuned(quality, alphaQ, filter, sns, segments, x));
        if (!changed) break;
    }

    double combinedDelta = (double)(bestSize - baseline.Bytes.Length) / baseline.Bytes.Length * 100;
    Console.WriteLine($"single-param best: {bestSingle} B ({(double)(bestSingle - baseline.Bytes.Length) / baseline.Bytes.Length * 100:+0.0;-0.0;0}%)");
    Console.WriteLine($"greedy combined: {bestSize} B ({combinedDelta:+0.0;-0.0;0}%)  alpha={alphaQ} filter={filter} sns={sns} segments={segments} pre={pre}");

    WebpEncoderOptions Tuned(float q, int alphaQuality = -1, int filterStrength = -1, int snsStrength = -1, int segments = -1, int preprocessing = -1)
        => new(q, WebpConcurrencyStrategy.Parallel, method: 2, lossless: false,
            alphaQuality, filterStrength, snsStrength, segments, preprocessing);

    Candidate Encode(int q, WebpEncoderOptions? opts = null)
    {
        var enc = opts ?? new WebpEncoderOptions(q, WebpConcurrencyStrategy.Parallel, method: 2);
        using var ms = new MemoryStream();
        new WebpEncoderAdapterRgba { Options = enc }.EncodeRgba32(ms, new Image<Rgba32>(w, h, rgba));
        byte[] bytes = ms.ToArray();
        using var decMs = new MemoryStream(bytes, false);
        byte[] decoded = new WebpDecoderAdapterRgba().DecodeRgba32(decMs).Buffer;
        return new Candidate(bytes, QualityMetrics.Compare(rgba, decoded, w, h, 4));
    }
}

readonly struct Candidate(byte[] bytes, ImageQuality score)
{
    public byte[] Bytes { get; } = bytes;
    public ImageQuality Score { get; } = score;
}
