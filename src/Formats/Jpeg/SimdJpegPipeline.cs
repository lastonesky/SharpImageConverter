using System.Runtime.CompilerServices;
using SharpImageConverter.Core;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Formats.Jpeg;

internal static class SimdJpegPipeline
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;
    private const int Pass1Shift = ConstBits - Pass1Bits;
    private const int Pass2Shift = ConstBits + Pass1Bits + 3;

    // LLM algorithm constants (scaled by 1 << ConstBits)
    // 用 const 而非 static readonly：const 参与编译期折叠，且与 FastIDCT.cs 的常量声明保持一致。
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

    // YCbCr to RGB constants (fixed-point 16 bits)
    private const int ColorShift = 16;
    private static readonly Vector128<int> ColorOneHalf = Vector128.Create(1 << 15);
    private const int Fix_1_402 = 91881;
    private const int Fix_0_34414 = 22554;
    private const int Fix_0_71414 = 46802;
    private const int Fix_1_772 = 116130;

    // 每块 / 每行都要用到的向量常量：提升到静态字段，避免在内联热路径里反复广播。
    private static readonly Vector128<int> HalfPass1 = Vector128.Create(1 << (Pass1Shift - 1));
    private static readonly Vector128<int> HalfPass2 = Vector128.Create(1 << (Pass2Shift - 1));
    private static readonly Vector128<short> Bias128 = Vector128.Create((short)128);

    // ---- RGB24 交错掩码 ----
    // 输入 rg = [R0 G0 R1 G1 R2 G2 R3 G3 R4 G4 R5 G5 R6 G6 R7 G7]（UnpackLow(r, g) 的结果）
    // 输入 b  = [B0 B1 B2 B3 B4 B5 B6 B7 0 0 0 0 0 0 0 0]
    // 目标    = R0 G0 B0 R1 G1 B1 ... R7 G7 B7 （24 字节）
    // out0（输出字节 0..15）：像素 0..4 完整 + 像素 5 的 R
    private static readonly Vector128<byte> RgbInterleaveLowFromRg = Vector128.Create(
        (byte)0, 1, 0x80, 2, 3, 0x80, 4, 5, 0x80, 6, 7, 0x80, 8, 9, 0x80, 10);
    private static readonly Vector128<byte> RgbInterleaveLowFromB = Vector128.Create(
        (byte)0x80, 0x80, 0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 4, 0x80);
    // out1（输出字节 16..23）：像素 5 的 G/B + 像素 6..7 完整；高 8 字节不用（写 8 字节）
    private static readonly Vector128<byte> RgbInterleaveHighFromRg = Vector128.Create(
        (byte)11, 0x80, 12, 13, 0x80, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbInterleaveHighFromB = Vector128.Create(
        (byte)0x80, 5, 0x80, 0x80, 6, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

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


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8Sse2(ref Vector128<short> v0, ref Vector128<short> v1, ref Vector128<short> v2, ref Vector128<short> v3,
                                         ref Vector128<short> v4, ref Vector128<short> v5, ref Vector128<short> v6, ref Vector128<short> v7)
    {
        Vector128<short> t0 = SimdCompat.UnpackLow(v0, v1);
        Vector128<short> t1 = SimdCompat.UnpackHigh(v0, v1);
        Vector128<short> t2 = SimdCompat.UnpackLow(v2, v3);
        Vector128<short> t3 = SimdCompat.UnpackHigh(v2, v3);
        Vector128<short> t4 = SimdCompat.UnpackLow(v4, v5);
        Vector128<short> t5 = SimdCompat.UnpackHigh(v4, v5);
        Vector128<short> t6 = SimdCompat.UnpackLow(v6, v7);
        Vector128<short> t7 = SimdCompat.UnpackHigh(v6, v7);

        Vector128<int> q0 = SimdCompat.UnpackLow(t0.AsInt32(), t2.AsInt32());
        Vector128<int> q1 = SimdCompat.UnpackHigh(t0.AsInt32(), t2.AsInt32());
        Vector128<int> q2 = SimdCompat.UnpackLow(t1.AsInt32(), t3.AsInt32());
        Vector128<int> q3 = SimdCompat.UnpackHigh(t1.AsInt32(), t3.AsInt32());
        Vector128<int> q4 = SimdCompat.UnpackLow(t4.AsInt32(), t6.AsInt32());
        Vector128<int> q5 = SimdCompat.UnpackHigh(t4.AsInt32(), t6.AsInt32());
        Vector128<int> q6 = SimdCompat.UnpackLow(t5.AsInt32(), t7.AsInt32());
        Vector128<int> q7 = SimdCompat.UnpackHigh(t5.AsInt32(), t7.AsInt32());

        v0 = SimdCompat.UnpackLow(q0.AsInt64(), q4.AsInt64()).AsInt16();
        v1 = SimdCompat.UnpackHigh(q0.AsInt64(), q4.AsInt64()).AsInt16();
        v2 = SimdCompat.UnpackLow(q1.AsInt64(), q5.AsInt64()).AsInt16();
        v3 = SimdCompat.UnpackHigh(q1.AsInt64(), q5.AsInt64()).AsInt16();
        v4 = SimdCompat.UnpackLow(q2.AsInt64(), q6.AsInt64()).AsInt16();
        v5 = SimdCompat.UnpackHigh(q2.AsInt64(), q6.AsInt64()).AsInt16();
        v6 = SimdCompat.UnpackLow(q3.AsInt64(), q7.AsInt64()).AsInt16();
        v7 = SimdCompat.UnpackHigh(q3.AsInt64(), q7.AsInt64()).AsInt16();
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void TransformAndConvertYCbCr422(
    ReadOnlySpan<short> yLeftCoef, ReadOnlySpan<short> yRightCoef, ushort[] yQuant,
    ReadOnlySpan<short> cbCoef, ushort[] cbQuant,
    ReadOnlySpan<short> crCoef, ushort[] crQuant,
    Span<byte> dest, int stride)
    {
        Block8x8Vectors vy0 = Idct8x8ToVectors(yLeftCoef, yQuant);
        Block8x8Vectors vy1 = Idct8x8ToVectors(yRightCoef, yQuant);
        Block8x8Vectors vcb = Idct8x8ToVectors(cbCoef, cbQuant);
        Block8x8Vectors vcr = Idct8x8ToVectors(crCoef, crQuant);

        byte* pDestBase = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(dest));

        // 4:2:2 = 水平 2 倍下采样、垂直与亮度同密度，故每行色度只服务一行 Y（不做垂直上采样）。
        UpsampleAndConvert(vy0.V0, vy1.V0, vcb.V0, vcr.V0, pDestBase + 0 * stride);
        UpsampleAndConvert(vy0.V1, vy1.V1, vcb.V1, vcr.V1, pDestBase + 1 * stride);
        UpsampleAndConvert(vy0.V2, vy1.V2, vcb.V2, vcr.V2, pDestBase + 2 * stride);
        UpsampleAndConvert(vy0.V3, vy1.V3, vcb.V3, vcr.V3, pDestBase + 3 * stride);
        UpsampleAndConvert(vy0.V4, vy1.V4, vcb.V4, vcr.V4, pDestBase + 4 * stride);
        UpsampleAndConvert(vy0.V5, vy1.V5, vcb.V5, vcr.V5, pDestBase + 5 * stride);
        UpsampleAndConvert(vy0.V6, vy1.V6, vcb.V6, vcr.V6, pDestBase + 6 * stride);
        UpsampleAndConvert(vy0.V7, vy1.V7, vcb.V7, vcr.V7, pDestBase + 7 * stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void UpsampleAndConvert(Vector128<short> yL, Vector128<short> yR, Vector128<short> cbRow, Vector128<short> crRow, byte* pRowDest)
    {
        // 水平上采样：用 Unpack 完美将 8 个像素拉伸为两组包含 8 个像素的 128位 向量
        Vector128<short> cbL = SimdCompat.UnpackLow(cbRow, cbRow);
        Vector128<short> cbR = SimdCompat.UnpackHigh(cbRow, cbRow);
        Vector128<short> crL = SimdCompat.UnpackLow(crRow, crRow);
        Vector128<short> crR = SimdCompat.UnpackHigh(crRow, crRow);

        // 转换左边 8 个像素，直接传入当前行的起始指针
        ConvertRowYCbCrToRgb(yL, cbL, crL, pRowDest);
        
        // 转换右边 8 个像素，指针向后偏移 8 个像素的 RGB 长度（8 * 3 = 24 字节）
        ConvertRowYCbCrToRgb(yR, cbR, crR, pRowDest + 24);
    }

    /// <summary>从 Block8x8Vectors 取第 i 行（0..7）。</summary>
    internal static Vector128<short> GetRow(in Block8x8Vectors b, int i) => i switch
    {
        0 => b.V0, 1 => b.V1, 2 => b.V2, 3 => b.V3,
        4 => b.V4, 5 => b.V5, 6 => b.V6, _ => b.V7,
    };

    private static readonly Vector128<short> LevelShift128 = Vector128.Create((short)128);
    private static readonly Vector128<short> ZeroShort = Vector128<short>.Zero;
    private static readonly Vector128<short> Neg128Short = Vector128.Create((short)-128);
    private static readonly Vector128<short> Pos127Short = Vector128.Create((short)127);
    /// <summary>色度 0..255 域转 -128 基有符号域的减数（ConvertRowYCbCrToRgb 的入参约定）。</summary>
    internal static readonly Vector128<short> ChromaLevel128 = Vector128.Create((short)128);
    private static readonly Vector128<short> MaxByteValue = Vector128.Create((short)255);

    /// <summary>
    /// IDCT 一个块并把空间域样本（0..255，已做 +128 电平偏移与钳位）写入字节平面。
    /// 供 fancy 上采样的两阶段路径使用：先重建完整色度平面，再逐行跨块过滤。
    /// </summary>
    internal static unsafe void Idct8x8ToPlane(ReadOnlySpan<short> coefficients, ushort[] quant, byte* plane, int stride)
    {
        Block8x8Vectors b = Idct8x8ToVectors(coefficients, quant);
        StorePlaneRow(b.V0, plane + 0 * stride);
        StorePlaneRow(b.V1, plane + 1 * stride);
        StorePlaneRow(b.V2, plane + 2 * stride);
        StorePlaneRow(b.V3, plane + 3 * stride);
        StorePlaneRow(b.V4, plane + 4 * stride);
        StorePlaneRow(b.V5, plane + 5 * stride);
        StorePlaneRow(b.V6, plane + 6 * stride);
        StorePlaneRow(b.V7, plane + 7 * stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void StorePlaneRow(Vector128<short> v, byte* dst)
    {
        // IDCT 输出是 -128 基的有符号域；+128 还原到 0..255 再钳位，随后按无符号打包。
        Vector128<short> shifted = v + LevelShift128;
        Vector128<short> clamped = SimdCompat.MaxInt16(SimdCompat.MinInt16(shifted, MaxByteValue), Vector128<short>.Zero);
        Vector128<byte> bytes = SimdCompat.PackUnsignedSaturate(clamped, clamped);
        Unsafe.WriteUnaligned(dst, bytes.AsUInt64().GetElement(0));
    }

    /// <summary>fancy h2v2：行0/行1 做垂直 (3,1) 混合 A=3*row0+row1，再水平滤波出 2x 宽的行（0..255 域）。
    /// 大图走 SIMD：A 行向量化计算，水平滤波用三路重叠加载 + unpack 交织存储。</summary>
    internal static unsafe void FancyVh2x2(byte* row0, byte* row1, int chromaCols, int realCols, short* dst)
    {
        if (!SimdCompat.VectorBytesSupported || SimdCompat.ForceScalar || realCols < 8)
        {
            FancyVh2x2Scalar(row0, row1, chromaCols, realCols, dst);
            return;
        }

        int last = realCols - 1;
        // A 行（含尾部复制到 chromaCols，右缘填充列的钳制语义与标量版一致）。
        short* aRow = stackalloc short[chromaCols + 16];
        int x = 0;
        for (; x + 8 <= realCols; x += 8)
        {
            Vector128<byte> b0 = Vector128.Load(row0 + x);
            Vector128<byte> b1 = Vector128.Load(row1 + x);
            Vector128<short> s0Lo = SimdCompat.UnpackLow<byte>(b0, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> s0Hi = SimdCompat.UnpackHigh<byte>(b0, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> s1Lo = SimdCompat.UnpackLow<byte>(b1, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> s1Hi = SimdCompat.UnpackHigh<byte>(b1, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> lo = SimdCompat.AddInt16(SimdCompat.AddInt16(SimdCompat.AddInt16(s0Lo, s0Lo), s0Lo), s1Lo);
            Vector128<short> hi = SimdCompat.AddInt16(SimdCompat.AddInt16(SimdCompat.AddInt16(s0Hi, s0Hi), s0Hi), s1Hi);
            lo.Store(aRow + x);
            hi.Store(aRow + x + 8);
        }
        int tailA = x <= last ? 3 * row0[last] + row1[last] : 0;
        for (; x < realCols; x++)
        {
            aRow[x] = (short)(3 * row0[x] + row1[x]);
            tailA = aRow[x];
        }
        for (; x < chromaCols; x++)
        {
            aRow[x] = (short)tailA;
        }

        // 水平滤波：左缘 x=0 鑑制，向量区三路重叠加载，尾部标量。
        int a0 = aRow[0];
        dst[0] = (short)((a0 * 3 + a0 + 8) >> 4);
        dst[1] = (short)((a0 * 3 + (realCols > 1 ? aRow[1] : a0) + 7) >> 4);

        int xx = 1;
        for (; xx + 8 < chromaCols; xx += 8)
        {
            Vector128<short> cur = Vector128.Load(aRow + xx);
            Vector128<short> prev = Vector128.Load(aRow + xx - 1);
            Vector128<short> next = Vector128.Load(aRow + xx + 1);
            Vector128<short> cur3 = SimdCompat.AddInt16(SimdCompat.AddInt16(cur, cur), cur);
            Vector128<short> even = (cur3 + prev + EightShort) >> 4;
            Vector128<short> odd = (cur3 + next + SevenShort) >> 4;
            SimdCompat.UnpackLow<short>(even, odd).Store(dst + 2L * xx);
            SimdCompat.UnpackHigh<short>(even, odd).Store(dst + 2L * xx + 8);
        }
        for (; xx < chromaCols; xx++)
        {
            int ac = aRow[xx];
            int ap = aRow[xx - 1];
            int an = xx + 1 < chromaCols ? aRow[xx + 1] : ac;
            dst[2 * xx] = (short)((ac * 3 + ap + 8) >> 4);
            dst[2 * xx + 1] = (short)((ac * 3 + an + 7) >> 4);
        }
    }

    private static unsafe void FancyVh2x2Scalar(byte* row0, byte* row1, int chromaCols, int realCols, short* dst)
    {
        int last = realCols - 1;
        int aPrev = 3 * row0[0] + row1[0];
        int aCur = aPrev;
        for (int x = 0; x < chromaCols; x++)
        {
            // x 超过真实色度宽度时（右缘填充列）邻域钳制到最后一个真实样本，与 libjpeg 的 context 复制一致。
            int aNext = x < last ? 3 * row0[x + 1] + row1[x + 1] : aCur;
            dst[2 * x] = (short)((aCur * 3 + aPrev + 8) >> 4);
            dst[2 * x + 1] = (short)((aCur * 3 + aNext + 7) >> 4);
            aPrev = aCur;
            aCur = aNext;
        }
    }

    /// <summary>fancy h2v1：单行水平滤波出 2x 宽的行（0..255 域）。大图走 SIMD。</summary>
    internal static unsafe void FancyH1x2(byte* row, int chromaCols, int realCols, short* dst)
    {
        if (!SimdCompat.VectorBytesSupported || SimdCompat.ForceScalar || realCols < 8)
        {
            FancyH1x2Scalar(row, chromaCols, realCols, dst);
            return;
        }

        int last = realCols - 1;
        int s0 = row[0];
        dst[0] = (short)((s0 * 3 + s0 + 1) >> 2);
        dst[1] = (short)((s0 * 3 + (realCols > 1 ? row[1] : s0) + 2) >> 2);

        int xx = 1;
        // 高位半段（x'=xx+8..xx+15）的 next 需读到 row[x'+1]=row[xx+16]，故边界为 xx+16 ≤ last。
        for (; xx + 16 <= last; xx += 8)
        {
            Vector128<byte> c = Vector128.Load(row + xx);
            Vector128<byte> p = Vector128.Load(row + xx - 1);
            Vector128<byte> n = Vector128.Load(row + xx + 1);
            Vector128<short> sc = SimdCompat.UnpackLow<byte>(c, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> sch = SimdCompat.UnpackHigh<byte>(c, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> sp = SimdCompat.UnpackLow<byte>(p, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> sph = SimdCompat.UnpackHigh<byte>(p, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> sn = SimdCompat.UnpackLow<byte>(n, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> snh = SimdCompat.UnpackHigh<byte>(n, SimdCompat.ZeroByte).AsInt16();
            Vector128<short> sc3 = SimdCompat.AddInt16(SimdCompat.AddInt16(sc, sc), sc);
            Vector128<short> sch3 = SimdCompat.AddInt16(SimdCompat.AddInt16(sch, sch), sch);
            Vector128<short> even = (sc3 + sp + OneShort) >> 2;
            Vector128<short> odd = (sc3 + sn + TwoShort) >> 2;
            Vector128<short> evenH = (sch3 + sph + OneShort) >> 2;
            Vector128<short> oddH = (sch3 + snh + TwoShort) >> 2;
            SimdCompat.UnpackLow<short>(even, odd).Store(dst + 2L * xx);
            SimdCompat.UnpackHigh<short>(even, odd).Store(dst + 2L * xx + 8);
        }
        for (; xx < chromaCols; xx++)
        {
            int sc2 = xx <= last ? row[xx] : row[last];
            int sp = xx > 0 ? (xx - 1 <= last ? row[xx - 1] : row[last]) : sc2;
            int sn = xx + 1 <= last ? row[xx + 1] : sc2;
            dst[2 * xx] = (short)((sc2 * 3 + sp + 1) >> 2);
            dst[2 * xx + 1] = (short)((sc2 * 3 + sn + 2) >> 2);
        }
    }

    private static unsafe void FancyH1x2Scalar(byte* row, int chromaCols, int realCols, short* dst)
    {
        int last = realCols - 1;
        int prev = row[0];
        int cur = prev;
        for (int x = 0; x < chromaCols; x++)
        {
            int next = x < last ? row[x + 1] : cur;
            dst[2 * x] = (short)((cur * 3 + prev + 1) >> 2);
            dst[2 * x + 1] = (short)((cur * 3 + next + 2) >> 2);
            prev = cur;
            cur = next;
        }
    }

    private static readonly Vector128<short> OneShort = Vector128.Create((short)1);
    private static readonly Vector128<short> TwoShort = Vector128.Create((short)2);
    private static readonly Vector128<short> SevenShort = Vector128.Create((short)7);
    private static readonly Vector128<short> EightShort = Vector128.Create((short)8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe Block8x8Vectors Idct8x8ToVectors(ReadOnlySpan<short> coefficients, ushort[] quant)
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
            // Pass 1: IDCT on columns
            Idct8ElementsSIMD2Pass(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, Pass1Shift, HalfPass1);
            // Transpose to make rows vertical
            Transpose8x8Sse2(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);
            // Pass 2: IDCT on original rows
            Idct8ElementsSIMD2Pass(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7, Pass2Shift, HalfPass2);
            // Transpose back to row-major
            Transpose8x8Sse2(ref v0, ref v1, ref v2, ref v3, ref v4, ref v5, ref v6, ref v7);

            return new Block8x8Vectors { V0 = v0, V1 = v1, V2 = v2, V3 = v3, V4 = v4, V5 = v5, V6 = v6, V7 = v7 };
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe static void ConvertRowYCbCrToRgb(Vector128<short> y, Vector128<short> cb, Vector128<short> cr, byte* pDest)
    {
        // libjpeg 在 IDCT 输出时就经 range_limit 鑑位到 [0,255] 再做色彩转换；
        // 此前这里直接用未鑑位的原始 IDCT 值参与运算、只对最终和鑑位，
        // 在饱和/过冲区域会产生 ±1~14 的系统性色差。这里对齐 libjpeg 语义：
        // Y 鉑到 [0,255]（再转 -128 域），色度鉑到 [−128,127]。
        y = SimdCompat.MinInt16(SimdCompat.MaxInt16(y + Bias128, ZeroShort), MaxByteValue);
        cb = SimdCompat.MinInt16(SimdCompat.MaxInt16(cb, Neg128Short), Pos127Short);
        cr = SimdCompat.MinInt16(SimdCompat.MaxInt16(cr, Neg128Short), Pos127Short);

        Vector128<int> yl = Vector128.WidenLower(y);
        Vector128<int> yh = Vector128.WidenUpper(y);
        Vector128<int> cbl = Vector128.WidenLower(cb);
        Vector128<int> cbh = Vector128.WidenUpper(cb);
        Vector128<int> crl = Vector128.WidenLower(cr);
        Vector128<int> crh = Vector128.WidenUpper(cr);

        ConvertCore(yl, cbl, crl, out Vector128<int> rl, out Vector128<int> gl, out Vector128<int> bl);
        ConvertCore(yh, cbh, crh, out Vector128<int> rh, out Vector128<int> gh, out Vector128<int> bh);

        // 1. 正确打包成 byte (把低 4 位和高 4 位拼成完整的 8 字节有效数据)
        Vector128<byte> r = SimdCompat.PackUnsignedSaturate(SimdCompat.PackSignedSaturateInt32(rl, rh), Vector128<short>.Zero);
        Vector128<byte> g = SimdCompat.PackUnsignedSaturate(SimdCompat.PackSignedSaturateInt32(gl, gh), Vector128<short>.Zero);
        Vector128<byte> b = SimdCompat.PackUnsignedSaturate(SimdCompat.PackSignedSaturateInt32(bl, bh), Vector128<short>.Zero);

        InterleaveRgb24(r, g, b, pDest);
    }

    /// <summary>
    /// 把三个"低 8 lane 各装 8 个分量"的字节向量交织成 RGB24，写出 24 字节。
    /// <para>
    /// 抽成独立方法是刻意的：交织是纯粹的字节置换，与色彩转换无关，
    /// 独立后可以让测试直接对拍置换结果（见 <c>SimdPixelOpsTests</c>）。
    /// </para>
    /// </summary>
    internal static unsafe void InterleaveRgb24(Vector128<byte> r, Vector128<byte> g, Vector128<byte> b, byte* pDest)
    {
        // 1. UnpackLow 把 R/G 交错成 [R0 G0 R1 G1 ... R7 G7]（16 字节，正好一个向量）
        Vector128<byte> rg = SimdCompat.UnpackLow(r, g);

        // 2. RGB24 交错**全程留在向量域**：两次 pshufb 分别从 rg 与 b 里挑字节，
        //    掩码中 0x80 的位置由 pshufb 置零，故两组结果可直接按位或合并。
        //    out0 覆盖输出字节 0..15（像素 0..4 完整 + 像素 5 的 R），
        //    out1 覆盖输出字节 16..23（像素 5 的 G/B + 像素 6..7 完整）。
        //    此前这里是 3 次 GetElement 拆成 ulong、再 12 次标量字节/ushort 写入。
        Vector128<byte> out0 = SimdCompat.OrBytes(SimdCompat.ShuffleBytes(rg, RgbInterleaveLowFromRg),
                                       SimdCompat.ShuffleBytes(b, RgbInterleaveLowFromB));
        Vector128<byte> out1 = SimdCompat.OrBytes(SimdCompat.ShuffleBytes(rg, RgbInterleaveHighFromRg),
                                       SimdCompat.ShuffleBytes(b, RgbInterleaveHighFromB));

        // 3. 两次存储写满 24 字节（调用方保证一行至少可写 8 像素 = 24 字节）
        out0.StoreUnsafe(ref *pDest);
        Unsafe.WriteUnaligned(pDest + 16, out1.AsUInt64().GetElement(0));
    }

    /// <summary><see cref="InterleaveRgb24(Vector128{byte}, Vector128{byte}, Vector128{byte}, byte*)"/> 的安全重载，供测试直接对拍。</summary>
    internal static void InterleaveRgb24(Vector128<byte> r, Vector128<byte> g, Vector128<byte> b, Span<byte> dest)
    {
        Vector128<byte> rg = SimdCompat.UnpackLow(r, g);
        Vector128<byte> out0 = SimdCompat.OrBytes(SimdCompat.ShuffleBytes(rg, RgbInterleaveLowFromRg),
                                       SimdCompat.ShuffleBytes(b, RgbInterleaveLowFromB));
        Vector128<byte> out1 = SimdCompat.OrBytes(SimdCompat.ShuffleBytes(rg, RgbInterleaveHighFromRg),
                                       SimdCompat.ShuffleBytes(b, RgbInterleaveHighFromB));

        ref byte d = ref MemoryMarshal.GetReference(dest);
        out0.StoreUnsafe(ref d);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 16), out1.AsUInt64().GetElement(0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConvertCore(Vector128<int> y, Vector128<int> cb, Vector128<int> cr, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b)
    {
        // 与 libjpeg jdcolor.c 一致：算术移位前先加 ONE_HALF（1 << 15）四舍五入。
        // 此前直接 >> 会向负方向截断，与 libjpeg 相比造成约四成像素 ±1 的普遍色差。
        Vector128<int> half = ColorOneHalf;
        Vector128<int> r_off = (cr * Fix_1_402 + half) >> ColorShift;
        Vector128<int> g_off = ((cb * Fix_0_34414) + (cr * Fix_0_71414) + half) >> ColorShift;
        Vector128<int> b_off = (cb * Fix_1_772 + half) >> ColorShift;

        r = y + r_off;
        g = y - g_off;
        b = y + b_off;
    }
}
