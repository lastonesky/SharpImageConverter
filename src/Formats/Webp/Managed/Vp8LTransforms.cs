namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// VP8L 四种逆变换实现，严格对照 libwebp lossless.c / vp8l_dec.c。
/// 关键：PredictorAdd 系列函数用"已经 Add 过的输出像素"做 left/top 邻居，
/// 不是变换域输入像素。
/// </summary>
internal static class Vp8LTransforms
{
    public enum TransformType { Predictor = 0, CrossColor = 1, SubtractGreen = 2, ColorIndexing = 3 }

    private static uint Clamp255(int v) => v switch { < 0 => 0, > 255 => 255, _ => (uint)v };

    /// <summary>
    /// Predictor 逆变换的像素加法：每个通道做 mod 256 加法，不 clamp。
    /// VP8L 编码时 residual = (pixel - predictor) mod 256，解码时做 (residual + predictor) mod 256。
    /// </summary>
    private static uint ModAddPixels(uint residual, uint predictor)
    {
        byte r = (byte)((residual & 0xFFu) + (predictor & 0xFFu));
        byte g = (byte)(((residual >> 8) & 0xFFu) + ((predictor >> 8) & 0xFFu));
        byte b = (byte)(((residual >> 16) & 0xFFu) + ((predictor >> 16) & 0xFFu));
        byte a = (byte)(((residual >> 24) & 0xFFu) + ((predictor >> 24) & 0xFFu));
        return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);
    private static uint Average3(uint a, uint b, uint c) => Average2(Average2(a, c), b);
    private static uint Average4(uint a, uint b, uint c, uint d) => Average2(Average2(a, b), Average2(c, d));

    private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

    private static uint Select(uint a, uint b, uint c)
    {
        int pa_minus_pb = Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24));
        pa_minus_pb += Sub3((int)((a >> 16) & 0xFF), (int)((b >> 16) & 0xFF), (int)((c >> 16) & 0xFF));
        pa_minus_pb += Sub3((int)((a >> 8) & 0xFF), (int)((b >> 8) & 0xFF), (int)((c >> 8) & 0xFF));
        pa_minus_pb += Sub3((int)(a & 0xFF), (int)(b & 0xFF), (int)(c & 0xFF));
        return pa_minus_pb <= 0 ? a : b;
    }

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        return ((uint)Clamp255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24)) << 24) |
               ((uint)Clamp255((int)((c0 >> 16) & 0xFF) + (int)((c1 >> 16) & 0xFF) - (int)((c2 >> 16) & 0xFF)) << 16) |
               ((uint)Clamp255((int)((c0 >> 8) & 0xFF) + (int)((c1 >> 8) & 0xFF) - (int)((c2 >> 8) & 0xFF)) << 8) |
               (uint)Clamp255((int)(c0 & 0xFF) + (int)(c1 & 0xFF) - (int)(c2 & 0xFF));
    }

    /// <summary>
    /// 对照 libwebp AddSubtractComponentHalf：a + (a - b) / 2（C# int 除法同样向零截断），再 Clip255。
    /// </summary>
    private static int AddSubtractComponentHalf(int a, int b) => (int)Clamp255(a + (a - b) / 2);

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint ave = Average2(c0, c1);
        int a = AddSubtractComponentHalf((int)(ave >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentHalf((int)((ave >> 16) & 0xFF), (int)((c2 >> 16) & 0xFF));
        int g = AddSubtractComponentHalf((int)((ave >> 8) & 0xFF), (int)((c2 >> 8) & 0xFF));
        int b = AddSubtractComponentHalf((int)(ave & 0xFF), (int)(c2 & 0xFF));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    /// <summary>
    /// 14 种 predictor 函数（对照 libwebp VP8LPredictor0_C ~ VP8LPredictor13_C）。
    /// left = out[x-1]（已处理的输出像素），top = 上一行输出像素数组。
    /// </summary>
    internal static uint Predictor(int mode, uint left, uint topCur, uint topLeft, uint topRight) => mode switch
    {
        0 => 0xFF000000,                              // black
        1 => left,                                     // left
        2 => topCur,                                   // top
        3 => topRight,                                 // top[1]
        4 => topLeft,                                  // top[-1]
        5 => Average3(left, topCur, topRight),         // avg(left, top, top[1])
        6 => Average2(left, topLeft),                  // avg(left, top[-1])
        7 => Average2(left, topCur),                   // avg(left, top)
        8 => Average2(topLeft, topCur),                // avg(top[-1], top)
        9 => Average2(topCur, topRight),               // avg(top, top[1])
        10 => Average4(left, topLeft, topCur, topRight), // avg(4)
        11 => Select(topCur, left, topLeft),           // select
        12 => ClampedAddSubtractFull(left, topCur, topLeft),
        13 => ClampedAddSubtractHalf(left, topCur, topLeft),
        _ => left,
    };

    // =====================================================
    //                    Predictor Transform
    // =====================================================

    /// <summary>
    /// Predictor 逆变换，严格对照 libwebp PredictorInverseTransform_C。
    /// 关键：逐像素按行扫描，每个像素的预测值依赖"已经 Add 过的输出像素"。
    /// 第一行：(0,0) mode=0 (black)，其余 mode=1 (left)。
    /// 每行第一像素：mode=2 (top)。
    /// 其余：按 tile 取 predictorData 的 mode（从 uint32 的 bits 8..11 取），tile 内逐像素处理。
    /// </summary>
    public static void ApplyPredictorInverse(Span<uint> data, int width, int height,
                                              ReadOnlySpan<uint> predictorData, int bits)
    {
        int tileWidth = 1 << bits;
        int mask = tileWidth - 1;
        int tilesPerRow = (width + tileWidth - 1) >> bits; // VP8LSubSampleSize

        // ---- 第一行 ----
        // (0,0): mode 0 (black) — predictor = ARGB_BLACK (0xFF000000)
        data[0] = ModAddPixels(data[0], 0xFF000000u);
        // (x,0) for x>=1: mode 1 (left)
        for (int x = 1; x < width; x++)
            data[x] = ModAddPixels(data[x], data[x - 1]);

        // ---- 其余行 ----
        for (int y = 1; y < height; y++)
        {
            int rowOffset = y * width;
            int topRowOffset = rowOffset - width;
            int dataRowIdx = y >> bits;
            int dataBase = dataRowIdx * tilesPerRow;

            // (0,y): mode 2 (top)
            data[rowOffset] = ModAddPixels(data[rowOffset], data[topRowOffset]);

            // (x,y) for x>=1: 按 tile 分段
            // 对照 libwebp: pred_mode_src++ 每次 tile 段迭代后递增
            int predSrcIdx = dataBase;
            int x = 1;
            while (x < width)
            {
                // predictor mode 从 uint32 的 bits 8..11 取（libwebp: ((*pred_mode_src++) >> 8) & 0xf）
                if (predSrcIdx >= predictorData.Length)
                    throw new WebpBitstreamException(
                        $"Predictor 模式数据不足：需要 tile {predSrcIdx}，实际长度 {predictorData.Length}");
                int mode = (int)((predictorData[predSrcIdx] >> 8) & 0xF);
                predSrcIdx++;

                int xEnd = (x & ~mask) + tileWidth;
                if (xEnd > width) xEnd = width;

                for (int px = x; px < xEnd; px++)
                {
                    uint leftVal = data[rowOffset + px - 1];
                    uint topCurVal = data[topRowOffset + px];
                    uint topLeftVal = data[topRowOffset + px - 1];
                    // libwebp 的 top 指针为 out - width，top[i + 1] 在每行最后一列恰好等于
                    // 当前行的第一个像素 data[rowOffset]（该像素已按 top 模式还原）。
                    uint topRightVal = data[topRowOffset + px + 1];

                    uint pred = Predictor(mode, leftVal, topCurVal, topLeftVal, topRightVal);
                    data[rowOffset + px] = ModAddPixels(data[rowOffset + px], pred);
                }

                x = xEnd;
            }
        }
    }

    // =====================================================
    //                    Color Transform
    // =====================================================

    /// <summary>
    /// Cross-color 逆变换，严格对照 libwebp VP8LTransformColorInverse_C。
    /// 从 colorData tile 提取三组乘数：
    ///   green_to_red  = color_code 的 B 分量 (bits 0..7)，
    ///   green_to_blue = color_code 的 G 分量 (bits 8..15)，
    ///   red_to_blue   = color_code 的 R 分量 (bits 16..23)。
    /// 修正顺序：先 R += green_to_red * G >> 5，再 B += green_to_blue * G >> 5，
    /// 最后 B += red_to_blue * R >> 5。所有运算 mod 256（自然 byte 溢出）。
    /// </summary>
    public static void ApplyColorTransformInverse(Span<uint> data, int width, int height,
                                                   ReadOnlySpan<uint> colorData, int bits)
    {
        int tileWidth = 1 << bits;
        int mask = tileWidth - 1;
        int tilesPerRow = (width + tileWidth - 1) >> bits;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            int dataRowIdx = y >> bits;
            int predRowIdx = dataRowIdx * tilesPerRow;

            // 每个 tileWidth 像素段用同一组 multiplier（对照 libwebp ColorSpaceInverseTransform_C）
            int x = 0;
            while (x < width)
            {
                int tileIdx = x / tileWidth;
                int ccIdx = predRowIdx + tileIdx;

                int g2r = 0, g2b = 0, r2b = 0;
                if (ccIdx < colorData.Length)
                {
                    uint cc = colorData[ccIdx];
                    // libwebp ColorCodeToMultipliers
                    g2r = (sbyte)(cc & 0xFF);            // bits 0..7
                    g2b = (sbyte)((cc >> 8) & 0xFF);      // bits 8..15
                    r2b = (sbyte)((cc >> 16) & 0xFF);     // bits 16..23
                }

                int xEnd = (x & ~mask) + tileWidth;
                if (xEnd > width) xEnd = width;

                for (int px = x; px < xEnd; px++)
                {
                    int idx = rowOffset + px;
                    uint argb = data[idx];
                    // libwebp 把 CrossColor 的像素分量和乘数都当 sbyte（有符号）处理
                    sbyte g = (sbyte)((argb >> 8) & 0xFF);
                    int r = (int)((argb >> 16) & 0xFF);
                    int b = (int)(argb & 0xFF);

                    // mod 256 加法（byte 自然溢出）
                    r = (r + ((g2r * g) >> 5)) & 0xFF;
                    b = (b + ((g2b * g) >> 5)) & 0xFF;
                    b = (b + ((r2b * (sbyte)r) >> 5)) & 0xFF;

                    data[idx] = (argb & 0xFF00FF00u) | ((uint)r << 16) | (uint)b;
                }

                x = xEnd;
            }
        }
    }

    // =====================================================
    //                 Subtract Green Transform
    // =====================================================

    /// <summary>
    /// Subtract Green 逆变换，严格对照 libwebp VP8LAddGreenToBlueAndRed_C。
    /// R 与 B 各 += G（mod 256，自然 byte 溢出，不 clamp）。
    /// </summary>
    public static void ApplySubtractGreenInverse(Span<uint> data, int width, int height)
    {
        int total = width * height;
        for (int i = 0; i < total; i++)
        {
            uint argb = data[i];
            uint green = (argb >> 8) & 0xFFu;
            uint redBlue = argb & 0x00FF00FFu;
            redBlue += (green << 16) | green;
            redBlue &= 0x00FF00FFu;
            data[i] = (argb & 0xFF00FF00u) | redBlue;
        }
    }

    // =====================================================
    //               Color Indexing Transform
    // =====================================================

    /// <summary>
    /// Color Indexing 逆变换，严格对照 libwebp ColorIndexInverseTransform_C。
    /// 索引恒存放在 packed 像素的绿字节（bits 8..15，VP8GetARGBIndex = (v >> 8) &amp; 0xff）。
    /// transform bits = 0 时每像素一个 8-bit 索引；bits &gt; 0 时每个绿字节内按 LSB-first
    /// 打包 2^(8-bits) 个索引（4 / 2 / 1 位）。
    /// </summary>
    /// <param name="subData">打包后的 sub-sample 像素。</param>
    /// <param name="subWidth">打包行宽 = VP8LSubSampleSize(origWidth, bits)。</param>
    /// <param name="subHeight">打包行数（= origHeight）。</param>
    /// <param name="origWidth">还原后的宽度。</param>
    /// <param name="origHeight">还原后的高度。</param>
    /// <param name="palette">已展开的调色板（长度 = 1 &lt;&lt; (8 &gt;&gt; bits)）。</param>
    /// <param name="bits">transform 的 bits 字段（0..3）。</param>
    public static uint[] ApplyColorIndexingInverse(ReadOnlySpan<uint> subData,
                                                   int subWidth, int subHeight,
                                                   int origWidth, int origHeight,
                                                   ReadOnlySpan<uint> palette, int bits)
    {
        int bitsPerPixel = 8 >> bits;   // 8 / 4 / 2 / 1
        int pixelsPerUnit = 1 << bits;  // 1 / 2 / 4 / 8
        uint indexMask = (1u << bitsPerPixel) - 1u;
        int totalUnits = subWidth * subHeight;

        var result = new uint[origWidth * origHeight];
        int srcIdx = 0;

        for (int y = 0; y < origHeight; y++)
        {
            int dstRow = y * origWidth;
            for (int x = 0; x < origWidth; x += pixelsPerUnit)
            {
                if (srcIdx >= totalUnits || srcIdx >= subData.Length)
                    throw new WebpBitstreamException("Color Indexing 打包像素数量不足");

                uint packed = subData[srcIdx++];
                uint packedByte = (packed >> 8) & 0xFFu;

                for (int k = 0; k < pixelsPerUnit; k++)
                {
                    int dstX = x + k;
                    if (dstX >= origWidth) break;

                    uint idx = bitsPerPixel == 8
                        ? packedByte
                        : (packedByte >> (k * bitsPerPixel)) & indexMask;

                    if (idx >= (uint)palette.Length)
                        throw new WebpBitstreamException($"调色板索引 {idx} 超出范围");
                    result[dstRow + dstX] = palette[(int)idx];
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 调色板展开，严格对照 libwebp ExpandColorMap：
    /// 第 1 项直接使用解码值，其余按字节差分累加（new[i] = raw[i] + new[i-4] mod 256），
    /// 最后补零（黑色）到 1 &lt;&lt; (8 &gt;&gt; bits) 项。
    /// </summary>
    public static uint[] ExpandPalette(ReadOnlySpan<uint> raw, int numColors, int bits)
    {
        int finalNumColors = 1 << (8 >> bits);
        if (numColors < 1 || numColors > raw.Length || numColors > finalNumColors)
            throw new WebpBitstreamException(
                $"Color Indexing 调色板颜色数 {numColors} 非法（raw={raw.Length}, 上限={finalNumColors}）");

        var palette = new uint[finalNumColors];
        palette[0] = raw[0];
        for (int i = 1; i < numColors; i++)
        {
            uint prev = palette[i - 1];
            uint cur = raw[i];
            uint a = (byte)(((cur >> 24) & 0xFFu) + ((prev >> 24) & 0xFFu));
            uint r = (byte)(((cur >> 16) & 0xFFu) + ((prev >> 16) & 0xFFu));
            uint g = (byte)(((cur >> 8) & 0xFFu) + ((prev >> 8) & 0xFFu));
            uint b = (byte)((cur & 0xFFu) + (prev & 0xFFu));
            palette[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }
        // 其余项保持 0（transparent black），与 libwebp 的 black tail 一致。
        return palette;
    }
}
