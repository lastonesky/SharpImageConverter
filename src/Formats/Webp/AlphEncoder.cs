using System;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// ALPH chunk 编码器：把独立 alpha 平面压缩为 ALPH 负载（1 字节头部 + 负载）。
///
/// 头部位布局（RFC 9649 §2.5）：
/// bits 0~1 = 压缩方式（1 = 无损 VP8L，负载为去掉 5 字节帧头的 VP8L 图像流）；
/// bits 2~3 = 过滤方式（0 = none，1 = horizontal，2 = vertical，3 = gradient）；
/// bits 4~5 = 预处理方式（写 0 = 无预处理）；
/// bits 6~7 = 保留位，必须为 0。
///
/// 过滤是编码端的**可逆**残差变换：先按 <see cref="ApplyFilter"/> 把 alpha 平面
/// 变成残差，交给 VP8L 压缩，解码端再按 <see cref="AlphDecoder"/> 的逆滤波还原。
/// 三种滤波的预测器与解码端逐字节对应（见 <see cref="AlphDecoder"/> 的逆滤波实现）。
///
/// ALPH 的 VP8L 位流按“灰度图像”承载 alpha：每个像素的 GREEN 字节即 alpha 值
/// （与解码端 <see cref="AlphDecoder"/> 及 libwebp 的 ExtractAlpha 一致）。
/// </summary>
internal static class AlphEncoder
{
    /// <summary>过滤方式（写入头部 bits 2~3）。</summary>
    internal const int FilterNone = 0;
    internal const int FilterHorizontal = 1;
    internal const int FilterVertical = 2;
    internal const int FilterGradient = 3;

    /// <summary>VP8L 帧头固定 5 字节（40 bit：signature/width/height/alpha/version）。</summary>
    private const int Vp8LHeaderBytes = 5;

    /// <summary>
    /// 试排用配置（SelectBest=false）：单一候选，走「调色板或空间变换 + color cache + LZ77」。
    /// 与 <see cref="Vp8LEncoderOptions.Auto"/> 同源，实测排序与完整编码一致，但只花一份代价。
    /// </summary>
    private static readonly Vp8LEncoderOptions TrialOptions =
        new(true, true, true, useColorIndexing: true, useMetaHuffman: true, autoPredictor: true);

    /// <summary>
    /// 允许对 4 种滤波逐一真编试排的像素上限。超过后改用零成本的采样估计：
    /// 试排代价随像素线性增长，而大图上 alpha 平面往往几千像素宽，4 份完整编码会拖垮整条链路。
    /// </summary>
    private const long TrialPixelLimit = 2_000_000;

    /// <summary>
    /// 把 alpha 平面编码为 ALPH chunk 数据（自动选择过滤方式）。
    /// </summary>
    /// <param name="alpha">alpha 值，长度必须为 width * height，行优先。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    public static byte[] Encode(ReadOnlySpan<byte> alpha, int width, int height, bool fast = false)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "ALPH 尺寸必须为正");
        if (alpha.Length != checked(width * height))
            throw new ArgumentException("alpha 数据长度与尺寸不匹配", nameof(alpha));

        var residual = new byte[alpha.Length];
        var rgba = new byte[checked(alpha.Length * 4)];
        int filter = SelectFilter(alpha, width, height, residual, rgba, fast);
        return EncodePlane(residual, rgba, width, height, filter, fast);
    }

    /// <summary>
    /// 把 alpha 平面按指定过滤方式编码为 ALPH chunk 数据（filter 为 0..3）。
    /// </summary>
    internal static byte[] Encode(ReadOnlySpan<byte> alpha, int width, int height, int filter)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "ALPH 尺寸必须为正");
        if (alpha.Length != checked(width * height))
            throw new ArgumentException("alpha 数据长度与尺寸不匹配", nameof(alpha));
        if ((uint)filter > FilterGradient)
            throw new ArgumentOutOfRangeException(nameof(filter));

        var residual = new byte[alpha.Length];
        var rgba = new byte[checked(alpha.Length * 4)];
        if (filter == FilterNone)
            alpha.CopyTo(residual);
        else
            ApplyFilter(alpha, residual, width, height, filter);
        return EncodePlane(residual, rgba, width, height, filter);
    }

    /// <summary>
    /// 把**已经过滤好**的残差平面直接编码为 ALPH 负载，并把 filter 写进头部。
    /// 供「调用方自己做了前向滤波」的场景使用（测试用独立的滤波实现造流，验证解码端）。
    /// </summary>
    internal static byte[] EncodePreFiltered(ReadOnlySpan<byte> residual, int width, int height, int filter)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "ALPH 尺寸必须为正");
        if (residual.Length != checked(width * height))
            throw new ArgumentException("残差平面长度与尺寸不匹配", nameof(residual));
        if ((uint)filter > FilterGradient)
            throw new ArgumentOutOfRangeException(nameof(filter));

        var rgba = new byte[checked(residual.Length * 4)];
        return EncodePlane(residual, rgba, width, height, filter);
    }

    /// <summary>把残差塞进 VP8L 并拼出 ALPH 负载。</summary>
    private static byte[] EncodePlane(ReadOnlySpan<byte> residual, byte[] rgba,
        int width, int height, int filter, bool fast = false)
    {
        // VP8L 内部像素为 ARGB，ALPH 只取 GREEN 通道，因此把残差放进 G、其余通道置 0。
        for (int i = 0; i < residual.Length; i++)
            rgba[i * 4 + 1] = residual[i];

        var options = fast ? Vp8LEncoderOptions.AutoFast : Vp8LEncoderOptions.Auto;
        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, options);
        if (vp8l.Length < Vp8LHeaderBytes)
            throw new WebpBitstreamException("VP8L 编码器输出过短，无法作为 ALPH 负载");

        // 去掉 VP8L 5 字节帧头（40 bit 恰好字节对齐），其余即为 ALPH 所需的裸图像流。
        var result = new byte[1 + vp8l.Length - Vp8LHeaderBytes];
        result[0] = (byte)(1 | (filter << 2));
        vp8l.AsSpan(Vp8LHeaderBytes).CopyTo(result.AsSpan(1));
        return result;
    }

    /// <summary>
    /// 按 libwebp alpha_enc.c 的正向滤波把 <paramref name="src"/> 写成残差到
    /// <paramref name="dst"/>。三种滤波的行 0 都退化为水平预测（预测值 0 起算），
    /// 与解码端 <see cref="AlphDecoder"/>「首行按 horizontal 逆滤波」严格对应。
    /// </summary>
    internal static void ApplyFilter(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int filter)
    {
        if (filter == FilterNone)
        {
            src.CopyTo(dst);
            return;
        }

        if (filter == FilterHorizontal)
        {
            ApplyHorizontalFilter(src, dst, width, height);
            return;
        }

        // vertical / gradient：行 0 与 horizontal 完全相同
        ApplyHorizontalRow(src, dst, 0, width);
        for (int y = 1; y < height; y++)
        {
            int row = y * width;
            int prev = row - width;
            if (filter == FilterVertical)
            {
                for (int x = 0; x < width; x++)
                    dst[row + x] = (byte)(src[row + x] - src[prev + x]);
            }
            else
            {
                // gradient：x = 0 时左/左上越界（视为 0），预测值即上方像素
                dst[row] = (byte)(src[row] - src[prev]);
                for (int x = 1; x < width; x++)
                {
                    int left = src[row + x - 1];
                    int top = src[prev + x];
                    int topLeft = src[prev + x - 1];
                    dst[row + x] = (byte)(src[row + x] - Clip255(left + top - topLeft));
                }
            }
        }
    }

    /// <summary>水平滤波：每行的前行预测值是上一行的首像素（行 0 为 0）。</summary>
    private static void ApplyHorizontalFilter(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height)
    {
        for (int y = 0; y < height; y++)
            ApplyHorizontalRow(src, dst, y, width);
    }

    private static void ApplyHorizontalRow(ReadOnlySpan<byte> src, Span<byte> dst, int y, int width)
    {
        int row = y * width;
        int pred = y == 0 ? 0 : src[row - width];
        for (int x = 0; x < width; x++)
        {
            int val = src[row + x];
            dst[row + x] = (byte)(val - pred);
            pred = val;
        }
    }

    private static int Clip255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

    /// <summary>
    /// 选过滤方式：
    ///
    /// ① 颜色数 ≤ 16 直接用 none——与 libwebp 的 <c>kMinColorsForFilterNone</c> 同口径。
    ///    低色数平面本身就是「少量符号 + 长游程」，VP8L 自带的空间预测已经吃干净了，
    ///    再预滤一次只会在边缘造噪点：实测 3 色的 alpha 图 horiz 只省 2.7%，
    ///    而 2 色的条纹图误用 grad 会从 70 B 涨到 124 B。
    ///
    /// ② 其余平面先按 none 真编一次，用「每像素字节数」判断值不值得再试：
    ///    已压到 ≤ 0.02 B/px 的平面（UI 面板、纯色块）滤波最多再省零点几个百分点，
    ///    而 1024² 上一次试排就要 50~130 ms；≥ 0.5 B/px 说明内容近似不可压
    ///    （噪声类 alpha），预滤只会加噪。
    ///
    /// ③ 落在中间区间才逐一对 4 种滤波做**真实**试排并取最小。试排用单候选配置
    ///    （<see cref="TrialOptions"/>），实测在 6 种平面上与完整候选枚举的排序完全一致、
    ///    体积差 &lt; 0.5%，代价只有一份编码。不用「残差熵 / |残差| / libwebp 采样打分」
    ///    这类代理：在平坦平面上它们会把该选 none 的图误判成 grad（实测 6 个平面错 4 个），
    ///    因为 VP8L 自己就会做空间预测。
    ///
    /// ④ 超过 <see cref="TrialPixelLimit"/> 的大图退回 libwebp 的零成本采样估计。
    /// </summary>
    private static int SelectFilter(ReadOnlySpan<byte> alpha, int width, int height,
        byte[] residual, byte[] rgba, bool fast = false)
    {
        if (CountColors(alpha) <= MaxColorsForNoFilter)
        {
            alpha.CopyTo(residual);
            return FilterNone;
        }

        if (fast)
        {
            // 快档：直接用 libwebp 同款零成本采样估计选滤波，跳过 4 次完整编码试排。
            // 试排只影响体积，不影响 ALPH 位流的合法性或可逆性。
            int estimated = EstimateBestFilter(alpha, width, height);
            if (estimated == FilterNone)
                alpha.CopyTo(residual);
            else
                ApplyFilter(alpha, residual, width, height, estimated);
            return estimated;
        }

        if ((long)width * height > TrialPixelLimit)
        {
            int estimated = EstimateBestFilter(alpha, width, height);
            if (estimated == FilterNone)
                alpha.CopyTo(residual);
            else
                ApplyFilter(alpha, residual, width, height, estimated);
            return estimated;
        }

        // 先量 none 的编码体积，作为「值不值得试排」与最终比较的基准。
        alpha.CopyTo(residual);
        int noneSize = TrialEncode(residual, width, height, rgba);
        double bytesPerPixel = (double)noneSize / alpha.Length;
        if (bytesPerPixel <= MinBytesPerPixelForFilter || bytesPerPixel >= MaxBytesPerPixelForFilter)
            return FilterNone;

        int best = FilterNone;
        int bestSize = noneSize;
        for (int filter = FilterHorizontal; filter <= FilterGradient; filter++)
        {
            ApplyFilter(alpha, residual, width, height, filter);
            int size = TrialEncode(residual, width, height, rgba);
            if (size < bestSize)
            {
                bestSize = size;
                best = filter;
            }
        }

        // 让 residual 里留下赢家的残差，供最终编码直接使用
        if (best == FilterNone)
            alpha.CopyTo(residual);
        else
            ApplyFilter(alpha, residual, width, height, best);
        return best;
    }

    private static int TrialEncode(ReadOnlySpan<byte> residual, int width, int height, byte[] rgba)
    {
        for (int i = 0; i < residual.Length; i++)
            rgba[i * 4 + 1] = residual[i];
        return Vp8LEncoder.EncodeVp8L(rgba, width, height, TrialOptions).Length;
    }

    /// <summary>≤ 该颜色数时不做滤波（与 libwebp kMinColorsForFilterNone 一致）。</summary>
    private const int MaxColorsForNoFilter = 16;

    /// <summary>none 已压到 ≤ 该每像素字节数时不再试排（再省也只有零点几个百分点）。</summary>
    private const double MinBytesPerPixelForFilter = 0.02;

    /// <summary>none 已 ≥ 该每像素字节数时不再试排（近似不可压，预滤只会加噪）。</summary>
    private const double MaxBytesPerPixelForFilter = 0.5;

    /// <summary>打分桶数（libwebp SMAX）。</summary>
    private const int ScoreBins = 16;

    private static int CountColors(ReadOnlySpan<byte> alpha)
    {
        Span<bool> seen = stackalloc bool[256];
        seen.Clear();
        int count = 0;
        for (int i = 0; i < alpha.Length; i++)
        {
            if (seen[alpha[i]])
                continue;
            seen[alpha[i]] = true;
            if (++count > MaxColorsForNoFilter)
                return count;   // 已确定要用估计器，提前退出
        }
        return count;
    }

    /// <summary>
    /// libwebp <c>WebPEstimateBestFilter</c>：每 2 行、每 2 列采一个点，
    /// 把 |残差|&gt;&gt;4 落到 16 个桶里，得分 = 出现过的最小桶下标之和（越小越好）。
    /// </summary>
    private static int EstimateBestFilter(ReadOnlySpan<byte> src, int width, int height)
    {
        var bins = new bool[4, ScoreBins];
        for (int y = 2; y < height - 1; y += 2)
        {
            int row = y * width;
            int prevRow = row - width;
            int mean = src[row];
            for (int x = 2; x < width - 1; x += 2)
            {
                int p = src[row + x];
                int left = src[row + x - 1];
                int top = src[prevRow + x];
                int topLeft = src[prevRow + x - 1];

                bins[FilterNone, ScoreDiff(p, mean)] = true;
                bins[FilterHorizontal, ScoreDiff(p, left)] = true;
                bins[FilterVertical, ScoreDiff(p, top)] = true;
                bins[FilterGradient, ScoreDiff(p, Clip255(left + top - topLeft))] = true;

                mean = (3 * mean + p + 2) >> 2;
            }
        }

        int best = FilterNone;
        int bestScore = int.MaxValue;
        for (int filter = 0; filter < 4; filter++)
        {
            int score = 0;
            for (int i = 0; i < ScoreBins; i++)
            {
                if (bins[filter, i])
                    score += i;
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = filter;
            }
        }
        return best;
    }

    private static int ScoreDiff(int a, int b) => Math.Abs(a - b) >> 4;

}
