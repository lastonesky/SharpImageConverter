using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// 编码方向的 SIMD 实现：FDCT 与 RGB-&gt;YCbCr 色彩转换。
/// 与解码方向 <see cref="SimdJpegPipeline"/>（IDCT 与 YCbCr-&gt;RGB）互为逆过程，
/// 均基于 SSE2/SSSE3 的 128 位向量实现，并在不满足条件时由调用方回退到标量。
/// </summary>
internal static class SimdJpegEncodePipeline
{
    // ---- FDCT 常量（与 JpegEncoder 标量实现保持一致） ----
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    private const int Fix_0_298631336 = 2446;
    private const int Fix_0_390180644 = 3196;
    private const int Fix_0_541196100 = 4433;
    private const int Fix_0_765366865 = 6270;
    private const int Fix_0_899976223 = 7373;
    private const int Fix_1_175875602 = 9633;
    private const int Fix_1_501321110 = 12299;
    private const int Fix_1_847759065 = 15137;
    private const int Fix_1_961570560 = 16069;
    private const int Fix_2_053119869 = 16819;
    private const int Fix_2_562915447 = 20995;
    private const int Fix_3_072711026 = 25172;

    // ---- 色彩转换常量 ----
    // 采用 16 位定点：pmaddwd 一次算一对 R/G，pmullw 算 B 分量。
    private static readonly Vector128<short> CoeffY = Vector128.Create((short)77, (short)150, (short)77, (short)150, (short)77, (short)150, (short)77, (short)150);
    private static readonly Vector128<short> CoeffCb = Vector128.Create((short)-43, (short)-85, (short)-43, (short)-85, (short)-43, (short)-85, (short)-43, (short)-85);
    private static readonly Vector128<short> CoeffCr = Vector128.Create((short)128, (short)-107, (short)128, (short)-107, (short)128, (short)-107, (short)128, (short)-107);
    private static readonly Vector128<short> CoeffBY = Vector128.Create((short)29);
    private static readonly Vector128<short> CoeffBCb = Vector128.Create((short)128);
    private static readonly Vector128<short> CoeffBCr = Vector128.Create((short)-21);
    private static readonly Vector128<int> C2 = Vector128.Create(2);
    private static readonly Vector128<int> CConst128 = Vector128.Create(128);

    // ---- RGB 去交错掩码（8 像素 = 24 字节） ----
    // lo 覆盖字节 0..15，hi 覆盖字节 8..23；0x80 表示该通道置零。
    private static readonly Vector128<byte> MaskRlo = Vector128.Create(
        (byte)0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> MaskRhi = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> MaskGlo = Vector128.Create(
        (byte)1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> MaskGhi = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> MaskBlo = Vector128.Create(
        (byte)2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> MaskBhi = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    /// <summary>
    /// FDCT 是否可用 SIMD。可通过内部 setter 在测试中强制关闭，以校验与标量实现一致。
    /// </summary>
    internal static bool FdctSupported { get; set; } = Sse2.IsSupported;

    /// <summary>
    /// RGB-&gt;YCbCr 是否可用 SIMD（需要 SSSE3 的 pshufb / phaddd）。可通过内部 setter 在测试中强制关闭。
    /// </summary>
    internal static bool ColorSupported { get; set; } = Ssse3.IsSupported;

    // ===================== FDCT =====================

    private static readonly Vector128<int> QuantBias = Vector128.Create(1 << 19);

    /// <summary>就地完成 8x8 整数 FDCT，输入/输出均为行主序的 64 个 int（调用方保证值域在 short 内）。</summary>
    internal static void ForwardDct8x8(Span<int> block)
    {
        Vector128<short> v0 = LoadRow(block, 0);
        Vector128<short> v1 = LoadRow(block, 8);
        Vector128<short> v2 = LoadRow(block, 16);
        Vector128<short> v3 = LoadRow(block, 24);
        Vector128<short> v4 = LoadRow(block, 32);
        Vector128<short> v5 = LoadRow(block, 40);
        Vector128<short> v6 = LoadRow(block, 48);
        Vector128<short> v7 = LoadRow(block, 56);

        FdctTransform(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

        StoreRow(block, 0, v0);
        StoreRow(block, 8, v1);
        StoreRow(block, 16, v2);
        StoreRow(block, 24, v3);
        StoreRow(block, 32, v4);
        StoreRow(block, 40, v5);
        StoreRow(block, 48, v6);
        StoreRow(block, 56, v7);
    }

    /// <summary>
    /// 融合 FDCT 与量化：变换结果在写回时直接用倒数乘完成量化，省去一次对 64 系数的独立遍历。
    /// </summary>
    internal static void ForwardDctQuantize8x8(Span<int> block, int[] quantRecip)
    {
        Vector128<short> v0 = LoadRow(block, 0);
        Vector128<short> v1 = LoadRow(block, 8);
        Vector128<short> v2 = LoadRow(block, 16);
        Vector128<short> v3 = LoadRow(block, 24);
        Vector128<short> v4 = LoadRow(block, 32);
        Vector128<short> v5 = LoadRow(block, 40);
        Vector128<short> v6 = LoadRow(block, 48);
        Vector128<short> v7 = LoadRow(block, 56);

        FdctTransform(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

        StoreRowQuantized(block, quantRecip, 0, v0);
        StoreRowQuantized(block, quantRecip, 8, v1);
        StoreRowQuantized(block, quantRecip, 16, v2);
        StoreRowQuantized(block, quantRecip, 24, v3);
        StoreRowQuantized(block, quantRecip, 32, v4);
        StoreRowQuantized(block, quantRecip, 40, v5);
        StoreRowQuantized(block, quantRecip, 48, v6);
        StoreRowQuantized(block, quantRecip, 56, v7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FdctTransform(
        ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
        ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        // 先转置，让“按行”的横向变换落到向量维度上；随后恢复行列。
        Transpose8x8(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
        Fdct8ElementsSimd2Pass(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, ConstBits - Pass1Bits, evenLeft: true);
        Transpose8x8(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

        // 纵向（列）变换，结果已是行主序。
        Fdct8ElementsSimd2Pass(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, ConstBits + Pass1Bits, evenLeft: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> LoadRow(Span<int> block, int offset)
    {
        ref int r = ref MemoryMarshal.GetReference(block);
        Vector128<int> lo = Vector128.LoadUnsafe(ref r, (nuint)offset);
        Vector128<int> hi = Vector128.LoadUnsafe(ref r, (nuint)(offset + 4));
        return Vector128.Narrow(lo, hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(Span<int> block, int offset, Vector128<short> v)
    {
        ref int r = ref MemoryMarshal.GetReference(block);
        Vector128.WidenLower(v).StoreUnsafe(ref r, (nuint)offset);
        Vector128.WidenUpper(v).StoreUnsafe(ref r, (nuint)(offset + 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRowQuantized(Span<int> block, int[] quantRecip, int offset, Vector128<short> v)
    {
        ref int r = ref MemoryMarshal.GetReference(block);
        ref int q = ref MemoryMarshal.GetReference(quantRecip.AsSpan());
        Vector128<int> lo = Quantize(Vector128.WidenLower(v), Vector128.LoadUnsafe(ref q, (nuint)offset));
        Vector128<int> hi = Quantize(Vector128.WidenUpper(v), Vector128.LoadUnsafe(ref q, (nuint)(offset + 4)));
        lo.StoreUnsafe(ref r, (nuint)offset);
        hi.StoreUnsafe(ref r, (nuint)(offset + 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Quantize(Vector128<int> coeff, Vector128<int> recip)
    {
        Vector128<int> negative = Vector128.LessThan(coeff, Vector128<int>.Zero);
        Vector128<int> abs = Vector128.ConditionalSelect(negative, -coeff, coeff);
        Vector128<int> scaled = (abs * recip + QuantBias) >> 20;
        return Vector128.ConditionalSelect(negative, -scaled, scaled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fdct8ElementsSimd2Pass(
        ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
        ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7,
        int oddShift, bool evenLeft)
    {
        Vector128<int> i0l = Vector128.WidenLower(v0);
        Vector128<int> i0h = Vector128.WidenUpper(v0);
        Vector128<int> i1l = Vector128.WidenLower(v1);
        Vector128<int> i1h = Vector128.WidenUpper(v1);
        Vector128<int> i2l = Vector128.WidenLower(v2);
        Vector128<int> i2h = Vector128.WidenUpper(v2);
        Vector128<int> i3l = Vector128.WidenLower(v3);
        Vector128<int> i3h = Vector128.WidenUpper(v3);
        Vector128<int> i4l = Vector128.WidenLower(v4);
        Vector128<int> i4h = Vector128.WidenUpper(v4);
        Vector128<int> i5l = Vector128.WidenLower(v5);
        Vector128<int> i5h = Vector128.WidenUpper(v5);
        Vector128<int> i6l = Vector128.WidenLower(v6);
        Vector128<int> i6h = Vector128.WidenUpper(v6);
        Vector128<int> i7l = Vector128.WidenLower(v7);
        Vector128<int> i7h = Vector128.WidenUpper(v7);

        Fdct8Core32Pass(ref i0l, ref i1l, ref i2l, ref i3l, ref i4l, ref i5l, ref i6l, ref i7l, oddShift, evenLeft);
        Fdct8Core32Pass(ref i0h, ref i1h, ref i2h, ref i3h, ref i4h, ref i5h, ref i6h, ref i7h, oddShift, evenLeft);

        v0 = Vector128.Narrow(i0l, i0h);
        v1 = Vector128.Narrow(i1l, i1h);
        v2 = Vector128.Narrow(i2l, i2h);
        v3 = Vector128.Narrow(i3l, i3h);
        v4 = Vector128.Narrow(i4l, i4h);
        v5 = Vector128.Narrow(i5l, i5h);
        v6 = Vector128.Narrow(i6l, i6h);
        v7 = Vector128.Narrow(i7l, i7h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fdct8Core32Pass(
        ref Vector128<int> v0, ref Vector128<int> v1, ref Vector128<int> v2, ref Vector128<int> v3,
        ref Vector128<int> v4, ref Vector128<int> v5, ref Vector128<int> v6, ref Vector128<int> v7,
        int oddShift, bool evenLeft)
    {
        Vector128<int> tmp0 = v0 + v7;
        Vector128<int> tmp7 = v0 - v7;
        Vector128<int> tmp1 = v1 + v6;
        Vector128<int> tmp6 = v1 - v6;
        Vector128<int> tmp2 = v2 + v5;
        Vector128<int> tmp5 = v2 - v5;
        Vector128<int> tmp3 = v3 + v4;
        Vector128<int> tmp4 = v3 - v4;

        Vector128<int> tmp10 = tmp0 + tmp3;
        Vector128<int> tmp13 = tmp0 - tmp3;
        Vector128<int> tmp11 = tmp1 + tmp2;
        Vector128<int> tmp12 = tmp1 - tmp2;

        Vector128<int> z1 = (tmp12 + tmp13) * Vector128.Create(Fix_0_541196100);

        Vector128<int> o0;
        Vector128<int> o4;
        if (evenLeft)
        {
            o0 = (tmp10 + tmp11) << Pass1Bits;
            o4 = (tmp10 - tmp11) << Pass1Bits;
        }
        else
        {
            o0 = Descale(tmp10 + tmp11, Pass1Bits);
            o4 = Descale(tmp10 - tmp11, Pass1Bits);
        }
        Vector128<int> o2 = Descale(z1 + tmp13 * Vector128.Create(Fix_0_765366865), oddShift);
        Vector128<int> o6 = Descale(z1 + tmp12 * Vector128.Create(-Fix_1_847759065), oddShift);

        Vector128<int> z11 = tmp4 + tmp7;
        Vector128<int> z12 = tmp5 + tmp6;
        Vector128<int> z13 = tmp4 + tmp6;
        Vector128<int> z14 = tmp5 + tmp7;
        Vector128<int> z15 = (z13 + z14) * Vector128.Create(Fix_1_175875602);

        Vector128<int> m4 = tmp4 * Vector128.Create(Fix_0_298631336);
        Vector128<int> m5 = tmp5 * Vector128.Create(Fix_2_053119869);
        Vector128<int> m6 = tmp6 * Vector128.Create(Fix_3_072711026);
        Vector128<int> m7 = tmp7 * Vector128.Create(Fix_1_501321110);
        Vector128<int> s11 = z11 * Vector128.Create(-Fix_0_899976223);
        Vector128<int> s12 = z12 * Vector128.Create(-Fix_2_562915447);
        Vector128<int> s13 = z13 * Vector128.Create(-Fix_1_961570560) + z15;
        Vector128<int> s14 = z14 * Vector128.Create(-Fix_0_390180644) + z15;

        v7 = Descale(m4 + s11 + s13, oddShift);
        v5 = Descale(m5 + s12 + s14, oddShift);
        v3 = Descale(m6 + s12 + s13, oddShift);
        v1 = Descale(m7 + s11 + s14, oddShift);
        v0 = o0;
        v4 = o4;
        v2 = o2;
        v6 = o6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Descale(Vector128<int> v, int shift)
        => (v + Vector128.Create(1 << (shift - 1))) >> shift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(
        ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
        ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        Vector128<short> t0 = Sse2.UnpackLow(v0, v1);
        Vector128<short> t1 = Sse2.UnpackHigh(v0, v1);
        Vector128<short> t2 = Sse2.UnpackLow(v2, v3);
        Vector128<short> t3 = Sse2.UnpackHigh(v2, v3);
        Vector128<short> t4 = Sse2.UnpackLow(v4, v5);
        Vector128<short> t5 = Sse2.UnpackHigh(v4, v5);
        Vector128<short> t6 = Sse2.UnpackLow(v6, v7);
        Vector128<short> t7 = Sse2.UnpackHigh(v6, v7);

        Vector128<int> q0 = Sse2.UnpackLow(t0.AsInt32(), t2.AsInt32());
        Vector128<int> q1 = Sse2.UnpackHigh(t0.AsInt32(), t2.AsInt32());
        Vector128<int> q2 = Sse2.UnpackLow(t1.AsInt32(), t3.AsInt32());
        Vector128<int> q3 = Sse2.UnpackHigh(t1.AsInt32(), t3.AsInt32());
        Vector128<int> q4 = Sse2.UnpackLow(t4.AsInt32(), t6.AsInt32());
        Vector128<int> q5 = Sse2.UnpackHigh(t4.AsInt32(), t6.AsInt32());
        Vector128<int> q6 = Sse2.UnpackLow(t5.AsInt32(), t7.AsInt32());
        Vector128<int> q7 = Sse2.UnpackHigh(t5.AsInt32(), t7.AsInt32());

        v0 = Sse2.UnpackLow(q0.AsInt64(), q4.AsInt64()).AsInt16();
        v1 = Sse2.UnpackHigh(q0.AsInt64(), q4.AsInt64()).AsInt16();
        v2 = Sse2.UnpackLow(q1.AsInt64(), q5.AsInt64()).AsInt16();
        v3 = Sse2.UnpackHigh(q1.AsInt64(), q5.AsInt64()).AsInt16();
        v4 = Sse2.UnpackLow(q2.AsInt64(), q6.AsInt64()).AsInt16();
        v5 = Sse2.UnpackHigh(q2.AsInt64(), q6.AsInt64()).AsInt16();
        v6 = Sse2.UnpackLow(q3.AsInt64(), q7.AsInt64()).AsInt16();
        v7 = Sse2.UnpackHigh(q3.AsInt64(), q7.AsInt64()).AsInt16();
    }

    // ===================== RGB -> YCbCr =====================

    /// <summary>4:4:4，8x8 全块 RGB -&gt; Y/Cb/Cr（均已去 128 偏置）。调用方需保证整块落在图像内。</summary>
    internal static unsafe void RgbToYCbCr444(byte[] rgb, int width, int baseX, int baseY, Span<int> y, Span<int> cb, Span<int> cr)
    {
        fixed (byte* p = rgb)
        {
            byte* basePtr = p + (long)baseY * width * 3 + (long)baseX * 3;
            int stride = width * 3;
            for (int r = 0; r < 8; r++)
            {
                Convert8Pixels(basePtr + (long)r * stride,
                    out Vector128<int> yl, out Vector128<int> yh,
                    out Vector128<int> cbl, out Vector128<int> cbh,
                    out Vector128<int> crl, out Vector128<int> crh);

                Store4(y, r * 8, yl, yh);
                Store4(cb, r * 8, cbl - CConst128, cbh - CConst128);
                Store4(cr, r * 8, crl - CConst128, crh - CConst128);
            }
        }
    }

    /// <summary>4:2:0，16x16 MCU RGB -&gt; 4 个 Y 块 + 1 个 Cb/Cr 块（2x2 均值，去 128 偏置）。调用方需保证整块落在图像内。</summary>
    internal static unsafe void RgbToYCbCr420(
        byte[] rgb, int width, int baseX, int baseY,
        Span<int> y00, Span<int> y10, Span<int> y01, Span<int> y11, Span<int> cb, Span<int> cr)
    {
        fixed (byte* p = rgb)
        {
            byte* basePtr = p + (long)baseY * width * 3 + (long)baseX * 3;
            int stride = width * 3;

            Vector128<int> cbAccL = Vector128<int>.Zero, cbAccR = Vector128<int>.Zero;
            Vector128<int> crAccL = Vector128<int>.Zero, crAccR = Vector128<int>.Zero;

            for (int r = 0; r < 16; r++)
            {
                Span<int> yLeft = r < 8 ? y00 : y01;
                Span<int> yRight = r < 8 ? y10 : y11;
                int yRow = (r & 7) * 8;
                bool evenRow = (r & 1) == 0;
                byte* rowPtr = basePtr + (long)r * stride;

                Convert8Pixels(rowPtr,
                    out Vector128<int> yl, out Vector128<int> yh,
                    out Vector128<int> cbl, out Vector128<int> cbh,
                    out Vector128<int> crl, out Vector128<int> crh);
                Store4(yLeft, yRow, yl, yh);
                Vector128<int> cbPairL = Ssse3.HorizontalAdd(cbl, cbh);
                Vector128<int> crPairL = Ssse3.HorizontalAdd(crl, crh);

                Convert8Pixels(rowPtr + 24,
                    out yl, out yh,
                    out cbl, out cbh,
                    out crl, out crh);
                Store4(yRight, yRow, yl, yh);
                Vector128<int> cbPairR = Ssse3.HorizontalAdd(cbl, cbh);
                Vector128<int> crPairR = Ssse3.HorizontalAdd(crl, crh);

                if (evenRow)
                {
                    cbAccL = cbPairL;
                    crAccL = crPairL;
                    cbAccR = cbPairR;
                    crAccR = crPairR;
                }
                else
                {
                    cbAccL += cbPairL;
                    crAccL += crPairL;
                    cbAccR += cbPairR;
                    crAccR += crPairR;

                    int chromaRow = (r >> 1) * 8;
                    (((cbAccL + C2) >> 2) - CConst128).CopyTo(cb.Slice(chromaRow, 4));
                    (((cbAccR + C2) >> 2) - CConst128).CopyTo(cb.Slice(chromaRow + 4, 4));
                    (((crAccL + C2) >> 2) - CConst128).CopyTo(cr.Slice(chromaRow, 4));
                    (((crAccR + C2) >> 2) - CConst128).CopyTo(cr.Slice(chromaRow + 4, 4));
                }
            }
        }
    }

    /// <summary>把 8 个连续像素（24 字节）转换为 Y（已去偏置）与原始 Cb/Cr（含 128 偏置）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Convert8Pixels(
        byte* src,
        out Vector128<int> yLo, out Vector128<int> yHi,
        out Vector128<int> cbLo, out Vector128<int> cbHi,
        out Vector128<int> crLo, out Vector128<int> crHi)
    {
        Vector128<byte> lo = Sse2.LoadVector128(src);
        Vector128<byte> hi = Sse2.LoadVector128(src + 8);

        Vector128<byte> rB = Sse2.Or(Ssse3.Shuffle(lo, MaskRlo), Ssse3.Shuffle(hi, MaskRhi));
        Vector128<byte> gB = Sse2.Or(Ssse3.Shuffle(lo, MaskGlo), Ssse3.Shuffle(hi, MaskGhi));
        Vector128<byte> bB = Sse2.Or(Ssse3.Shuffle(lo, MaskBlo), Ssse3.Shuffle(hi, MaskBhi));

        Vector128<short> r = Vector128.WidenLower(rB).AsInt16();
        Vector128<short> g = Vector128.WidenLower(gB).AsInt16();
        Vector128<short> b = Vector128.WidenLower(bB).AsInt16();

        Vector128<short> rgLo = Sse2.UnpackLow(r, g);
        Vector128<short> rgHi = Sse2.UnpackHigh(r, g);

        // Y = (77R + 150G + 29B) >> 8 - 128
        Vector128<int> yLoV = Sse2.MultiplyAddAdjacent(rgLo, CoeffY);
        Vector128<int> yHiV = Sse2.MultiplyAddAdjacent(rgHi, CoeffY);
        Vector128<short> bY = Sse2.MultiplyLow(b, CoeffBY);
        yLoV += Vector128.WidenLower(bY);
        yHiV += Vector128.WidenUpper(bY);
        yLo = (yLoV >> 8) - CConst128;
        yHi = (yHiV >> 8) - CConst128;

        // Cb = (-43R - 85G + 128B) >> 8 + 128
        Vector128<int> cbLoV = Sse2.MultiplyAddAdjacent(rgLo, CoeffCb);
        Vector128<int> cbHiV = Sse2.MultiplyAddAdjacent(rgHi, CoeffCb);
        Vector128<short> bCb = Sse2.MultiplyLow(b, CoeffBCb);
        cbLoV += Vector128.WidenLower(bCb);
        cbHiV += Vector128.WidenUpper(bCb);
        cbLo = (cbLoV >> 8) + CConst128;
        cbHi = (cbHiV >> 8) + CConst128;

        // Cr = (128R - 107G - 21B) >> 8 + 128
        Vector128<int> crLoV = Sse2.MultiplyAddAdjacent(rgLo, CoeffCr);
        Vector128<int> crHiV = Sse2.MultiplyAddAdjacent(rgHi, CoeffCr);
        Vector128<short> bCr = Sse2.MultiplyLow(b, CoeffBCr);
        crLoV += Vector128.WidenLower(bCr);
        crHiV += Vector128.WidenUpper(bCr);
        crLo = (crLoV >> 8) + CConst128;
        crHi = (crHiV >> 8) + CConst128;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(Span<int> dst, int offset, Vector128<int> lo, Vector128<int> hi)
    {
        lo.CopyTo(dst.Slice(offset, 4));
        hi.CopyTo(dst.Slice(offset + 4, 4));
    }
}
