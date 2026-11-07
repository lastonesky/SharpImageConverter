using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// YUV420 → RGBA 转换，逐字移植 libwebp 的 fancy upsampling 解码输出路径：
/// <list type="bullet">
/// <item>定点 YUV→RGB：src/dsp/yuv.h 的 <c>VP8YUVToR/G/B</c>（<c>MultHi</c> 内联乘 + <c>VP8Clip8</c> 算术裁剪版，
/// 该版本与 SSE2/NEON 输出逐位一致，是本类采用的参考实现）。</item>
/// <item>行对 fancy 上采样器：src/dsp/upsampling.c 的 <c>UPSAMPLE_FUNC(UpsampleRgbaLinePair_C, VP8YuvToRgba, 4)</c>。</item>
/// <item>行调度：src/dec/io_dec.c 的 <c>EmitFancyRGB</c>（按整图一次调用展开，即 mb_y=0、mb_h=height、
/// crop 覆盖全图，因此永不进入"暂存未完成行等待下次调用"分支）。</item>
/// </list>
/// 输出与 libwebp <c>WebPDecodeRGBA</c> 默认路径（fancy upsampling 开启、MODE_RGBA）逐位一致。
///
/// SIMD 化说明（与标量路径逐位等价，不是近似）：
/// <list type="bullet">
/// <item><b>MultHi</b>：<c>(v*coeff)>>8</c> 在 16 位通道里会溢出 32 位中间值，
/// 因此把系数拆成 <c>coeff = hi*256 + lo</c>，利用
/// <c>(v*coeff)>>8 = v*hi + ((v*lo)>>8)</c>：两项乘积都落回 16 位
/// （<c>v*lo ≤ 255*255 = 65025</c>、<c>v*hi ≤ 255*129 = 32895</c>），且展开是<b>恒等式</b>，
/// 不引入任何舍入差异。</item>
/// <item><b>VP8Clip8</b>：<c>clamp(v,0,16383)>>6，否则 0/255</c> 等价于
/// <c>min((v ⊖ 0) >>> 6, 255)</c> 配饱和减法，负值天然落到 0、超过 16383 的落到 255。</item>
/// <item>每批处理 8 个色度对（16 个输出像素），上下两个输出行共享同一组色度插值。</item>
/// </list>
/// 本类无可变静态状态，多线程并发调用安全。
/// </summary>
internal static class Vp8YuvToRgb
{
    // ------------------------------------------------------------------
    // 定点常量（对应 src/dsp/yuv.h 的 enum）
    // ------------------------------------------------------------------

    /// <summary>YUV→RGB 定点精度（C: <c>YUV_FIX2</c>）。</summary>
    private const int YuvFix2 = 6;

    /// <summary>定点有效位掩码（C: <c>YUV_MASK2</c>）。</summary>
    private const int YuvMask2 = (256 << YuvFix2) - 1;

    // ------------------------------------------------------------------
    // 定点 YUV→RGB（对应 src/dsp/yuv.h 内联函数），标量路径供边界像素使用
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 C 的 <c>MultHi</c>：模拟 _mm_mulhi_epu16，a·b = ((a&lt;&lt;8)·b)&gt;&gt;16，
    /// 因调用方传入的 v 已是 8 位样本（等价于 C 中 y/u/v 以 uint8_t 传入），
    /// 故直接写作 (v * coeff) &gt;&gt; 8，保留 8 位小数精度供最终去标定。
    /// </summary>
    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    /// <summary>
    /// 对应 C 的 <c>VP8Clip8</c>（算术版，无查表）：值落在 [0, 256&lt;&lt;6) 内则去标定（&gt;&gt;6），
    /// 否则按符号钳到 0/255。
    /// </summary>
    private static int VP8Clip8(int v) =>
        (v & ~YuvMask2) == 0 ? v >> YuvFix2 : (v < 0 ? 0 : 255);

    /// <summary>对应 C 的 <c>VP8YUVToR</c>：R = (19077·y + 26149·v − 14234) &gt;&gt; 6。</summary>
    private static int YuvToR(int y, int v) =>
        VP8Clip8(MultHi(y, 19077) + MultHi(v, 26149) - 14234);

    /// <summary>对应 C 的 <c>VP8YUVToG</c>：G = (19077·y − 6419·u − 13320·v + 8708) &gt;&gt; 6。</summary>
    private static int YuvToG(int y, int u, int v) =>
        VP8Clip8(MultHi(y, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);

    /// <summary>对应 C 的 <c>VP8YUVToB</c>：B = (19077·y + 33050·u − 17685) &gt;&gt; 6。</summary>
    private static int YuvToB(int y, int u) =>
        VP8Clip8(MultHi(y, 19077) + MultHi(u, 33050) - 17685);

    /// <summary>
    /// 对应 C 的 <c>VP8YuvToRgba</c>：写一个像素 R,G,B,A（A=0xff）。
    /// 与 C 一致，y/u/v 均按 uint8_t 语义传入（取值 0..255）。
    /// </summary>
    private static void YuvToRgba(int y, int u, int v, byte[] dst, int dstOff)
    {
        dst[dstOff] = (byte)YuvToR(y, v);
        dst[dstOff + 1] = (byte)YuvToG(y, u, v);
        dst[dstOff + 2] = (byte)YuvToB(y, u);
        dst[dstOff + 3] = 0xff;
    }

    // ------------------------------------------------------------------
    // SIMD：16 位通道原语
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 VP8YuvToR/G/B 里的 <c>MultHi</c>：<c>(v*coeff)>>8</c>。
    /// coeff 拆成 hi*256+lo 后两项都落回 16 位，展开是恒等式（见类型注释）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> MulHi(Vector128<ushort> v, int coeff)
    {
        int hi = coeff >> 8;
        int lo = coeff & 0xFF;
        Vector128<ushort> partHi = Vector128.Multiply(v, Vector128.Create((ushort)hi));
        Vector128<ushort> partLo = Vector128.ShiftRightLogical(
            Vector128.Multiply(v, Vector128.Create((ushort)lo)), 8);
        return Vector128.Add(partHi, partLo);
    }

    private static readonly Vector128<ushort> V255u = Vector128.Create((ushort)255);
    private static readonly Vector128<ushort> V14234 = Vector128.Create((ushort)14234);
    private static readonly Vector128<ushort> V17685 = Vector128.Create((ushort)17685);
    private static readonly Vector128<ushort> V8708 = Vector128.Create((ushort)8708);

    /// <summary>量化到 [0,255]：<c>(v ⊖ 0) >>> 6</c> 后饱和到 255，等价 VP8Clip8。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Clip8V(Vector128<ushort> v)
    {
        Vector128<ushort> shifted = Vector128.ShiftRightLogical(v, YuvFix2);
        // 值域 ≤ 1023，用有符号 min 走 SSE2 的 pminsw / NEON 的 smin
        return Vector128.Min(shifted.AsInt16(), V255u.AsInt16()).AsUInt16();
    }

    /// <summary>
    /// 8 个像素的 YUV→RGBA。y/u/v 均为 8 个 8 位样本零扩展后的 16 位通道。
    /// 输出两个 16 字节向量，共 8 个 RGBA 像素。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void YuvToRgba8(Vector128<ushort> y, Vector128<ushort> u, Vector128<ushort> v,
                                   out Vector128<byte> out0, out Vector128<byte> out1)
    {
        Vector128<ushort> my = MulHi(y, 19077);
        Vector128<ushort> mv = MulHi(v, 26149);
        Vector128<ushort> mu1 = MulHi(u, 6419);
        Vector128<ushort> mv2 = MulHi(v, 13320);
        Vector128<ushort> mu3 = MulHi(u, 33050);

        // R = clip8(my + mv - 14234)
        Vector128<ushort> r = Clip8V(Vector128.SubtractSaturate(Vector128.Add(my, mv), V14234));
        // G = clip8(my - mu1 - mv2 + 8708)：先加常数再饱和减，负值等价钳到 0
        Vector128<ushort> g = Clip8V(Vector128.SubtractSaturate(
            Vector128.Add(my, V8708), Vector128.Add(mu1, mv2)));
        // B = clip8(my + mu3 - 17685)
        Vector128<ushort> b = Clip8V(Vector128.SubtractSaturate(Vector128.Add(my, mu3), V17685));

        Vector128<byte> rb = Vector128.Narrow(r, r);
        Vector128<byte> gb = Vector128.Narrow(g, g);
        Vector128<byte> bb = Vector128.Narrow(b, b);

        out0 = Vector128.Shuffle(rb, IdxRLo) | Vector128.Shuffle(gb, IdxGLo)
             | Vector128.Shuffle(bb, IdxBLo) | AlphaMask;
        out1 = Vector128.Shuffle(rb, IdxRHi) | Vector128.Shuffle(gb, IdxGHi)
             | Vector128.Shuffle(bb, IdxBHi) | AlphaMask;
    }

    // pshufb 索引：0x80 表示该通道在此字节位置不参与（置零）。
    // 每个字节位置必须且只有一个通道取到值，否则 OR 会把两个通道串在一起。
    private static readonly Vector128<byte> IdxRLo = Vector128.Create(
        (byte)0, 0x80, 0x80, 0x80, 1, 0x80, 0x80, 0x80, 2, 0x80, 0x80, 0x80, 3, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> IdxGLo = Vector128.Create(
        (byte)0x80, 0, 0x80, 0x80, 0x80, 1, 0x80, 0x80, 0x80, 2, 0x80, 0x80, 0x80, 3, 0x80, 0x80);
    private static readonly Vector128<byte> IdxBLo = Vector128.Create(
        (byte)0x80, 0x80, 0, 0x80, 0x80, 0x80, 1, 0x80, 0x80, 0x80, 2, 0x80, 0x80, 0x80, 3, 0x80);
    private static readonly Vector128<byte> IdxRHi = Vector128.Create(
        (byte)4, 0x80, 0x80, 0x80, 5, 0x80, 0x80, 0x80, 6, 0x80, 0x80, 0x80, 7, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> IdxGHi = Vector128.Create(
        (byte)0x80, 4, 0x80, 0x80, 0x80, 5, 0x80, 0x80, 0x80, 6, 0x80, 0x80, 0x80, 7, 0x80, 0x80);
    private static readonly Vector128<byte> IdxBHi = Vector128.Create(
        (byte)0x80, 0x80, 4, 0x80, 0x80, 0x80, 5, 0x80, 0x80, 0x80, 6, 0x80, 0x80, 0x80, 7, 0x80);
    private static readonly Vector128<byte> AlphaMask = Vector128.Create(
        (byte)0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF);

    private static readonly Vector128<byte> InterleaveIdx = Vector128.Create(
        (byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);

    /// <summary>8 个连续字节零扩展为 8 个 16 位通道。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Load8Wide(ref byte src)
    {
        Vector128<byte> wide = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref src)).AsByte();
        return Vector128.WidenLower(wide);
    }

    /// <summary>
    /// 把 8 个像素对的两个通道分量（偶位/奇位）交织成 16 个像素值，
    /// 结果拆成两个 8 通道向量；支持时直接在寄存器中交织。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interleave8(Vector128<ushort> even, Vector128<ushort> odd,
                                    out Vector128<ushort> pix0, out Vector128<ushort> pix1)
    {
        Vector128<byte> pe = Vector128.Narrow(even, even);
        Vector128<byte> po = Vector128.Narrow(odd, odd);
        if (SimdCompat.ByteShuffleSupported)
        {
            var interleaved = SimdCompat.UnpackLow(pe, po);
            pix0 = Vector128.WidenLower(interleaved);
            pix1 = Vector128.WidenUpper(interleaved);
            return;
        }
        Span<byte> tmp = stackalloc byte[16];
        ref byte t = ref MemoryMarshal.GetReference(tmp);
        Unsafe.WriteUnaligned(ref t, pe.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref t, 8), po.AsUInt64().GetElement(0));
        Vector128<byte> shuffled = Vector128.Shuffle(Vector128.LoadUnsafe(ref t), InterleaveIdx);
        pix0 = Vector128.WidenLower(shuffled);
        pix1 = Vector128.WidenUpper(shuffled);
    }

    /// <summary>
    /// 8 个像素对的色度双线性插值（对应 C 的 avg / diag_12 / diag_03 三式）。
    /// 输出上/下行的偶位与奇位分量。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Interpolate8(ref byte top, ref byte cur, int x,
                                     out Vector128<ushort> topEven, out Vector128<ushort> topOdd,
                                     out Vector128<ushort> botEven, out Vector128<ushort> botOdd)
    {
        Vector128<ushort> a = Load8Wide(ref Unsafe.Add(ref top, x - 1));
        Vector128<ushort> b = Load8Wide(ref Unsafe.Add(ref top, x));
        Vector128<ushort> c = Load8Wide(ref Unsafe.Add(ref cur, x - 1));
        Vector128<ushort> d = Load8Wide(ref Unsafe.Add(ref cur, x));

        Vector128<ushort> eight = Vector128.Create((ushort)8);
        Vector128<ushort> avg = Vector128.Add(Vector128.Add(Vector128.Add(a, b), Vector128.Add(c, d)), eight);
        Vector128<ushort> diag12 = Vector128.ShiftRightLogical(
            Vector128.Add(avg, Vector128.Add(Vector128.Add(b, c), Vector128.Add(b, c))), 3);
        Vector128<ushort> diag03 = Vector128.ShiftRightLogical(
            Vector128.Add(avg, Vector128.Add(Vector128.Add(a, d), Vector128.Add(a, d))), 3);

        topEven = Vector128.ShiftRightLogical(Vector128.Add(diag12, a), 1);
        topOdd = Vector128.ShiftRightLogical(Vector128.Add(diag03, b), 1);
        botEven = Vector128.ShiftRightLogical(Vector128.Add(diag03, c), 1);
        botOdd = Vector128.ShiftRightLogical(Vector128.Add(diag12, d), 1);
    }

    // ------------------------------------------------------------------
    // Fancy 上采样器（对应 src/dsp/upsampling.c 的
    // UPSAMPLE_FUNC(UpsampleRgbaLinePair_C, VP8YuvToRgba, 4)）
    //
    // 对 2x2 色度样本 [a b; c d]（a=左上 top_uv[x-1]，b=右上 top_uv[x]，
    // c=左下 cur_uv[x-1]，d=右下 cur_uv[x]），四个输出像素的双线性插值为：
    //   (9a+3b+3c+d+8)/16   (3a+9b+3c+d+8)/16
    //   (3a+b+9c+3d+8)/16   (a+3b+3c+9d+8)/16
    // C 里 u/v 打包进一个 uint 的低/高 16 位并行运算；SIMD 路径直接按平面分开算，
    // 每通道最大值 4*255+8，不会发生跨通道进位，与 C 的 uint32_t 算术逐位一致。
    // ------------------------------------------------------------------

    /// <summary>
    /// 处理一对相邻输出行（top 行与 bottom 行），宽度为 len 个像素。
    /// 对应 C 宏展开的函数体；bottomYOff &lt; 0 对应 C 的 <c>bottom_y == NULL</c>
    /// （此时 bottomDstOff 同样为 -1，下行完全不写）。
    /// </summary>
    private static void UpsampleRgbaLinePair(
        byte[] yPlane, int topYOff, int bottomYOff,
        byte[] uPlane, byte[] vPlane, int topUvOff, int curUvOff,
        byte[] dst, int topDstOff, int bottomDstOff, int len)
    {
        // C: const int last_pixel_pair = (len - 1) >> 1;
        int lastPixelPair = (len - 1) >> 1;
        bool hasBottom = bottomYOff >= 0;

        ref byte yTop = ref MemoryMarshal.GetArrayDataReference(yPlane);
        ref byte uTop = ref MemoryMarshal.GetArrayDataReference(uPlane);
        ref byte vTop = ref MemoryMarshal.GetArrayDataReference(vPlane);
        ref byte dstRef = ref MemoryMarshal.GetArrayDataReference(dst);

        // C: uint32_t tl_uv = LOAD_UV(top_u[0], top_v[0]);  // 左上样本
        uint tlUv = LoadUv(uPlane, topUvOff, vPlane, topUvOff);
        // C: uint32_t l_uv = LOAD_UV(cur_u[0], cur_v[0]);   // 左下样本
        uint lUv = LoadUv(uPlane, curUvOff, vPlane, curUvOff);

        // 左边界首像素：左/右样本镜像为同一点，权重退化为 (3:1)/4。
        // C: uv0 = (3 * tl_uv + l_uv + 0x00020002u) >> 2;（0x00020002 为两通道各自的 +2 舍入）
        {
            uint uv0 = (3u * tlUv + lUv + 0x00020002u) >> 2;
            YuvToRgba(yPlane[topYOff], (int)(uv0 & 0xff), (int)(uv0 >> 16), dst, topDstOff);
        }
        if (hasBottom)
        {
            uint uv0 = (3u * lUv + tlUv + 0x00020002u) >> 2;
            YuvToRgba(yPlane[bottomYOff], (int)(uv0 & 0xff), (int)(uv0 >> 16), dst, bottomDstOff);
        }

        ref byte uCur = ref Unsafe.Add(ref uTop, curUvOff);
        ref byte uTopRow = ref Unsafe.Add(ref uTop, topUvOff);
        ref byte vCur = ref Unsafe.Add(ref vTop, curUvOff);
        ref byte vTopRow = ref Unsafe.Add(ref vTop, topUvOff);
        ref byte dTop = ref Unsafe.Add(ref dstRef, topDstOff);
        ref byte yTopRow = ref Unsafe.Add(ref yTop, topYOff);

        int x = 1;

        // 向量主体：一次 8 个像素对（16 个输出像素），需要 x..x+7 全部落在 lastPixelPair 内
        if (hasBottom)
        {
            ref byte yBot = ref Unsafe.Add(ref yTop, bottomYOff);
            ref byte dBot = ref Unsafe.Add(ref dstRef, bottomDstOff);
            for (; x + 7 <= lastPixelPair; x += 8)
                ChunkPair16(ref yTopRow, ref yBot, ref uTopRow, ref uCur, ref vTopRow, ref vCur,
                            ref dTop, ref dBot, x);
        }
        else
        {
            for (; x + 7 <= lastPixelPair; x += 8)
                ChunkSingle16(ref yTopRow, ref uTopRow, ref uCur, ref vTopRow, ref vCur, ref dTop, x);
        }

        // 标量余量：向量块不推进 tlUv/lUv，这里按 C 的语义重新取到 x-1 处的样本
        if (x <= lastPixelPair)
        {
            tlUv = LoadUv(uPlane, topUvOff + x - 1, vPlane, topUvOff + x - 1);
            lUv = LoadUv(uPlane, curUvOff + x - 1, vPlane, curUvOff + x - 1);
        }
        for (; x <= lastPixelPair; ++x)
        {
            // C: t_uv = LOAD_UV(top_u[x], top_v[x]);  // 右上样本
            uint tUv = LoadUv(uPlane, topUvOff + x, vPlane, topUvOff + x);
            // C: uv = LOAD_UV(cur_u[x], cur_v[x]);    // 右下样本
            uint uv = LoadUv(uPlane, curUvOff + x, vPlane, curUvOff + x);

            // 与第一、第二对角线相关的不变量（0x00080008 为两通道各自的 +8 舍入）
            // C: avg = tl_uv + t_uv + l_uv + uv + 0x00080008u;
            uint avg = tlUv + tUv + lUv + uv + 0x00080008u;
            // C: diag_12 = (avg + 2 * (t_uv + l_uv)) >> 3;  → (a+3b+3c+d+8)>>3
            uint diag12 = (avg + 2u * (tUv + lUv)) >> 3;
            // C: diag_03 = (avg + 2 * (tl_uv + uv)) >> 3;   → (3a+b+c+3d+8)>>3
            uint diag03 = (avg + 2u * (tlUv + uv)) >> 3;

            {
                uint uv0 = (diag12 + tlUv) >> 1;
                uint uv1 = (diag03 + tUv) >> 1;
                YuvToRgba(yPlane[topYOff + 2 * x - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, topDstOff + (2 * x - 1) * 4);
                YuvToRgba(yPlane[topYOff + 2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16),
                          dst, topDstOff + (2 * x) * 4);
            }
            if (hasBottom)
            {
                uint uv0 = (diag03 + lUv) >> 1;
                uint uv1 = (diag12 + uv) >> 1;
                YuvToRgba(yPlane[bottomYOff + 2 * x - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, bottomDstOff + (2 * x - 1) * 4);
                YuvToRgba(yPlane[bottomYOff + 2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16),
                          dst, bottomDstOff + (2 * x) * 4);
            }

            tlUv = tUv;
            lUv = uv;
        }

        // C: if (!(len & 1)) —— 偶数宽时右边界尾像素同样做镜像（权重 (3:1)/4）。
        // 奇数宽时最后一个像素已在循环中由像素对 (2x-1, 2x) 覆盖。
        // C 里循环退出时 tl_uv/l_uv 恰好是 uv[lastPair]，这里直接显式取值，
        // 不依赖循环走了标量路径还是向量路径。
        if ((len & 1) == 0)
        {
            uint tl = LoadUv(uPlane, topUvOff + lastPixelPair, vPlane, topUvOff + lastPixelPair);
            uint l = LoadUv(uPlane, curUvOff + lastPixelPair, vPlane, curUvOff + lastPixelPair);
            {
                uint uv0 = (3u * tl + l + 0x00020002u) >> 2;
                YuvToRgba(yPlane[topYOff + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, topDstOff + (len - 1) * 4);
            }
            if (hasBottom)
            {
                uint uv0 = (3u * l + tl + 0x00020002u) >> 2;
                YuvToRgba(yPlane[bottomYOff + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, bottomDstOff + (len - 1) * 4);
            }
        }
    }

    private static uint LoadUv(byte[] uPlane, int uOff, byte[] vPlane, int vOff)
        => (uint)(uPlane[uOff] | (vPlane[vOff] << 16));

    /// <summary>一次 8 个色度对、上下两行共 16 个输出像素的向量块。</summary>
    private static void ChunkPair16(
        ref byte yTop, ref byte yBot,
        ref byte uTop, ref byte uCur, ref byte vTop, ref byte vCur,
        ref byte dTop, ref byte dBot, int x)
    {
        Interpolate8(ref uTop, ref uCur, x, out var tue, out var tuo, out var bue, out var buo);
        Interpolate8(ref vTop, ref vCur, x, out var tve, out var tvo, out var bve, out var bvo);

        Interleave8(tue, tuo, out var tu0, out var tu1);
        Interleave8(bue, buo, out var bu0, out var bu1);
        Interleave8(tve, tvo, out var tv0, out var tv1);
        Interleave8(bve, bvo, out var bv0, out var bv1);

        int yOff = 2 * x - 1;
        int dOff = yOff * 4;

        Vector128<byte> ty = Vector128.LoadUnsafe(ref Unsafe.Add(ref yTop, yOff));
        YuvToRgba8(Vector128.WidenLower(ty), tu0, tv0, out var a0, out var a1);
        YuvToRgba8(Vector128.WidenUpper(ty), tu1, tv1, out var a2, out var a3);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff), a0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff + 16), a1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff + 32), a2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff + 48), a3);

        Vector128<byte> by = Vector128.LoadUnsafe(ref Unsafe.Add(ref yBot, yOff));
        YuvToRgba8(Vector128.WidenLower(by), bu0, bv0, out var b0, out var b1);
        YuvToRgba8(Vector128.WidenUpper(by), bu1, bv1, out var b2, out var b3);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dBot, dOff), b0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dBot, dOff + 16), b1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dBot, dOff + 32), b2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dBot, dOff + 48), b3);
    }

    /// <summary>只有上行的向量块（首行 / 末行镜像场景）。</summary>
    private static void ChunkSingle16(
        ref byte yTop, ref byte uTop, ref byte uCur, ref byte vTop, ref byte vCur,
        ref byte dTop, int x)
    {
        Interpolate8(ref uTop, ref uCur, x, out var tue, out var tuo, out _, out _);
        Interpolate8(ref vTop, ref vCur, x, out var tve, out var tvo, out _, out _);

        Interleave8(tue, tuo, out var tu0, out var tu1);
        Interleave8(tve, tvo, out var tv0, out var tv1);

        int yOff = 2 * x - 1;
        int dOff = yOff * 4;

        Vector128<byte> ty = Vector128.LoadUnsafe(ref Unsafe.Add(ref yTop, yOff));
        YuvToRgba8(Vector128.WidenLower(ty), tu0, tv0, out var a0, out var a1);
        YuvToRgba8(Vector128.WidenUpper(ty), tu1, tv1, out var a2, out var a3);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff), a0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff + 16), a1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff + 32), a2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dTop, dOff + 48), a3);
    }

    // ------------------------------------------------------------------
    // 行调度（对应 src/dec/io_dec.c 的 EmitFancyRGB，按整图单次调用展开）
    // ------------------------------------------------------------------

    /// <summary>
    /// 行级流式解码入口（与 ConvertToRgba 共用实现）：按输出行对调用同一 fancy 上采样实现，保证与整帧路径逐位一致。
    /// <paramref name="topUvOff"/> / <paramref name="curUvOff"/> 分别指向行对的上、下色度行；
    /// <paramref name="bottomYOff"/> 与 <paramref name="bottomDstOff"/> 为 -1 时只写上行（镜像行）。
    /// </summary>
    internal static void ConvertLinePair(
        byte[] yPlane, int topYOff, int bottomYOff,
        byte[] uPlane, byte[] vPlane, int topUvOff, int curUvOff,
        byte[] dst, int topDstOff, int bottomDstOff, int len) =>
        UpsampleRgbaLinePair(yPlane, topYOff, bottomYOff,
            uPlane, vPlane, topUvOff, curUvOff,
            dst, topDstOff, bottomDstOff, len);

    /// <summary>
    /// YUV420（含完整平面，无边框）→ RGBA。每像素 4 字节 R,G,B,A（A=255）。
    /// fancy upsampling，与 libwebp WebPDecodeRGBA 默认路径一致。
    /// </summary>
    /// <param name="y">Y 平面（height 行 × strideY 列，有效 width 列）</param>
    /// <param name="u">U 平面（(height+1)/2 行 × strideUV 列，有效 (width+1)/2 列）</param>
    /// <param name="v">V 平面，同 U</param>
    public static byte[] ConvertToRgba(byte[] y, byte[] u, byte[] v, int width, int height, int strideY, int strideUV)
    {
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(u);
        ArgumentNullException.ThrowIfNull(v);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (strideY < width)
            throw new ArgumentException("Y 平面行跨度必须不小于有效宽度。", nameof(strideY));
        if (strideUV < (width + 1) / 2)
            throw new ArgumentException("U/V 平面行跨度必须不小于有效色度宽度。", nameof(strideUV));
        if ((long)y.Length < (long)(height - 1) * strideY + width)
            throw new ArgumentException("Y 平面数据长度不足。", nameof(y));
        if ((long)u.Length < (long)((height + 1) / 2 - 1) * strideUV + (width + 1) / 2)
            throw new ArgumentException("U 平面数据长度不足。", nameof(u));
        if ((long)v.Length < (long)((height + 1) / 2 - 1) * strideUV + (width + 1) / 2)
            throw new ArgumentException("V 平面数据长度不足。", nameof(v));

        int dstStride = checked(width * 4);
        byte[] rgba = new byte[checked((long)dstStride * height)];

        // 对应 EmitFancyRGB：整图一次交付，mb_y=0、y_end=height，
        // 且 crop_top + y_end >= crop_bottom 恒成立（全图），
        // 故只保留"首行镜像 / 行对循环 / 偶数高末行镜像"三段流程。
        int curYOff = 0;      // C: cur_y（io->y 当前行）
        int curUvOff = 0;     // C: cur_u / cur_v（当前色度行）
        int dstOff = 0;       // C: dst（当前 RGBA 行）
        int yRow = 0;         // C: y = io->mb_y
        int yEnd = height;    // C: y_end = io->mb_y + io->mb_h

        // 首行特判：上边界处镜像 u/v 样本（top 与 cur 同指一行），bottom_y == NULL。
        UpsampleRgbaLinePair(y, curYOff, -1, u, v, curUvOff, curUvOff,
                             rgba, dstOff, -1, width);

        // 逐行对处理：输出第 (yRow+1, yRow+2) 行，色度样本为 (top=第 yRow/2 行, cur=第 yRow/2+1 行)。
        // C: for (; y + 2 < y_end; y += 2)
        for (; yRow + 2 < yEnd; yRow += 2)
        {
            int topUvOff = curUvOff;
            curUvOff += strideUV;
            dstOff += 2 * dstStride;
            curYOff += 2 * strideY;
            UpsampleRgbaLinePair(y, curYOff - strideY, curYOff, u, v, topUvOff, curUvOff,
                                 rgba, dstOff - dstStride, dstOff, width);
        }

        // C: cur_y += io->y_stride;（移动到最后一行）
        curYOff += strideY;

        // 整图转换即解码终态（crop 全覆盖），不进入 tmp 暂存分支；
        // 偶数高时处理最后一行：下边界处镜像 u/v（cur 兼作 top 与 cur），bottom_dst == NULL。
        if ((yEnd & 1) == 0)
        {
            UpsampleRgbaLinePair(y, curYOff, -1, u, v, curUvOff, curUvOff,
                                 rgba, dstOff + dstStride, -1, width);
        }

        return rgba;
    }
}
