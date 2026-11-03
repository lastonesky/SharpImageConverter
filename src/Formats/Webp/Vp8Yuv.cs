namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// RGBA → YUV 4:2:0（BT.601 有限范围）转换。
///
/// 亮度采用 libwebp <c>src/dsp/yuv.h</c> 的 16 位定点系数（<c>YUV_FIX = 16</c>）：
/// <c>Y = (16839·R + 33059·G + 6420·B + 32768 + 16·2^16) &gt;&gt; 16</c>，
/// 色度 <c>U/V</c> 同样为其固定点系数，偏置 128。
///
/// 色度下采样默认采用 2×2 盒式平均（与 libwebp 默认编码路径一致）：
/// 先把 4 个像素的 RGB 求和，再用放大 2 位的舍入与偏置一次性转换，等价于“先平均后转换”。
/// 非偶宽高的最后一列/行通过边缘复制参与平均。
/// </summary>
internal static class Vp8Yuv
{
    private const int YuvFix = 16;
    private const int YuvHalf = 1 << (YuvFix - 1);

    // libwebp BV.601 固定点系数（Y / U / V）。
    private const int YR = 16839;
    private const int YG = 33059;
    private const int YB = 6420;
    private const int UR = -9719;
    private const int UG = -19070;
    private const int UB = 28794;
    private const int VR = 28794;
    private const int VG = -24121;
    private const int VB = -4680;

    /// <summary>色度平面行距（半宽）。</summary>
    internal static int ChromaWidth(int width) => (width + 1) / 2;

    /// <summary>色度平面高度（半高）。</summary>
    internal static int ChromaHeight(int height) => (height + 1) / 2;

    /// <summary>
    /// 把 RGBA 转成 Y / U / V 平面。
    /// </summary>
    /// <param name="rgba">RGBA 像素，长度必须为 width·height·4。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="y">亮度平面，长度 width·height（行距 = width）。</param>
    /// <param name="u">U 平面，长度 ChromaWidth·ChromaHeight（行距 = ChromaWidth）。</param>
    /// <param name="v">V 平面，同上。</param>
    /// <param name="fancy">true 时改用 4×4 权重 (1,3,3,1)⊗(1,3,3,1)/64 的锐化下采样，仅用于对拍。</param>
    internal static void ToYuv420(ReadOnlySpan<byte> rgba, int width, int height,
        byte[] y, byte[] u, byte[] v, bool fancy = false)
    {
        for (int row = 0; row < height; row++)
        {
            int src = row * width * 4;
            int dst = row * width;
            for (int col = 0; col < width; col++, src += 4, dst++)
            {
                int r = rgba[src];
                int g = rgba[src + 1];
                int b = rgba[src + 2];
                y[dst] = (byte)((YR * r + YG * g + YB * b + YuvHalf + (16 << YuvFix)) >> YuvFix);
            }
        }

        int cw = ChromaWidth(width);
        int chh = ChromaHeight(height);
        for (int cy = 0; cy < chh; cy++)
        {
            for (int cx = 0; cx < cw; cx++)
            {
                long sumR = 0, sumG = 0, sumB = 0;
                int weight = 0;
                if (!fancy)
                {
                    int x0 = cx * 2;
                    int x1 = Math.Min(x0 + 1, width - 1);
                    int y0 = cy * 2;
                    int y1 = Math.Min(y0 + 1, height - 1);
                    Accumulate(rgba, width, x0, y0, 1, ref sumR, ref sumG, ref sumB);
                    Accumulate(rgba, width, x1, y0, 1, ref sumR, ref sumG, ref sumB);
                    Accumulate(rgba, width, x0, y1, 1, ref sumR, ref sumG, ref sumB);
                    Accumulate(rgba, width, x1, y1, 1, ref sumR, ref sumG, ref sumB);
                    weight = 4;
                }
                else
                {
                    for (int dy = 0; dy < 4; dy++)
                    {
                        int sy = Math.Min(Math.Max(cy * 2 - 1 + dy, 0), height - 1);
                        int wy = FancyWeights[dy];
                        for (int dx = 0; dx < 4; dx++)
                        {
                            int sx = Math.Min(Math.Max(cx * 2 - 1 + dx, 0), width - 1);
                            Accumulate(rgba, width, sx, sy, FancyWeights[dx] * wy, ref sumR, ref sumG, ref sumB);
                        }
                    }
                    weight = 64;
                }

                long uValue = UR * sumR + UG * sumG + UB * sumB;
                long vValue = VR * sumR + VG * sumG + VB * sumB;
                // 偏置与舍入项按总权重放大，随后一次右移还原为平均值。
                long offset = (long)(128 << YuvFix) * weight;
                long round = (long)YuvHalf * weight;
                int idx = cy * cw + cx;
                u[idx] = (byte)((uValue + round + offset) / (weight << YuvFix));
                v[idx] = (byte)((vValue + round + offset) / (weight << YuvFix));
            }
        }
    }

    private static readonly int[] FancyWeights = { 1, 3, 3, 1 };

    private static void Accumulate(ReadOnlySpan<byte> rgba, int width, int x, int y, int weight,
        ref long sumR, ref long sumG, ref long sumB)
    {
        int off = (y * width + x) * 4;
        sumR += (long)rgba[off] * weight;
        sumG += (long)rgba[off + 1] * weight;
        sumB += (long)rgba[off + 2] * weight;
    }
}
