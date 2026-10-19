using System.Diagnostics;
using SharpImageConverter;

if (args.Length < 1)
{
    Console.WriteLine("Usage: benchjpeg <size> [iters] [warmup]");
    return;
}

int size = int.Parse(args[0]);
int iters = args.Length > 1 ? int.Parse(args[1]) : 20;
int warmup = args.Length > 2 ? int.Parse(args[2]) : 10;

byte[] rgb = MakeImage(size, size);

// Warmup
for (int i = 0; i < warmup; i++)
{
    using var ms = new MemoryStream();
    JpegEncoder.Write(ms, size, size, rgb, 75);
}
GC.Collect(2, GCCollectionMode.Forced, true, true);
GC.WaitForPendingFinalizers();

int bytes;
using (var ms = new MemoryStream())
{
    JpegEncoder.Write(ms, size, size, rgb, 75);
    bytes = (int)ms.Length;
}

var times = new double[iters];
for (int i = 0; i < iters; i++)
{
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();
    using var ms = new MemoryStream();
    var sw = Stopwatch.StartNew();
    JpegEncoder.Write(ms, size, size, rgb, 75);
    sw.Stop();
    times[i] = sw.Elapsed.TotalMilliseconds;
}

Array.Sort(times);
double avg = times.Average();
double med = times[iters / 2];
double trimmedMean = times.Skip(2).Take(iters - 4).Average(); // trim 2 outliers each side

Console.WriteLine($"size={size} PC={Environment.ProcessorCount} bytes={bytes} warmup={warmup} iters={iters}");
Console.WriteLine($"  trimmedMean={trimmedMean:F2}ms med={med:F2}ms avg={avg:F2}ms");
Console.WriteLine($"  all: {string.Join(", ", times.Select(t => t.ToString("F2")))}");

static byte[] MakeImage(int w, int h)
{
    var px = new byte[w * h * 3];
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        int i = (y * w + x) * 3;
        px[i] = (byte)(x * 3);
        px[i + 1] = (byte)(y * 3);
        px[i + 2] = (byte)((x + y) & 0xFF);
    }
    return px;
}
