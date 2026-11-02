namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// VP8 逆变换：4×4 逆 DCT（叠加到像素）与 4×4 逆 Walsh-Hadamard（Y2 块 DC 系数）。
/// 严格对照 libwebp src/dsp/dec.c 的 C 参考实现（非 SIMD 路径）逐字移植，
/// 保证位精确一致，对应 C 函数：TransformOne_C / TransformDC_C / TransformWHT_C。
/// 所有中间计算使用 int，右移均为算术右移（C# 的 int >> 与 C 在 gcc/msvc 下的行为一致）。
/// 本类无可变静态状态，并发解码线程安全。
/// </summary>
internal static class Vp8Idct
{
    // ------------------------------------------------------------------
    // 定点常量，对应 src/dsp/dsp.h：
    //   WEBP_TRANSFORM_AC3_C1 = 20091（≈ cos(pi/8)·sqrt(2)-1 的 16 位定点）
    //   WEBP_TRANSFORM_AC3_C2 = 35468（≈ sin(pi/8)·sqrt(2)  的 16 位定点）
    //   WEBP_TRANSFORM_AC3_MUL1(a) = (((a) * C1) >> 16) + (a)
    //   WEBP_TRANSFORM_AC3_MUL2(a) = (((a) * C2) >> 16)
    // ------------------------------------------------------------------
    private const int kC1 = 20091;
    private const int kC2 = 35468;

    /// <summary>对应 WEBP_TRANSFORM_AC3_MUL1。</summary>
    private static int Mul1(int a) => ((a * kC1) >> 16) + a;

    /// <summary>对应 WEBP_TRANSFORM_AC3_MUL2。</summary>
    private static int Mul2(int a) => (a * kC2) >> 16;

    /// <summary>对应 dec.c 的 clip_8b：(!(v &amp; ~0xff)) ? v : (v &lt; 0) ? 0 : 255。</summary>
    private static byte Clip8b(int v) => (v & ~0xff) == 0 ? (byte)v : (v < 0 ? (byte)0 : (byte)255);

    /// <summary>
    /// 对应 STORE(x, y, v)：dst[(x)+(y)*BPS] = clip_8b(dst[(x)+(y)*BPS] + ((v) &gt;&gt; 3))。
    /// </summary>
    private static void Store(byte[] dst, int off, int stride, int x, int y, int v)
    {
        int idx = off + x + y * stride;
        dst[idx] = Clip8b(dst[idx] + (v >> 3));
    }

    // ------------------------------------------------------------------
    // Paragraph 14.3：逆 WHT，用于 Y2 宏块的 16 个 DC 系数
    // ------------------------------------------------------------------

    /// <summary>
    /// 逆 WHT：input 为 16 个 Y2 DC 系数（按 VP8 系数顺序，即 C 中 in[0..15] 光栅顺序），
    /// 输出 16 个直流分量。对应 libwebp TransformWHT_C。
    /// 输出布局与 C 完全一致：第 i 轮（i ∈ [0,4)）写到
    /// output[64*i + 0]、output[64*i + 16]、output[64*i + 32]、output[64*i + 48]，
    /// 即 16 个亮度块系数缓冲区中各块的 DC 槽位；因此 output 至少需 241 个 short，
    /// 调用方通常直接传入 16×16 的亮度系数缓冲区。
    /// </summary>
    public static void TransformWHT(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];

        // 第一遍：对应 C 中 for (i = 0; i < 4; ++i) 的列变换
        for (int i = 0; i < 4; ++i)
        {
            int a0 = input[0 + i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[0 + i] - input[12 + i];
            tmp[0 + i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        // 第二遍：对应 C 中 for (i = 0; i < 4; ++i) 的行变换 + 舍入截位输出
        int outOff = 0; // 对应 C 的 out 指针，每轮 out += 64
        for (int i = 0; i < 4; ++i)
        {
            int dc = tmp[0 + i * 4] + 3; // 对应 C 注释 "w/ rounder"：+3 舍入
            int a0 = dc + tmp[3 + i * 4];
            int a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
            int a2 = tmp[1 + i * 4] - tmp[2 + i * 4];
            int a3 = dc - tmp[3 + i * 4];
            output[outOff + 0] = (short)((a0 + a1) >> 3);
            output[outOff + 16] = (short)((a3 + a2) >> 3);
            output[outOff + 32] = (short)((a0 - a1) >> 3);
            output[outOff + 48] = (short)((a3 - a2) >> 3);
            outOff += 64;
        }
    }

    // ------------------------------------------------------------------
    // 变体：8×8 色度（四个 4×4 块）用的批量接口
    // 对应 libwebp dec.c 的 TransformUV_C / TransformDCUV_C；
    // 四个块的布局为 (0,0)、(0,4)、(4,0)、(4,4)，系数偏移 0/16/32/48。
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 STORE2(y, dc, d, c)：一行四个像素依次为 dc+d、dc+c、dc-c、dc-d。
    /// </summary>
    private static void StoreRow(byte[] dst, int off, int stride, int y, int dc, int d, int c)
    {
        Store(dst, off, stride, 0, y, dc + d);
        Store(dst, off, stride, 1, y, dc + c);
        Store(dst, off, stride, 2, y, dc - c);
        Store(dst, off, stride, 3, y, dc - d);
    }

    /// <summary>
    /// 对应 libwebp TransformAC3_C：仅使用 AC 系数 [1,2,3,4] 与 DC，用于 i4x4 亮度块
    /// （其 DC 已由 Y2 的逆 WHT 补入）。
    /// </summary>
    public static void TransformAc3(ReadOnlySpan<short> coeffs, byte[] dst, int off, int stride)
    {
        int a = coeffs[0] + 4;
        int c4 = Mul2(coeffs[4]);
        int d4 = Mul1(coeffs[4]);
        int c1 = Mul2(coeffs[1]);
        int d1 = Mul1(coeffs[1]);
        StoreRow(dst, off, stride, 0, a + d4, d1, c1);
        StoreRow(dst, off, stride, 1, a + c4, d1, c1);
        StoreRow(dst, off, stride, 2, a - c4, d1, c1);
        StoreRow(dst, off, stride, 3, a - d4, d1, c1);
    }

    /// <summary>对应 libwebp TransformDC_C：仅 DC 系数，16 个像素统一叠加 DC &gt;&gt; 3。</summary>
    public static void TransformDc(ReadOnlySpan<short> coeffs, byte[] dst, int off, int stride)
    {
        int dc = coeffs[0] + 4;
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                Store(dst, off, stride, x, y, dc);
            }
        }
    }

    /// <summary>
    /// 对应 libwebp TransformUV_C：两个 8×4 半块（各含左右两个 4×4 块）。
    /// </summary>
    public static void TransformUv(ReadOnlySpan<short> coeffs, byte[] dst, int off, int stride)
    {
        TransformOne(coeffs[..16], dst, off, stride);
        TransformOne(coeffs.Slice(16, 16), dst, off + 4, stride);
        TransformOne(coeffs.Slice(32, 16), dst, off + 4 * stride, stride);
        TransformOne(coeffs.Slice(48, 16), dst, off + 4 * stride + 4, stride);
    }

    /// <summary>
    /// 对应 libwebp TransformDCUV_C：四个块各自仅在 DC 非零时才叠加。
    /// </summary>
    public static void TransformDcUv(ReadOnlySpan<short> coeffs, byte[] dst, int off, int stride)
    {
        if (coeffs[0] != 0) TransformDc(coeffs[..16], dst, off, stride);
        if (coeffs[16] != 0) TransformDc(coeffs.Slice(16, 16), dst, off + 4, stride);
        if (coeffs[32] != 0) TransformDc(coeffs.Slice(32, 16), dst, off + 4 * stride, stride);
        if (coeffs[48] != 0) TransformDc(coeffs.Slice(48, 16), dst, off + 4 * stride + 4, stride);
    }

    // ------------------------------------------------------------------
    // Paragraph 14.4：4×4 逆 DCT，结果叠加到目标像素
    // ------------------------------------------------------------------

    /// <summary>
    /// 4×4 逆 DCT 并把结果叠加到目标像素（dst[off + x + y*stride]，x,y ∈ [0,4)）。
    /// 对应 libwebp TransformOne_C；coeffs 为 16 个已 dequant 的系数
    /// （按光栅顺序，即 C 中 in[0..15]）。
    /// 若仅 coeffs[0] 非零，走 DC 快路径，对应 TransformDC_C。
    /// </summary>
    public static void TransformOne(ReadOnlySpan<short> coeffs, byte[] dst, int off, int stride)
    {
        // DC 快路径：coeffs[1..15] 全零时与 TransformDC_C 行为一致
        bool dcOnly = true;
        for (int i = 1; i < 16; ++i)
        {
            if (coeffs[i] != 0)
            {
                dcOnly = false;
                break;
            }
        }

        if (dcOnly)
        {
            // 对应 TransformDC_C：DC = in[0] + 4，16 个像素统一叠加 DC >> 3
            int dcValue = coeffs[0] + 4;
            for (int y = 0; y < 4; ++y)
            {
                for (int x = 0; x < 4; ++x)
                {
                    Store(dst, off, stride, x, y, dcValue);
                }
            }
            return;
        }

        Span<int> tmp = stackalloc int[16]; // 对应 C 的 int C[4 * 4]

        // 垂直方向（vertical pass）：对应 C 中 tmp 步进 4、in 步进 1 的四轮
        for (int i = 0; i < 4; ++i)
        {
            int a = coeffs[i] + coeffs[8 + i]; // [-4096, 4094]
            int b = coeffs[i] - coeffs[8 + i]; // [-4095, 4095]
            int c = Mul2(coeffs[4 + i]) - Mul1(coeffs[12 + i]); // [-3783, 3783]
            int d = Mul1(coeffs[4 + i]) + Mul2(coeffs[12 + i]); // [-3785, 3781]
            tmp[4 * i + 0] = a + d; // [-7881, 7875]
            tmp[4 * i + 1] = b + c; // [-7878, 7878]
            tmp[4 * i + 2] = b - c; // [-7878, 7878]
            tmp[4 * i + 3] = a - d; // [-7877, 7879]
        }

        // 水平方向（horizontal pass）：对应 C 中 tmp 步进 1、dst 步进 BPS 的四轮
        int rowOff = off;
        for (int i = 0; i < 4; ++i)
        {
            int dc = tmp[i] + 4; // +4 舍入
            int a = dc + tmp[8 + i];
            int b = dc - tmp[8 + i];
            int c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            int d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            Store(dst, rowOff, stride, 0, 0, a + d);
            Store(dst, rowOff, stride, 1, 0, b + c);
            Store(dst, rowOff, stride, 2, 0, b - c);
            Store(dst, rowOff, stride, 3, 0, a - d);
            rowOff += stride;
        }
    }
}
