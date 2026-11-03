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
    // 定点 YUV→RGB（对应 src/dsp/yuv.h 内联函数）
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
    // Fancy 上采样器（对应 src/dsp/upsampling.c 的
    // UPSAMPLE_FUNC(UpsampleRgbaLinePair_C, VP8YuvToRgba, 4)）
    //
    // 对 2x2 色度样本 [a b; c d]（a=左上 top_uv[x-1]，b=右上 top_uv[x]，
    // c=左下 cur_uv[x-1]，d=右下 cur_uv[x]），四个输出像素的双线性插值为：
    //   (9a+3b+3c+d+8)/16   (3a+9b+3c+d+8)/16
    //   (3a+b+9c+3d+8)/16   (a+3b+3c+9d+8)/16
    // u/v 打包进一个 uint 的低/高 16 位（C: LOAD_UV），两条 16 位通道并行运算；
    // 每个通道最大值 4*255+8，不会发生跨通道进位，与 C 的 uint32_t 算术逐位一致。
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

        // C: uint32_t tl_uv = LOAD_UV(top_u[0], top_v[0]);  // 左上样本
        uint tlUv = (uint)(uPlane[topUvOff] | (vPlane[topUvOff] << 16));
        // C: uint32_t l_uv = LOAD_UV(cur_u[0], cur_v[0]);   // 左下样本
        uint lUv = (uint)(uPlane[curUvOff] | (vPlane[curUvOff] << 16));

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

        for (int x = 1; x <= lastPixelPair; ++x)
        {
            // C: t_uv = LOAD_UV(top_u[x], top_v[x]);  // 右上样本
            uint tUv = (uint)(uPlane[topUvOff + x] | (vPlane[topUvOff + x] << 16));
            // C: uv = LOAD_UV(cur_u[x], cur_v[x]);    // 右下样本
            uint uv = (uint)(uPlane[curUvOff + x] | (vPlane[curUvOff + x] << 16));

            // 与第一、第二对角线相关的不变量（0x00080008 为两通道各自的 +8 舍入）
            // C: avg = tl_uv + t_uv + l_uv + uv + 0x00080008u;
            uint avg = tlUv + tUv + lUv + uv + 0x00080008u;
            // C: diag_12 = (avg + 2 * (t_uv + l_uv)) >> 3;  → (a+3b+3c+d+8)>>3
            uint diag12 = (avg + 2u * (tUv + lUv)) >> 3;
            // C: diag_03 = (avg + 2 * (tl_uv + uv)) >> 3;   → (3a+b+c+3d+8)>>3
            uint diag03 = (avg + 2u * (tlUv + uv)) >> 3;

            // 上行像素对：uv0 ≈ (9a+3b+3c+d+8)/16，uv1 ≈ (3a+9b+3c+d+8)/16
            {
                uint uv0 = (diag12 + tlUv) >> 1;
                uint uv1 = (diag03 + tUv) >> 1;
                YuvToRgba(yPlane[topYOff + 2 * x - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, topDstOff + (2 * x - 1) * 4);
                YuvToRgba(yPlane[topYOff + 2 * x], (int)(uv1 & 0xff), (int)(uv1 >> 16),
                          dst, topDstOff + (2 * x) * 4);
            }
            // 下行像素对：uv0 ≈ (3a+b+9c+3d+8)/16，uv1 ≈ (a+3b+3c+9d+8)/16
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
        if ((len & 1) == 0)
        {
            {
                uint uv0 = (3u * tlUv + lUv + 0x00020002u) >> 2;
                YuvToRgba(yPlane[topYOff + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, topDstOff + (len - 1) * 4);
            }
            if (hasBottom)
            {
                uint uv0 = (3u * lUv + tlUv + 0x00020002u) >> 2;
                YuvToRgba(yPlane[bottomYOff + len - 1], (int)(uv0 & 0xff), (int)(uv0 >> 16),
                          dst, bottomDstOff + (len - 1) * 4);
            }
        }
    }

    // ------------------------------------------------------------------
    // 行调度（对应 src/dec/io_dec.c 的 EmitFancyRGB，按整图单次调用展开）
    // ------------------------------------------------------------------

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
