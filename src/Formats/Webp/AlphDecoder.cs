namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// ALPH chunk 解码器：独立 alpha 通道（用于有损 VP8 图像，无损 VP8L 自带 alpha）。
/// ALPH chunk 由 1 字节头部 + 负载组成，头部按位划分：
/// bits 0~1 = 压缩方式（0=原始未压缩字节，1=去掉 5 字节头的 VP8L 裸位流，2/3 为保留值）；
/// bits 2~3 = 过滤方式（0=none，1=horizontal，2=vertical，3=gradient）；
/// bits 4~5 = 预处理方式（0=none，1=encoder 端 level reduction，2/3 为保留值）。
/// 注意：level reduction 是编码端的**有损**预处理（把 alpha 量化到更少的级别），
/// libwebp 解码器只校验取值、不做任何恢复，直接按解码出的 alpha 使用；
/// 本实现与 libwebp 保持一致，否则无法逐像素对拍。
/// bits 6~7 = 保留位（必须为 0）。
/// 压缩方式 1 时，每个像素的 alpha 取自 VP8L 解码像素的 GREEN 字节（bits 8~15）。
/// </summary>
internal static class AlphDecoder
{
    private const int CompressionNone = 0;
    private const int CompressionVp8L = 1;

    /// <summary>
    /// 解码 ALPH chunk 为 alpha 数组（宽度 × 高度的 bytes）。
    /// </summary>
    /// <param name="data">ALPH chunk 原始字节（不含 chunk 头，含 1 字节 ALPH 头）。</param>
    /// <param name="width">目标宽度（通常等于 canvas 宽度）。</param>
    /// <param name="height">目标高度。</param>
    /// <returns>alpha 值数组，0~255。</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new WebpBitstreamException($"ALPH 尺寸非法：{width}x{height}");
        if (data.Length < 1)
            throw new WebpBitstreamException("ALPH 数据过短，缺少头部字节");

        byte header = data[0];
        int compression = header & 0x03;
        int filter = (header >> 2) & 0x03;
        int preprocessing = (header >> 4) & 0x03;
        int reserved = (header >> 6) & 0x03;

        if (reserved != 0)
            throw new WebpBitstreamException($"ALPH 头部保留位非零：0x{header:X2}");
        // libwebp 的 ALPHInit 对 preprocessing > ALPHA_PREPROCESSED_LEVELS(1) 返回
        // VP8_STATUS_BITSTREAM_ERROR；合法值 0/1 在解码阶段的处理完全相同。
        if (preprocessing > 1)
            throw new WebpBitstreamException($"ALPH 预处理方式 {preprocessing} 为保留值（合法值 0/1）");

        var payload = data.Slice(1);
        int pixelCount = width * height;

        byte[] alpha;
        if (compression == CompressionNone)
        {
            // 原始 alpha 字节，行优先排列
            if (payload.Length < pixelCount)
                throw new WebpBitstreamException(
                    $"ALPH 未压缩数据长度 {payload.Length} 小于所需 {width}x{height}={pixelCount}");
            alpha = payload.Slice(0, pixelCount).ToArray();
        }
        else if (compression == CompressionVp8L)
        {
            // 负载是不含 5 字节 VP8L 头的裸 VP8L 位流，尺寸由调用方给出
            var pixels = Vp8LDecoder.DecodeBareStream(payload, width, height);
            if (pixels.Length != pixelCount)
                throw new WebpBitstreamException(
                    $"ALPH VP8L 解码输出像素数 {pixels.Length} 与 {width}x{height}={pixelCount} 不符");

            alpha = new byte[pixelCount];
            for (int i = 0; i < pixelCount; i++)
                alpha[i] = (byte)((pixels[i] >> 8) & 0xFF); // GREEN 通道
        }
        else
        {
            throw new WebpUnsupportedException($"ALPH 压缩方式 {compression}（保留值）不支持");
        }

        ApplyInverseFilter(alpha, width, height, filter);
        return alpha;
    }

    /// <summary>
    /// 对解压后的 alpha 施加逆过滤（按行处理，使用已重建的输出像素）。
    /// </summary>
    private static void ApplyInverseFilter(Span<byte> alpha, int width, int height, int filter)
    {
        switch (filter)
        {
            case 0: // none
                break;

            case 1: // horizontal：out[x] = out[x] + out[x-1]
                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * width;
                    for (int x = 1; x < width; x++)
                    {
                        int idx = rowStart + x;
                        alpha[idx] = (byte)(alpha[idx] + alpha[idx - 1]);
                    }
                }
                break;

            case 2: // vertical：out[x] = out[x] + prevRow[x]
                for (int y = 1; y < height; y++)
                {
                    int rowStart = y * width;
                    int prevStart = rowStart - width;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowStart + x;
                        alpha[idx] = (byte)(alpha[idx] + alpha[prevStart + x]);
                    }
                }
                break;

            case 3: // gradient：首行退化为 horizontal，其余行用 clip(left + top - topLeft)
                for (int x = 1; x < width; x++)
                    alpha[x] = (byte)(alpha[x] + alpha[x - 1]);

                for (int y = 1; y < height; y++)
                {
                    int rowStart = y * width;
                    int prevStart = rowStart - width;

                    // x = 0：左与左上越界（视为 0），预测值即上方像素
                    alpha[rowStart] = (byte)(alpha[rowStart] + alpha[prevStart]);

                    for (int x = 1; x < width; x++)
                    {
                        int idx = rowStart + x;
                        int left = alpha[idx - 1];
                        int top = alpha[prevStart + x];
                        int topLeft = alpha[prevStart + x - 1];
                        int pred = left + top - topLeft;
                        if (pred < 0)
                            pred = 0;
                        else if (pred > 255)
                            pred = 255;
                        alpha[idx] = (byte)(alpha[idx] + pred);
                    }
                }
                break;
        }
    }
}
