using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 环内去块滤波器（in-loop deblocking filter），严格对照 libwebp
/// src/dsp/dec.c 的 C 参考实现（非 SIMD 路径），保证位精确一致。
/// 对应 C 函数：DoFilter2_C / DoFilter4_C / DoFilter6_C / Hev /
/// NeedsFilter_C / NeedsFilter2_C / FilterLoop26_C / FilterLoop24_C /
/// SimpleVFilter16_C / SimpleHFilter16_C / VFilter16_C / HFilter16_C /
/// VFilter16i_C / HFilter16i_C / VFilter8_C / HFilter8_C / VFilter8i_C / HFilter8i_C。
///
/// SIMD 化说明：沿边界方向的 8 个像素被装进 Vector128&lt;short&gt; 的 8 个通道，
/// 逐像素的判定（NeedsFilter / NeedsFilter2 / Hev）与滤波（DoFilter2/4/6）
/// 全部改成掩码运算 + ConditionalSelect，语义与标量版逐位等价：
///   钳制表 sclip1/sclip2/clip1/abs0 直接用 min/max 算术表达（范围已在 int16 内），
///   因此不需要查表，也没有任何近似。
/// 两种访存形态：
///   vstride == 1（垂直边界）—— 8 个像素在内存中连续，直接 8 字节加载；
///   hstride == 1（水平边界）—— 8 个像素相隔 stride，先收集到 64 字节栈缓冲再转置加载。
/// </summary>
internal static class Vp8LoopFilter
{
    // ------------------------------------------------------------------
    // 钳制查找表，对应 src/dsp/dec_clip_tables.c 中的
    // abs0 / sclip1 / sclip2 / clip1。
    // 这里采用 libwebp 自身的运行时初始化路径（USE_STATIC_TABLES == 0 时
    // VP8InitClipTables 的公式）建表，与静态表内容逐字节一致：
    //   abs0[255 + i]  = |i|                      i ∈ [-255, 255]
    //   sclip1[893 + i] = clamp(i, -128, 127)     i ∈ [-893, 892]
    //   sclip2[112 + i] = clamp(i, -16, 15)       i ∈ [-112, 112]
    //   clip1[255 + i]  = clamp(i, 0, 255)        i ∈ [-255, 511]
    // C 中 VP8kabs0/VP8ksclip1/VP8ksclip2/VP8kclip1 分别指向表中心，
    // 用带符号下标访问；C# 中以下标加中心偏移的方式等价表达。
    // 静态构造由 CLR 保证线程安全，并发解码下只初始化一次。
    // 标量回退路径（size < 8 的残余像素）仍使用这些表。
    // ------------------------------------------------------------------

    private const int Abs0Center = 255;
    private const int SClip1Center = 893;
    private const int SClip2Center = 112;
    private const int Clip1Center = 255;

    private static readonly byte[] Abs0 = new byte[255 + 255 + 1];
    private static readonly sbyte[] SClip1 = new sbyte[893 + 892 + 1];
    private static readonly sbyte[] SClip2 = new sbyte[112 + 112 + 1];
    private static readonly byte[] Clip1 = new byte[255 + 511 + 1];

    static Vp8LoopFilter()
    {
        // 对应 VP8InitClipTables（USE_STATIC_TABLES == 0 分支）
        for (int i = -255; i <= 255; ++i)
        {
            Abs0[Abs0Center + i] = (byte)(i < 0 ? -i : i);
        }
        for (int i = -893; i <= 892; ++i)
        {
            SClip1[SClip1Center + i] = (sbyte)(i < -128 ? -128 : i > 127 ? 127 : i);
        }
        for (int i = -112; i <= 112; ++i)
        {
            SClip2[SClip2Center + i] = (sbyte)(i < -16 ? -16 : i > 15 ? 15 : i);
        }
        for (int i = -255; i <= 511; ++i)
        {
            Clip1[Clip1Center + i] = (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }
    }

    // ------------------------------------------------------------------
    // SIMD 常量与钳制原语
    // ------------------------------------------------------------------

    private static readonly Vector128<short> VZero = Vector128<short>.Zero;
    private static readonly Vector128<short> V255 = Vector128.Create((short)255);
    private static readonly Vector128<short> V127 = Vector128.Create((short)127);
    private static readonly Vector128<short> VNeg128 = Vector128.Create((short)-128);
    private static readonly Vector128<short> V15 = Vector128.Create((short)15);
    private static readonly Vector128<short> VNeg16 = Vector128.Create((short)-16);
    private static readonly Vector128<short> V3 = Vector128.Create((short)3);
    private static readonly Vector128<short> V4 = Vector128.Create((short)4);
    private static readonly Vector128<short> V1 = Vector128.Create((short)1);
    private static readonly Vector128<short> V63 = Vector128.Create((short)63);
    private static readonly Vector128<short> V27 = Vector128.Create((short)27);
    private static readonly Vector128<short> V18 = Vector128.Create((short)18);
    private static readonly Vector128<short> V9 = Vector128.Create((short)9);

    /// <summary>等价于 clip1：clamp(v, 0, 255)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clip1V(Vector128<short> v)
        => Vector128.Min(Vector128.Max(v, VZero), V255);

    /// <summary>等价于 sclip1：clamp(v, -128, 127)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> SClip1V(Vector128<short> v)
        => Vector128.Min(Vector128.Max(v, VNeg128), V127);

    /// <summary>等价于 sclip2：clamp(v, -16, 15)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> SClip2V(Vector128<short> v)
        => Vector128.Min(Vector128.Max(v, VNeg16), V15);

    /// <summary>8 个连续字节零扩展为 8 个 int16 通道。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Load8(ref byte src)
    {
        Vector64<byte> raw = Unsafe.ReadUnaligned<Vector64<byte>>(ref src);
        Vector128<byte> wide = Vector128.Create(raw, Vector64<byte>.Zero);
        return Vector128.AsInt16(Vector128.WidenLower(wide));
    }

    /// <summary>把 8 个 int16 通道饱和收窄并写回 8 个连续字节。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(ref byte dst, Vector128<short> v)
    {
        Vector128<byte> packed = Vector128.Narrow(v.AsUInt16(), v.AsUInt16());
        Unsafe.WriteUnaligned(ref dst, packed.AsUInt64().GetElement(0));
    }

    /// <summary>把 8 个 int16 通道按 laneStep 间隔散写（水平边界用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Scatter8(ref byte dst, int laneStep, Vector128<short> v)
    {
        Vector128<byte> packed = Vector128.Narrow(v.AsUInt16(), v.AsUInt16());
        for (int i = 0; i < 8; i++)
            Unsafe.Add(ref dst, i * laneStep) = packed.GetElement(i);
    }

    // ------------------------------------------------------------------
    // SIMD 滤波核：8 个像素一批
    // ------------------------------------------------------------------

    /// <summary>一批 8 个像素的 p3..q3 共 8 个抽头。</summary>
    private struct Taps8
    {
        public Vector128<short> P3, P2, P1, P0, Q0, Q1, Q2, Q3;
    }

    /// <summary>
    /// 对 8 个通道执行 NeedsFilter/NeedsFilter2 + Hev 判定与 DoFilter2/4/6，
    /// 未通过判定的通道原样保留。taps 取 2（简单滤波）、4（内边界）、6（外边界）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyFilter(in Taps8 t, int thresh2, int ithresh, int hevThresh, int taps, out Taps8 r)
    {
        Vector128<short> d0 = t.Q0 - t.P0;
        Vector128<short> d1 = t.P1 - t.Q1;
        Vector128<short> absD0 = Vector128.Abs(d0);
        Vector128<short> absD1 = Vector128.Abs(d1);

        // NeedsFilter：4*|p0-q0| + |p1-q1| <= 2*thresh+1
        Vector128<short> need = Vector128.LessThanOrEqual(
            absD0 + absD0 + absD0 + absD0 + absD1, Vector128.Create((short)thresh2));

        if (taps != 2)
        {
            // NeedsFilter2 额外要求两侧各三个内差都 <= ithresh
            Vector128<short> it = Vector128.Create((short)ithresh);
            need &= Vector128.LessThanOrEqual(Vector128.Abs(t.P1 - t.P0), it);
            need &= Vector128.LessThanOrEqual(Vector128.Abs(t.P2 - t.P1), it);
            need &= Vector128.LessThanOrEqual(Vector128.Abs(t.P3 - t.P2), it);
            need &= Vector128.LessThanOrEqual(Vector128.Abs(t.Q1 - t.Q0), it);
            need &= Vector128.LessThanOrEqual(Vector128.Abs(t.Q2 - t.Q1), it);
            need &= Vector128.LessThanOrEqual(Vector128.Abs(t.Q3 - t.Q2), it);
        }

        // DoFilter2：a = 3*(q0-p0) + sclip1(p1-q1)
        Vector128<short> a2 = d0 + d0 + d0 + SClip1V(d1);
        Vector128<short> f2P0 = Clip1V(t.P0 + SClip2V(Vector128.ShiftRightArithmetic(a2 + V3, 3)));
        Vector128<short> f2Q0 = Clip1V(t.Q0 - SClip2V(Vector128.ShiftRightArithmetic(a2 + V4, 3)));

        r = t;

        if (taps == 2)
        {
            r.P0 = Vector128.ConditionalSelect(need, f2P0, t.P0);
            r.Q0 = Vector128.ConditionalSelect(need, f2Q0, t.Q0);
            return;
        }

        // Hev：|p1-p0| > hev || |q1-q0| > hev
        Vector128<short> hv = Vector128.Create((short)hevThresh);
        Vector128<short> hev = Vector128.GreaterThan(Vector128.Abs(t.P1 - t.P0), hv)
                             | Vector128.GreaterThan(Vector128.Abs(t.Q1 - t.Q0), hv);

        if (taps == 6)
        {
            // DoFilter6：a 先经 sclip1，再用 27/18/9 三档加权
            Vector128<short> a6 = SClip1V(a2);
            Vector128<short> s1 = Vector128.ShiftRightArithmetic(a6 * V27 + V63, 7);
            Vector128<short> s2 = Vector128.ShiftRightArithmetic(a6 * V18 + V63, 7);
            Vector128<short> s3 = Vector128.ShiftRightArithmetic(a6 * V9 + V63, 7);

            Vector128<short> m6 = need & ~hev;
            r.P2 = Vector128.ConditionalSelect(m6, Clip1V(t.P2 + s3), t.P2);
            r.P1 = Vector128.ConditionalSelect(m6, Clip1V(t.P1 + s2), t.P1);
            r.Q1 = Vector128.ConditionalSelect(m6, Clip1V(t.Q1 - s2), t.Q1);
            r.Q2 = Vector128.ConditionalSelect(m6, Clip1V(t.Q2 - s3), t.Q2);
            r.P0 = Vector128.ConditionalSelect(need, Vector128.ConditionalSelect(hev, f2P0, Clip1V(t.P0 + s1)), t.P0);
            r.Q0 = Vector128.ConditionalSelect(need, Vector128.ConditionalSelect(hev, f2Q0, Clip1V(t.Q0 - s1)), t.Q0);
        }
        else
        {
            // DoFilter4：a = 3*(q0-p0)（不含 p1-q1 项）
            Vector128<short> a4 = d0 + d0 + d0;
            Vector128<short> t1 = SClip2V(Vector128.ShiftRightArithmetic(a4 + V4, 3));
            Vector128<short> t2 = SClip2V(Vector128.ShiftRightArithmetic(a4 + V3, 3));
            Vector128<short> t3 = Vector128.ShiftRightArithmetic(t1 + V1, 1);

            Vector128<short> m4 = need & ~hev;
            r.P1 = Vector128.ConditionalSelect(m4, Clip1V(t.P1 + t3), t.P1);
            r.Q1 = Vector128.ConditionalSelect(m4, Clip1V(t.Q1 - t3), t.Q1);
            r.P0 = Vector128.ConditionalSelect(need, Vector128.ConditionalSelect(hev, f2P0, Clip1V(t.P0 + t2)), t.P0);
            r.Q0 = Vector128.ConditionalSelect(need, Vector128.ConditionalSelect(hev, f2Q0, Clip1V(t.Q0 - t1)), t.Q0);
        }
    }

    /// <summary>垂直边界（vstride == 1）：8 个像素连续。</summary>
    private static void ChunkContiguous(ref byte b, int start, int hstride,
                                        int thresh2, int ithresh, int hevThresh, int taps)
    {
        Taps8 t;
        t.P3 = Load8(ref Unsafe.Add(ref b, start - 4 * hstride));
        t.P2 = Load8(ref Unsafe.Add(ref b, start - 3 * hstride));
        t.P1 = Load8(ref Unsafe.Add(ref b, start - 2 * hstride));
        t.P0 = Load8(ref Unsafe.Add(ref b, start - 1 * hstride));
        t.Q0 = Load8(ref Unsafe.Add(ref b, start));
        t.Q1 = Load8(ref Unsafe.Add(ref b, start + 1 * hstride));
        t.Q2 = Load8(ref Unsafe.Add(ref b, start + 2 * hstride));
        t.Q3 = Load8(ref Unsafe.Add(ref b, start + 3 * hstride));

        ApplyFilter(in t, thresh2, ithresh, hevThresh, taps, out Taps8 r);

        if (taps == 6)
        {
            Store8(ref Unsafe.Add(ref b, start - 3 * hstride), r.P2);
            Store8(ref Unsafe.Add(ref b, start - 2 * hstride), r.P1);
            Store8(ref Unsafe.Add(ref b, start + 1 * hstride), r.Q1);
            Store8(ref Unsafe.Add(ref b, start + 2 * hstride), r.Q2);
        }
        else if (taps == 4)
        {
            Store8(ref Unsafe.Add(ref b, start - 2 * hstride), r.P1);
            Store8(ref Unsafe.Add(ref b, start + 1 * hstride), r.Q1);
        }
        Store8(ref Unsafe.Add(ref b, start - 1 * hstride), r.P0);
        Store8(ref Unsafe.Add(ref b, start), r.Q0);
    }

    /// <summary>
    /// 水平边界（hstride == 1）：8 个像素相隔 vstride。
    /// 先把 8 行 × 8 抽头收集到列优先的栈缓冲（buf[c*8 + r]），再按列连续加载。
    /// </summary>
    private static void ChunkStrided(ref byte b, int start, int vstride, ref byte buf,
                                     int thresh2, int ithresh, int hevThresh, int taps)
    {
        if (SimdCompat.ByteShuffleSupported)
        {
            ChunkStridedTransposed(ref b, start, vstride, thresh2, ithresh, hevThresh, taps);
            return;
        }
        for (int row = 0; row < 8; row++)
        {
            ref byte src = ref Unsafe.Add(ref b, start + row * vstride);
            Unsafe.Add(ref buf, 0 * 8 + row) = Unsafe.Add(ref src, -4);
            Unsafe.Add(ref buf, 1 * 8 + row) = Unsafe.Add(ref src, -3);
            Unsafe.Add(ref buf, 2 * 8 + row) = Unsafe.Add(ref src, -2);
            Unsafe.Add(ref buf, 3 * 8 + row) = Unsafe.Add(ref src, -1);
            Unsafe.Add(ref buf, 4 * 8 + row) = Unsafe.Add(ref src, 0);
            Unsafe.Add(ref buf, 5 * 8 + row) = Unsafe.Add(ref src, 1);
            Unsafe.Add(ref buf, 6 * 8 + row) = Unsafe.Add(ref src, 2);
            Unsafe.Add(ref buf, 7 * 8 + row) = Unsafe.Add(ref src, 3);
        }

        Taps8 t;
        t.P3 = Load8(ref Unsafe.Add(ref buf, 0 * 8));
        t.P2 = Load8(ref Unsafe.Add(ref buf, 1 * 8));
        t.P1 = Load8(ref Unsafe.Add(ref buf, 2 * 8));
        t.P0 = Load8(ref Unsafe.Add(ref buf, 3 * 8));
        t.Q0 = Load8(ref Unsafe.Add(ref buf, 4 * 8));
        t.Q1 = Load8(ref Unsafe.Add(ref buf, 5 * 8));
        t.Q2 = Load8(ref Unsafe.Add(ref buf, 6 * 8));
        t.Q3 = Load8(ref Unsafe.Add(ref buf, 7 * 8));

        ApplyFilter(in t, thresh2, ithresh, hevThresh, taps, out Taps8 r);

        if (taps == 6)
        {
            Scatter8(ref Unsafe.Add(ref b, start - 3), vstride, r.P2);
            Scatter8(ref Unsafe.Add(ref b, start - 2), vstride, r.P1);
            Scatter8(ref Unsafe.Add(ref b, start + 1), vstride, r.Q1);
            Scatter8(ref Unsafe.Add(ref b, start + 2), vstride, r.Q2);
        }
        else if (taps == 4)
        {
            Scatter8(ref Unsafe.Add(ref b, start - 2), vstride, r.P1);
            Scatter8(ref Unsafe.Add(ref b, start + 1), vstride, r.Q1);
        }
        Scatter8(ref Unsafe.Add(ref b, start - 1), vstride, r.P0);
        Scatter8(ref Unsafe.Add(ref b, start), vstride, r.Q0);
    }

    // Four registers hold eight rows of eight bytes. The same transpose converts
    // rows to taps before filtering, and taps back to rows for contiguous stores.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(ref Vector128<byte> a, ref Vector128<byte> b,
        ref Vector128<byte> c, ref Vector128<byte> d)
    {
        var ab = SimdCompat.UnpackLow(a, a.GetUpper().ToVector128());
        var cd = SimdCompat.UnpackLow(b, b.GetUpper().ToVector128());
        var ef = SimdCompat.UnpackLow(c, c.GetUpper().ToVector128());
        var gh = SimdCompat.UnpackLow(d, d.GetUpper().ToVector128());
        var abcd0 = SimdCompat.UnpackLow(ab.AsUInt16(), cd.AsUInt16()).AsUInt32();
        var abcd1 = SimdCompat.UnpackHigh(ab.AsUInt16(), cd.AsUInt16()).AsUInt32();
        var efgh0 = SimdCompat.UnpackLow(ef.AsUInt16(), gh.AsUInt16()).AsUInt32();
        var efgh1 = SimdCompat.UnpackHigh(ef.AsUInt16(), gh.AsUInt16()).AsUInt32();
        a = SimdCompat.UnpackLow(abcd0, efgh0).AsByte();
        b = SimdCompat.UnpackHigh(abcd0, efgh0).AsByte();
        c = SimdCompat.UnpackLow(abcd1, efgh1).AsByte();
        d = SimdCompat.UnpackHigh(abcd1, efgh1).AsByte();
    }

    private static void ChunkStridedTransposed(ref byte b, int start, int stride,
        int thresh2, int ithresh, int hevThresh, int taps)
    {
        ref byte src = ref Unsafe.Add(ref b, start - 4);
        var a = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref src),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, stride))).AsByte();
        var c = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 4 * stride)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 5 * stride))).AsByte();
        var d = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 6 * stride)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 7 * stride))).AsByte();
        var middle = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 2 * stride)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 3 * stride))).AsByte();
        Transpose8x8(ref a, ref middle, ref c, ref d);
        Taps8 t = new()
        {
            P3 = Vector128.WidenLower(a).AsInt16(), P2 = Vector128.WidenUpper(a).AsInt16(),
            P1 = Vector128.WidenLower(middle).AsInt16(), P0 = Vector128.WidenUpper(middle).AsInt16(),
            Q0 = Vector128.WidenLower(c).AsInt16(), Q1 = Vector128.WidenUpper(c).AsInt16(),
            Q2 = Vector128.WidenLower(d).AsInt16(), Q3 = Vector128.WidenUpper(d).AsInt16()
        };
        ApplyFilter(in t, thresh2, ithresh, hevThresh, taps, out Taps8 r);
        a = Vector128.Narrow(r.P3.AsUInt16(), r.P2.AsUInt16());
        middle = Vector128.Narrow(r.P1.AsUInt16(), r.P0.AsUInt16());
        c = Vector128.Narrow(r.Q0.AsUInt16(), r.Q1.AsUInt16());
        d = Vector128.Narrow(r.Q2.AsUInt16(), r.Q3.AsUInt16());
        Transpose8x8(ref a, ref middle, ref c, ref d);
        Unsafe.WriteUnaligned(ref src, a.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, stride), a.AsUInt64().GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, 2 * stride), middle.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, 3 * stride), middle.AsUInt64().GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, 4 * stride), c.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, 5 * stride), c.AsUInt64().GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, 6 * stride), d.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref src, 7 * stride), d.AsUInt64().GetElement(1));
    }

    // ------------------------------------------------------------------
    // 标量回退路径（仅用于 size 不被 8 整除的残余像素，正常码流不会走到）
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 DoFilter2_C：4 像素输入，2 像素输出。
    /// off 对应 C 的指针 p，step 为相邻像素间距（垂直滤波取 stride，水平滤波取 1）。
    /// </summary>
    private static void DoFilter2(byte[] p, int off, int step)
    {
        int p1 = p[off - 2 * step], p0 = p[off - step], q0 = p[off], q1 = p[off + step];
        int a = 3 * (q0 - p0) + SClip1[SClip1Center + (p1 - q1)];   // 范围 [-893, 892]
        int a1 = SClip2[SClip2Center + ((a + 4) >> 3)];             // 范围 [-16, 15]
        int a2 = SClip2[SClip2Center + ((a + 3) >> 3)];
        p[off - step] = Clip1[Clip1Center + (p0 + a2)];
        p[off] = Clip1[Clip1Center + (q0 - a1)];
    }

    /// <summary>
    /// 对应 DoFilter4_C：4 像素输入，4 像素输出。
    /// </summary>
    private static void DoFilter4(byte[] p, int off, int step)
    {
        int p1 = p[off - 2 * step], p0 = p[off - step], q0 = p[off], q1 = p[off + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2[SClip2Center + ((a + 4) >> 3)];
        int a2 = SClip2[SClip2Center + ((a + 3) >> 3)];
        int a3 = (a1 + 1) >> 1;
        p[off - 2 * step] = Clip1[Clip1Center + (p1 + a3)];
        p[off - step] = Clip1[Clip1Center + (p0 + a2)];
        p[off] = Clip1[Clip1Center + (q0 - a1)];
        p[off + step] = Clip1[Clip1Center + (q1 - a3)];
    }

    /// <summary>
    /// 对应 DoFilter6_C：6 像素输入，6 像素输出。
    /// </summary>
    private static void DoFilter6(byte[] p, int off, int step)
    {
        int p2 = p[off - 3 * step], p1 = p[off - 2 * step], p0 = p[off - step];
        int q0 = p[off], q1 = p[off + step], q2 = p[off + 2 * step];
        int a = SClip1[SClip1Center + (3 * (q0 - p0) + SClip1[SClip1Center + (p1 - q1)])];
        // a ∈ [-128,127]，a1 ∈ [-27,27]，a2 ∈ [-18,18]，a3 ∈ [-9,9]
        int a1 = (27 * a + 63) >> 7;    // 等价于 ((3 * a + 7) * 9) >> 7
        int a2 = (18 * a + 63) >> 7;    // 等价于 ((2 * a + 7) * 9) >> 7
        int a3 = (9 * a + 63) >> 7;     // 等价于 ((1 * a + 7) * 9) >> 7
        p[off - 3 * step] = Clip1[Clip1Center + (p2 + a3)];
        p[off - 2 * step] = Clip1[Clip1Center + (p1 + a2)];
        p[off - step] = Clip1[Clip1Center + (p0 + a1)];
        p[off] = Clip1[Clip1Center + (q0 - a1)];
        p[off + step] = Clip1[Clip1Center + (q1 - a2)];
        p[off + 2 * step] = Clip1[Clip1Center + (q2 - a3)];
    }

    /// <summary>
    /// 对应 Hev：检测边界两侧是否存在高边缘方差（high edge variance）。
    /// </summary>
    private static bool Hev(byte[] p, int off, int step, int thresh)
    {
        int p1 = p[off - 2 * step], p0 = p[off - step], q0 = p[off], q1 = p[off + step];
        return Abs0[Abs0Center + (p1 - p0)] > thresh || Abs0[Abs0Center + (q1 - q0)] > thresh;
    }

    /// <summary>
    /// 对应 NeedsFilter_C：简单滤波器的是否需要滤波判定。
    /// </summary>
    private static bool NeedsFilter(byte[] p, int off, int step, int t)
    {
        int p1 = p[off - 2 * step], p0 = p[off - step], q0 = p[off], q1 = p[off + step];
        return 4 * Abs0[Abs0Center + (p0 - q0)] + Abs0[Abs0Center + (p1 - q1)] <= t;
    }

    /// <summary>
    /// 对应 NeedsFilter2_C：复杂滤波器的是否需要滤波判定（含内部一致性检查）。
    /// </summary>
    private static bool NeedsFilter2(byte[] p, int off, int step, int t, int it)
    {
        int p3 = p[off - 4 * step], p2 = p[off - 3 * step], p1 = p[off - 2 * step];
        int p0 = p[off - step], q0 = p[off];
        int q1 = p[off + step], q2 = p[off + 2 * step], q3 = p[off + 3 * step];
        if (4 * Abs0[Abs0Center + (p0 - q0)] + Abs0[Abs0Center + (p1 - q1)] > t)
        {
            return false;
        }
        return Abs0[Abs0Center + (p3 - p2)] <= it && Abs0[Abs0Center + (p2 - p1)] <= it &&
               Abs0[Abs0Center + (p1 - p0)] <= it && Abs0[Abs0Center + (q3 - q2)] <= it &&
               Abs0[Abs0Center + (q2 - q1)] <= it && Abs0[Abs0Center + (q1 - q0)] <= it;
    }

    // ------------------------------------------------------------------
    // 滤波循环调度
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 FilterLoop26_C / FilterLoop24_C 的统一实现。
    /// taps = 6 时 non-hev 走 DoFilter6，taps = 4 时走 DoFilter4，taps = 2 时只做简单滤波。
    /// </summary>
    private static void FilterLoop(byte[] p, int off, int hstride, int vstride, int size,
                                   int thresh, int ithresh, int hevThresh, int taps)
    {
        int thresh2 = 2 * thresh + 1;
        int end = off + size * vstride;

        if (vstride == 1)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(p);
            for (int s = off; s + 8 <= end; s += 8)
                ChunkContiguous(ref b, s, hstride, thresh2, ithresh, hevThresh, taps);
            return;
        }

        if (hstride == 1)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(p);
            Span<byte> gather = stackalloc byte[64];
            ref byte g = ref MemoryMarshal.GetReference(gather);
            for (int s = off; s + 8 * vstride <= end; s += 8 * vstride)
                ChunkStrided(ref b, s, vstride, ref g, thresh2, ithresh, hevThresh, taps);
            return;
        }

        // 兜底：标量（当前所有调用点都不会命中）
        switch (taps)
        {
            case 6:
                while (size-- > 0)
                {
                    if (NeedsFilter2(p, off, hstride, thresh2, ithresh))
                    {
                        if (Hev(p, off, hstride, hevThresh)) DoFilter2(p, off, hstride);
                        else DoFilter6(p, off, hstride);
                    }
                    off += vstride;
                }
                break;
            case 4:
                while (size-- > 0)
                {
                    if (NeedsFilter2(p, off, hstride, thresh2, ithresh))
                    {
                        if (Hev(p, off, hstride, hevThresh)) DoFilter2(p, off, hstride);
                        else DoFilter4(p, off, hstride);
                    }
                    off += vstride;
                }
                break;
            default:
                while (size-- > 0)
                {
                    if (NeedsFilter(p, off, hstride, thresh2))
                        DoFilter2(p, off, hstride);
                    off += vstride;
                }
                break;
        }
    }

    // ------------------------------------------------------------------
    // 简单环内滤波（RFC 6386 第 15.2 节）
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 SimpleVFilter16_C：宏块垂直边界的简单滤波（16 像素宽）。
    /// off 为边界下一行首像素的下标（与 C 传入的指针 p 一致）。
    /// </summary>
    public static void VFilter16Simple(byte[] y, int off, int stride, int thresh)
        => FilterLoop(y, off, stride, 1, 16, thresh, 0, 0, 2);

    /// <summary>
    /// 对应 SimpleHFilter16_C：宏块水平边界的简单滤波（16 像素高）。
    /// </summary>
    public static void HFilter16Simple(byte[] y, int off, int stride, int thresh)
        => FilterLoop(y, off, 1, stride, 16, thresh, 0, 0, 2);

    /// <summary>
    /// 对应 SimpleVFilter16i_C：宏块内部 3 条垂直边界的简单滤波（步进 4 像素）。
    /// off 为宏块原点（左上角像素）下标。
    /// </summary>
    public static void VFilter16Simplei(byte[] y, int off, int stride, int thresh)
    {
        for (int k = 1; k <= 3; ++k)
            VFilter16Simple(y, off + 4 * k * stride, stride, thresh);
    }

    /// <summary>
    /// 对应 SimpleHFilter16i_C：宏块内部 3 条水平边界的简单滤波（步进 4 像素）。
    /// off 为宏块原点（左上角像素）下标。
    /// </summary>
    public static void HFilter16Simplei(byte[] y, int off, int stride, int thresh)
    {
        for (int k = 1; k <= 3; ++k)
            HFilter16Simple(y, off + 4 * k, stride, thresh);
    }

    // ------------------------------------------------------------------
    // 复杂环内滤波（RFC 6386 第 15.3 节）
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 VFilter16_C：亮度宏块垂直外边界滤波。
    /// </summary>
    public static void VFilter16(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop(y, off, stride, 1, 16, thresh, ithresh, hevThresh, 6);

    /// <summary>
    /// 对应 HFilter16_C：亮度宏块水平外边界滤波。
    /// </summary>
    public static void HFilter16(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop(y, off, 1, stride, 16, thresh, ithresh, hevThresh, 6);

    /// <summary>
    /// 对应 VFilter16i_C：亮度宏块内部三条垂直边界滤波。
    /// </summary>
    public static void VFilter16i(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            off += 4 * stride;
            FilterLoop(y, off, stride, 1, 16, thresh, ithresh, hevThresh, 4);
        }
    }

    /// <summary>
    /// 对应 HFilter16i_C：亮度宏块内部三条水平边界滤波。
    /// </summary>
    public static void HFilter16i(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            off += 4;
            FilterLoop(y, off, 1, stride, 16, thresh, ithresh, hevThresh, 4);
        }
    }

    /// <summary>
    /// 对应 VFilter8_C 的色度垂直外边界滤波。
    /// C 版本一次接收 u、v 两个指针并分别滤波；本实现每次调用处理一个色度平面，
    /// 调用方分别以 U、V 平面（同一或不同缓冲区）调用一次，操作序列与 C 完全一致。
    /// </summary>
    public static void VFilter8(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop(uv, off, stride, 1, 8, thresh, ithresh, hevThresh, 6);

    /// <summary>
    /// 对应 HFilter8_C 的色度水平外边界滤波（单次处理一个色度平面，同 VFilter8 说明）。
    /// </summary>
    public static void HFilter8(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop(uv, off, 1, stride, 8, thresh, ithresh, hevThresh, 6);

    /// <summary>
    /// 对应 VFilter8i_C 的色度垂直内边界滤波（单次处理一个色度平面，同 VFilter8 说明）。
    /// </summary>
    public static void VFilter8i(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop(uv, off + 4 * stride, stride, 1, 8, thresh, ithresh, hevThresh, 4);

    /// <summary>
    /// 对应 HFilter8i_C 的色度水平内边界滤波（单次处理一个色度平面，同 VFilter8 说明）。
    /// </summary>
    public static void HFilter8i(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
        => FilterLoop(uv, off + 4, 1, stride, 8, thresh, ithresh, hevThresh, 4);
}
