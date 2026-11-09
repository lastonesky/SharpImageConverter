using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 系数量化公用的取整口径。
///
/// 与 libwebp <c>src/enc/quant_enc.c</c> 的 <c>ExpandMatrix</c> 对齐：量化不是纯
/// 「四舍五入」，而是每类系数各带一个偏置（<c>kBiasMatrices</c>，单位 1/256），
/// 即 <c>level = floor(|coeff| / dq + bias/256)</c>。
///
/// 差别看似只有 0.07 个量化步，但在**平坦区**会决定位流是否振荡：色度 DC 的残差
/// 常常稳定在 ±1~2，步长 30 时 <c>2/30 = 0.533</c>——按 0.5 取整会一直量化到
/// 相邻电平（每次重建反向过冲 2 个灰度级），下一个块再反向修正，于是整片纯色
/// 每个 4×4 块都买一个 token（1024² 纯色实测 1.8KB → 4.2KB，PSNR 反而更低）；
/// 按 0.43 取整则落回 0，直接 skip。libwebp 的偏置表就是这个边界的参考口径。
/// </summary>
internal static class Vp8Quant
{
    /// <summary>
    /// <c>kBiasMatrices[type][dc/ac]</c>：0=Y1（含 i4x4），1=Y2，2=UV。
    /// 亮度 DC 偏置最小（0.375，DC 更精确），色度整体最钝。
    /// </summary>
    private static readonly byte[,] Bias =
    {
        { 96, 110 },
        { 96, 108 },
        { 110, 115 },
    };

    /// <summary>
    /// <c>kFreqSharpening</c>：仅亮度 AC 使用的频率锐化偏置（单位 1/2048）。
    /// 中频系数在量化前被轻微抬高，换取边缘/纹理的主观锐度。
    /// </summary>
    private static readonly byte[] FreqSharpening =
    {
        0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90,
    };

    /// <summary>按 token 类型（0=Y1、1=Y2、2=UV、3=i4）取量化偏置（1/256）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int BiasOf(int type, int scanIndex)
    {
        int matrix = type == 3 ? 0 : type;
        return Bias[matrix, scanIndex == 0 ? 0 : 1];
    }

    /// <summary>亮度 AC 的频率锐化量（加在幅值上），其余类型为 0。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Sharpen(int type, int scanIndex, int step)
    {
        if (type != 0 && type != 3) return 0;
        if (scanIndex == 0) return 0;
        return (FreqSharpening[scanIndex] * step) >> 11;
    }

    /// <summary>定点精度（libwebp <c>vp8i_enc.h</c> 的 <c>QFIX</c>）。</summary>
    internal const int QFix = 17;

    /// <summary>
    /// 带偏置的量化，逐位对齐 libwebp 的
    /// <c>QUANTDIV(n, iQ, B) = (n * iQ + B) >> QFIX</c>，其中
    /// <c>iQ = (1 &lt;&lt; QFIX) / dq</c>、<c>B = bias &lt;&lt; (QFIX - 8)</c>。
    ///
    /// 语义等价于 <c>floor(|coeff| / dq + bias/256)</c>：一次 32 位乘加就能出结果，
    /// 没有除法、没有取整校正分支。libwebp 明确接受 <c>iQ</c> 截断带来的极少数
    /// 边界偏差（注释称这是「唯一真正丢位的一行」），本实现跟随它以保证与 C 版同口径。
    /// <paramref name="iq"/> 由 <see cref="DivisionInverse"/> 给出。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Quantize(int coefficient, int iq, int bias)
    {
        if (coefficient == 0)
            return 0;

        uint magnitude = (uint)(coefficient < 0 ? -coefficient : coefficient);
        int level = (int)((magnitude * (uint)iq + ((uint)bias << (QFix - 8))) >> QFix);
        if (level > Vp8Trellis.MaxCoefficient)
            level = Vp8Trellis.MaxCoefficient;
        return coefficient < 0 ? -level : level;
    }

    /// <summary>
    /// 量化步长 <paramref name="dq"/> 的定点倒数 <c>iQ = (1 &lt;&lt; QFIX) / dq</c>
    /// （**向下**取整，与 libwebp <c>ExpandMatrix</c> 的 <c>m-&gt;iq[i] = (1 &lt;&lt; QFIX) / m-&gt;q[i]</c> 一致）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DivisionInverse(int dq) => (1 << QFix) / dq;

    /// <summary>
    /// DC 电平 <paramref name="level"/> 的实际重建量（灰度级），与解码端
    /// <c>TransformDC</c> 的 <c>(dc + 4) &gt;&gt; 3</c> 逐位一致。
    ///
    /// 这个映射**不是**线性的：<c>dq = 13</c> 时电平 1 重建 2 个灰度级、电平 2 重建 3 个，
    /// 而系数域看的是 <c>8·残留</c>。于是「残留 1、dq 13」在系数域是 8/13 = 0.62（够一个
    /// 电平），在像素域却是「要么不动、要么跳 2 格」——跳过去就过冲 1 格，下一个块再
    /// 反着跳回来，形成逐块买 token 的极限环（纯色/平坦区体积翻数倍）。
    /// 因此 DC 位置的电平必须按这里的真实重建量比较，而不是按系数比取整。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DcRecon(int level, int dq)
    {
        int dc = level * dq;
        // 与解码端一致：算术右移，负数向下取整。
        return (dc + 4) >> 3;
    }

    /// <summary>DC 位置的像素域误差（以 8 倍灰度级为单位，等价于系数域尺度）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long DcPixelError(int coefficient, int level, int dq)
    {
        long recon = (long)DcRecon(level, dq) << 3;
        long diff = coefficient - recon;
        return diff < 0 ? -diff : diff;
    }
}
