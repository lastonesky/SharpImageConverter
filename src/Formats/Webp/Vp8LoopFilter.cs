namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 环内去块滤波器（in-loop deblocking filter），严格对照 libwebp
/// src/dsp/dec.c 的 C 参考实现（非 SIMD 路径），保证位精确一致。
/// 对应 C 函数：DoFilter2_C / DoFilter4_C / DoFilter6_C / Hev /
/// NeedsFilter_C / NeedsFilter2_C / FilterLoop26_C / FilterLoop24_C /
/// SimpleVFilter16_C / SimpleHFilter16_C / VFilter16_C / HFilter16_C /
/// VFilter16i_C / HFilter16i_C / VFilter8_C / HFilter8_C / VFilter8i_C / HFilter8i_C。
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
    // 基本滤波原语
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
    // 简单环内滤波（RFC 6386 第 15.2 节）
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 SimpleVFilter16_C：宏块垂直边界的简单滤波（16 像素宽）。
    /// off 为边界下一行首像素的下标（与 C 传入的指针 p 一致）。
    /// </summary>
    public static void VFilter16Simple(byte[] y, int off, int stride, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; ++i)
        {
            if (NeedsFilter(y, off + i, stride, thresh2))
            {
                DoFilter2(y, off + i, stride);
            }
        }
    }

    /// <summary>
    /// 对应 SimpleHFilter16_C：宏块水平边界的简单滤波（16 像素高）。
    /// </summary>
    public static void HFilter16Simple(byte[] y, int off, int stride, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; ++i)
        {
            if (NeedsFilter(y, off + i * stride, 1, thresh2))
            {
                DoFilter2(y, off + i * stride, 1);
            }
        }
    }

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
    /// 对应 FilterLoop26_C：沿边界逐像素判定，hev 时 DoFilter2，否则 DoFilter6。
    /// hstride 为跨越边界方向上的像素间距，vstride 为沿边界方向上的像素间距。
    /// </summary>
    private static void FilterLoop26(byte[] p, int off, int hstride, int vstride,
                                     int size, int thresh, int ithresh, int hevThresh)
    {
        int thresh2 = 2 * thresh + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, off, hstride, thresh2, ithresh))
            {
                if (Hev(p, off, hstride, hevThresh))
                {
                    DoFilter2(p, off, hstride);
                }
                else
                {
                    DoFilter6(p, off, hstride);
                }
            }
            off += vstride;
        }
    }

    /// <summary>
    /// 对应 FilterLoop24_C：沿边界逐像素判定，hev 时 DoFilter2，否则 DoFilter4。
    /// </summary>
    private static void FilterLoop24(byte[] p, int off, int hstride, int vstride,
                                     int size, int thresh, int ithresh, int hevThresh)
    {
        int thresh2 = 2 * thresh + 1;
        while (size-- > 0)
        {
            if (NeedsFilter2(p, off, hstride, thresh2, ithresh))
            {
                if (Hev(p, off, hstride, hevThresh))
                {
                    DoFilter2(p, off, hstride);
                }
                else
                {
                    DoFilter4(p, off, hstride);
                }
            }
            off += vstride;
        }
    }

    // ------------------------------------------------------------------
    // 公开滤波入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 对应 VFilter16_C：亮度宏块垂直外边界滤波。
    /// </summary>
    public static void VFilter16(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop26(y, off, stride, 1, 16, thresh, ithresh, hevThresh);
    }

    /// <summary>
    /// 对应 HFilter16_C：亮度宏块水平外边界滤波。
    /// </summary>
    public static void HFilter16(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop26(y, off, 1, stride, 16, thresh, ithresh, hevThresh);
    }

    /// <summary>
    /// 对应 VFilter16i_C：亮度宏块内部三条垂直边界滤波。
    /// </summary>
    public static void VFilter16i(byte[] y, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        for (int k = 3; k > 0; --k)
        {
            off += 4 * stride;
            FilterLoop24(y, off, stride, 1, 16, thresh, ithresh, hevThresh);
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
            FilterLoop24(y, off, 1, stride, 16, thresh, ithresh, hevThresh);
        }
    }

    /// <summary>
    /// 对应 VFilter8_C 的色度垂直外边界滤波。
    /// C 版本一次接收 u、v 两个指针并分别滤波；本实现每次调用处理一个色度平面，
    /// 调用方分别以 U、V 平面（同一或不同缓冲区）调用一次，操作序列与 C 完全一致。
    /// </summary>
    public static void VFilter8(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop26(uv, off, stride, 1, 8, thresh, ithresh, hevThresh);
    }

    /// <summary>
    /// 对应 HFilter8_C 的色度水平外边界滤波（单次处理一个色度平面，同 VFilter8 说明）。
    /// </summary>
    public static void HFilter8(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop26(uv, off, 1, stride, 8, thresh, ithresh, hevThresh);
    }

    /// <summary>
    /// 对应 VFilter8i_C 的色度垂直内边界滤波（单次处理一个色度平面，同 VFilter8 说明）。
    /// </summary>
    public static void VFilter8i(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop24(uv, off + 4 * stride, stride, 1, 8, thresh, ithresh, hevThresh);
    }

    /// <summary>
    /// 对应 HFilter8i_C 的色度水平内边界滤波（单次处理一个色度平面，同 VFilter8 说明）。
    /// </summary>
    public static void HFilter8i(byte[] uv, int off, int stride, int thresh, int ithresh, int hevThresh)
    {
        FilterLoop24(uv, off + 4, 1, stride, 8, thresh, ithresh, hevThresh);
    }
}
