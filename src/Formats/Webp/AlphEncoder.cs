using System;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// ALPH chunk 编码器：把独立 alpha 平面压缩为 ALPH 负载（1 字节头部 + 负载）。
///
/// 头部位布局（RFC 9649 §2.5）：
/// bits 0~1 = 压缩方式（1 = 无损 VP8L，负载为去掉 5 字节帧头的 VP8L 图像流）；
/// bits 2~3 = 过滤方式（本实现写 0 = 无过滤）；
/// bits 4~5 = 预处理方式（写 0 = 无预处理）；
/// bits 6~7 = 保留位，必须为 0。
///
/// ALPH 的 VP8L 位流按“灰度图像”承载 alpha：每个像素的 GREEN 字节即 alpha 值
/// （与解码端 <see cref="AlphDecoder"/> 及 libwebp 的 ExtractAlpha 一致）。
/// </summary>
internal static class AlphEncoder
{
    /// <summary>ALPH 头部：压缩方式 1（VP8L 无损）、filter 0、preprocessing 0、保留 0。</summary>
    private const byte HeaderCompressedVp8L = 0x01;

    /// <summary>VP8L 帧头固定 5 字节（40 bit：signature/width/height/alpha/version）。</summary>
    private const int Vp8LHeaderBytes = 5;

    /// <summary>
    /// 把 alpha 平面编码为 ALPH chunk 数据。
    /// </summary>
    /// <param name="alpha">alpha 值，长度必须为 width * height，行优先。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    public static byte[] Encode(ReadOnlySpan<byte> alpha, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "ALPH 尺寸必须为正");
        if (alpha.Length != checked(width * height))
            throw new ArgumentException("alpha 数据长度与尺寸不匹配", nameof(alpha));

        // VP8L 内部像素为 ARGB，ALPH 只取 GREEN 通道，因此把 alpha 放进 G、其余通道置 0。
        var rgba = new byte[checked(width * height * 4)];
        for (int i = 0; i < alpha.Length; i++)
            rgba[i * 4 + 1] = alpha[i];

        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, Vp8LEncoderOptions.Auto);
        if (vp8l.Length < Vp8LHeaderBytes)
            throw new WebpBitstreamException("VP8L 编码器输出过短，无法作为 ALPH 负载");

        // 去掉 VP8L 5 字节帧头（40 bit 恰好字节对齐），其余即为 ALPH 所需的裸图像流。
        var result = new byte[1 + vp8l.Length - Vp8LHeaderBytes];
        result[0] = HeaderCompressedVp8L;
        vp8l.AsSpan(Vp8LHeaderBytes).CopyTo(result.AsSpan(1));
        return result;
    }
}
