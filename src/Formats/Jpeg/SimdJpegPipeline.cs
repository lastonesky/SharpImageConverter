using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Formats.Jpeg;

internal static class SimdJpegPipeline
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    // LLM algorithm constants (scaled by 1 << ConstBits)
    private static readonly int Fix_0_298631336 = 2446;
    private static readonly int Fix_0_390180644 = 3196;
    private static readonly int Fix_0_541196100 = 4433;
    private static readonly int Fix_0_765366865 = 6270;
    private static readonly int Fix_0_899976223 = 7373;
    private static readonly int Fix_1_175875602 = 9633;
    private static readonly int Fix_1_501321110 = 12299;
    private static readonly int Fix_1_847759065 = 15137;
    private static readonly int Fix_1_961570560 = 16069;
    private static readonly int Fix_2_053119869 = 16819;
    private static readonly int Fix_2_562915447 = 20995;
    private static readonly int Fix_3_072711026 = 25172;

    // YCbCr to RGB constants (fixed-point 16 bits)
    private const int ColorShift = 16;
    private static readonly int Fix_1_402 = 91881;
    private static readonly int Fix_0_34414 = 22554;
    private static readonly int Fix_0_71414 = 46802;
    private static readonly int Fix_1_772 = 116130;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transform8x8(ReadOnlySpan<short> coefficients, ushort[] quant, Span<byte> dest, int stride)
    {
        if (Sse2.IsSupported)
        {
            Transform8x8Sse2(coefficients, quant, dest, stride);
        }
        else
        {
            FastIDCT.Transform(coefficients, quant, dest, stride);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transform8x8Sse2(ReadOnlySpan<short> coefficients, ushort[] quant, Span<byte> dest, int stride)
    {
        Block8x8Vectors v = Idct8x8ToVectors(coefficients, quant);
        StoreRowSse2(v.V0, dest, 0, stride);
        StoreRowSse2(v.V1, dest, 1, stride);
        StoreRowSse2(v.V2, dest, 2, stride);
        StoreRowSse2(v.V3, dest, 3, stride);
        StoreRowSse2(v.V4, dest, 4, stride);
        StoreRowSse2(v.V5, dest, 5, stride);
        StoreRowSse2(v.V6, dest, 6, stride);
        StoreRowSse2(v.V7, dest, 7, stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct8ElementsSse2(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
                                          ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7, int shift)
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

        Idct8Core32(ref i0l, ref i1l, ref i2l, ref i3l, ref i4l, ref i5l, ref i6l, ref i7l, shift);
        Idct8Core32(ref i0h, ref i1h, ref i2h, ref i3h, ref i4h, ref i5h, ref i6h, ref i7h, shift);

        v0 = Sse2.PackSignedSaturate(i0l, i0h);
        v1 = Sse2.PackSignedSaturate(i1l, i1h);
        v2 = Sse2.PackSignedSaturate(i2l, i2h);
        v3 = Sse2.PackSignedSaturate(i3l, i3h);
        v4 = Sse2.PackSignedSaturate(i4l, i4h);
        v5 = Sse2.PackSignedSaturate(i5l, i5h);
        v6 = Sse2.PackSignedSaturate(i6l, i6h);
        v7 = Sse2.PackSignedSaturate(i7l, i7h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct8Core32(ref Vector128<int> v0, ref Vector128<int> v1, ref Vector128<int> v2, ref Vector128<int> v3,
                                    ref Vector128<int> v4, ref Vector128<int> v5, ref Vector128<int> v6, ref Vector128<int> v7, int shift)
    {
        Vector128<int> tmp0 = Sse2.Add(v0, v4);
        tmp0 = Sse2.ShiftLeftLogical(tmp0, ConstBits);
        Vector128<int> tmp1 = Sse2.Subtract(v0, v4);
        tmp1 = Sse2.ShiftLeftLogical(tmp1, ConstBits);

        Vector128<int> z1 = Multiply32(Sse2.Add(v2, v6), Fix_0_541196100);
        Vector128<int> tmp2 = Sse2.Add(z1, Multiply32(v6, -Fix_1_847759065));
        Vector128<int> tmp3 = Sse2.Add(z1, Multiply32(v2, Fix_0_765366865));

        Vector128<int> tmp10 = Sse2.Add(tmp0, tmp3);
        Vector128<int> tmp13 = Sse2.Subtract(tmp0, tmp3);
        Vector128<int> tmp11 = Sse2.Add(tmp1, tmp2);
        Vector128<int> tmp12 = Sse2.Subtract(tmp1, tmp2);

        Vector128<int> tmp0o = v7;
        Vector128<int> tmp1o = v5;
        Vector128<int> tmp2o = v3;
        Vector128<int> tmp3o = v1;

        Vector128<int> z1o = Sse2.Add(tmp0o, tmp3o);
        Vector128<int> z2o = Sse2.Add(tmp1o, tmp2o);
        Vector128<int> z3o = Sse2.Add(tmp0o, tmp2o);
        Vector128<int> z4o = Sse2.Add(tmp1o, tmp3o);
        Vector128<int> z5o = Multiply32(Sse2.Add(z3o, z4o), Fix_1_175875602);

        tmp0o = Multiply32(tmp0o, Fix_0_298631336);
        tmp1o = Multiply32(tmp1o, Fix_2_053119869);
        tmp2o = Multiply32(tmp2o, Fix_3_072711026);
        tmp3o = Multiply32(tmp3o, Fix_1_501321110);
        z1o = Multiply32(z1o, -Fix_0_899976223);
        z2o = Multiply32(z2o, -Fix_2_562915447);
        z3o = Sse2.Add(Multiply32(z3o, -Fix_1_961570560), z5o);
        z4o = Sse2.Add(Multiply32(z4o, -Fix_0_390180644), z5o);

        tmp0o = Sse2.Add(tmp0o, Sse2.Add(z1o, z3o));
        tmp1o = Sse2.Add(tmp1o, Sse2.Add(z2o, z4o));
        tmp2o = Sse2.Add(tmp2o, Sse2.Add(z2o, z3o));
        tmp3o = Sse2.Add(tmp3o, Sse2.Add(z1o, z4o));

        v0 = Descale32(Sse2.Add(tmp10, tmp3o), shift);
        v7 = Descale32(Sse2.Subtract(tmp10, tmp3o), shift);
        v1 = Descale32(Sse2.Add(tmp11, tmp2o), shift);
        v6 = Descale32(Sse2.Subtract(tmp11, tmp2o), shift);
        v2 = Descale32(Sse2.Add(tmp12, tmp1o), shift);
        v5 = Descale32(Sse2.Subtract(tmp12, tmp1o), shift);
        v3 = Descale32(Sse2.Add(tmp13, tmp0o), shift);
        v4 = Descale32(Sse2.Subtract(tmp13, tmp0o), shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Multiply32(Vector128<int> a, int b)
    {
        if (Avx2.IsSupported)
        {
            return Avx2.MultiplyLow(a, Vector128.Create(b));
        }
        Vector128<int> bVec = Vector128.Create(b);
        Vector128<long> low = Sse2.Multiply(a.AsUInt32(), bVec.AsUInt32()).AsInt64();
        Vector128<long> high = Sse2.Multiply(Sse2.ShiftRightLogical(a, 32).AsUInt32(), bVec.AsUInt32()).AsInt64();
        return Sse2.UnpackLow(Sse2.Shuffle(low.AsInt32(), 0x08), Sse2.Shuffle(high.AsInt32(), 0x08));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Descale32(Vector128<int> v, int shift)
    {
        Vector128<int> half = Vector128.Create(1 << (shift - 1));
        return Sse2.ShiftRightArithmetic(Sse2.Add(v, half), (byte)shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8Sse2(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRowSse2(Vector128<short> v, Span<byte> dest, int y, int stride)
    {
        Vector128<short> bias = Vector128.Create((short)128);
        v = Sse2.Add(v, bias);
        Vector128<byte> b = Sse2.PackUnsignedSaturate(v, v);
        Unsafe.WriteUnaligned(ref dest[y * stride], b.GetLower());
    }

    // --- Full-link vectorized methods ---

    internal struct Block8x8Vectors
    {
        public Vector128<short> V0, V1, V2, V3, V4, V5, V6, V7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TransformAndConvertYCbCr8x8(
        ReadOnlySpan<short> yCoef, ushort[] yQuant,
        ReadOnlySpan<short> cbCoef, ushort[] cbQuant,
        ReadOnlySpan<short> crCoef, ushort[] crQuant,
        Span<byte> dest, int stride)
    {
        Block8x8Vectors y = Idct8x8ToVectors(yCoef, yQuant);
        Block8x8Vectors cb = Idct8x8ToVectors(cbCoef, cbQuant);
        Block8x8Vectors cr = Idct8x8ToVectors(crCoef, crQuant);

        ConvertRowYCbCrToRgb(y.V0, cb.V0, cr.V0, dest.Slice(0 * stride));
        ConvertRowYCbCrToRgb(y.V1, cb.V1, cr.V1, dest.Slice(1 * stride));
        ConvertRowYCbCrToRgb(y.V2, cb.V2, cr.V2, dest.Slice(2 * stride));
        ConvertRowYCbCrToRgb(y.V3, cb.V3, cr.V3, dest.Slice(3 * stride));
        ConvertRowYCbCrToRgb(y.V4, cb.V4, cr.V4, dest.Slice(4 * stride));
        ConvertRowYCbCrToRgb(y.V5, cb.V5, cr.V5, dest.Slice(5 * stride));
        ConvertRowYCbCrToRgb(y.V6, cb.V6, cr.V6, dest.Slice(6 * stride));
        ConvertRowYCbCrToRgb(y.V7, cb.V7, cr.V7, dest.Slice(7 * stride));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TransformAndConvertYCbCr420(
        ReadOnlySpan<short> y0Coef, ReadOnlySpan<short> y1Coef, ReadOnlySpan<short> y2Coef, ReadOnlySpan<short> y3Coef, ushort[] yQuant,
        ReadOnlySpan<short> cbCoef, ushort[] cbQuant,
        ReadOnlySpan<short> crCoef, ushort[] crQuant,
        Span<byte> dest, int stride)
    {
        Block8x8Vectors vy0 = Idct8x8ToVectors(y0Coef, yQuant);
        Block8x8Vectors vy1 = Idct8x8ToVectors(y1Coef, yQuant);
        Block8x8Vectors vy2 = Idct8x8ToVectors(y2Coef, yQuant);
        Block8x8Vectors vy3 = Idct8x8ToVectors(y3Coef, yQuant);
        Block8x8Vectors vcb = Idct8x8ToVectors(cbCoef, cbQuant);
        Block8x8Vectors vcr = Idct8x8ToVectors(crCoef, crQuant);

        // Top 8 rows of MCU (VY0 and VY1)
        UpsampleAndConvert(vy0.V0, vy1.V0, vcb.V0, vcr.V0, dest, 0, stride);
        UpsampleAndConvert(vy0.V1, vy1.V1, vcb.V0, vcr.V0, dest, 1, stride);
        UpsampleAndConvert(vy0.V2, vy1.V2, vcb.V1, vcr.V1, dest, 2, stride);
        UpsampleAndConvert(vy0.V3, vy1.V3, vcb.V1, vcr.V1, dest, 3, stride);
        UpsampleAndConvert(vy0.V4, vy1.V4, vcb.V2, vcr.V2, dest, 4, stride);
        UpsampleAndConvert(vy0.V5, vy1.V5, vcb.V2, vcr.V2, dest, 5, stride);
        UpsampleAndConvert(vy0.V6, vy1.V6, vcb.V3, vcr.V3, dest, 6, stride);
        UpsampleAndConvert(vy0.V7, vy1.V7, vcb.V3, vcr.V3, dest, 7, stride);

        // Bottom 8 rows of MCU (VY2 and VY3)
        UpsampleAndConvert(vy2.V0, vy3.V0, vcb.V4, vcr.V4, dest, 8, stride);
        UpsampleAndConvert(vy2.V1, vy3.V1, vcb.V4, vcr.V4, dest, 9, stride);
        UpsampleAndConvert(vy2.V2, vy3.V2, vcb.V5, vcr.V5, dest, 10, stride);
        UpsampleAndConvert(vy2.V3, vy3.V3, vcb.V5, vcr.V5, dest, 11, stride);
        UpsampleAndConvert(vy2.V4, vy3.V4, vcb.V6, vcr.V6, dest, 12, stride);
        UpsampleAndConvert(vy2.V5, vy3.V5, vcb.V6, vcr.V6, dest, 13, stride);
        UpsampleAndConvert(vy2.V6, vy3.V6, vcb.V7, vcr.V7, dest, 14, stride);
        UpsampleAndConvert(vy2.V7, vy3.V7, vcb.V7, vcr.V7, dest, 15, stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpsampleAndConvert(Vector128<short> yL, Vector128<short> yR, Vector128<short> cbRow, Vector128<short> crRow, Span<byte> dest, int row, int stride)
    {
        Vector128<short> cbL = Sse2.UnpackLow(cbRow, cbRow);
        Vector128<short> cbR = Sse2.UnpackHigh(cbRow, cbRow);
        Vector128<short> crL = Sse2.UnpackLow(crRow, crRow);
        Vector128<short> crR = Sse2.UnpackHigh(crRow, crRow);

        ConvertRowYCbCrToRgb(yL, cbL, crL, dest.Slice(row * stride));
        ConvertRowYCbCrToRgb(yR, cbR, crR, dest.Slice(row * stride + 8 * 3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Block8x8Vectors Idct8x8ToVectors(ReadOnlySpan<short> coefficients, ushort[] quant)
    {
        fixed (short* cPtr = coefficients)
        fixed (ushort* qPtr = quant)
        {
            short* sqPtr = (short*)qPtr;
            Vector128<short> v0 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr), Sse2.LoadVector128(sqPtr));
            Vector128<short> v1 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 8), Sse2.LoadVector128(sqPtr + 8));
            Vector128<short> v2 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 16), Sse2.LoadVector128(sqPtr + 16));
            Vector128<short> v3 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 24), Sse2.LoadVector128(sqPtr + 24));
            Vector128<short> v4 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 32), Sse2.LoadVector128(sqPtr + 32));
            Vector128<short> v5 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 40), Sse2.LoadVector128(sqPtr + 40));
            Vector128<short> v6 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 48), Sse2.LoadVector128(sqPtr + 48));
            Vector128<short> v7 = Sse2.MultiplyLow(Sse2.LoadVector128(cPtr + 56), Sse2.LoadVector128(sqPtr + 56));

            // Pass 1: IDCT on columns
            Idct8ElementsSse2(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, ConstBits - Pass1Bits);
            // Transpose to make rows vertical
            Transpose8x8Sse2(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
            // Pass 2: IDCT on original rows
            Idct8ElementsSse2(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, ConstBits + Pass1Bits + 3);
            // Transpose back to row-major
            Transpose8x8Sse2(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

            return new Block8x8Vectors { V0 = v0, V1 = v1, V2 = v2, V3 = v3, V4 = v4, V5 = v5, V6 = v6, V7 = v7 };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertRowYCbCrToRgb(Vector128<short> y, Vector128<short> cb, Vector128<short> cr, Span<byte> dest)
    {
        Vector128<short> bias128 = Vector128.Create((short)128);
        y = Sse2.Add(y, bias128);

        Vector128<int> yl = Vector128.WidenLower(y);
        Vector128<int> yh = Vector128.WidenUpper(y);
        Vector128<int> cbl = Vector128.WidenLower(cb);
        Vector128<int> cbh = Vector128.WidenUpper(cb);
        Vector128<int> crl = Vector128.WidenLower(cr);
        Vector128<int> crh = Vector128.WidenUpper(cr);

        ConvertCore(yl, cbl, crl, out Vector128<int> rl, out Vector128<int> gl, out Vector128<int> bl);
        ConvertCore(yh, cbh, crh, out Vector128<int> rh, out Vector128<int> gh, out Vector128<int> bh);

        Vector128<short> rs = Sse2.PackSignedSaturate(rl, rh);
        Vector128<short> gs = Sse2.PackSignedSaturate(gl, gh);
        Vector128<short> bs = Sse2.PackSignedSaturate(bl, bh);

        Vector128<byte> rb = Sse2.PackUnsignedSaturate(rs, rs);
        Vector128<byte> gb = Sse2.PackUnsignedSaturate(gs, gs);
        Vector128<byte> bb = Sse2.PackUnsignedSaturate(bs, bs);

        ref byte d = ref MemoryMarshal.GetReference(dest);
        for (int i = 0; i < 8; i++)
        {
            Unsafe.Add(ref d, i * 3 + 0) = rb.GetElement(i);
            Unsafe.Add(ref d, i * 3 + 1) = gb.GetElement(i);
            Unsafe.Add(ref d, i * 3 + 2) = bb.GetElement(i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertCore(Vector128<int> y, Vector128<int> cb, Vector128<int> cr, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b)
    {
        Vector128<int> r_off = Sse2.ShiftRightArithmetic(Multiply32(cr, Fix_1_402), ColorShift);
        Vector128<int> g_off = Sse2.ShiftRightArithmetic(Sse2.Add(Multiply32(cb, Fix_0_34414), Multiply32(cr, Fix_0_71414)), ColorShift);
        Vector128<int> b_off = Sse2.ShiftRightArithmetic(Multiply32(cb, Fix_1_772), ColorShift);

        r = Sse2.Add(y, r_off);
        g = Sse2.Subtract(y, g_off);
        b = Sse2.Add(y, b_off);
    }
}
