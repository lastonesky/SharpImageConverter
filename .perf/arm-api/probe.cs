// 明天在 Mac mini 上跑：dotnet run .perf/arm-api/probe.cs
//
// 目的：只做**探测与微基准**，不改动任何生产代码。回答三件事：
//   1) M4 上到底有哪些 ARM 扩展可用（尤其是 SVE 到底支不支持）；
//   2) 硬件 CRC32 是否就是 PNG 要的 IEEE 多项式（用 "123456789" -> 0xCBF43926 这个标准检验值判定）；
//   3) 候选指令的实际吞吐（CRC32 / LD3 解交织 / SQRDMLAH），拿到"值不值得动生产代码"的第一手数字。
#:property AllowUnsafeBlocks=true
#pragma warning disable SYSLIB5003   // Sve/Sve2 在 .NET 10 仍是实验性 API，探测用途可忽略

using System;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

double gb = 1 << 30;

Console.WriteLine("=== 1. 能力探测 ===");
void Cap(string n, bool v) => Console.WriteLine($"  {n,-22} : {v}");
Cap("AdvSimd", AdvSimd.IsSupported);
Cap("AdvSimd.Arm64", AdvSimd.Arm64.IsSupported);
Cap("Aes", Aes.IsSupported);
Cap("Crc32", Crc32.IsSupported);
Cap("Crc32.Arm64", Crc32.Arm64.IsSupported);
Cap("Dp (SDOT/UDOT)", Dp.IsSupported);
Cap("Rdm (SQRDMLAH)", Rdm.IsSupported);
Cap("Sha1", Sha1.IsSupported);
Cap("Sha256", Sha256.IsSupported);
Cap("Sve", Sve.IsSupported);
Cap("Sve2", Sve2.IsSupported);
Console.WriteLine($"  Vector<byte>.Count   : {System.Numerics.Vector<byte>.Count}");

Console.WriteLine("\n=== 2. 硬件 CRC32 多项式判定 ===");
// CRC-32/ISO-HDLC（PNG 用的那个）对 "123456789" 的检验值是 0xCBF43926。
// 若硬件算出这个值 => 硬件就是 IEEE 多项式，可直接替换 src/Formats/Png/Crc32.cs。
byte[] chk = System.Text.Encoding.ASCII.GetBytes("123456789");
uint refCrc = Crc32Ref(chk);
Console.WriteLine($"  参考实现(位运算, IEEE)  : 0x{refCrc:X8}   (期望 0xCBF43926)");
if (Crc32.IsSupported)
{
    uint hw = 0xFFFFFFFFu;
    foreach (byte b in chk) hw = Crc32.ComputeCrc32(hw, b);
    hw = ~hw;
    Console.WriteLine($"  硬件 Crc32(IEEE)       : 0x{hw:X8}   {(hw == refCrc ? "一致 ✓ 可替换" : "不一致 ✗ 多项式不同")}");

    uint hwc = 0xFFFFFFFFu;
    foreach (byte b in chk) hwc = Crc32.ComputeCrc32C(hwc, b);
    hwc = ~hwc;
    Console.WriteLine($"  硬件 Crc32C(Castagnoli): 0x{hwc:X8}   (必然与 IEEE 不同；x86 的 Sse42.Crc32 只有这一档)");
}
else Console.WriteLine("  跳过（Crc32 不可用）");

Console.WriteLine("\n=== 3. CRC 吞吐对比（64 MB）===");
int n = 64 << 20;
byte[] buf = new byte[n];
new Random(42).NextBytes(buf);
uint s8 = Crc32Slice8(buf);
double t8 = TimeIt(() => { if (Crc32Slice8(buf) != s8) throw new Exception(); }, 3);
Console.WriteLine($"  slice-by-8 表驱动      : {n / gb / t8:F2} GB/s   ({t8 * 1000:F1} ms / 64MB)   crc=0x{s8:X8}");
if (Crc32.IsSupported)
{
    uint h = Crc32Hw(buf);
    double t = TimeIt(() => { if (Crc32Hw(buf) != h) throw new Exception(); }, 3);
    Console.WriteLine($"  ARM crc32x 硬件        : {n / gb / t:F2} GB/s   ({t * 1000:F1} ms / 64MB)   crc=0x{h:X8}");
    Console.WriteLine($"  => 两者 crc 必须完全相等才能替换: {(h == s8 ? "相等 ✓" : "不等 ✗")}");
}

Console.WriteLine("\n=== 4. LD3 解交织（RGB24 -> R/G/B 平面）===");
if (AdvSimd.Arm64.IsSupported)
{
    byte[] rgb = new byte[48];
    for (int i = 0; i < 48; i++) rgb[i] = (byte)i;
    unsafe
    {
        fixed (byte* p = rgb)
        {
            var (r, g, b) = AdvSimd.Arm64.Load3xVector128AndUnzip(p);
            Console.WriteLine($"  R = {Hex(r)}");
            Console.WriteLine($"  G = {Hex(g)}");
            Console.WriteLine($"  B = {Hex(b)}");
            Console.WriteLine($"  期望 R = 00 03 06 09 0C 0F 12 15 18 1B 1E 21 24 27 2A 2D");
        }
    }

    byte[] big = new byte[48 * 4096];
    new Random(7).NextBytes(big);
    unsafe
    {
        fixed (byte* p = big)
        {
            TimeLd3(p, 4096, 50);                       // 预热
            var sw = Stopwatch.StartNew();
            TimeLd3(p, 4096, 50);
            sw.Stop();
            double t = sw.Elapsed.TotalSeconds / 50;
            Console.WriteLine($"  LD3 解交织吞吐         : {48.0 * 4096 / gb / t:F2} GB/s");
        }
    }
}
else Console.WriteLine("  跳过（非 ARM64）");

Console.WriteLine("\n=== 5. SQRDMLAH 语义抽样 ===");
if (Rdm.IsSupported)
{
    Vector128<short> a = Vector128.Create((short)1000, 2000, -3000, 4000, 100, -100, 5, -5);
    Vector128<short> b = Vector128.Create((short)3, -3, 2, -2, 300, 300, 9, -9);
    Vector128<short> acc = Vector128.Create((short)7, 7, 7, 7, 7, 7, 7, 7);
    var mla = Rdm.MultiplyRoundedDoublingAndAddSaturateHigh(acc, a, b);
    Console.WriteLine($"  SQRDMLAH               = {HexS(mla)}");
    Console.WriteLine($"  手工 2ab+acc（无饱和/舍入）= {string.Join(" ", Manual(a, b, acc))}");
    Console.WriteLine("  注意: 该指令自带 doubling + rounding + saturate，与现有定点内核逐位等价性必须与现有实现对拍");
}
else Console.WriteLine("  跳过（Rdm 不可用）");

// ------------------------------------------------------------------
static unsafe void TimeLd3(byte* p, int blocks, int iters)
{
    ulong acc = 0;
    for (int it = 0; it < iters; it++)
        for (int i = 0; i < blocks; i++)
        {
            var (r, g, b) = AdvSimd.Arm64.Load3xVector128AndUnzip(p + i * 48);
            acc += r.GetElement(0) + (ulong)g.GetElement(0) + b.GetElement(0);
        }
    if (acc == 12345) Console.Write("");
}

static string Hex(Vector128<byte> v)
{
    var s = new System.Text.StringBuilder();
    for (int i = 0; i < 16; i++) s.Append(v.GetElement(i).ToString("X2") + " ");
    return s.ToString().TrimEnd();
}

static string HexS(Vector128<short> v)
{
    var s = new System.Text.StringBuilder();
    for (int i = 0; i < 8; i++) s.Append(((int)v.GetElement(i)).ToString() + " ");
    return s.ToString().TrimEnd();
}

static int[] Manual(Vector128<short> a, Vector128<short> b, Vector128<short> acc)
{
    var r = new int[8];
    for (int i = 0; i < 8; i++) r[i] = 2 * a.GetElement(i) * b.GetElement(i) + acc.GetElement(i);
    return r;
}

static double TimeIt(Action body, int iters)
{
    body();
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iters; i++) body();
    sw.Stop();
    return sw.Elapsed.TotalSeconds / iters;
}

static uint Crc32Hw(byte[] data)
{
    uint c = 0xFFFFFFFFu;
    int i = 0;
    for (; i + 8 <= data.Length; i += 8)
    {
        ulong w = BitConverter.ToUInt64(data, i);
        c = Crc32.ComputeCrc32(c, (uint)w);
        c = Crc32.ComputeCrc32(c, (uint)(w >> 32));
    }
    for (; i < data.Length; i++) c = Crc32.ComputeCrc32(c, data[i]);
    return ~c;
}

// 位运算参考实现（IEEE / ISO-HDLC，PNG 用的多项式）
static uint Crc32Ref(byte[] data)
{
    uint c = 0xFFFFFFFFu;
    foreach (byte b in data)
    {
        c ^= b;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
    }
    return ~c;
}

// slice-by-8（与 src/Formats/Png/Crc32Optimized.cs 同算法，此处内联便于独立测速）
static uint Crc32Slice8(byte[] data)
{
    Span<uint> tab = stackalloc uint[8 * 256];
    for (uint i = 0; i < 256; i++)
    {
        uint crc = i;
        for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        tab[(int)i] = crc;
    }
    for (uint i = 0; i < 256; i++)
        for (int j = 1; j < 8; j++)
        {
            uint prev = tab[(j - 1) * 256 + (int)i];
            tab[j * 256 + (int)i] = (prev >> 8) ^ tab[(int)(prev & 0xFF)];
        }

    uint c = 0xFFFFFFFFu;
    int p = 0, len = data.Length;
    for (; p + 8 <= len; p += 8)
    {
        uint one = BitConverter.ToUInt32(data, p) ^ c;
        uint two = BitConverter.ToUInt32(data, p + 4);
        c = tab[7 * 256 + (int)(one & 0xFF)] ^ tab[6 * 256 + (int)((one >> 8) & 0xFF)] ^
            tab[5 * 256 + (int)((one >> 16) & 0xFF)] ^ tab[4 * 256 + (int)(one >> 24)] ^
            tab[3 * 256 + (int)(two & 0xFF)] ^ tab[2 * 256 + (int)((two >> 8) & 0xFF)] ^
            tab[1 * 256 + (int)((two >> 16) & 0xFF)] ^ tab[0 * 256 + (int)(two >> 24)];
    }
    for (; p < len; p++) c = (c >> 8) ^ tab[(int)(c ^ data[p])];
    return ~c;
}
