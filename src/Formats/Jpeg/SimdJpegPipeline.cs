using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
// Cross-platform: no platform-specific intrinsics

namespace SharpImageConverter.Formats.Jpeg;

internal static class SimdJpegPipeline
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;
    private const int Pass1Shift = ConstBits - Pass1Bits;
    private const int Pass2Shift = ConstBits + Pass1Bits + 3;

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
    private static void Idct8ElementsSIMD2Pass(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
                                               ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7,
                                               int PassShift, Vector128<int> half)
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
        
        Idct8Core32Pass(ref i0l, ref i1l, ref i2l, ref i3l, ref i4l, ref i5l, ref i6l, ref i7l, PassShift, half);
        Idct8Core32Pass(ref i0h, ref i1h, ref i2h, ref i3h, ref i4h, ref i5h, ref i6h, ref i7h, PassShift, half);

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
    private static void Idct8Core32Pass(ref Vector128<int> v0, ref Vector128<int> v1, ref Vector128<int> v2, ref Vector128<int> v3,
                                         ref Vector128<int> v4, ref Vector128<int> v5, ref Vector128<int> v6, ref Vector128<int> v7,
                                         int PassShift, Vector128<int> half)
    {
        Vector128<int> tmp0 = (v0 + v4) << ConstBits;        
        Vector128<int> tmp1 = (v0 - v4) << ConstBits;

        Vector128<int> z1 = (v2 + v6) * Fix_0_541196100;
        Vector128<int> tmp2 = (z1 + v6 * -Fix_1_847759065);
        Vector128<int> tmp3 = (z1 + v2 * Fix_0_765366865);

        Vector128<int> tmp10 = (tmp0 + tmp3);
        Vector128<int> tmp13 = (tmp0 - tmp3);
        Vector128<int> tmp11 = (tmp1 + tmp2);
        Vector128<int> tmp12 = (tmp1 - tmp2);

        Vector128<int> tmp0o = v7;
        Vector128<int> tmp1o = v5;
        Vector128<int> tmp2o = v3;
        Vector128<int> tmp3o = v1;

        Vector128<int> z1o = (tmp0o + tmp3o);
        Vector128<int> z2o = (tmp1o + tmp2o);
        Vector128<int> z3o = (tmp0o + tmp2o);
        Vector128<int> z4o = (tmp1o + tmp3o);
        Vector128<int> z5o = (z3o + z4o) * Fix_1_175875602;

        tmp0o = tmp0o * Fix_0_298631336;
        tmp1o = tmp1o * Fix_2_053119869;
        tmp2o = tmp2o * Fix_3_072711026;
        tmp3o = tmp3o * Fix_1_501321110;
        z1o = z1o * -Fix_0_899976223;
        z2o = z2o * -Fix_2_562915447;
        z3o = (z3o * -Fix_1_961570560 + z5o);
        z4o = (z4o * -Fix_0_390180644 + z5o);

        tmp0o = tmp0o + (z1o + z3o);
        tmp1o = tmp1o + (z2o + z4o);
        tmp2o = tmp2o + (z2o + z3o);
        tmp3o = tmp3o + (z1o + z4o);
        v0 = Descale32Pass((tmp10 + tmp3o), PassShift, half);
        v7 = Descale32Pass((tmp10 - tmp3o), PassShift, half);
        v1 = Descale32Pass((tmp11 + tmp2o), PassShift, half);
        v6 = Descale32Pass((tmp11 - tmp2o), PassShift, half);
        v2 = Descale32Pass((tmp12 + tmp1o), PassShift, half);
        v5 = Descale32Pass((tmp12 - tmp1o), PassShift, half);
        v3 = Descale32Pass((tmp13 + tmp0o), PassShift, half);
        v4 = Descale32Pass((tmp13 - tmp0o), PassShift, half);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Descale32Pass(Vector128<int> v,int PassShift, Vector128<int> half)
    {
        return (v + half) >> PassShift;
    }


    // Shuffle masks for emulating UnpackLow/High on shorts (2-byte elements)
    // Each mask selects bytes from one source; OR combines two sources.
    // Byte value >= 128 → zero in output (Vector128.Shuffle semantics)
    private static readonly Vector128<byte> ShortUnpackLowMaskA = Vector128.Create(
        (byte)0, 1, 0x80, 0x80, 2, 3, 0x80, 0x80, 4, 5, 0x80, 0x80, 6, 7, 0x80, 0x80);
    private static readonly Vector128<byte> ShortUnpackLowMaskB = Vector128.Create(
        (byte)0x80, 0x80, 0, 1, 0x80, 0x80, 2, 3, 0x80, 0x80, 4, 5, 0x80, 0x80, 6, 7);
    private static readonly Vector128<byte> ShortUnpackHighMaskA = Vector128.Create(
        (byte)8, 9, 0x80, 0x80, 10, 11, 0x80, 0x80, 12, 13, 0x80, 0x80, 14, 15, 0x80, 0x80);
    private static readonly Vector128<byte> ShortUnpackHighMaskB = Vector128.Create(
        (byte)0x80, 0x80, 8, 9, 0x80, 0x80, 10, 11, 0x80, 0x80, 12, 13, 0x80, 0x80, 14, 15);

    // Masks for int32 UnpackLow/High (4-byte elements)
    private static readonly Vector128<byte> IntUnpackLowMaskA = Vector128.Create(
        (byte)0, 1, 2, 3, 0x80, 0x80, 0x80, 0x80, 4, 5, 6, 7, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> IntUnpackLowMaskB = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 0x80, 0x80, 0x80, 0x80, 4, 5, 6, 7);
    private static readonly Vector128<byte> IntUnpackHighMaskA = Vector128.Create(
        (byte)8, 9, 10, 11, 0x80, 0x80, 0x80, 0x80, 12, 13, 14, 15, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> IntUnpackHighMaskB = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 8, 9, 10, 11, 0x80, 0x80, 0x80, 0x80, 12, 13, 14, 15);

    // Masks for int64 UnpackLow/High (8-byte elements) — only 2 elements per vector
    private static readonly Vector128<byte> LongUnpackLowMaskA = Vector128.Create(
        (byte)0, 1, 2, 3, 4, 5, 6, 7, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> LongUnpackLowMaskB = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 1, 2, 3, 4, 5, 6, 7);
    private static readonly Vector128<byte> LongUnpackHighMaskA = Vector128.Create(
        (byte)8, 9, 10, 11, 12, 13, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> LongUnpackHighMaskB = Vector128.Create(
        (byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 8, 9, 10, 11, 12, 13, 14, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> InterleaveLowShort(Vector128<short> a, Vector128<short> b)
    {
        var aS = Vector128.Shuffle(a.AsByte(), ShortUnpackLowMaskA);
        var bS = Vector128.Shuffle(b.AsByte(), ShortUnpackLowMaskB);
        return (aS | bS).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> InterleaveHighShort(Vector128<short> a, Vector128<short> b)
    {
        var aS = Vector128.Shuffle(a.AsByte(), ShortUnpackHighMaskA);
        var bS = Vector128.Shuffle(b.AsByte(), ShortUnpackHighMaskB);
        return (aS | bS).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> InterleaveLowInt(Vector128<int> a, Vector128<int> b)
    {
        var aS = Vector128.Shuffle(a.AsByte(), IntUnpackLowMaskA);
        var bS = Vector128.Shuffle(b.AsByte(), IntUnpackLowMaskB);
        return (aS | bS).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> InterleaveHighInt(Vector128<int> a, Vector128<int> b)
    {
        var aS = Vector128.Shuffle(a.AsByte(), IntUnpackHighMaskA);
        var bS = Vector128.Shuffle(b.AsByte(), IntUnpackHighMaskB);
        return (aS | bS).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> InterleaveLowLong(Vector128<long> a, Vector128<long> b)
    {
        var aS = Vector128.Shuffle(a.AsByte(), LongUnpackLowMaskA);
        var bS = Vector128.Shuffle(b.AsByte(), LongUnpackLowMaskB);
        return (aS | bS).AsInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> InterleaveHighLong(Vector128<long> a, Vector128<long> b)
    {
        var aS = Vector128.Shuffle(a.AsByte(), LongUnpackHighMaskA);
        var bS = Vector128.Shuffle(b.AsByte(), LongUnpackHighMaskB);
        return (aS | bS).AsInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
                                     ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        // Level 1: interleave short pairs (same algorithm as SSE2 but using Shuffle + OR)
        Vector128<short> t0 = InterleaveLowShort(v0, v1);
        Vector128<short> t1 = InterleaveHighShort(v0, v1);
        Vector128<short> t2 = InterleaveLowShort(v2, v3);
        Vector128<short> t3 = InterleaveHighShort(v2, v3);
        Vector128<short> t4 = InterleaveLowShort(v4, v5);
        Vector128<short> t5 = InterleaveHighShort(v4, v5);
        Vector128<short> t6 = InterleaveLowShort(v6, v7);
        Vector128<short> t7 = InterleaveHighShort(v6, v7);

        // Level 2: interleave int pairs
        Vector128<int> q0 = InterleaveLowInt(t0.AsInt32(), t2.AsInt32());
        Vector128<int> q1 = InterleaveHighInt(t0.AsInt32(), t2.AsInt32());
        Vector128<int> q2 = InterleaveLowInt(t1.AsInt32(), t3.AsInt32());
        Vector128<int> q3 = InterleaveHighInt(t1.AsInt32(), t3.AsInt32());
        Vector128<int> q4 = InterleaveLowInt(t4.AsInt32(), t6.AsInt32());
        Vector128<int> q5 = InterleaveHighInt(t4.AsInt32(), t6.AsInt32());
        Vector128<int> q6 = InterleaveLowInt(t5.AsInt32(), t7.AsInt32());
        Vector128<int> q7 = InterleaveHighInt(t5.AsInt32(), t7.AsInt32());

        // Level 3: interleave long pairs, reinterpret as short
        v0 = InterleaveLowLong(q0.AsInt64(), q4.AsInt64()).AsInt16();
        v1 = InterleaveHighLong(q0.AsInt64(), q4.AsInt64()).AsInt16();
        v2 = InterleaveLowLong(q1.AsInt64(), q5.AsInt64()).AsInt16();
        v3 = InterleaveHighLong(q1.AsInt64(), q5.AsInt64()).AsInt16();
        v4 = InterleaveLowLong(q2.AsInt64(), q6.AsInt64()).AsInt16();
        v5 = InterleaveHighLong(q2.AsInt64(), q6.AsInt64()).AsInt16();
        v6 = InterleaveLowLong(q3.AsInt64(), q7.AsInt64()).AsInt16();
        v7 = InterleaveHighLong(q3.AsInt64(), q7.AsInt64()).AsInt16();
    }

    // StoreRowSse2 was unused — removed.

    // --- Full-link vectorized methods ---

    internal struct Block8x8Vectors
    {
        public Vector128<short> V0, V1, V2, V3, V4, V5, V6, V7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe static void TransformAndConvertYCbCr8x8(
        ReadOnlySpan<short> yCoef, ushort[] yQuant,
        ReadOnlySpan<short> cbCoef, ushort[] cbQuant,
        ReadOnlySpan<short> crCoef, ushort[] crQuant,
        Span<byte> dest, int stride)
    {
        Block8x8Vectors y = Idct8x8ToVectors(yCoef, yQuant);
        Block8x8Vectors cb = Idct8x8ToVectors(cbCoef, cbQuant);
        Block8x8Vectors cr = Idct8x8ToVectors(crCoef, crQuant);

        // 在最外层只获取一次核心指针，消除后续所有 Slice 损耗
        byte* pDestBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(dest));
        //ConvertRowYCbCrToRgb(yL, cbL, crL, pRowDest);
        ConvertRowYCbCrToRgb(y.V0, cb.V0, cr.V0, pDestBase + 0 * stride);
        ConvertRowYCbCrToRgb(y.V1, cb.V1, cr.V1, pDestBase + 1 * stride);
        ConvertRowYCbCrToRgb(y.V2, cb.V2, cr.V2, pDestBase + 2 * stride);
        ConvertRowYCbCrToRgb(y.V3, cb.V3, cr.V3, pDestBase + 3 * stride);
        ConvertRowYCbCrToRgb(y.V4, cb.V4, cr.V4, pDestBase + 4 * stride);
        ConvertRowYCbCrToRgb(y.V5, cb.V5, cr.V5, pDestBase + 5 * stride);
        ConvertRowYCbCrToRgb(y.V6, cb.V6, cr.V6, pDestBase + 6 * stride);
        ConvertRowYCbCrToRgb(y.V7, cb.V7, cr.V7, pDestBase + 7 * stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void TransformAndConvertYCbCr420(
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

        // 在最外层只获取一次核心指针，消除后续所有 Slice 损耗
        byte* pDestBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(dest));

        // Top 8 rows of MCU (VY0 and VY1)
        UpsampleAndConvert(vy0.V0, vy1.V0, vcb.V0, vcr.V0, pDestBase + 0 * stride);
        UpsampleAndConvert(vy0.V1, vy1.V1, vcb.V0, vcr.V0, pDestBase + 1 * stride);
        UpsampleAndConvert(vy0.V2, vy1.V2, vcb.V1, vcr.V1, pDestBase + 2 * stride);
        UpsampleAndConvert(vy0.V3, vy1.V3, vcb.V1, vcr.V1, pDestBase + 3 * stride);
        UpsampleAndConvert(vy0.V4, vy1.V4, vcb.V2, vcr.V2, pDestBase + 4 * stride);
        UpsampleAndConvert(vy0.V5, vy1.V5, vcb.V2, vcr.V2, pDestBase + 5 * stride);
        UpsampleAndConvert(vy0.V6, vy1.V6, vcb.V3, vcr.V3, pDestBase + 6 * stride);
        UpsampleAndConvert(vy0.V7, vy1.V7, vcb.V3, vcr.V3, pDestBase + 7 * stride);

        // Bottom 8 rows of MCU (VY2 and VY3)
        UpsampleAndConvert(vy2.V0, vy3.V0, vcb.V4, vcr.V4, pDestBase + 8 * stride);
        UpsampleAndConvert(vy2.V1, vy3.V1, vcb.V4, vcr.V4, pDestBase + 9 * stride);
        UpsampleAndConvert(vy2.V2, vy3.V2, vcb.V5, vcr.V5, pDestBase + 10 * stride);
        UpsampleAndConvert(vy2.V3, vy3.V3, vcb.V5, vcr.V5, pDestBase + 11 * stride);
        UpsampleAndConvert(vy2.V4, vy3.V4, vcb.V6, vcr.V6, pDestBase + 12 * stride);
        UpsampleAndConvert(vy2.V5, vy3.V5, vcb.V6, vcr.V6, pDestBase + 13 * stride);
        UpsampleAndConvert(vy2.V6, vy3.V6, vcb.V7, vcr.V7, pDestBase + 14 * stride);
        UpsampleAndConvert(vy2.V7, vy3.V7, vcb.V7, vcr.V7, pDestBase + 15 * stride);
    }

    // Shuffle mask for duplicating low 4 shorts (each 2-byte element repeated)
    private static readonly Vector128<byte> DupLowShortsMask = Vector128.Create(
        (byte)0, 1, 0, 1, 2, 3, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7);
    // Shuffle mask for duplicating high 4 shorts (each 2-byte element repeated)
    private static readonly Vector128<byte> DupHighShortsMask = Vector128.Create(
        (byte)8, 9, 8, 9, 10, 11, 10, 11, 12, 13, 12, 13, 14, 15, 14, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void UpsampleAndConvert(Vector128<short> yL, Vector128<short> yR, Vector128<short> cbRow, Vector128<short> crRow, byte* pRowDest)
    {
        // 水平上采样：用 Shuffle 将 8 个像素的色度伸为两组 8 个像素（重复每个元素）
        Vector128<short> cbL = Vector128.Shuffle(cbRow.AsByte(), DupLowShortsMask).AsInt16();
        Vector128<short> cbR = Vector128.Shuffle(cbRow.AsByte(), DupHighShortsMask).AsInt16();
        Vector128<short> crL = Vector128.Shuffle(crRow.AsByte(), DupLowShortsMask).AsInt16();
        Vector128<short> crR = Vector128.Shuffle(crRow.AsByte(), DupHighShortsMask).AsInt16();

        ConvertRowYCbCrToRgb(yL, cbL, crL, pRowDest);
        ConvertRowYCbCrToRgb(yR, cbR, crR, pRowDest + 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Block8x8Vectors Idct8x8ToVectors(ReadOnlySpan<short> coefficients, ushort[] quant)
    {
        fixed (short* cPtr = coefficients)
        fixed (ushort* qPtr = quant)
        {
            short* sqPtr = (short*)qPtr;
            Vector128<short> v0 = Vector128.Load(cPtr) * Vector128.Load(sqPtr);
            Vector128<short> v1 = Vector128.Load(cPtr + 8) * Vector128.Load(sqPtr + 8);
            Vector128<short> v2 = Vector128.Load(cPtr + 16) * Vector128.Load(sqPtr + 16);
            Vector128<short> v3 = Vector128.Load(cPtr + 24) * Vector128.Load(sqPtr + 24);
            Vector128<short> v4 = Vector128.Load(cPtr + 32) * Vector128.Load(sqPtr + 32);
            Vector128<short> v5 = Vector128.Load(cPtr + 40) * Vector128.Load(sqPtr + 40);
            Vector128<short> v6 = Vector128.Load(cPtr + 48) * Vector128.Load(sqPtr + 48);
            Vector128<short> v7 = Vector128.Load(cPtr + 56) * Vector128.Load(sqPtr + 56);
            Vector128<int> half = Vector128.Create(1 << (Pass1Shift - 1));
            // Pass 1: IDCT on columns
            Idct8ElementsSIMD2Pass(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, Pass1Shift, half);
            // Transpose to make rows vertical
            Transpose8x8(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
            // Pass 2: IDCT on original rows
            half = Vector128.Create(1 << (Pass2Shift - 1));
            Idct8ElementsSIMD2Pass(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, Pass2Shift, half);
            // Transpose back to row-major
            Transpose8x8(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

            return new Block8x8Vectors { V0 = v0, V1 = v1, V2 = v2, V3 = v3, V4 = v4, V5 = v5, V6 = v6, V7 = v7 };
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ConvertRowYCbCrToRgb(Vector128<short> y, Vector128<short> cb, Vector128<short> cr, byte* pDest)
    {
        Vector128<short> bias128 = Vector128.Create((short)128);
        y = (y + bias128);

        Vector128<int> yl = Vector128.WidenLower(y);
        Vector128<int> yh = Vector128.WidenUpper(y);
        Vector128<int> cbl = Vector128.WidenLower(cb);
        Vector128<int> cbh = Vector128.WidenUpper(cb);
        Vector128<int> crl = Vector128.WidenLower(cr);
        Vector128<int> crh = Vector128.WidenUpper(cr);

        ConvertCore(yl, cbl, crl, out Vector128<int> rl, out Vector128<int> gl, out Vector128<int> bl);
        ConvertCore(yh, cbh, crh, out Vector128<int> rh, out Vector128<int> gh, out Vector128<int> bh);

        // Cross-platform: clamp int to [0, 255], then Narrow int→short→byte
        var zero = Vector128<int>.Zero;
        var c255 = Vector128.Create(255);

        rl = Vector128.Max(Vector128.Min(rl, c255), zero);
        rh = Vector128.Max(Vector128.Min(rh, c255), zero);
        gl = Vector128.Max(Vector128.Min(gl, c255), zero);
        gh = Vector128.Max(Vector128.Min(gh, c255), zero);
        bl = Vector128.Max(Vector128.Min(bl, c255), zero);
        bh = Vector128.Max(Vector128.Min(bh, c255), zero);

        // Narrow int→short, then short→byte (values already clamped, so truncation is safe)
        ulong rData = Vector128.Narrow(
            Vector128.Narrow(rl, rh), Vector128<short>.Zero).AsUInt64().GetElement(0);
        ulong gData = Vector128.Narrow(
            Vector128.Narrow(gl, gh), Vector128<short>.Zero).AsUInt64().GetElement(0);
        ulong bData = Vector128.Narrow(
            Vector128.Narrow(bl, bh), Vector128<short>.Zero).AsUInt64().GetElement(0);

        // Interleave R and G bytes into rg0 (R0,G0,R1,G1,R2,G2,R3,G3) and rg1 (R4,G4,R5,G5,R6,G6,R7,G7)
        // Each line: extract byte i from rData/gData, shift to output byte position
        ulong rg0 =
            ((rData >> 0)  & 0xFFUL)       | ((gData >> 0)  & 0xFFUL) << 8 |
            ((rData >> 8)  & 0xFFUL) << 16 | ((gData >> 8)  & 0xFFUL) << 24 |
            ((rData >> 16) & 0xFFUL) << 32 | ((gData >> 16) & 0xFFUL) << 40 |
            ((rData >> 24) & 0xFFUL) << 48 | ((gData >> 24) & 0xFFUL) << 56;

        ulong rg1 =
            ((rData >> 32) & 0xFFUL)       | ((gData >> 32) & 0xFFUL) << 8 |
            ((rData >> 40) & 0xFFUL) << 16 | ((gData >> 40) & 0xFFUL) << 24 |
            ((rData >> 48) & 0xFFUL) << 32 | ((gData >> 48) & 0xFFUL) << 40 |
            ((rData >> 56) & 0xFFUL) << 48 | ((gData >> 56) & 0xFFUL) << 56;

        // Manual bit-packing: write RGB24 (same as original, cross-platform C#)
        // Pixels 0 & 1
        *(ushort*)(pDest + 0)  = (ushort)rg0;
        *(pDest + 2)           = (byte)bData;
        *(ushort*)(pDest + 3)  = (ushort)(rg0 >> 16);
        *(pDest + 5)           = (byte)(bData >> 8);

        // Pixels 2 & 3
        *(ushort*)(pDest + 6)  = (ushort)(rg0 >> 32);
        *(pDest + 8)           = (byte)(bData >> 16);
        *(ushort*)(pDest + 9)  = (ushort)(rg0 >> 48);
        *(pDest + 11)          = (byte)(bData >> 24);

        // Pixels 4 & 5
        *(ushort*)(pDest + 12) = (ushort)rg1;
        *(pDest + 14)          = (byte)(bData >> 32);
        *(ushort*)(pDest + 15) = (ushort)(rg1 >> 16);
        *(pDest + 17)          = (byte)(bData >> 40);

        // Pixels 6 & 7
        *(ushort*)(pDest + 18) = (ushort)(rg1 >> 32);
        *(pDest + 20)          = (byte)(bData >> 48);
        *(ushort*)(pDest + 21) = (ushort)(rg1 >> 48);
        *(pDest + 23)          = (byte)(bData >> 56);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe static void ConvertRowYCbCrToRgb(Vector128<short> y, Vector128<short> cb, Vector128<short> cr, Span<byte> dest)
    {
        byte* pDest = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(dest));
        ConvertRowYCbCrToRgb(y, cb, cr, pDest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertCore(Vector128<int> y, Vector128<int> cb, Vector128<int> cr, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b)
    {
        Vector128<int> r_off = cr * Fix_1_402 >> ColorShift;
        Vector128<int> g_off = ((cb * Fix_0_34414) + (cr * Fix_0_71414)) >> ColorShift;
        Vector128<int> b_off = cb * Fix_1_772 >> ColorShift;

        r = y + r_off;
        g = y - g_off;
        b = y + b_off;
    }
}
