using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpImageConverter.Formats.Webp;

internal static class Comparison
{
    private delegate byte[] Lossy(byte[] rgba, int width, int height, float quality);
    private delegate byte[] Lossless(byte[] rgba, int width, int height);
    private delegate byte[] Decode(byte[] data, out int width, out int height);
    private delegate void Transform(ReadOnlySpan<int> input, Span<int> output);
    private delegate void Filter(byte[] data, int offset, int stride, int threshold, int inner, int hev);

    internal static void Run(string baselinePath, List<(string, byte[])> cases, int w, int h)
    {
        // Isolate the saved baseline assembly, then alternate both versions in one
        // process to reduce drift from CPU frequency, temperature and system load.
        var context = new AssemblyLoadContext("baseline");
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
        var encoder = assembly.GetType(typeof(WebpEncoder).FullName!)!;
        var decoder = assembly.GetType(typeof(WebpDecoder).FullName!)!;
        var encodeLossy = encoder.GetMethod("EncodeLossyRgba", [typeof(byte[]), typeof(int), typeof(int), typeof(float)])!.CreateDelegate<Lossy>();
        var encodeLossless = encoder.GetMethod("EncodeRgba", [typeof(byte[]), typeof(int), typeof(int)])!.CreateDelegate<Lossless>();
        var decode = decoder.GetMethod("DecodeRgba", [typeof(byte[]), typeof(int).MakeByRefType(), typeof(int).MakeByRefType()])!.CreateDelegate<Decode>();
        Console.WriteLine("case,operation,baseline_ms,candidate_ms,speedup,bytes,sha256");
        foreach (var (name, rgba) in cases)
        {
            byte[] lossy = encodeLossy(rgba, w, h, 75), lossless = encodeLossless(rgba, w, h);
            Compare(name, "encode-lossy", () => encodeLossy(rgba, w, h, 75), () => WebpEncoder.EncodeLossyRgba(rgba, w, h, 75));
            Compare(name, "decode-lossy", () => decode(lossy, out _, out _), () => WebpDecoder.DecodeRgba(lossy, out _, out _));
            Compare(name, "encode-lossless", () => encodeLossless(rgba, w, h), () => WebpEncoder.EncodeRgba(rgba, w, h));
            Compare(name, "decode-lossless", () => decode(lossless, out _, out _), () => WebpDecoder.DecodeRgba(lossless, out _, out _));
        }
        var transform = assembly.GetType("SharpImageConverter.Formats.Webp.Vp8ForwardTransform")!;
        int[] input = new int[16], output = new int[16];
        var random = new Random(321);
        for (int i = 0; i < 16; i++) input[i] = random.Next(-255, 256);
        foreach (string method in new[] { "Dct4x4", "Wht4x4" })
        {
            var original = transform.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Transform>();
            Transform candidate = method == "Dct4x4" ? Vp8ForwardTransform.Dct4x4 : Vp8ForwardTransform.Wht4x4;
            Compare("kernel", method, () => { for (int i = 0; i < 200000; i++) original(input, output); return MemoryMarshal.AsBytes(output.AsSpan()).ToArray(); },
                () => { for (int i = 0; i < 200000; i++) candidate(input, output); return MemoryMarshal.AsBytes(output.AsSpan()).ToArray(); }, batch: 1);
        }
        var filterType = assembly.GetType("SharpImageConverter.Formats.Webp.Vp8LoopFilter")!;
        var filter = filterType.GetMethod("HFilter16", BindingFlags.Static | BindingFlags.Public)!.CreateDelegate<Filter>();
        byte[] beforeFilter = new byte[32 * 24];
        for (int i = 0; i < beforeFilter.Length; i++) beforeFilter[i] = (byte)(120 + random.Next(16));
        byte[] afterFilter = (byte[])beforeFilter.Clone();
        Compare("kernel", "strided-filter", () => { for (int i = 0; i < 100000; i++) filter(beforeFilter, 8, 32, 20, 10, 2); return beforeFilter; },
            () => { for (int i = 0; i < 100000; i++) Vp8LoopFilter.HFilter16(afterFilter, 8, 32, 20, 10, 2); return afterFilter; }, batch: 1);
    }

    private static void Compare(string name, string op, Func<byte[]> original, Func<byte[]> candidate, int batch = 0)
    {
        byte[] expected = original(), actual = candidate();
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException($"Output mismatch: {name}/{op}");
        for (int i = 0; i < 3; i++) { original(); candidate(); }
        double Measure(Func<byte[]> action, int count)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++) action();
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds / count;
        }
        if (batch == 0) batch = Math.Clamp((int)(30 / Math.Max(0.01, Measure(original, 1))), 1, 64);
        double[] before = new double[11], after = new double[11];
        for (int i = 0; i < before.Length; i++)
        {
            if ((i & 1) == 0) { before[i] = Measure(original, batch); after[i] = Measure(candidate, batch); }
            else { after[i] = Measure(candidate, batch); before[i] = Measure(original, batch); }
        }
        Array.Sort(before); Array.Sort(after);
        Console.WriteLine($"{name},{op},{before[5]:F4},{after[5]:F4},{before[5] / after[5]:F3},{actual.Length},{Convert.ToHexString(SHA256.HashData(actual))}");
    }
}
