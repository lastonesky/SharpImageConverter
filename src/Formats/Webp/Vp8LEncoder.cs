using System;
using System.Collections.Generic;

namespace SharpImageConverter.Formats.Webp;

/// <summary>VP8L 编码器可选特性（阶段 A 用 <see cref="LiteralOnly"/>，阶段 B 用 <see cref="Full"/>）。</summary>
internal readonly struct Vp8LEncoderOptions
{
    public Vp8LEncoderOptions(bool useTransforms, bool useColorCache, bool useLz77,
        bool useColorIndexing = false, bool uniformPredictor = false, bool selectBest = false,
        bool useMetaHuffman = true, bool autoPredictor = false, bool autoCrossColor = true,
        int forcedPredictorMode = -1, int forcedCacheBits = -1)
    {
        UseTransforms = useTransforms;
        UseColorCache = useColorCache;
        UseLz77 = useLz77;
        UseColorIndexing = useColorIndexing;
        UniformPredictor = uniformPredictor;
        SelectBest = selectBest;
        UseMetaHuffman = useMetaHuffman;
        AutoPredictor = autoPredictor;
        AutoCrossColor = autoCrossColor;
        ForcedPredictorMode = forcedPredictorMode;
        ForcedCacheBits = forcedCacheBits;
    }

    public bool UseTransforms { get; }
    public bool UseColorCache { get; }
    public bool UseLz77 { get; }

    /// <summary>颜色数 ≤256 时尝试 color indexing（调色板）变换。</summary>
    public bool UseColorIndexing { get; }

    /// <summary>
    /// 整幅图使用同一个 predictor 模式（所有 tile 写同一模式），而不是逐 tile 选择。
    /// 图像内容高度规整（渐变、纯色、周而复始的图案）时，统一模式能让残差在整幅图内
    /// 保持一致、最大化 LZ77 可重复性；逐 tile 模式在近并列时会引入 tile 边界断点。
    /// </summary>
    public bool UniformPredictor { get; }

    /// <summary>
    /// 由编码器在若干确定性候选配置（空间变换 / 调色板 × cache / LZ77 组合）中
    /// 选输出最小的一个。候选编码全部是合法且可逆的，选择过程本身也是确定性的。
    /// </summary>
    public bool SelectBest { get; }

    /// <summary>
    /// 使用 meta-Huffman（多组码表 + entropy image）。仅 level 0 生效，
    /// 由编码器按 tile 直方图聚类决定组数；组数为 1 时自动退回单组位流。
    /// </summary>
    public bool UseMetaHuffman { get; }

    /// <summary>
    /// 比较"统一 predictor 模式"与"逐 tile 模式"的估计体积后自动选择
    /// （<see cref="UniformPredictor"/> 为 true 时强制统一模式）。
    /// </summary>
    public bool AutoPredictor { get; }

    /// <summary>
    /// 只在 cross-color 确实降低残差体积时才写出该变换。
    /// 逐 tile 最小二乘拟合在 mod-256 回绕处会给出很差的系数，硬用反而变大；
    /// 关掉这个变换整条变换列表仍然合法。
    /// </summary>
    public bool AutoCrossColor { get; }

    /// <summary>分析用：强制使用指定 predictor 模式（-1 表示正常搜索）。</summary>
    public int ForcedPredictorMode { get; }

    /// <summary>分析用：强制使用指定 color cache 位数（-1 表示按尺寸自动选）。</summary>
    public int ForcedCacheBits { get; }

    /// <summary>仅 literal：无变换、无 color cache、无 LZ77，用于阶段 A 体积基线。</summary>
    public static readonly Vp8LEncoderOptions LiteralOnly = new(false, false, false, useMetaHuffman: false);

    /// <summary>阶段 B 全部已实现特性（不含 auto 选择）。</summary>
    public static readonly Vp8LEncoderOptions Full =
        new(true, true, true, useMetaHuffman: true, autoPredictor: true);

    /// <summary>生产入口使用：在候选配置中选最小输出。</summary>
    public static readonly Vp8LEncoderOptions Auto =
        new(true, true, true, useColorIndexing: true, selectBest: true,
            useMetaHuffman: true, autoPredictor: true);
}

/// <summary>
/// 纯托管 VP8L 无损编码器。
/// 位流结构严格对照 RFC 9649 与 <see cref="Vp8LDecoder"/>：
/// 5 字节帧头 → 变换（仅 level 0）→ color cache → （level 0）meta-Huffman 标志 →
/// entropy image（可选）→ 每组 5 张 Huffman 表 → 像素 token。
/// transform 使用 subtract-green、cross-color、predictor（统一模式 / 逐 tile 模式
/// 按估计体积自动选择）；LZ77 使用 hash 链多候选并按码表代价近似做取舍；
/// meta-Huffman 按 tile 直方图聚类成若干组码表。
/// </summary>
internal static class Vp8LEncoder
{
    private const int Vp8LSignature = 0x2F;
    private const int MaxDimension = 16384;
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int MaxCopyLength = 4096;
    private const int MaxDistance = 1048456;

    /// <summary>VP8L color cache 的乘性哈希常数（编码端与解码端定义一致）。</summary>
    private const uint HashMultiplier = 0x1e35a7bdu;

    /// <summary>meta-Huffman 的 entropy image 最多保留的 tile 数（同时也是内存上界）。</summary>
    private const int MaxHuffmanTiles = 1024;

    /// <summary>meta-Huffman 单幅图像最多使用的组数（远低于解码端 200 组的紧凑映射阈值）。</summary>
    private const int MaxHuffmanGroups = 16;

    /// <summary>
    /// token 缓存（统计遍 → 写出遍复用）的像素数上限。超过该尺寸不再缓存，
    /// 写出遍重新遍历一遍，以换取可控的峰值内存。
    /// 每像素最坏一个 token（紧凑 <see cref="Token"/> 16B）+ 位置 4B，2048×2048
    /// 时约 84MB，是"能省一次 LZ77 搜索"与峰值内存之间的折中。
    /// </summary>
    private const int MaxCachedTokenPixels = 1 << 22;

    /// <summary>
    /// 启用「真实码长多遍统计」的最小像素数。更小的图上 LZ77 命中很少，后续各遍几乎不改
    /// 变结果，跳过可省掉额外遍历。
    /// </summary>
    private const int MinRefinePixels = 4096;

    /// <summary>
    /// 真实码长精化的最大轮数。每轮都要整图重走一遍 LZ77；实测第 3 轮起收益已低于
    /// 0.02%，再多轮只会线性增加耗时。
    /// </summary>
    private const int MaxRefinePasses = 2;

    /// <summary>调色板剪枝的颜色数上限（配合"≤0.5 字节/像素"一起判断）。</summary>
    private const int MaxDecisivePaletteColors = 16;

    /// <summary>把 RGBA 编码成完整的 VP8L chunk 数据（不含 RIFF 容器）。</summary>
    public static byte[] EncodeVp8L(ReadOnlySpan<byte> rgba, int width, int height, Vp8LEncoderOptions options,
        Vp8LEncodeDiagnostics? diagnostics = null)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "VP8L 尺寸必须在 1..16384 之间");
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA 像素长度与尺寸不匹配", nameof(rgba));

        if (options.SelectBest) return EncodeBest(rgba, width, height, diagnostics);

        int total = width * height;
        var pixels = ToArgb(rgba, total);
        bool alphaUsed = IsAlphaUsed(pixels);

        // 变换阶段（subtract-green / cross-color / predictor）只依赖像素本身，
        // 与 LZ77 / color cache 无关，因此先算一次，供后续 token 编码复用。
        PreparedPalette? palette = options.UseColorIndexing ? TryPreparePalette(pixels, width, height) : null;
        PreparedSpatial? spatial = null;
        if (palette == null && options.UseTransforms && width >= 8 && height >= 8)
        {
            var pair = PrepareSpatialPair(pixels, width, height, options.AutoCrossColor, options.AutoPredictor,
                options.UseLz77, options.UseColorCache, options.ForcedPredictorMode, options.ForcedCacheBits);
            spatial = options.AutoCrossColor ? pair.WithColor : pair.WithoutColor;
        }

        return EncodePrepared(pixels, width, height, alphaUsed, options, spatial, palette, diagnostics);
    }

    /// <summary>
    /// 编码诊断快照：记录最终位流各段占用与实际 token 构成。
    /// 只在显式请求时收集，正常生产路径传入 null，不产生额外开销。
    /// </summary>
    internal sealed class Vp8LEncodeDiagnostics
    {
        public int Width;
        public int Height;
        public bool HasPalette;
        public int PaletteColors;
        public bool HasSpatial;
        public bool HasCrossColor;
        public int CrossColorBits;
        public int PredictorBits;
        public int UniformPredictorMode;
        public bool UsesTileModes;

        public int CacheBits;
        public int NumGroups;
        public int MetaPrecisionBits;
        public int NumTiles;

        public long HeaderBits;
        public long CacheFlagBits;
        public long MetaImageBits;
        public long TableBits;
        public long TokenBits;

        /// <summary>按实际统计直方图计算的四通道一阶熵 + 附加位，即当前 token 集的理论下界。</summary>
        public long IdealTokenBits;

        /// <summary>各通道（G/R/B/A/dist）实际 Huffman 码长位数，用于诊断码表构造质量。</summary>
        public double[] ChannelHuffBits = new double[5];

        /// <summary>各通道的理论熵位数。</summary>
        public double[] ChannelIdealBits = new double[5];

        /// <summary>构建出的码表中出现的最大码长（等于上限 15 说明限长逻辑实际生效）。</summary>
        public int MaxCodeLengthSeen;

        public int LiteralCount;
        public int CacheSymbolCount;
        public int CopyCount;
        public int CopiedPixels;

        /// <summary>copy 距离分布：≤8 / ≤32 / ≤128 / ≤512 / ≤2048 / ≤8192 / &gt;8192。</summary>
        public readonly int[] DistanceBuckets = new int[7];

        /// <summary>匹配长度分布：&lt;8 / &lt;16 / &lt;32 / &lt;64 / &lt;256 / ≥256。</summary>
        public readonly int[] MatchLengthBuckets = new int[6];

        public int TotalPixels;

        public double BytesPerPixel(int totalBytes) => Width * Height == 0 ? 0 : (double)totalBytes / (Width * Height);
    }


    /// <summary>调色板路线预处理结果：调色板差分图与打包索引像素。</summary>
    private sealed class PreparedPalette
    {
        public uint[] Deltas = System.Array.Empty<uint>();
        public int NumColors;
        public uint[] Packed = System.Array.Empty<uint>();
        public int PackedWidth;
        public int Width;
        public int Height;
    }

    /// <summary>空间变换路线预处理结果：残差像素、transform 头所需数据与 predictor 模式图。</summary>
    private sealed class PreparedSpatial
    {
        public uint[] Residual = System.Array.Empty<uint>();
        public int Width;
        public int Height;
        public int Bits;
        public PredictorPlan Plan;
        public uint[] ModePixels = System.Array.Empty<uint>();
        public uint[]? ColorData;
        public int ColorBits;
    }

    private static PreparedPalette? TryPreparePalette(uint[] pixels, int width, int height)
    {
        if (!TryBuildPalette(pixels, width, height, out uint[] deltas, out int numColors,
                out uint[] packed, out int packedWidth))
            return null;
        return new PreparedPalette
        {
            Deltas = deltas,
            NumColors = numColors,
            Packed = packed,
            PackedWidth = packedWidth,
            Width = width,
            Height = height,
        };
    }

    /// <summary>空间变换的两种产物：带 cross-color 与不带 cross-color，供不同 token 候选复用。</summary>
    private sealed class PreparedSpatialPair
    {
        public PreparedSpatial WithColor = null!;
        public PreparedSpatial WithoutColor = null!;
    }

    /// <summary>
    /// 计算空间变换：subtract-green →（可选）cross-color → predictor。
    /// predictor 搜索以"仅 subtract-green"的像素为基准，无 cross-color 的候选直接复用这次搜索结果；
    /// cross-color 的多个候选先用基准方案做一次廉价代价估计，只有确实更优的候选才单独重做一遍
    /// predictor 搜索。返回结果与逐候选全量搜索只在近并列的情况下可能不同。
    /// </summary>
    private static PreparedSpatialPair PrepareSpatialPair(uint[] source, int width, int height,
        bool autoCrossColor, bool autoPredictor, bool useLz77, bool useColorCache, int forcedMode = -1,
        int forcedCacheBits = -1)
    {
        var sg = (uint[])source.Clone();
        ApplySubtractGreenForward(sg);
        int cacheBits = useColorCache ? ChooseCacheBitsBySize(sg.Length, forcedCacheBits) : 0;

        int bits = width >= 16 && height >= 16 ? 4 : 3;
        int subWidth = SubSampleSize(width, bits);
        int subHeight = SubSampleSize(height, bits);

        var basePlan = ChoosePredictorPlan(sg, width, height, bits, !autoPredictor, forcedMode, useLz77, cacheBits);

        var plain = (uint[])sg.Clone();
        var plainModes = new uint[subWidth * subHeight];
        ApplyPredictorPlan(plain, width, height, bits, basePlan, plainModes);
        var withoutColor = new PreparedSpatial
        {
            Residual = plain,
            Width = width,
            Height = height,
            Bits = bits,
            Plan = basePlan,
            ModePixels = plainModes,
            ColorData = null,
            ColorBits = 0,
        };

        if (!autoCrossColor)
            return new PreparedSpatialPair { WithColor = withoutColor, WithoutColor = withoutColor };

        // cross-color 不是免费的：逐 tile 拟合在 mod-256 回绕处可能得到很差的系数，
        // 反而把残差打散。这里用实际残差熵比较候选：逐 tile 乘数，以及整幅图共用一张
        // 系数表（取逐 tile 拟合的中位数，抗离群点）。都不如"不写这个变换"时放弃
        // （整条变换列表不出现该变换仍完全合法）。
        var perTile = new uint[subWidth * subHeight];
        FitCrossColorMultipliers(sg, width, height, bits, perTile);

        CrossColorCandidate? bestCandidate = null;
        double bestCost = basePlan.Cost;
        foreach (var candidate in BuildCrossColorCandidates(sg, width, height, bits, perTile))
        {
            double cost = PlanCostUnder(candidate.Pixels, width, height, bits, basePlan);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestCandidate = candidate;
            }
        }
        if (bestCandidate == null)
            return new PreparedSpatialPair { WithColor = withoutColor, WithoutColor = withoutColor };

        var chosenCandidate = bestCandidate.Value;
        // cross-color 之后的模式沿用基准方案的统一模式（cross-color 只改 R/B，邻域结构不变），
        // 避免为每种候选再跑一次完整模式搜索；基准方案用了逐 tile 模式时才重新搜索。
        int colorForced = forcedMode >= 0 ? forcedMode
            : basePlan.TileModes == null ? basePlan.UniformMode : -1;
        var colorPlan = ChoosePredictorPlan(chosenCandidate.Pixels, width, height, bits,
            !autoPredictor, colorForced, useLz77, cacheBits);
        if (colorPlan.Cost >= basePlan.Cost)
            return new PreparedSpatialPair { WithColor = withoutColor, WithoutColor = withoutColor };

        var colorModes = new uint[subWidth * subHeight];
        ApplyPredictorPlan(chosenCandidate.Pixels, width, height, bits, colorPlan, colorModes);
        var withColor = new PreparedSpatial
        {
            Residual = chosenCandidate.Pixels,
            Width = width,
            Height = height,
            Bits = bits,
            Plan = colorPlan,
            ModePixels = colorModes,
            ColorData = chosenCandidate.Data,
            ColorBits = chosenCandidate.Bits,
        };
        return new PreparedSpatialPair { WithColor = withColor, WithoutColor = withoutColor };
    }

    /// <summary>用给定 predictor 方案估算像素数组的残差代价（与 <see cref="ChoosePredictorPlan"/> 的度量一致）。</summary>
    private static double PlanCostUnder(uint[] pixels, int width, int height, int bits, PredictorPlan plan)
    {
        var histogram = new int[4 * NumLiteralCodes];
        if (plan.TileModes == null)
        {
            AccumulateResidualHistogram(pixels, width, 0, width, 0, height, plan.UniformMode, histogram);
        }
        else
        {
            AccumulateResidualHistogramWithTileModes(pixels, width, height, SubSampleSize(width, bits),
                bits, plan.TileModes, histogram);
        }
        double cost = ChannelsEntropyAndTables(histogram);
        if (plan.TileModes != null) cost += EstimateModeImageCost(plan.TileModes);
        return cost;
    }

    /// <summary>写 VP8L 图像流：5 字节帧头 + 变换列表 + token 流。</summary>
    private static byte[] EncodePrepared(uint[] pixels, int width, int height, bool alphaUsed,
        Vp8LEncoderOptions options, PreparedSpatial? spatial, PreparedPalette? palette,
        Vp8LEncodeDiagnostics? diagnostics = null)
    {
        var writer = new LsbBitWriter();
        writer.WriteBits(Vp8LSignature, 8);
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBit(alphaUsed ? 1 : 0);
        writer.WriteBits(0, 3); // version

        uint[] streamPixels = pixels;
        int streamWidth = width;
        int streamHeight = height;

        if (palette != null)
        {
            // Color indexing 会改变图像尺寸，必须排在变换列表最前（解码端最后应用）。
            writer.WriteBit(1);
            writer.WriteBits(3, 2);
            writer.WriteBits((uint)(palette.NumColors - 1), 8);
            EncodeSubImage(writer, palette.Deltas, palette.NumColors, 1);
            writer.WriteBit(0); // 变换列表结束

            streamPixels = palette.Packed;
            streamWidth = palette.PackedWidth;
        }
        else if (spatial != null)
        {
            WriteSpatialTransforms(writer, spatial);
            streamPixels = spatial.Residual;
            streamWidth = spatial.Width;
            streamHeight = spatial.Height;
        }
        else
        {
            writer.WriteBit(0); // 变换列表结束
        }

        if (diagnostics != null)
        {
            diagnostics.Width = width;
            diagnostics.Height = height;
            diagnostics.TotalPixels = width * height;
            diagnostics.HasPalette = palette != null;
            if (palette != null) diagnostics.PaletteColors = palette.NumColors;
            diagnostics.HasSpatial = spatial != null;
            if (spatial != null)
            {
                diagnostics.HasCrossColor = spatial.ColorData != null;
                diagnostics.CrossColorBits = spatial.ColorBits;
                diagnostics.PredictorBits = spatial.Bits;
                diagnostics.UsesTileModes = spatial.Plan.TileModes != null;
                diagnostics.UniformPredictorMode = spatial.Plan.UniformMode;
            }
            diagnostics.HeaderBits = writer.TotalBits;
        }

        EncodeTokenStream(writer, streamPixels, streamWidth, streamHeight, options, level0: true, diagnostics);
        return writer.ToArray();
    }

    /// <summary>写 subtract-green / cross-color / predictor 三个变换头（不含结束位）。</summary>
    private static void WriteSpatialTransforms(LsbBitWriter writer, PreparedSpatial spatial)
    {
        writer.WriteBit(1);
        writer.WriteBits(2, 2); // subtract-green

        if (spatial.ColorData != null)
        {
            writer.WriteBit(1);
            writer.WriteBits(1, 2); // cross-color
            writer.WriteBits((uint)(spatial.ColorBits - 2), 3);
            EncodeSubImage(writer, spatial.ColorData,
                SubSampleSize(spatial.Width, spatial.ColorBits),
                SubSampleSize(spatial.Height, spatial.ColorBits));
        }

        writer.WriteBit(1);
        writer.WriteBits(0, 2); // predictor
        writer.WriteBits((uint)(spatial.Bits - 2), 3);
        EncodeSubImage(writer, spatial.ModePixels,
            SubSampleSize(spatial.Width, spatial.Bits),
            SubSampleSize(spatial.Height, spatial.Bits));

        writer.WriteBit(0); // 变换列表结束
    }

    /// <summary>子图像（调色板 / cross-color 系数 / predictor 模式 / entropy image）一律 literal-only。</summary>
    private static void EncodeSubImage(LsbBitWriter writer, uint[] pixels, int width, int height)
        => EncodeTokenStream(writer, pixels, width, height, Vp8LEncoderOptions.LiteralOnly, level0: false);

    /// <summary>RGBA → ARGB（VP8L 内部像素布局）。</summary>
    private static uint[] ToArgb(ReadOnlySpan<byte> rgba, int total)
    {
        var pixels = new uint[total];
        for (int i = 0, o = 0; i < total; i++, o += 4)
            pixels[i] = ((uint)rgba[o + 3] << 24) | ((uint)rgba[o] << 16) | ((uint)rgba[o + 1] << 8) | rgba[o + 2];
        return pixels;
    }

    private static bool IsAlphaUsed(uint[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((pixels[i] >> 24) != 0xFF) return true;
        }
        return false;
    }

    /// <summary>
    /// 在若干确定性候选配置中选输出最小的一个。
    /// 候选涵盖：空间变换（subtract-green + cross-color + predictor）与调色板（color indexing）
    /// 两条互斥路线，各自再与 color cache / LZ77 组合。所有候选都是合法可逆的位流，
    /// 选择只影响体积，不影响正确性。
    ///
    /// 变换阶段与 token 编码阶段分离：同一份变换结果被多个 token 候选复用，
    /// 避免为每个候选重复做 predictor 搜索（这是编码耗时的主要来源）。
    /// </summary>
    private static byte[] EncodeBest(ReadOnlySpan<byte> rgba, int width, int height,
        Vp8LEncodeDiagnostics? diagnostics = null)
    {
        int total = width * height;
        var pixels = ToArgb(rgba, total);
        bool alphaUsed = IsAlphaUsed(pixels);

        // 调色板候选代价很低，先算出来：它们既参与最终比较，也用来判断是否值得再做
        // （较贵的）空间变换搜索。
        PreparedPalette? palette = TryPreparePalette(pixels, width, height);
        byte[]? paletteBest = null;
        Vp8LEncoderOptions paletteBestOptions = default;
        if (palette != null)
        {
            var withCache = new Vp8LEncoderOptions(false, true, true, useColorIndexing: true);
            paletteBest = EncodePrepared(pixels, width, height, alphaUsed, withCache, null, palette);
            paletteBestOptions = withCache;
            var noCacheOptions = new Vp8LEncoderOptions(false, false, true, useColorIndexing: true);
            var noCache = EncodePrepared(pixels, width, height, alphaUsed, noCacheOptions, null, palette);
            if (noCache.Length < paletteBest.Length) { paletteBest = noCache; paletteBestOptions = noCacheOptions; }
            var noLz77Options = new Vp8LEncoderOptions(false, true, false, useColorIndexing: true);
            var noLz77 = EncodePrepared(pixels, width, height, alphaUsed, noLz77Options, null, palette);
            if (noLz77.Length < paletteBest.Length) { paletteBest = noLz77; paletteBestOptions = noLz77Options; }
        }

        // 剪枝：颜色数很少且调色板已经把整幅图压到 ≤ 0.5 字节/像素时，
        // color indexing 每个像素只需 log2(颜色数) 位，空间变换不可能低于这个量级，
        // 再做一遍逐 tile predictor 搜索只会白花时间（大图上占大头）。
        // 阈值取 16 色 + 0.5B/px，避免把"多色但抖动"的图误剪（那类图索引流本身不压缩）。
        bool paletteDecisive = palette != null && palette.NumColors <= MaxDecisivePaletteColors &&
            paletteBest!.Length * 4L <= total;

        // 变换阶段的结果只依赖像素，与 cache / LZ77 无关，因此每种变换组合只算一次。
        PreparedSpatial? spatial = null;
        PreparedSpatial? spatialNoColor = null;
        if (!paletteDecisive && width >= 8 && height >= 8)
        {
            var pair = PrepareSpatialPair(pixels, width, height, autoCrossColor: true, autoPredictor: true,
                useLz77: true, useColorCache: true);
            spatial = pair.WithColor;
            spatialNoColor = pair.WithoutColor;
        }

        byte[]? best = null;
        Vp8LEncoderOptions bestOptions = default;
        PreparedSpatial? bestSpatial = null;
        PreparedPalette? bestPalette = null;
        void Consider(byte[] candidate, in Vp8LEncoderOptions options, PreparedSpatial? usedSpatial, PreparedPalette? usedPalette)
        {
            if (best == null || candidate.Length < best.Length)
            {
                best = candidate;
                bestOptions = options;
                bestSpatial = usedSpatial;
                bestPalette = usedPalette;
            }
        }

        if (!paletteDecisive)
        {
            // cross-color 被拒绝时 WithColor 与 WithoutColor 是同一个对象，
            // 此时 (cache, spatial) 与 (no-cache, spatialNoColor) 等候选会完全重合，
            // 必须去重，否则大图上会白跑一整遍 token 编码（耗时占比很高）。
            bool colorActive = spatial != null && spatial != spatialNoColor;
            var a1 = new Vp8LEncoderOptions(true, true, true, autoPredictor: true);
            Consider(EncodePrepared(pixels, width, height, alphaUsed, a1, spatial, null), a1, spatial, null);
            var a2 = new Vp8LEncoderOptions(true, false, true, autoPredictor: true, autoCrossColor: false);
            Consider(EncodePrepared(pixels, width, height, alphaUsed, a2, spatialNoColor, null), a2, spatialNoColor, null);
            if (colorActive)
            {
                // 无 cross-color 的候选：cross-color 的残差熵通常更低，但 mod-256 回绕会在
                // 残差里留下一道随行推进的断点（渐变图每行恰好一个像素），整行重复因此被
                // 打断，LZ77 收益大幅缩水。这类收益只有在真实编码后才看得出来，所以交给
                // 候选选择而不是熵估计来裁决。
                var a3 = new Vp8LEncoderOptions(true, false, true, autoPredictor: true);
                Consider(EncodePrepared(pixels, width, height, alphaUsed, a3, spatial, null), a3, spatial, null);
            }
            var a4 = new Vp8LEncoderOptions(true, false, false, autoPredictor: true);
            Consider(EncodePrepared(pixels, width, height, alphaUsed, a4, spatial, null), a4, spatial, null);
        }
        Consider(EncodePrepared(pixels, width, height, alphaUsed, Vp8LEncoderOptions.LiteralOnly, null, null),
            Vp8LEncoderOptions.LiteralOnly, null, null);
        if (paletteBest != null) Consider(paletteBest, paletteBestOptions, null, palette);

        // 诊断快照按胜出候选重编一遍：只在显式请求时发生，生产路径不付这份代价。
        if (diagnostics != null && best != null)
            EncodePrepared(pixels, width, height, alphaUsed, bestOptions, bestSpatial, bestPalette, diagnostics);

        return best!;
    }

    // ==================================================================
    //  token 流（level 0 与 transform 子图像共用）
    // ==================================================================

    /// <summary>
    /// 写 color cache 标志 + 统计 + （level 0）meta-Huffman + 各 Huffman 表 + 像素 token。
    /// 变换列表必须由调用方在此之前写好。
    /// </summary>
    private static void EncodeTokenStream(LsbBitWriter writer, uint[] pixels, int width, int height,
        Vp8LEncoderOptions options, bool level0, Vp8LEncodeDiagnostics? diagnostics = null)
    {

        int cacheBits = options.UseColorCache
            ? ChooseCacheBits(pixels, width, height, options.ForcedCacheBits) : 0;
        if (cacheBits > 0)
        {
            writer.WriteBit(1);
            writer.WriteBits((uint)cacheBits, 4);
        }
        else
        {
            writer.WriteBit(0);
        }
        if (diagnostics != null)
        {
            diagnostics.CacheBits = cacheBits;
            diagnostics.CacheFlagBits = writer.TotalBits - diagnostics.HeaderBits;
        }

        int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
        int greenAlphabet = NumLiteralCodes + NumLengthCodes + cacheSize;

        // 统计 token（顺带决定 meta-Huffman 分组），并构造码表。
        var stats = CollectStatistics(pixels, width, height, cacheBits, greenAlphabet, options,
            allowMetaHuffman: level0 && options.UseMetaHuffman, diagnostics);

        if (diagnostics != null)
        {
            double ideal = stats.ExtraBits;
            for (int g = 0; g < stats.NumGroups; g++)
            {
                ideal += EntropyBits(stats.GreenFreq[g]);
                ideal += EntropyBits(stats.RedFreq[g]);
                ideal += EntropyBits(stats.BlueFreq[g]);
                ideal += EntropyBits(stats.AlphaFreq[g]);
                ideal += EntropyBits(stats.DistanceFreq[g]);
            }
            diagnostics.IdealTokenBits = (long)Math.Round(ideal);

            for (int g = 0; g < stats.NumGroups; g++)
            {
                MeasureChannel(stats.GreenFreq[g], stats.GreenCodes[g], 0);
                MeasureChannel(stats.RedFreq[g], stats.RedCodes[g], 1);
                MeasureChannel(stats.BlueFreq[g], stats.BlueCodes[g], 2);
                MeasureChannel(stats.AlphaFreq[g], stats.AlphaCodes[g], 3);
                MeasureChannel(stats.DistanceFreq[g], stats.DistanceCodes[g], 4);
            }
        }

        void MeasureChannel(int[] freq, Vp8LHuffmanCode code, int index)
        {
            long total = 0;
            for (int i = 0; i < freq.Length; i++) total += freq[i];
            if (total == 0) return;
            double huff = 0, entropy = 0;
            for (int i = 0; i < freq.Length; i++)
            {
                int f = freq[i];
                if (f == 0) continue;
                huff += (double)f * code.Lengths[i];
                entropy += f * Math.Log2(total / (double)f);
                if (code.Lengths[i] > diagnostics!.MaxCodeLengthSeen) diagnostics.MaxCodeLengthSeen = code.Lengths[i];
            }
            diagnostics!.ChannelHuffBits[index] += huff;
            diagnostics.ChannelIdealBits[index] += entropy;
        }

        long beforeMetaImage = writer.TotalBits;
        // meta-Huffman 标志只在 level 0 出现（RFC 9649 §2.5），子图像必须不写这个位。
        if (level0)
        {
            if (stats.NumGroups > 1)
            {
                writer.WriteBit(1);
                writer.WriteBits((uint)(stats.PrecisionBits - 2), 3);
                EncodeSubImage(writer, stats.EntropyPixels, stats.TilesX, stats.TilesY);
            }
            else
            {
                writer.WriteBit(0);
            }
        }
        long beforeTables = writer.TotalBits;
        if (diagnostics != null)
        {
            diagnostics.MetaImageBits = beforeTables - beforeMetaImage;
            diagnostics.NumGroups = stats.NumGroups;
            diagnostics.MetaPrecisionBits = stats.NumGroups > 1 ? stats.PrecisionBits : 0;
            diagnostics.NumTiles = stats.NumGroups > 1 ? stats.TilesX * stats.TilesY : 0;
        }

        for (int g = 0; g < stats.NumGroups; g++)
        {
            stats.GreenCodes[g].WriteTo(writer);
            stats.RedCodes[g].WriteTo(writer);
            stats.BlueCodes[g].WriteTo(writer);
            stats.AlphaCodes[g].WriteTo(writer);
            stats.DistanceCodes[g].WriteTo(writer);
        }
        if (diagnostics != null) diagnostics.TableBits = writer.TotalBits - beforeTables;
        long beforeTokens = writer.TotalBits;


        // 写出遍：统计遍已经缓存了 token 序列（LZ77 搜索不必重跑），
        // 只有在图像大到不值得缓存时才重新遍历。
        if (stats.CachedTokens != null)
        {
            var tokens = stats.CachedTokens;
            var positions = stats.CachedPositions!;
            for (int i = 0; i < stats.CachedCount; i++)
                WriteToken(tokens[i], positions[i]);
        }
        else
        {
            var emitter = new TokenWalker(pixels, width, height, options.UseLz77, cacheBits, stats.Cost);
            Token token;
            int position;
            while (emitter.Next(out token, out position))
                WriteToken(token, position);
        }
        if (diagnostics != null) diagnostics.TokenBits = writer.TotalBits - beforeTokens;

        void WriteToken(Token token, int position)
        {
            int group = 0;
            if (stats.NumGroups > 1)
            {
                int tile = (position / width >> stats.PrecisionBits) * stats.TilesX +
                           (position % width >> stats.PrecisionBits);
                group = stats.GroupMap[tile];
            }

            var green = stats.GreenCodes[group];
            switch (token.Kind)
            {
                case TokenKind.Literal:
                    WriteSymbol(writer, green, token.GreenSymbol);
                    WriteSymbol(writer, stats.RedCodes[group], token.Red);
                    WriteSymbol(writer, stats.BlueCodes[group], token.Blue);
                    WriteSymbol(writer, stats.AlphaCodes[group], token.Alpha);
                    break;
                case TokenKind.ColorCache:
                    WriteSymbol(writer, green, token.GreenSymbol);
                    break;
                case TokenKind.Copy:
                    WriteSymbol(writer, green, token.GreenSymbol);
                    if (token.LengthExtraBits > 0)
                        writer.WriteBits((uint)token.LengthExtraValue, token.LengthExtraBits);
                    WriteSymbol(writer, stats.DistanceCodes[group], token.DistanceSymbol);
                    if (token.DistanceExtraBits > 0)
                        writer.WriteBits((uint)token.DistanceExtraValue, token.DistanceExtraBits);
                    break;
            }
        }
    }

    private static void WriteSymbol(LsbBitWriter writer, Vp8LHuffmanCode code, int symbol)
    {
        if (code.SingleSymbol >= 0) return;
        writer.WriteCode(code.Codes[symbol], code.Lengths[symbol]);
    }

    /// <summary>color cache 位数候选：位数越小码表越小，位数越大命中越多，逐图实测取最小。</summary>
    /// <remarks>
    /// 10 位在渐变类样本上只比 8 位省 0.1% 以内，却要多付一次整图走查，因此不参与搜索。
    /// </remarks>
    private static readonly int[] CacheBitsCandidates = { 4, 6, 8 };

    /// <summary>cache 位数选择时的抽样像素上限（中等图像只估算中部横带）。</summary>
    private const int CacheBitsSamplePixels = 1 << 15;

    /// <summary>
    /// 逐图选择 color cache 位数。
    /// cache 尺寸并非越大越好：表变大后 green 字母表与缓存符号码长同步上升，命中率增长
    /// 抵不过码表开销增长。这里用真实 token 码长（含码表开销）比较候选位数。
    ///
    /// 适用范围有明确边界：位流体积同时受 meta-Huffman 分组影响，而估计阶段只能按单组
    /// 计算，图像越大、分组越多，排序越不可靠（实测 peppers/scene 整图估计会把 6 位
    /// 排到 4 位之上，实际 c4 明显更小）。因此超过 1MP 的图像不启用逐图选择，沿用
    /// 按尺寸的保守取值；中等图像取中部 ≤32K 像素横带估算，兼顾代表性与耗时。
    /// 候选按位数升序遍历，严格小于才替换，近并列时保留更小的表。
    /// </summary>
    private static int ChooseCacheBits(uint[] pixels, int width, int height, int forcedBits)
    {
        if (forcedBits >= 0) return forcedBits;
        int total = pixels.Length;
        if (total < 256) return 4;
        if (total > 1 << 20) return ChooseCacheBitsBySize(total, forcedBits);

        int bestBits = CacheBitsCandidates[0];
        double bestCost = double.MaxValue;
        foreach (int bits in CacheBitsCandidates)
        {
            double cost = EstimateTokenBitsBand(pixels, width, height, bits, CacheBitsSamplePixels);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestBits = bits;
            }
        }
        return bestBits;
    }

    /// <summary>
    /// 仅按像素数取 color cache 位数（供 predictor 方案搜索时的代价估算使用）。
    /// 真正的 cache 位数在拿到最终残差后用 <see cref="ChooseCacheBits"/> 逐图决定，
    /// 这里不参与输出选择。
    /// </summary>
    private static int ChooseCacheBitsBySize(int total, int forcedBits)
    {
        if (forcedBits >= 0) return forcedBits;
        if (total >= 4096) return 8;
        if (total >= 256) return 6;
        return 4;
    }

    private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    // ==================================================================
    //  Token 统计 + meta-Huffman 分组
    // ==================================================================

    private sealed class TokenStatistics
    {
        public int NumGroups = 1;
        public int PrecisionBits;
        public int TilesX;
        public int TilesY;
        public int[] GroupMap = System.Array.Empty<int>();
        public uint[] EntropyPixels = System.Array.Empty<uint>();
        public int[][] GreenFreq = System.Array.Empty<int[]>();
        public int[][] RedFreq = System.Array.Empty<int[]>();
        public int[][] BlueFreq = System.Array.Empty<int[]>();
        public int[][] AlphaFreq = System.Array.Empty<int[]>();
        public int[][] DistanceFreq = System.Array.Empty<int[]>();
        public Vp8LHuffmanCode[] GreenCodes = System.Array.Empty<Vp8LHuffmanCode>();
        public Vp8LHuffmanCode[] RedCodes = System.Array.Empty<Vp8LHuffmanCode>();
        public Vp8LHuffmanCode[] BlueCodes = System.Array.Empty<Vp8LHuffmanCode>();
        public Vp8LHuffmanCode[] AlphaCodes = System.Array.Empty<Vp8LHuffmanCode>();
        public Vp8LHuffmanCode[] DistanceCodes = System.Array.Empty<Vp8LHuffmanCode>();
        public CostModel Cost = null!;

        /// <summary>
        /// 统计遍缓存的 token 序列（含起始位置），写出遍直接复用，避免重复 LZ77 搜索。
        /// 图像过大时不缓存（改由写出遍重新遍历），以控制峰值内存。
        /// </summary>
        public Token[]? CachedTokens;
        public int[]? CachedPositions;
        public int CachedCount;

        /// <summary>所有 copy token 的长度/距离附加位数之和（与码符号位数相加即为 token 位数）。</summary>
        public long ExtraBits;
    }

    /// <summary>
    /// 多遍统计：
    /// <list type="number">
    /// <item>首遍用静态代价模型（像素直方图熵取整）做 LZ77/cache 选择，并缓存 token；</item>
    /// <item>后续各遍用上一遍的<b>真实 Huffman 码长</b>重建代价模型，重新做 LZ77/cache 选择；</item>
    /// <item>每遍都用 <see cref="PassBits"/> 算出该 token 集合在最终码表下的真实总位数，
    ///       只有严格更小才采纳，因此结果永远不会劣于首遍（首遍即多遍之前的原行为）。</item>
    /// </list>
    /// 采纳精化结果时首遍的 token 缓存作废（写出遍用自己的代价模型重走一遍），
    /// 因此任何时刻只持有一份 token 缓存，峰值内存与单遍相同。
    /// </summary>
    private static TokenStatistics CollectStatistics(uint[] pixels, int width, int height, int cacheBits,
        int greenAlphabet, Vp8LEncoderOptions options, bool allowMetaHuffman,
        Vp8LEncodeDiagnostics? diagnostics = null, int maxChainOverride = -1, bool cacheTokens = true)
    {
        var first = CollectStatisticsPass(pixels, width, height, cacheBits, greenAlphabet, options,
            allowMetaHuffman, diagnostics, maxChainOverride, cacheTokens, null);

        // 小图/无 LZ77 时精化不会改变选择（只有 literal 与 cache），直接返回首遍。
        if (!options.UseLz77 || width * height < MinRefinePixels)
            return first;

        double bestBits = PassBits(first, greenAlphabet);
        TokenStatistics best = first;
        CostModel? bestModel = null;
        var model = CostModel.FromStatistics(first, pixels, cacheBits, greenAlphabet,
            buildCoveredPrefix: true);
        double previousBits = bestBits;

        for (int pass = 0; pass < MaxRefinePasses; pass++)
        {
            var candidate = CollectStatisticsPass(pixels, width, height, cacheBits, greenAlphabet, options,
                allowMetaHuffman, diagnostics: null, maxChainOverride, cacheTokens: false, costOverride: model);
            double bits = PassBits(candidate, greenAlphabet);

            // 代价继续下降才进入下一轮；停止下降时说明已经收敛（再走一遍只会得到同样结果）。
            bool improving = bits < previousBits;
            previousBits = bits;
            if (bits < bestBits)
            {
                bestBits = bits;
                best = candidate;
                bestModel = model;
            }
            if (!improving) break;

            model = CostModel.FromStatistics(candidate, pixels, cacheBits, greenAlphabet,
                buildCoveredPrefix: true);
        }

        if (bestModel == null)
            return best;

        // 精化胜出：首遍缓存作废（写出遍按 best.Cost 重新走一遍，结果与统计遍确定一致），
        // 避免同时持有两份 token 缓存。
        first.CachedTokens = null;
        first.CachedPositions = null;
        if (diagnostics == null)
            return best;

        // 诊断快照必须反映实际输出，按胜出模型重编一遍（仅在显式请求诊断时发生）。
        return CollectStatisticsPass(pixels, width, height, cacheBits, greenAlphabet, options,
            allowMetaHuffman, diagnostics, maxChainOverride, cacheTokens: false, costOverride: bestModel);
    }

    /// <summary>
    /// 一趟统计结果在最终码表下的真实总位数：各通道 Σ freq·码长 + 码表位数 +
    /// copy 附加位 + entropy image 实际编码位数。单符号通道写出时被完全跳过，这里同样记 0。
    /// 这是多遍选择与 meta-Huffman 组数选择的统一度量。
    /// </summary>
    private static double PassBits(TokenStatistics stats, int greenAlphabet)
    {
        double bits = stats.ExtraBits;
        for (int g = 0; g < stats.NumGroups; g++)
        {
            bits += ChannelBits(stats.GreenFreq[g], greenAlphabet);
            bits += ChannelBits(stats.RedFreq[g], NumLiteralCodes);
            bits += ChannelBits(stats.BlueFreq[g], NumLiteralCodes);
            bits += ChannelBits(stats.AlphaFreq[g], NumLiteralCodes);
            bits += ChannelBits(stats.DistanceFreq[g], NumDistanceCodes);
        }
        if (stats.NumGroups > 1)
            bits += MetaImageBits(stats.TilesX, stats.TilesY, stats.GroupMap, stats.NumGroups);
        return bits;
    }

    /// <summary>
     /// 遍历 token 一遍收集频次：默认单组；启用 meta-Huffman 时按 2^precision 的 tile
     /// 收集直方图，再聚类成若干组（组数按估计码长 + 码表开销选最优）。
     /// 组 id 一定连续覆盖 0..numGroups-1，且 numGroups ≤ <see cref="MaxHuffmanGroups"/>，
     /// 因此解码端不会触发紧凑映射，位流中的表顺序与 entropy image 的组 id 一一对应。
     /// </summary>
    private static TokenStatistics CollectStatisticsPass(uint[] pixels, int width, int height, int cacheBits,
        int greenAlphabet, Vp8LEncoderOptions options, bool allowMetaHuffman,
        Vp8LEncodeDiagnostics? diagnostics, int maxChainOverride, bool cacheTokens,
        CostModel? costOverride)
    {
        var stats = new TokenStatistics();
        stats.Cost = costOverride ?? CostModel.Build(pixels, cacheBits, greenAlphabet, options.UseLz77);

        int precisionBits = ChooseHuffmanPrecision(width, height);
        int tilesX = SubSampleSize(width, precisionBits);
        int tilesY = SubSampleSize(height, precisionBits);
        int numTiles = tilesX * tilesY;
        bool meta = allowMetaHuffman && numTiles >= 4 && width * height >= 256;

        var walker = new TokenWalker(pixels, width, height, options.UseLz77, cacheBits, stats.Cost,
            maxChainOverride);
        Token token;
        int position;

        // 统计遍顺带缓存 token 序列，写出遍直接复用（LZ77 搜索只做一次）。
        // 缓存按像素数上限（每像素最坏一个 token、约 16 字节 + 位置），避免大图内存失控。
        bool cache = cacheTokens && width * height <= MaxCachedTokenPixels;
        if (cache)
        {
            stats.CachedTokens = new Token[width * height];
            stats.CachedPositions = new int[width * height];
        }

        void RecordToken(Token t, int pos)
        {
            if (t.Kind == TokenKind.Copy) stats.ExtraBits += t.LengthExtraBits + t.DistanceExtraBits;
            if (diagnostics != null) CountToken(t);
            if (!cache) return;
            stats.CachedTokens![stats.CachedCount] = t;
            stats.CachedPositions![stats.CachedCount] = pos;
            stats.CachedCount++;
        }

        void CountToken(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.Literal:
                    diagnostics!.LiteralCount++;
                    break;
                case TokenKind.ColorCache:
                    diagnostics!.CacheSymbolCount++;
                    break;
                case TokenKind.Copy:
                    diagnostics!.CopyCount++;
                    int length = DecodeCopyValue(t.GreenSymbol - NumLiteralCodes, t.LengthExtraValue);
                    diagnostics!.CopiedPixels += length;
                    int bucket = length < 8 ? 0 : length < 16 ? 1 : length < 32 ? 2 :
                        length < 64 ? 3 : length < 256 ? 4 : 5;
                    diagnostics!.MatchLengthBuckets[bucket]++;
                    int distance = Vp8LDistance.PlaneCodeToDistance(
                        width, DecodeCopyValue(t.DistanceSymbol, t.DistanceExtraValue));
                    int dbucket = distance <= 8 ? 0 : distance <= 32 ? 1 : distance <= 128 ? 2 :
                        distance <= 512 ? 3 : distance <= 2048 ? 4 : distance <= 8192 ? 5 : 6;
                    diagnostics!.DistanceBuckets[dbucket]++;
                    break;
            }
        }

        if (!meta)
        {
            var green = new int[greenAlphabet];
            var red = new int[NumLiteralCodes];
            var blue = new int[NumLiteralCodes];
            var alpha = new int[NumLiteralCodes];
            var distance = new int[NumDistanceCodes];
            while (walker.Next(out token, out position))
            {
                RecordToken(token, position);
                AccumulateToken(green, red, blue, alpha, distance, token);
            }

            stats.GreenFreq = new[] { green };
            stats.RedFreq = new[] { red };
            stats.BlueFreq = new[] { blue };
            stats.AlphaFreq = new[] { alpha };
            stats.DistanceFreq = new[] { distance };
            BuildCodes(stats, greenAlphabet);
            return stats;
        }

        // ---- 每 tile 的精确直方图 + 粗特征（用于聚类）----
        const int CoarseBuckets = 32;
        int coarseDims = 4 * CoarseBuckets + NumDistanceCodes;
        var tileGreen = new int[numTiles][];
        var tileRed = new int[numTiles][];
        var tileBlue = new int[numTiles][];
        var tileAlpha = new int[numTiles][];
        var tileDistance = new int[numTiles][];
        var coarse = new int[numTiles][];

        while (walker.Next(out token, out position))
        {
            RecordToken(token, position);
            int x = position % width;
            int y = position / width;
            int tile = (y >> precisionBits) * tilesX + (x >> precisionBits);

            int[] green = tileGreen[tile];
            if (green == null)
            {
                green = tileGreen[tile] = new int[greenAlphabet];
                tileRed[tile] = new int[NumLiteralCodes];
                tileBlue[tile] = new int[NumLiteralCodes];
                tileAlpha[tile] = new int[NumLiteralCodes];
                tileDistance[tile] = new int[NumDistanceCodes];
                coarse[tile] = new int[coarseDims];
            }

            AccumulateToken(green, tileRed[tile], tileBlue[tile], tileAlpha[tile], tileDistance[tile], token);
            AccumulateCoarse(coarse[tile], CoarseBuckets, token);
        }

        int bestGroups = 1;
        int[] bestAssign = new int[numTiles];
        Histograms best = MergeTiles(tileGreen, tileRed, tileBlue, tileAlpha, tileDistance,
            bestAssign, 1, greenAlphabet);
        double bestCost = ExactCost(best, 1, greenAlphabet) +
            MetaImageBits(tilesX, tilesY, bestAssign, 1);

        foreach (int wanted in new[] { 2, 4, 8, MaxHuffmanGroups })
        {
            if (wanted > numTiles || wanted <= bestGroups) continue;

            var assign = ClusterTiles(coarse, numTiles, wanted, coarseDims);
            CompactGroupIds(assign, numTiles, out int groups);
            if (groups <= 1) continue;

            var candidate = MergeTiles(tileGreen, tileRed, tileBlue, tileAlpha, tileDistance,
                assign, groups, greenAlphabet);
            double cost = ExactCost(candidate, groups, greenAlphabet) +
                MetaImageBits(tilesX, tilesY, assign, groups);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestGroups = groups;
                bestAssign = assign;
                best = candidate;
            }
        }

        stats.NumGroups = bestGroups;
        if (bestGroups > 1)
        {
            stats.PrecisionBits = precisionBits;
            stats.TilesX = tilesX;
            stats.TilesY = tilesY;
            stats.GroupMap = bestAssign;
            stats.EntropyPixels = new uint[numTiles];
            for (int t = 0; t < numTiles; t++) stats.EntropyPixels[t] = (uint)(bestAssign[t] << 8);
        }

        stats.GreenFreq = best.Green;
        stats.RedFreq = best.Red;
        stats.BlueFreq = best.Blue;
        stats.AlphaFreq = best.Alpha;
        stats.DistanceFreq = best.Distance;
        BuildCodes(stats, greenAlphabet);
        return stats;
    }

    private sealed class Histograms
    {
        public int[][] Green = System.Array.Empty<int[]>();
        public int[][] Red = System.Array.Empty<int[]>();
        public int[][] Blue = System.Array.Empty<int[]>();
        public int[][] Alpha = System.Array.Empty<int[]>();
        public int[][] Distance = System.Array.Empty<int[]>();
    }

    private static void AccumulateToken(int[] green, int[] red, int[] blue, int[] alpha, int[] distance,
        Token token)
    {
        switch (token.Kind)
        {
            case TokenKind.Literal:
                green[token.GreenSymbol]++;
                red[token.Red]++;
                blue[token.Blue]++;
                alpha[token.Alpha]++;
                break;
            case TokenKind.ColorCache:
                green[token.GreenSymbol]++;
                break;
            case TokenKind.Copy:
                green[token.GreenSymbol]++;
                distance[token.DistanceSymbol]++;
                break;
        }
    }

    private static void AccumulateCoarse(int[] feature, int buckets, Token token)
    {
        switch (token.Kind)
        {
            case TokenKind.Literal:
                feature[(token.GreenSymbol >> 3) & (buckets - 1)]++;
                feature[buckets + (token.Red >> 3)]++;
                feature[2 * buckets + (token.Blue >> 3)]++;
                feature[3 * buckets + (token.Alpha >> 3)]++;
                break;
            case TokenKind.ColorCache:
                feature[(token.GreenSymbol >> 3) & (buckets - 1)]++;
                break;
            case TokenKind.Copy:
                feature[(token.GreenSymbol >> 3) & (buckets - 1)]++;
                feature[4 * buckets + token.DistanceSymbol]++;
                break;
        }
    }

    /// <summary>把 tile 直方图按 <paramref name="assign"/> 合并成每组一份。</summary>
    private static Histograms MergeTiles(int[][] tileGreen, int[][] tileRed, int[][] tileBlue,
        int[][] tileAlpha, int[][] tileDistance, int[] assign, int numGroups, int greenAlphabet)
    {
        var result = new Histograms
        {
            Green = new int[numGroups][],
            Red = new int[numGroups][],
            Blue = new int[numGroups][],
            Alpha = new int[numGroups][],
            Distance = new int[numGroups][],
        };
        for (int g = 0; g < numGroups; g++)
        {
            result.Green[g] = new int[greenAlphabet];
            result.Red[g] = new int[NumLiteralCodes];
            result.Blue[g] = new int[NumLiteralCodes];
            result.Alpha[g] = new int[NumLiteralCodes];
            result.Distance[g] = new int[NumDistanceCodes];
        }

        for (int t = 0; t < assign.Length; t++)
        {
            if (tileGreen[t] == null) continue;
            int g = assign[t];
            AddInto(result.Green[g], tileGreen[t]);
            AddInto(result.Red[g], tileRed[t]);
            AddInto(result.Blue[g], tileBlue[t]);
            AddInto(result.Alpha[g], tileAlpha[t]);
            AddInto(result.Distance[g], tileDistance[t]);
        }
        return result;
    }

    private static void AddInto(int[] destination, int[] source)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] += source[i];
    }

    /// <summary>
    /// 用粗直方图做 k-means（整数点积 + 归一化 = 交叉熵的一阶近似），返回每个 tile 的组 id。
    /// 两轮迭代、初始中心取等间隔 tile，全程确定性。
    /// </summary>
    private static int[] ClusterTiles(int[][] features, int numTiles, int groupCount, int dims)
    {
        var assign = new int[numTiles];
        var centers = new int[groupCount][];
        var mass = new long[groupCount];
        for (int j = 0; j < groupCount; j++)
        {
            int idx = (int)((long)j * numTiles / groupCount);
            centers[j] = features[idx] == null ? new int[dims] : (int[])features[idx].Clone();
            mass[j] = Mass(centers[j]);
        }

        for (int round = 0; round < 2; round++)
        {
            for (int t = 0; t < numTiles; t++)
            {
                int[] f = features[t];
                double bestScore = double.MinValue;
                int bestGroup = 0;
                for (int j = 0; j < groupCount; j++)
                {
                    if (mass[j] == 0) { if (bestScore < 0) { bestScore = 0; bestGroup = j; } continue; }
                    double score = f == null ? 0 : Dot(f, centers[j]) / (double)mass[j];
                    if (score > bestScore || (score == bestScore && j < bestGroup))
                    {
                        bestScore = score;
                        bestGroup = j;
                    }
                }
                assign[t] = bestGroup;
            }

            for (int j = 0; j < groupCount; j++)
            {
                mass[j] = 0;
                if (centers[j] != null) System.Array.Clear(centers[j]);
            }
            for (int t = 0; t < numTiles; t++)
            {
                if (features[t] == null) continue;
                int g = assign[t];
                if (centers[g] == null) centers[g] = new int[dims];
                for (int d = 0; d < dims; d++) centers[g][d] += features[t][d];
                mass[g] += Mass(features[t]);
            }
        }
        return assign;
    }

    private static long Mass(int[] feature)
    {
        long sum = 0;
        for (int i = 0; i < feature.Length; i++) sum += feature[i];
        return sum;
    }

    private static long Dot(int[] a, int[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += (long)a[i] * b[i];
        return sum;
    }

    /// <summary>把组 id 压缩成 0..K-1（按首次出现顺序），避免空洞 id 影响位流表数量。</summary>
    private static void CompactGroupIds(int[] assign, int numTiles, out int groupCount)
    {
        var remap = new int[MaxHuffmanGroups + 1];
        System.Array.Fill(remap, -1);
        int next = 0;
        for (int t = 0; t < numTiles; t++)
        {
            int old = assign[t];
            if ((uint)old >= (uint)remap.Length)
                throw new WebpBitstreamException($"meta-Huffman 组 id {old} 超出内部上限 {remap.Length - 1}");
            if (remap[old] < 0) remap[old] = next++;
            assign[t] = remap[old];
        }
        groupCount = next;
    }

    // ---- meta-Huffman 组数选择 ----
    /// <summary>
    /// 精确代价：直接按频次构造码表，用「Σ freq·码长 + 实际码表位数」比较候选分组。
    /// 旧的 <c>EntropyBits + TableBits</c> 只是近似（熵没有码长上限、码表开销用线性估计），
    /// 在组数较多、各组符号集差异大时会系统性高估/低估，选错组数。
    /// </summary>
    private static double ExactCost(Histograms h, int numGroups, int greenAlphabet)
    {
        double cost = 0;
        for (int g = 0; g < numGroups; g++)
        {
            cost += ChannelBits(h.Green[g], greenAlphabet);
            cost += ChannelBits(h.Red[g], NumLiteralCodes);
            cost += ChannelBits(h.Blue[g], NumLiteralCodes);
            cost += ChannelBits(h.Alpha[g], NumLiteralCodes);
            cost += ChannelBits(h.Distance[g], NumDistanceCodes);
        }
        return cost;
    }

    /// <summary>单个通道的精确代价：token 位数（Σ freq·码长）+ 码表自身写出的位数。</summary>
    private static double ChannelBits(int[] frequency, int alphabet)
    {
        var code = Vp8LHuffmanCode.Build(frequency, alphabet);
        long tokenBits = 0;
        // 单符号通道按规范写 simple code 且码长 1，但写出端（WriteSymbol）会完全跳过，
        // 实际 token 位数是 0。若仍按 1 bit/符号累加，恒定 alpha 的图上会把每个 token
        // 多算 1 bit（照片类约 70 万 bit），组数/代价比较会因此失真。
        if (code.SingleSymbol < 0)
        {
            for (int i = 0; i < alphabet; i++)
            {
                if (frequency[i] > 0) tokenBits += (long)frequency[i] * code.Lengths[i];
            }
        }
        var writer = new LsbBitWriter();
        code.WriteTo(writer);
        return tokenBits + writer.TotalBits;
    }

    private static double EntropyBits(int[] histogram) => EntropyBits(histogram, 0, histogram.Length);

    /// <summary>
    /// 整数 log2 查表上限（含）。tile 级直方图的计数不超过 tile 像素数，
    /// 而 predictor 模式选择器会对每个 tile × 每个模式重复求熵，
    /// 直接用 <see cref="Math.Log2(double)"/> 会把这一步变成绝对热点。
    /// </summary>
    private const int LogTableSize = 4097;

    private static readonly double[] LogTable = BuildLogTable();

    private static double[] BuildLogTable()
    {
        var table = new double[LogTableSize];
        for (int i = 0; i < table.Length; i++) table[i] = Math.Log2(i);
        return table;
    }

    /// <summary>log2（整数入参）；表内取值与 <see cref="Math.Log2(double)"/> 完全一致。</summary>
    private static double Log2(int value) => value < LogTableSize ? LogTable[value] : Math.Log2(value);

    private static double EntropyBits(int[] histogram, int offset, int length)
    {
        long total = 0;
        for (int i = 0; i < length; i++) total += histogram[offset + i];
        if (total == 0) return 0;
        // Σ c·log2(T/c) = T·log2(T) - Σ c·log2(c)，两者都用整数 log2 表，
        // 表内取值与原先 (double)T/c 的写法在数值上等价。
        double totalBits = total < LogTableSize ? LogTable[(int)total] : Math.Log2(total);
        double bits = 0;
        for (int i = 0; i < length; i++)
        {
            int c = histogram[offset + i];
            if (c > 0) bits += c * (totalBits - Log2(c));
        }
        return bits;
    }

    private static double TableBits(int[] histogram)
        => TableBits(histogram, 0, histogram.Length);

    private static double TableBits(int[] histogram, int offset, int length)
    {
        int active = 0;
        for (int i = 0; i < length; i++) if (histogram[offset + i] > 0) active++;
        return active <= 1 ? 6 : 10 + 2.0 * active;
    }

    /// <summary>
    /// entropy image 自身的真实位流开销：把候选分组按最终写法完整编码一遍子图像，
    /// 取实际写出位数。
    /// 旧实现用 <c>24 + numTiles·log2(numGroups)</c> 线性估计，忽略了子图像自身的
    /// 帧头、变换列表结束位与 5 张 Huffman 表，系统性低估开销，导致组数选择偏向
    /// 过多分组 —— 小图/强渐变图上元 Huffman 反而比单组更大。
    /// </summary>
    private static double MetaImageBits(int tilesX, int tilesY, int[] assign, int numGroups)
    {
        if (numGroups <= 1) return 0;
        var pixels = new uint[tilesX * tilesY];
        for (int t = 0; t < pixels.Length; t++) pixels[t] = (uint)(assign[t] << 8);
        var writer = new LsbBitWriter();
        EncodeSubImage(writer, pixels, tilesX, tilesY);
        return writer.TotalBits;
    }

    private static void BuildCodes(TokenStatistics stats, int greenAlphabet)
    {
        int groups = stats.NumGroups;
        stats.GreenCodes = new Vp8LHuffmanCode[groups];
        stats.RedCodes = new Vp8LHuffmanCode[groups];
        stats.BlueCodes = new Vp8LHuffmanCode[groups];
        stats.AlphaCodes = new Vp8LHuffmanCode[groups];
        stats.DistanceCodes = new Vp8LHuffmanCode[groups];
        for (int g = 0; g < groups; g++)
        {
            stats.GreenCodes[g] = Vp8LHuffmanCode.Build(stats.GreenFreq[g], greenAlphabet);
            stats.RedCodes[g] = Vp8LHuffmanCode.Build(stats.RedFreq[g], NumLiteralCodes);
            stats.BlueCodes[g] = Vp8LHuffmanCode.Build(stats.BlueFreq[g], NumLiteralCodes);
            stats.AlphaCodes[g] = Vp8LHuffmanCode.Build(stats.AlphaFreq[g], NumLiteralCodes);
            stats.DistanceCodes[g] = Vp8LHuffmanCode.Build(stats.DistanceFreq[g], NumDistanceCodes);
        }
    }

    private static int ChooseHuffmanPrecision(int width, int height)
    {
        int bits = 4;
        while (bits < 9 &&
               SubSampleSize(width, bits) * SubSampleSize(height, bits) > MaxHuffmanTiles)
        {
            bits++;
        }
        return bits;
    }

    // ==================================================================
    //  码表代价模型（用于 LZ77 / cache 取舍）
    // ==================================================================

    /// <summary>
    /// 近似码表代价：由整幅图各通道的原始直方图估出每个符号的位数。
    /// 只用于 token 决策（literal vs copy vs cache），最终码表仍由实际 token 频次构造，
    /// 因此估计偏差只影响压缩率，绝不影响合法性。
    /// </summary>
    private sealed class CostModel
    {
        private readonly int[] _green;
        private readonly int[] _red;
        private readonly int[] _blue;
        private readonly int[] _alpha;

        /// <summary>长度前缀符号的实际码长（首遍统计后才有；静态模型为 null）。</summary>
        private readonly int[]? _length;
        private readonly int[]? _distance;

        /// <summary>被覆盖像素代价的前缀和（长度 total+1），仅在启用 LZ77 时构造。</summary>
        private readonly int[]? _coveredPrefix;

        /// <summary>color cache 引用的近似代价（单个 green 符号）。</summary>
        public int CacheSymbolCost { get; }

        /// <summary>长度前缀符号的近似代价。</summary>
        public const int LengthSymbolCost = 6;

        /// <summary>距离前缀符号的近似代价。</summary>
        public const int DistanceSymbolCost = 6;

        /// <summary>一次 copy token 的最低开销（长度符号 + 距离符号，extra 位另计）。</summary>
        public const int MinCopyCost = LengthSymbolCost + DistanceSymbolCost;

        private CostModel(int[] green, int[] red, int[] blue, int[] alpha, int cacheSymbolCost,
            uint[]? prefixSource, int[]? distance = null, int[]? length = null)
        {
            _green = green;
            _red = red;
            _blue = blue;
            _alpha = alpha;
            _length = length;
            _distance = distance;
            CacheSymbolCost = cacheSymbolCost;

            if (prefixSource == null)
            {
                _coveredPrefix = null;
                return;
            }

            // 前缀和把"一段区间被 copy 覆盖时原本要花多少位"变成 O(1) 查询。
            // LZ77 搜索每考察一个候选都要评估收益，逐像素累加会把搜索成本放大到
            // 候选数 × 匹配长度 × 4 次查表，在纯色/强渐变图上退化成 O(n²)。
            var prefix = new int[prefixSource.Length + 1];
            int accumulated = 0;
            for (int i = 0; i < prefixSource.Length; i++)
            {
                accumulated += CoveredPixelCost(prefixSource[i]);
                prefix[i + 1] = accumulated;
            }
            _coveredPrefix = prefix;
        }

        public static CostModel Build(uint[] pixels, int cacheBits, int greenAlphabet,
            bool buildCoveredPrefix)
        {
            LiteralCosts(pixels, out int[] green, out int[] red, out int[] blue, out int[] alpha);

            int cacheSymbolCost = cacheBits > 0
                ? (int)Math.Ceiling(Math.Log2(greenAlphabet))
                : int.MaxValue / 2;
            return new CostModel(green, red, blue, alpha,
                cacheSymbolCost, buildCoveredPrefix ? pixels : null);
        }

        /// <summary>按像素直方图统计四个 literal 通道的代价（熵取整）。</summary>
        private static void LiteralCosts(uint[] pixels, out int[] green, out int[] red,
            out int[] blue, out int[] alpha)
        {
            var greenFreq = new int[NumLiteralCodes];
            var redFreq = new int[NumLiteralCodes];
            var blueFreq = new int[NumLiteralCodes];
            var alphaFreq = new int[NumLiteralCodes];
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];
                greenFreq[(p >> 8) & 0xFF]++;
                redFreq[(p >> 16) & 0xFF]++;
                blueFreq[p & 0xFF]++;
                alphaFreq[p >> 24]++;
            }
            green = BitsCost(greenFreq);
            red = BitsCost(redFreq);
            blue = BitsCost(blueFreq);
            alpha = BitsCost(alphaFreq);
        }

        /// <summary>
        /// 混合代价模型：literal 侧仍用<b>像素直方图</b>的熵估计，长度/距离/color-cache
        /// 符号则改用首遍 token 统计得到的<b>真实 Huffman 码长</b>。
        ///
        /// 为什么 literal 不能直接用 token 码长：LZ77 在评估一个候选 copy 时，需要的是
        /// 「这些像素若不 copy、各自要花多少位」的反事实代价。被 copy 覆盖的像素本来
        /// 就很少以 literal 形式出现，它们在 token 直方图里的码长反而偏长；直接用它会让
        /// 覆盖收益被系统性高估，LZ77 逐渐偏向又长又远的 copy（实测 photo 上额外位
        /// 从 33 万 bit 涨到 57 万 bit，净体积反而变大）。
        /// 而静态模型把长度/距离符号一律按 6 bit 估，对常见短距离明显过贵、对罕见
        /// 长距离明显便宜，正是 LZ77 选择偏保守的根源。两者各取所长即可。
        /// </summary>
        public static CostModel FromStatistics(TokenStatistics stats, uint[] pixels, int cacheBits,
            int greenAlphabet, bool buildCoveredPrefix)
        {
            LiteralCosts(pixels, out int[] greenCost, out int[] redCost, out int[] blueCost,
                out int[] alphaCost);

            int[] greenFreq = MergeFrequency(stats.GreenFreq, greenAlphabet);
            var greenCode = Vp8LHuffmanCode.Build(greenFreq, greenAlphabet);

            var lengthCost = new int[NumLengthCodes];
            for (int i = 0; i < NumLengthCodes; i++)
            {
                int symbol = NumLiteralCodes + i;
                lengthCost[i] = symbol < greenCode.Lengths.Length && greenFreq[symbol] == 0
                    ? 12
                    : Math.Max(1, greenCode.Lengths[symbol]);
            }

            int[] distanceCost = CodeLengthCosts(MergeFrequency(stats.DistanceFreq, NumDistanceCodes),
                NumDistanceCodes);

            int cacheSymbolCost = int.MaxValue / 2;
            if (cacheBits > 0)
            {
                int cacheStart = NumLiteralCodes + NumLengthCodes;
                int cacheEnd = Math.Min(greenAlphabet, cacheStart + (1 << cacheBits));
                long weighted = 0;
                long count = 0;
                for (int i = cacheStart; i < cacheEnd; i++)
                {
                    if (greenFreq[i] > 0)
                    {
                        weighted += (long)greenFreq[i] * Math.Max(1, greenCode.Lengths[i]);
                        count += greenFreq[i];
                    }
                }
                cacheSymbolCost = count > 0
                    ? (int)((weighted + count - 1) / count)
                    : (int)Math.Ceiling(Math.Log2(greenAlphabet));
            }

            return new CostModel(greenCost, redCost, blueCost, alphaCost, cacheSymbolCost,
                buildCoveredPrefix ? pixels : null, distanceCost, lengthCost);
        }

        private static int[] MergeFrequency(int[][] perGroup, int alphabet)
        {
            var merged = new int[alphabet];
            for (int g = 0; g < perGroup.Length; g++)
            {
                var group = perGroup[g];
                int n = Math.Min(alphabet, group.Length);
                for (int i = 0; i < n; i++) merged[i] += group[i];
            }
            return merged;
        }

        private static int[] CodeLengthCosts(int[] frequency, int alphabet)
        {
            var code = Vp8LHuffmanCode.Build(frequency, alphabet);
            var costs = new int[alphabet];
            for (int i = 0; i < alphabet; i++)
            {
                costs[i] = frequency[i] == 0 ? 12 : Math.Max(1, code.Lengths[i]);
            }
            return costs;
        }

        /// <summary>长度前缀符号的代价：精化模型用实际码长，静态模型退回 6 bit 估计。</summary>
        public int LengthCost(int prefix)
        {
            if (_length != null && (uint)prefix < (uint)_length.Length)
                return Math.Max(1, _length[prefix]);
            return LengthSymbolCost;
        }

        /// <summary>距离前缀符号的实际码长。</summary>
        public int DistanceCost(int symbol)
            => _distance != null && symbol >= 0 && symbol < _distance.Length
                ? Math.Max(1, _distance[symbol])
                : DistanceSymbolCost;

        private static int[] BitsCost(int[] histogram)
        {
            long total = 0;
            for (int i = 0; i < histogram.Length; i++) total += histogram[i];
            var costs = new int[histogram.Length];
            for (int i = 0; i < histogram.Length; i++)
            {
                int c = histogram[i];
                if (c == 0) { costs[i] = 12; continue; }
                // 码长最短也是 1 bit（两个符号必然各占 1 位），按对数取整会给出 0，
                // 会让"被覆盖像素"看起来免费，从而系统性低估 copy 的收益。
                double bits = Math.Log2((double)total / c);
                costs[i] = Math.Max(1, (int)Math.Round(bits));
            }
            return costs;
        }

        /// <summary>一个 literal 像素（绿/红/蓝/alpha 四个符号）的总代价。</summary>
        public int LiteralCost(uint pixel)
        {
            return _green[(pixel >> 8) & 0xFF] + _red[(pixel >> 16) & 0xFF] +
                   _blue[pixel & 0xFF] + _alpha[pixel >> 24];
        }

        /// <summary>
        /// 被 LZ77 覆盖时该像素"原本要花"的代价：literal 与 cache 引用取廉价者。
        /// 用这个而不是纯 literal 代价，避免在 cache 命中率高的图上高估 copy 收益。
        /// </summary>
        public int CoveredPixelCost(uint pixel)
        {
            int literal = LiteralCost(pixel);
            return CacheSymbolCost < literal ? CacheSymbolCost : literal;
        }

        /// <summary>
        /// 区间 [start, start+length) 全部由 copy 覆盖时，原本要花的位数（前缀和 O(1)）。
        /// 仅当启用 LZ77（<see cref="Build"/> 传入 buildCoveredPrefix）时可用。
        /// </summary>
        public int CoveredCost(int start, int length)
        {
            var prefix = _coveredPrefix ?? throw new InvalidOperationException(
                "代价模型未构造前缀和，调用方必须先启用 LZ77");
            return prefix[start + length] - prefix[start];
        }
    }

    // ==================================================================
    //  Token 生成（literal / color cache / LZ77）
    // ==================================================================

    private enum TokenKind : byte
    {
        Literal = 0,
        ColorCache = 1,
        Copy = 2,
    }

    private readonly struct Token
    {
        public Token(TokenKind kind, int greenSymbol, byte red, byte blue, byte alpha,
            int lengthExtraValue, int lengthExtraBits, int distanceSymbol, int distanceExtraValue, int distanceExtraBits)
        {
            Kind = kind;
            GreenSymbol = (ushort)greenSymbol;
            Red = red;
            Blue = blue;
            Alpha = alpha;
            LengthExtraValue = (ushort)lengthExtraValue;
            LengthExtraBits = (byte)lengthExtraBits;
            DistanceSymbol = (byte)distanceSymbol;
            DistanceExtraValue = distanceExtraValue;
            DistanceExtraBits = (byte)distanceExtraBits;
        }

        // 紧凑字段布局（16B）：大图 token 缓存是编码耗时的主要来源之一，
        // 每像素一个 token 时缓存体积与写出遍的内存带宽都直接受字段宽度影响。
        public readonly TokenKind Kind;
        public readonly ushort GreenSymbol;
        public readonly byte Red;
        public readonly byte Blue;
        public readonly byte Alpha;
        public readonly byte LengthExtraBits;
        public readonly byte DistanceSymbol;
        public readonly byte DistanceExtraBits;
        public readonly ushort LengthExtraValue;
        public readonly int DistanceExtraValue;
    }

    /// <summary>
    /// 确定性的 token 遍历器：同一输入、同一代价模型两次遍历产生完全一致的 token 序列
    /// （编码器先用第一遍统计频次，再用第二遍写位流）。
    /// LZ77 用 hash 链保存同 hash 的历史位置，每次考察的候选数按图像尺寸自适应
    /// （小图保留更多候选以稳住压缩率，大图减少无效深搜以降低耗时），
    /// 按"可被覆盖像素的码表代价 - copy 代价"选收益最大的一个。
    /// </summary>
    private sealed class TokenWalker
    {
        private const uint HashMultiplier2 = 0x9e3779b1u;
        private const int HashBits = 17;

        /// <summary>自适应哈希表的位宽下限（512 项，128×128 以下的小图不再继续缩小）。</summary>
        private const int MinHashBits = 9;
        private const int MinMatchLength = 3;

        private readonly uint[] _pixels;
        private readonly int _total;
        private readonly int _width;
        private readonly bool _useLz77;
        private readonly int _cacheBits;
        private readonly CostModel _cost;
        private readonly int[] _hashHead;
        private readonly int _hashShift;
        private readonly int[] _chain;
        private readonly int _chainMask;
        private readonly int _maxChainCandidates;
        private readonly uint[] _cache;
        private readonly int[] _distanceToPlaneCode;
        private int _pos;
        private int _lastDistance;

        public TokenWalker(uint[] pixels, int width, int height, bool useLz77, int cacheBits, CostModel cost,
            int maxChainOverride = 0)
        {
            _pixels = pixels;
            _total = width * height;
            _width = width;
            _useLz77 = useLz77;
            _cacheBits = cacheBits;
            _cost = cost;

            // 哈希表按像素数自适应：小图用更小的表（建表与清零代价下降），
            // 大图仍用 2^17。表长不小于像素数，保证平均链长不随尺寸变长。
            int hashBits = MinHashBits;
            while (hashBits < HashBits && (1 << hashBits) < _total) hashBits++;
            _hashShift = 32 - hashBits;
            _hashHead = useLz77 ? new int[1 << hashBits] : System.Array.Empty<int>();
            if (useLz77) System.Array.Fill(_hashHead, -1);

            int chainSize = 1;
            int wanted = Math.Min(_total, MaxDistance + 1);
            while (chainSize < wanted) chainSize <<= 1;
            _chain = useLz77 ? new int[chainSize] : System.Array.Empty<int>();
            _chainMask = chainSize - 1;

            // 链深度按像素数自适应。哈希只取两个像素，周期图案（渐变、alpha 渐变）上
            // 同 hash 的候选极多，只看最近 24 个时候选距离上限只有几十像素，
            // 会系统性漏掉 distance ≈ 2×行宽的长匹配；中小图候选比较便宜，
            // 加深到 96 可让平均匹配长度明显上升。大图逐一位置搜索代价随深度线性增长，
            // 保持 16 以守住耗时。体积变化由 WebpLEncoderTests 的体积回归锁定。
            _maxChainCandidates = useLz77
                ? (maxChainOverride > 0 ? maxChainOverride : ChooseChainDepth(_total))
                : 0;
            _distanceToPlaneCode = useLz77 ? BuildDistanceToPlaneCode(width) : System.Array.Empty<int>();
            _cache = cacheBits > 0 ? new uint[1 << cacheBits] : System.Array.Empty<uint>();
        }

        /// <summary>
        /// hash 链深度：中小图用深链换压缩率，大图保持浅链守耗时。
        /// 阈值按像素数划分，与候选配置无关，保证统计遍/写出遍一致。
        /// </summary>
        private static int ChooseChainDepth(int total)
            => total < 1 << 14 ? 32 : total < 1 << 16 ? 64 : total < 1 << 20 ? 96 : 16;

        public bool Next(out Token token, out int startPosition)
        {
            startPosition = _pos;
            if (_pos >= _total)
            {
                token = default;
                return false;
            }

            int pos = _pos;
            if (_useLz77 && TryFindCopy(pos, _maxChainCandidates, out int length, out int distance))
            {
                _lastDistance = distance;
                token = BuildCopyToken(length, distance, _distanceToPlaneCode);
                InsertRange(pos, length);
                _pos = pos + length;
                return true;
            }

            uint pixel = _pixels[_pos];
            bool useCache = false;
            if (_cacheBits > 0)
            {
                int key = CacheKey(pixel);
                if (_cache[key] == pixel)
                {
                    useCache = _cost.CacheSymbolCost < _cost.LiteralCost(pixel);
                }
                _cache[key] = pixel;
            }

            InsertPosition(_pos);
            _pos++;

            token = useCache ? BuildCacheToken(pixel) : BuildLiteralToken(pixel);
            return true;
        }

        private Token BuildCacheToken(uint pixel)
            => new Token(TokenKind.ColorCache, NumLiteralCodes + NumLengthCodes + CacheKey(pixel),
                0, 0, 0, 0, 0, 0, 0, 0);

        private static Token BuildLiteralToken(uint pixel)
            => new Token(TokenKind.Literal, (int)((pixel >> 8) & 0xFF),
                (byte)((pixel >> 16) & 0xFF), (byte)(pixel & 0xFF), (byte)((pixel >> 24) & 0xFF),
                0, 0, 0, 0, 0);


        private int CacheKey(uint pixel) => (int)((pixel * HashMultiplier) >> (32 - _cacheBits));

        private bool TryFindCopy(int pos, int chainLimit, out int bestLength, out int bestDistance)
        {
            bestLength = 0;
            bestDistance = 0;

            int maxLength = Math.Min(MaxCopyLength, _total - pos);
            if (maxLength < MinMatchLength || pos + 1 >= _total) return false;

            // 收益上界：把这个窗口整段覆盖也抵不过一次 copy 的最低开销时，放弃搜索。
            // 纯色、强渐变经 predictor 后残差几乎全零，这一句把原本逐位置全量比较的扫描压成 O(1)。
            if (_cost.CoveredCost(pos, maxLength) <= CostModel.MinCopyCost) return false;

            int bestGain = 0;

            // 结构候选：图像里最常见的重复周期是"整行"（row stride），逐行相同的图案靠
            // distance = width 一次覆盖整行；再加上上一次 copy 的距离（LZ 系编码器的
            // classic repeat-distance）。hash 链永远只给最近的若干候选，命中不了
            // 周期恰好等于行距的长匹配，这两个候选正是补这个洞。
            TryCandidate(pos, maxLength, _width, ref bestLength, ref bestDistance, ref bestGain);
            // 残差按对角线平移的图（如 45° 渐变）每行的重复周期是 width±1，而不是 width。
            if (_width > 1)
            {
                TryCandidate(pos, maxLength, _width - 1, ref bestLength, ref bestDistance, ref bestGain);
                TryCandidate(pos, maxLength, _width + 1, ref bestLength, ref bestDistance, ref bestGain);
            }
            if (_lastDistance > 0)
                TryCandidate(pos, maxLength, _lastDistance, ref bestLength, ref bestDistance, ref bestGain);
            if (_width * 2 <= MaxDistance)
                TryCandidate(pos, maxLength, _width * 2, ref bestLength, ref bestDistance, ref bestGain);
            if (_width * 2 + 1 <= MaxDistance)
                TryCandidate(pos, maxLength, _width * 2 + 1, ref bestLength, ref bestDistance, ref bestGain);

            int hash = Hash(_pixels[pos], _pixels[pos + 1]);
            int candidate = _hashHead[hash];
            int examined = 0;

            while (candidate >= 0 && examined < chainLimit)
            {
                if (bestLength >= maxLength) break;

                int distance = pos - candidate;
                if (distance <= 0 || distance > MaxDistance) break;

                int next = _chain[candidate & _chainMask];
                examined++;

                // 快速排除：候选在第 bestLength 个像素上已经不同，匹配长度必 ≤ bestLength，
                // 而 covered 代价随长度单调不减，收益不可能超过当前最优。
                if (bestLength >= MinMatchLength &&
                    _pixels[candidate + bestLength] != _pixels[pos + bestLength])
                {
                    candidate = next;
                    continue;
                }

                int length = 0;
                while (length < maxLength && _pixels[candidate + length] == _pixels[pos + length]) length++;

                if (length >= MinMatchLength)
                {
                    int gain = _cost.CoveredCost(pos, length) - CopyCost(length, distance);
                    // 取收益最大的候选（而不是"最便宜的 copy"）：长匹配即使多花几个 extra 位，
                    // 只要覆盖的码表代价更高就更划算。
                    if (gain > bestGain || (gain == bestGain && gain > 0 && length > bestLength))
                    {
                        bestGain = gain;
                        bestLength = length;
                        bestDistance = distance;
                    }
                }

                if (bestLength >= maxLength) break;
                candidate = next;
            }

            if (bestLength < MinMatchLength || bestGain <= 0) return false;
            return true;
        }

        private void TryCandidate(int pos, int maxLength, int distance,
            ref int bestLength, ref int bestDistance, ref int bestGain)
        {
            if (distance <= 0 || distance > MaxDistance) return;
            int start = pos - distance;
            if (start < 0) return;
            // 已有候选铺满整个窗口时无需再比较（同时保证下面按 bestLength 的索引不越界）。
            if (bestLength >= maxLength) return;
            if (bestLength >= MinMatchLength && _pixels[start + bestLength] != _pixels[pos + bestLength]) return;

            int length = 0;
            while (length < maxLength && _pixels[start + length] == _pixels[pos + length]) length++;
            if (length < MinMatchLength) return;

            int gain = _cost.CoveredCost(pos, length) - CopyCost(length, distance);
            if (gain > bestGain || (gain == bestGain && gain > 0 && length > bestLength))
            {
                bestGain = gain;
                bestLength = length;
                bestDistance = distance;
            }
        }

        private int CopyCost(int length, int distance)
        {
            var lengthCode = EncodeCopyValue(length);
            var distanceCode = EncodeDistance(distance, _distanceToPlaneCode);
            return _cost.LengthCost(lengthCode.Prefix) + lengthCode.ExtraBits +
                   _cost.DistanceCost(distanceCode.Prefix) + distanceCode.ExtraBits;
        }

        private void InsertPosition(int pos)
        {
            if (!_useLz77 || pos + 1 >= _total) return;
            int hash = Hash(_pixels[pos], _pixels[pos + 1]);
            _chain[pos & _chainMask] = _hashHead[hash];
            _hashHead[hash] = pos;
        }

        /// <summary>LZ77 覆盖的每个像素都要进入 hash 链与 color cache，与解码端产出顺序一致。</summary>
        private void InsertRange(int start, int length)
        {
            for (int i = 0; i < length; i++)
            {
                int pos = start + i;
                if (pos + 1 < _total)
                {
                    int hash = Hash(_pixels[pos], _pixels[pos + 1]);
                    _chain[pos & _chainMask] = _hashHead[hash];
                    _hashHead[hash] = pos;
                }
                if (_cacheBits > 0) _cache[CacheKey(_pixels[pos])] = _pixels[pos];
            }
        }

        private int Hash(uint p0, uint p1)
            => (int)(((p0 * HashMultiplier) ^ (p1 * HashMultiplier2)) >> _hashShift);
    }

    private static Token BuildCopyToken(int length, int distance, int[] distanceToPlaneCode)
    {
        var lengthCode = EncodeCopyValue(length);
        var distanceCode = EncodeDistance(distance, distanceToPlaneCode);
        return new Token(TokenKind.Copy, NumLiteralCodes + lengthCode.Prefix, 0, 0, 0,
            lengthCode.ExtraValue, lengthCode.ExtraBits,
            distanceCode.Prefix, distanceCode.ExtraValue, distanceCode.ExtraBits);
    }

    private readonly struct CopyCode
    {
        public CopyCode(int prefix, int extraValue, int extraBits)
        {
            Prefix = prefix;
            ExtraValue = extraValue;
            ExtraBits = extraBits;
        }

        public int Prefix { get; }
        public int ExtraValue { get; }
        public int ExtraBits { get; }
    }

    /// <summary>
    /// 距离 → (planeCode → prefix/extra) 的编码。
    /// VP8L 的距离先从 "plane code" 经 2D 映射还原（<see cref="Vp8LDistance.PlaneCodeToDistance"/>），
    /// plane code 1..120 覆盖了大量"短距离/近邻"组合，代价远低于线性形式 dist+120；
    /// 因此优先查表，只有映射表中没有的距离才退回 dist+120。
    /// </summary>
    private static CopyCode EncodeDistance(int distance, int[] distanceToPlaneCode)
    {
        int planeCode = distance + 120;
        if (distance > 0 && distance < distanceToPlaneCode.Length)
        {
            int mapped = distanceToPlaneCode[distance];
            if (mapped > 0) planeCode = mapped;
        }
        return EncodeCopyValue(planeCode);
    }

    private static int[] BuildDistanceToPlaneCode(int width)
    {
        int size = Math.Min(7 * width + 9, MaxDistance + 1);
        var map = new int[size];
        for (int planeCode = 1; planeCode <= 120; planeCode++)
        {
            int distance = Vp8LDistance.PlaneCodeToDistance(width, planeCode);
            if (distance >= 1 && distance < map.Length && map[distance] == 0)
                map[distance] = planeCode;
        }
        return map;
    }

    /// <summary><see cref="EncodeCopyValue"/> 的反解（仅诊断统计用）。</summary>
    private static int DecodeCopyValue(int prefix, int extraValue)
    {
        if (prefix < 4) return prefix + 1;
        int extraBits = (prefix - 2) >> 1;
        int min = ((2 + (prefix & 1)) << extraBits) + 1;
        return min + extraValue;
    }

    /// <summary><see cref="Vp8LDistance.GetCopyValue"/> 的逆映射：把数值拆成 prefix 与附加位。</summary>
    private static CopyCode EncodeCopyValue(int value)
    {
        for (int prefix = 0; prefix < NumDistanceCodes; prefix++)
        {
            if (prefix < 4)
            {
                if (value == prefix + 1) return new CopyCode(prefix, 0, 0);
                continue;
            }

            int extraBits = (prefix - 2) >> 1;
            int min = ((2 + (prefix & 1)) << extraBits) + 1;
            int max = min + (1 << extraBits) - 1;
            if (value >= min && value <= max) return new CopyCode(prefix, value - min, extraBits);
        }
        throw new WebpBitstreamException($"无法编码的 copy 值 {value}");
    }

    // ==================================================================
    //  正向变换
    // ==================================================================

    /// <summary>Predictor 正向：residual = (pixel - predictor) 逐通道 mod 256（逆变换为逐通道加法）。</summary>
    private static uint ModSubPixels(uint pixel, uint predictor)
    {
        byte b = (byte)((pixel & 0xFFu) - (predictor & 0xFFu));
        byte g = (byte)(((pixel >> 8) & 0xFFu) - ((predictor >> 8) & 0xFFu));
        byte r = (byte)(((pixel >> 16) & 0xFFu) - ((predictor >> 16) & 0xFFu));
        byte a = (byte)(((pixel >> 24) & 0xFFu) - ((predictor >> 24) & 0xFFu));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>Subtract Green 正向：R 与 B 各 -= G（mod 256）。</summary>
    private static void ApplySubtractGreenForward(uint[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint argb = pixels[i];
            uint green = (argb >> 8) & 0xFFu;
            uint red = ((argb >> 16) & 0xFFu);
            uint blue = argb & 0xFFu;
            red = (red - green) & 0xFFu;
            blue = (blue - green) & 0xFFu;
            pixels[i] = (argb & 0xFF00FF00u) | (red << 16) | blue;
        }
    }

    /// <summary>
    /// Color indexing 正向：颜色数 ≤ 256 时把每个像素换成调色板索引，
    /// 索引按 LSB-first 打包进每个 sub-sample 像素的绿字节；调色板自身按字节差分后作为
    /// 1 行子图像编码。索引位数由颜色数决定（1/2/4/8 bit per pixel）。
    /// </summary>
    private static bool TryBuildPalette(uint[] pixels, int width, int height,
        out uint[] paletteDeltas, out int numColors, out uint[] packed, out int packedWidth)
    {
        paletteDeltas = System.Array.Empty<uint>();
        packed = System.Array.Empty<uint>();
        packedWidth = width;
        numColors = 0;

        var map = new Dictionary<uint, int>();
        var order = new List<uint>();
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            if (map.ContainsKey(p)) continue;
            if (order.Count == 256) return false;
            map[p] = order.Count;
            order.Add(p);
        }

        numColors = order.Count;
        int bits = numColors <= 2 ? 3 : numColors <= 4 ? 2 : numColors <= 16 ? 1 : 0;
        int bitsPerPixel = 8 >> bits;
        int unitsPerRow = 1 << bits;
        packedWidth = SubSampleSize(width, bits);

        packed = new uint[packedWidth * height];
        uint indexMask = (1u << bitsPerPixel) - 1u;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width;
            int dstRow = y * packedWidth;
            for (int x = 0; x < width; x += unitsPerRow)
            {
                uint unit = 0;
                for (int k = 0; k < unitsPerRow; k++)
                {
                    int sx = x + k;
                    if (sx >= width) break;
                    int index = map[pixels[srcRow + sx]];
                    unit |= ((uint)index & indexMask) << (k * bitsPerPixel);
                }
                packed[dstRow + (x / unitsPerRow)] = (unit & 0xFFu) << 8;
            }
        }

        paletteDeltas = new uint[numColors];
        paletteDeltas[0] = order[0];
        for (int i = 1; i < numColors; i++)
        {
            uint previous = order[i - 1];
            uint current = order[i];
            uint delta = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                byte value = (byte)(((current >> shift) & 0xFFu) - ((previous >> shift) & 0xFFu));
                delta |= (uint)value << shift;
            }
            paletteDeltas[i] = delta;
        }
        return true;
    }

    /// <summary>
    /// 由逐 tile 拟合结果派生 cross-color 候选：逐 tile 乘数，以及整幅图共用一张系数表。
    /// 后者取逐 tile 拟合的中位数，天然抗 mod-256 回绕造成的个别失真 tile。
    /// 所有候选都只做可逆的 mod-256 减法，选哪个只影响体积。
    /// </summary>
    private static CrossColorCandidate[] BuildCrossColorCandidates(uint[] source, int width, int height,
        int bits, uint[] perTile)
    {
        var candidates = new List<CrossColorCandidate>(2);

        bool perTileUsed = false;
        for (int i = 0; i < perTile.Length; i++)
        {
            if ((perTile[i] & 0x00FFFFFFu) != 0) { perTileUsed = true; break; }
        }
        if (perTileUsed)
        {
            var pixels = (uint[])source.Clone();
            ApplyCrossColorMultipliers(pixels, width, height, bits, perTile);
            candidates.Add(new CrossColorCandidate(pixels, perTile, bits));
        }

        var (g2r, g2b, r2b) = MedianMultipliers(perTile);
        if (g2r != 0 || g2b != 0 || r2b != 0)
        {
            // 单个 tile 最多覆盖 2^9 = 512 像素，更大的图像必须把同一份系数重复到整张
            // 系数图上：transform 头里的子图像尺寸是 SubSampleSize(w, bits) × SubSampleSize(h, bits)。
            int globalBits = GlobalTransformBits(width, height);
            uint code = (uint)(byte)g2r | ((uint)(byte)g2b << 8) | ((uint)(byte)r2b << 16);
            int subWidth = SubSampleSize(width, globalBits);
            int subHeight = SubSampleSize(height, globalBits);
            var data = new uint[subWidth * subHeight];
            System.Array.Fill(data, code);

            var pixels = (uint[])source.Clone();
            ApplyCrossColorMultipliers(pixels, width, height, globalBits, data);
            candidates.Add(new CrossColorCandidate(pixels, data, globalBits));
        }

        return candidates.ToArray();
    }

    /// <summary>
    /// 能把整幅图收进一个 tile 的最小 bits（上限 9，即变换头里 3 bit 字段能表示的最大值）。
    /// 与 libwebp 在渐变图上选用 bits=9 的做法一致：整幅共用一张系数表。
    /// </summary>
    private static int GlobalTransformBits(int width, int height)
    {
        int bits = 0;
        int maxSize = Math.Max(width, height);
        while ((1 << bits) < maxSize) bits++;
        return Math.Min(bits, 9);
    }

    private readonly record struct CrossColorCandidate(uint[] Pixels, uint[] Data, int Bits);

    /// <summary>
    /// Cross-color 正向：按 (2^bits) 的 tile 做最小二乘回归，得到三个 sbyte 乘数
    /// （green→red、green→blue、red→blue），并对每个像素减去预测的串色分量。
    /// 解码端恰好做相反的加法，因此回归系数只是压缩率选择，不影响可逆性。
    /// </summary>
    private static void FitCrossColorMultipliers(uint[] pixels, int width, int height, int bits,
        uint[] colorPixels)
    {
        int tileSize = 1 << bits;
        int subWidth = SubSampleSize(width, bits);
        int subHeight = SubSampleSize(height, bits);

        for (int ty = 0; ty < subHeight; ty++)
        {
            int y0 = ty * tileSize;
            int y1 = Math.Min(height, y0 + tileSize);
            for (int tx = 0; tx < subWidth; tx++)
            {
                int x0 = tx * tileSize;
                int x1 = Math.Min(width, x0 + tileSize);

                double count = 0, sumG = 0, sumG2 = 0, sumR = 0, sumB = 0;
                double sumGR = 0, sumGB = 0, sumR2 = 0, sumRB = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        uint p = pixels[row + x];
                        // 三个分量必须统一按 int8 取值再回归：cross-color 的整体运算都是
                        // mod 256 的有符号算术，用 0..255 的无符号值拟合会在"跨 0 回绕"处
                        // 得到锯齿状样本，回归斜率完全失真（渐变图残差会退化成噪声）。
                        double g = (sbyte)((p >> 8) & 0xFFu);
                        double r = (sbyte)((p >> 16) & 0xFFu);
                        double b = (sbyte)(p & 0xFFu);
                        count++;
                        sumG += g;
                        sumG2 += g * g;
                        sumR += r;
                        sumB += b;
                        sumGR += g * r;
                        sumGB += g * b;
                        sumR2 += r * r;
                        sumRB += r * b;
                    }
                }

                int g2r = SlopeToMultiplier(count, sumG, sumG2, sumR, sumGR);
                int g2b = SlopeToMultiplier(count, sumG, sumG2, sumB, sumGB);
                int r2b = SlopeToMultiplier(count, sumR, sumR2, sumB, sumRB);
                colorPixels[ty * subWidth + tx] =
                    (uint)(byte)g2r | ((uint)(byte)g2b << 8) | ((uint)(byte)r2b << 16);
            }
        }
    }

    /// <summary>
    /// 逐 tile 的拟合结果里，个别 tile 会因 mod-256 回绕得到完全失真的系数（例如斜率翻号）。
    /// 取三个分量的中位数作为"整幅图共用一张系数表"的候选：多数 tile 的拟合是准确的，
    /// 中位数天然抗少数离群点，比全图一次最小二乘更稳（后者会被整条回绕带整体带偏）。
    /// </summary>
    private static (int G2R, int G2B, int R2B) MedianMultipliers(uint[] perTile)
    {
        var g2r = new List<int>(perTile.Length);
        var g2b = new List<int>(perTile.Length);
        var r2b = new List<int>(perTile.Length);
        for (int i = 0; i < perTile.Length; i++)
        {
            uint code = perTile[i];
            g2r.Add((sbyte)(code & 0xFFu));
            g2b.Add((sbyte)((code >> 8) & 0xFFu));
            r2b.Add((sbyte)((code >> 16) & 0xFFu));
        }
        return (Median(g2r), Median(g2b), Median(r2b));
    }

    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>按已给定的逐 tile 乘数（或全图同一个乘数）做 cross-color 正向减法。</summary>
    private static void ApplyCrossColorMultipliers(uint[] pixels, int width, int height, int bits,
        ReadOnlySpan<uint> colorPixels)
    {
        int tileSize = 1 << bits;
        int subWidth = SubSampleSize(width, bits);
        int subHeight = SubSampleSize(height, bits);

        for (int ty = 0; ty < subHeight; ty++)
        {
            int y0 = ty * tileSize;
            int y1 = Math.Min(height, y0 + tileSize);
            for (int tx = 0; tx < subWidth; tx++)
            {
                uint code = colorPixels[ty * subWidth + tx];
                int g2r = (sbyte)(code & 0xFFu);
                int g2b = (sbyte)((code >> 8) & 0xFFu);
                int r2b = (sbyte)((code >> 16) & 0xFFu);
                int x0 = tx * tileSize;
                int x1 = Math.Min(width, x0 + tileSize);
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        uint argb = pixels[row + x];
                        int g = (sbyte)((argb >> 8) & 0xFFu);
                        int r = (int)((argb >> 16) & 0xFFu);
                        int b = (int)(argb & 0xFFu);
                        // 逆向顺序与解码端一致：先补 green→red，再用"已补回"的 r 做 red→blue。
                        // 因此正向做 red→blue 时必须用未被 g2r 修正的 r。
                        b = (b - ((g2b * g) >> 5)) & 0xFF;
                        b = (b - ((r2b * (sbyte)r) >> 5)) & 0xFF;
                        r = (r - ((g2r * g) >> 5)) & 0xFF;
                        pixels[row + x] = (argb & 0xFF00FF00u) | ((uint)r << 16) | (uint)b;
                    }
                }
            }
        }
    }

    /// <summary>线性回归斜率 → sbyte 乘数（乘数 = 32 * 斜率，限制在 -128..127）。</summary>
    private static int SlopeToMultiplier(double n, double sumX, double sumX2, double sumY, double sumXY)
    {
        double denominator = n * sumX2 - sumX * sumX;
        if (denominator <= 0.5) return 0;
        double slope = (n * sumXY - sumX * sumY) / denominator;
        double multiplier = Math.Round(slope * 32.0);
        if (multiplier > 127) return 127;
        if (multiplier < -128) return -128;
        return (int)multiplier;
    }

    /// <summary>predictor 选择结果：统一模式，或逐 tile 的一整套模式，外加估计代价（单位 bit）。</summary>
    private readonly record struct PredictorPlan(int UniformMode, int[]? TileModes, double Cost);

    /// <summary>
    /// 选择 predictor 方案。代价一律按"整幅图共用一套码表"的熵 + 表开销估算：
    /// 逐 tile 各自算熵会把每块局部最优当成全局收益，系统性低估真实体积，
    /// 在渐变/纯色这类强规律图上会选出明显更大的方案。
    ///
    /// 大图先用隔行隔列抽样给 14 个模式排序，只对前几名做全量统计：
    /// 表开销对所有模式相同，因此抽样的排序仍然有效；最终模式仍由全量熵决定。
    /// </summary>
    private static PredictorPlan ChoosePredictorPlan(uint[] pixels, int width, int height, int bits,
        bool uniformOnly, int forcedMode, bool useLz77, int cacheBits)
    {
        var histogram = new int[4 * NumLiteralCodes];
        int uniformMode = 1;
        double uniformCost = double.MaxValue;

        if (forcedMode >= 0)
        {
            System.Array.Clear(histogram);
            AccumulateResidualHistogram(pixels, width, 0, width, 0, height, forcedMode, histogram);
            return new PredictorPlan(forcedMode, null, ChannelsEntropyAndTables(histogram));
        }

        // 熵排名保留前几名，供后面用真实 token 代价复评。
        var ranked = new List<(double Cost, int Mode)>(4);
        void Rank(int mode, double cost)
        {
            if (ranked.Count < 4) ranked.Add((cost, mode));
            else if (cost < ranked[3].Cost) ranked[3] = (cost, mode);
            else return;
            ranked.Sort(static (a, b) => a.Cost.CompareTo(b.Cost));
        }

        // 1/2 抽样（≥8192 像素时）把 14 个模式的首轮比较降到 1/4 代价。
        int stride = width * height >= 1 << 13 ? 2 : 1;
        int refine = stride == 1 ? 14 : 4;
        if (stride == 1)
        {
            for (int mode = 0; mode < 14; mode++)
            {
                System.Array.Clear(histogram);
                AccumulateResidualHistogram(pixels, width, 0, width, 0, height, mode, histogram);
                double bitsCost = ChannelsEntropyAndTables(histogram);
                Rank(mode, bitsCost);
                if (bitsCost < uniformCost)
                {
                    uniformCost = bitsCost;
                    uniformMode = mode;
                }
            }
        }
        else
        {
            Span<double> sampleCost = stackalloc double[14];
            for (int mode = 0; mode < 14; mode++)
            {
                System.Array.Clear(histogram);
                AccumulateResidualHistogramSampled(pixels, width, 0, width, 0, height, stride, mode, histogram);
                sampleCost[mode] = ChannelsEntropyAndTables(histogram);
            }

            Span<bool> chosen = stackalloc bool[14];
            for (int round = 0; round < refine; round++)
            {
                int bestMode = -1;
                double bestSample = double.MaxValue;
                for (int mode = 0; mode < 14; mode++)
                {
                    if (chosen[mode] || sampleCost[mode] >= bestSample) continue;
                    bestSample = sampleCost[mode];
                    bestMode = mode;
                }
                if (bestMode < 0) break;
                chosen[bestMode] = true;

                System.Array.Clear(histogram);
                AccumulateResidualHistogram(pixels, width, 0, width, 0, height, bestMode, histogram);
                double bitsCost = ChannelsEntropyAndTables(histogram);
                Rank(bestMode, bitsCost);
                if (bitsCost < uniformCost)
                {
                    uniformCost = bitsCost;
                    uniformMode = bestMode;
                }
            }
        }

        bool repetitionStrong = false;
        double uniformTokenBits = double.MaxValue;
        var scratch = useLz77 ? new uint[pixels.Length] : System.Array.Empty<uint>();
        var modePixels = useLz77
            ? new uint[SubSampleSize(width, bits) * SubSampleSize(height, bits)]
            : System.Array.Empty<uint>();

        if (useLz77)
        {
            Span<int> rowCheck = stackalloc int[3];
            rowCheck[0] = uniformMode; rowCheck[1] = 1; rowCheck[2] = 2;
            for (int i = 0; i < 3; i++)
            {
                int mode = rowCheck[i];
                bool duplicate = false;
                for (int k = 0; k < i; k++) if (rowCheck[k] == mode) duplicate = true;
                if (duplicate) continue;

                System.Array.Copy(pixels, scratch, pixels.Length);
                ApplyPredictorPlan(scratch, width, height, bits, new PredictorPlan(mode, null, 0), modePixels);
                if (CountRowRepeats(scratch, width) * 8 >= scratch.Length) repetitionStrong = true;
            }

        // ---- LZ77 感知的复评（对所有图生效）----
        // 熵最优不等于字节最优：mode 0（不预测）在左右/中心对称的渐变上能让 LZ77
        // 直接复制成段像素，mode 1（left）在自然照片上通常最利于整行重复；这两者
        // 都可能因残差熵偏高而落选。复评按真实 token 走查取最小，不依赖行重复代理。
            int topK = pixels.Length >= 1 << 18 ? 2 : 3;
            var candidates = new List<int>(8) { uniformMode };
            for (int i = 0; i < ranked.Count && i < topK; i++)
            {
                if (!candidates.Contains(ranked[i].Mode)) candidates.Add(ranked[i].Mode);
            }
            if (!candidates.Contains(0)) candidates.Add(0);
            int structuralLimit = pixels.Length <= 1 << 20 ? 4 : 2;
            for (int mode = 1; mode <= structuralLimit; mode++)
            {
                if (!candidates.Contains(mode)) candidates.Add(mode);
            }

            int tokenMode = uniformMode;
            foreach (int mode in candidates)
            {
                System.Array.Copy(pixels, scratch, pixels.Length);
                ApplyPredictorPlan(scratch, width, height, bits, new PredictorPlan(mode, null, 0), modePixels);
                double tokenBits = EstimateTokenBitsSampled(scratch, width, height, cacheBits);
                if (tokenBits < uniformTokenBits)
                {
                    uniformTokenBits = tokenBits;
                    tokenMode = mode;
                }
            }
            uniformMode = tokenMode;
        }

        // 所选模式的熵代价（供 cross-color / 逐 tile 比较使用，统一在熵空间）。
        System.Array.Clear(histogram);
        AccumulateResidualHistogram(pixels, width, 0, width, 0, height, uniformMode, histogram);
        uniformCost = ChannelsEntropyAndTables(histogram);

        if (uniformOnly) return new PredictorPlan(uniformMode, null, uniformCost);

        int tilesX = SubSampleSize(width, bits);
        int tilesY = SubSampleSize(height, bits);
        var tileModes = ChooseTileModes(pixels, width, height, bits);

        System.Array.Clear(histogram);
        AccumulateResidualHistogramWithTileModes(pixels, width, height, tilesX, bits, tileModes, histogram);
        double tileCost = ChannelsEntropyAndTables(histogram) + EstimateModeImageCost(tileModes);

        // 逐 tile 残差还会打散整幅图的可重复性（LZ77 收益变差），因此只有明显更优才采用。
        // 行重复性强的图直接用同一套 token 代价比较，避免"熵更优但拆散整行复制"。
        bool useTiles;
        if (repetitionStrong)
        {
            var tileScratch = (uint[])scratch.Clone();
            var tileModePixels = new uint[tilesX * tilesY];
            ApplyPredictorPlan(tileScratch, width, height, bits,
                new PredictorPlan(uniformMode, tileModes, 0), tileModePixels);
            useTiles = EstimateTokenBits(tileScratch, width, height, cacheBits) < uniformTokenBits * 0.95;
        }
        else
        {
            useTiles = tileCost < uniformCost * 0.95;
        }

        return useTiles
            ? new PredictorPlan(uniformMode, tileModes, tileCost)
            : new PredictorPlan(uniformMode, null, uniformCost);
    }

    /// <summary>残差图像中与上一行同列像素相等的比例（整行可复制的程度，LZ77 收益代理）。</summary>
    private static int CountRowRepeats(uint[] pixels, int width)
    {
        int repeats = 0;
        for (int i = width; i < pixels.Length; i++)
        {
            if (pixels[i] == pixels[i - width]) repeats++;
        }
        return repeats;
    }

    /// <summary>
    /// 估计一套预测方案的 token 位数：用同一套 LZ77 / cache 决策产生 token，
    /// 再按实际 token 频次构造 Huffman 码表并累加码长与码表开销。
    /// 近似代价模型（熵估计）在 alpha 渐变等样本上会给出相反的排序，因此必须用真实码长。
    /// </summary>
    private static double EstimateTokenBits(uint[] pixels, int width, int height, int cacheBits)
    {
        int greenAlphabet = NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        var options = new Vp8LEncoderOptions(true, cacheBits > 0, true, useMetaHuffman: false);
        // 估算路径不缓存 token：频次与附加位总已在统计遍累加，直接按码长求和即可，
        // 避免为每个候选模式分配一份整图 token 数组（大图上会造成明显的 GC 抖动）。
        var stats = CollectStatistics(pixels, width, height, cacheBits, greenAlphabet, options,
            allowMetaHuffman: false, cacheTokens: false);

        // 码表开销同样计入：不同模式的符号使用分布不同，表大小也随之变化。
        double bits = SumCodeBits(stats.GreenFreq[0], stats.GreenCodes[0]) +
                      SumCodeBits(stats.RedFreq[0], stats.RedCodes[0]) +
                      SumCodeBits(stats.BlueFreq[0], stats.BlueCodes[0]) +
                      SumCodeBits(stats.AlphaFreq[0], stats.AlphaCodes[0]) +
                      SumCodeBits(stats.DistanceFreq[0], stats.DistanceCodes[0]) +
                      stats.ExtraBits +
                      TableBits(stats.GreenFreq[0]) + TableBits(stats.RedFreq[0]) +
                      TableBits(stats.BlueFreq[0]) + TableBits(stats.AlphaFreq[0]) +
                      TableBits(stats.DistanceFreq[0]);
        return bits;
    }

    private static double SumCodeBits(int[] freq, Vp8LHuffmanCode code)
    {
        double bits = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] != 0) bits += (double)freq[i] * code.Lengths[i];
        }
        return bits;
    }

    /// <summary>
    /// predictor 模式复评用的 token 位数估计：大图取中部一条不超过 64K 像素的横带，
    /// 避免为每个候选模式付一次整图 LZ77 走查；小图直接整幅估算。
    /// 横带保留了真实的行宽与邻域关系，足以区分"整行可复制"与"行结构被打散"。
    /// </summary>
    private static double EstimateTokenBitsSampled(uint[] pixels, int width, int height, int cacheBits)
    {
        if (pixels.Length <= 1 << 20) return EstimateTokenBits(pixels, width, height, cacheBits);
        int rows = Math.Clamp(65536 / Math.Max(1, width), 8, height);
        int start = (height - rows) / 2;
        var band = new uint[width * rows];
        System.Array.Copy(pixels, start * width, band, 0, band.Length);
        return EstimateTokenBits(band, width, rows, cacheBits);
    }

    /// <summary>
    /// color cache 位数选择用的 token 位数估计：图像超过横带预算时只估算中部横带
    /// （预算越小耗时越低、代表性越弱）。横带保留真实行宽与邻域，能反映 cache 命中率。
    /// </summary>
    private static double EstimateTokenBitsBand(uint[] pixels, int width, int height, int cacheBits,
        int bandPixels)
    {
        if (pixels.Length <= bandPixels || height < 2)
            return EstimateTokenBits(pixels, width, height, cacheBits);
        int rows = Math.Max(1, Math.Min(height, bandPixels / Math.Max(1, width)));
        int start = (height - rows) / 2;
        var band = new uint[width * rows];
        System.Array.Copy(pixels, start * width, band, 0, band.Length);
        return EstimateTokenBits(band, width, rows, cacheBits);
    }

    /// <summary>隔行隔列抽样统计残差直方图（仅用于给 predictor 模式排序）。</summary>
    private static void AccumulateResidualHistogramSampled(uint[] pixels, int width,
        int x0, int x1, int y0, int y1, int stride, int mode, int[] histogram)
    {
        int yStart = y0 == 0 ? 0 : ((y0 + stride - 1) / stride) * stride;
        int xStart = x0 == 0 ? 0 : ((x0 + stride - 1) / stride) * stride;
        for (int y = yStart; y < y1; y += stride)
        {
            int row = y * width;
            int topRow = row - width;
            for (int x = xStart; x < x1; x += stride)
            {
                uint predictor;
                if (y == 0)
                {
                    predictor = x == 0 ? 0xFF000000u : pixels[row + x - 1];
                }
                else if (x == 0)
                {
                    predictor = pixels[topRow + x];
                }
                else
                {
                    predictor = Vp8LTransforms.Predictor(mode,
                        pixels[row + x - 1], pixels[topRow + x], pixels[topRow + x - 1],
                        pixels[topRow + x + 1]);
                }
                uint residual = ModSubPixels(pixels[row + x], predictor);
                histogram[residual & 0xFF]++;
                histogram[NumLiteralCodes + ((residual >> 8) & 0xFF)]++;
                histogram[2 * NumLiteralCodes + ((residual >> 16) & 0xFF)]++;
                histogram[3 * NumLiteralCodes + (residual >> 24)]++;
            }
        }
    }

    /// <summary>
    /// Predictor 正向：按方案把残差写回 pixels。
    /// 自右下向左上运算，保证预测用的邻居仍是原始像素（解码端此时已经重建出同样的值）。
    /// </summary>
    private static void ApplyPredictorPlan(uint[] pixels, int width, int height, int bits,
        PredictorPlan plan, uint[] modePixels)
    {
        int tilesX = SubSampleSize(width, bits);
        if (plan.TileModes == null)
        {
            for (int i = 0; i < modePixels.Length; i++) modePixels[i] = (uint)plan.UniformMode << 8;
        }
        else
        {
            for (int i = 0; i < modePixels.Length && i < plan.TileModes.Length; i++)
                modePixels[i] = (uint)plan.TileModes[i] << 8;
        }

        for (int y = height - 1; y >= 0; y--)
        {
            int row = y * width;
            int topRow = row - width;
            int modeRow = (y >> bits) * tilesX;
            for (int x = width - 1; x >= 0; x--)
            {
                uint predictor;
                if (y == 0)
                {
                    predictor = x == 0 ? 0xFF000000u : pixels[row + x - 1];
                }
                else if (x == 0)
                {
                    predictor = pixels[topRow + x];
                }
                else
                {
                    int mode = plan.TileModes == null
                        ? plan.UniformMode
                        : plan.TileModes[modeRow + (x >> bits)];
                    predictor = Vp8LTransforms.Predictor(mode,
                        pixels[row + x - 1], pixels[topRow + x], pixels[topRow + x - 1],
                        pixels[topRow + x + 1]);
                }
                pixels[row + x] = ModSubPixels(pixels[row + x], predictor);
            }
        }
    }

    /// <summary>逐 tile 选模式：每个 tile 用 14 种 predictor 里残差熵最小的那个。</summary>
    private static int[] ChooseTileModes(uint[] pixels, int width, int height, int bits)
    {
        int tileSize = 1 << bits;
        int tilesX = SubSampleSize(width, bits);
        int tilesY = SubSampleSize(height, bits);
        var modes = new int[tilesX * tilesY];
        var tileHistogram = new TileHistogram();

        // 大图的 tile 内部也隔行隔列抽样：tile 仍是最终单位（每个 tile 各自选一个模式），
        // 只是候选比较时少看 3/4 像素。逐 tile 模式本身是启发式选择，抽样只影响
        // 近并列时的取舍；最终代价仍由全量残差直方图决定。
        int stride = width * height >= 1 << 16 && tileSize >= 16 ? 2 : 1;

        for (int ty = 0; ty < tilesY; ty++)
        {
            // 解码端：第一行 (y=0) 用固定模式，tile 行 0 从 y=1 开始；第一列同理从 x=1 开始。
            int y0 = Math.Max(1, ty * tileSize);
            int y1 = Math.Min(height, (ty + 1) * tileSize);
            if (y0 >= y1) continue;

            for (int tx = 0; tx < tilesX; tx++)
            {
                int x0 = Math.Max(1, tx * tileSize);
                int x1 = Math.Min(width, (tx + 1) * tileSize);
                if (x0 >= x1) continue;

                int bestMode = 0;
                double bestBits = double.MaxValue;
                for (int mode = 0; mode < 14; mode++)
                {
                    tileHistogram.Reset();
                    AccumulateTileHistogram(pixels, width, x0, x1, y0, y1, stride, mode, tileHistogram);
                    double entropy = tileHistogram.Cost();
                    if (entropy < bestBits)
                    {
                        bestBits = entropy;
                        bestMode = mode;
                    }
                }
                modes[ty * tilesX + tx] = bestMode;
            }
        }
        return modes;
    }

    /// <summary>
    /// tile 级 4 通道直方图 + "被写过的 bin"索引。tile 模式选择会对每个 tile × 14 个模式
    /// 比较代价，如果每次都清零并扫描全部 1024 个 bin，固定开销会盖过残差计算本身；
    /// 记录命中的 bin 后，清零与求熵都只与"不同取值个数"成正比。
    /// </summary>
    private sealed class TileHistogram
    {
        public readonly int[][] Counts = new int[4][];
        public readonly int[][] Touched = new int[4][];
        public readonly int[] TouchedCount = new int[4];

        public TileHistogram()
        {
            for (int c = 0; c < 4; c++)
            {
                Counts[c] = new int[NumLiteralCodes];
                Touched[c] = new int[NumLiteralCodes];
            }
        }

        public void Reset()
        {
            for (int c = 0; c < 4; c++)
            {
                int n = TouchedCount[c];
                if (n == 0) continue;
                var counts = Counts[c];
                var touched = Touched[c];
                for (int i = 0; i < n; i++) counts[touched[i]] = 0;
                TouchedCount[c] = 0;
            }
        }

        public void Add(uint residual)
        {
            AddComponent(0, (int)(residual & 0xFF));
            AddComponent(1, (int)((residual >> 8) & 0xFF));
            AddComponent(2, (int)((residual >> 16) & 0xFF));
            AddComponent(3, (int)(residual >> 24));
        }

        private void AddComponent(int channel, int value)
        {
            var counts = Counts[channel];
            if (counts[value] == 0) Touched[channel][TouchedCount[channel]++] = value;
            counts[value]++;
        }

        /// <summary>四通道熵 + 码长表开销，与 <see cref="ChannelsEntropyAndTables(int[])"/> 口径一致。</summary>
        public double Cost()
        {
            double bits = 0;
            for (int c = 0; c < 4; c++)
            {
                int n = TouchedCount[c];
                if (n == 0)
                {
                    bits += 6; // 空通道：熵 0，码长表仍按空表计
                    continue;
                }
                var counts = Counts[c];
                var touched = Touched[c];
                long total = 0;
                for (int i = 0; i < n; i++) total += counts[touched[i]];
                double totalBits = total < LogTableSize ? LogTable[(int)total] : Math.Log2(total);
                double entropy = 0;
                for (int i = 0; i < n; i++)
                {
                    int cc = counts[touched[i]];
                    entropy += cc * (totalBits - Log2(cc));
                }
                bits += entropy + (n <= 1 ? 6 : 10 + 2.0 * n);
            }
            return bits;
        }
    }

    /// <summary>按 tile 区域（可隔行隔列抽样）统计残差直方图。</summary>
    private static void AccumulateTileHistogram(uint[] pixels, int width, int x0, int x1, int y0, int y1,
        int stride, int mode, TileHistogram histogram)
    {
        int yStart = stride == 1 || y0 == 0 ? y0 : ((y0 + stride - 1) / stride) * stride;
        int xStart = stride == 1 || x0 == 0 ? x0 : ((x0 + stride - 1) / stride) * stride;
        for (int y = yStart; y < y1; y += stride)
        {
            int row = y * width;
            int topRow = row - width;
            for (int x = xStart; x < x1; x += stride)
            {
                uint predictor;
                if (y == 0)
                {
                    predictor = x == 0 ? 0xFF000000u : pixels[row + x - 1];
                }
                else if (x == 0)
                {
                    predictor = pixels[topRow + x];
                }
                else
                {
                    predictor = Vp8LTransforms.Predictor(mode,
                        pixels[row + x - 1], pixels[topRow + x], pixels[topRow + x - 1],
                        pixels[topRow + x + 1]);
                }
                histogram.Add(ModSubPixels(pixels[row + x], predictor));
            }
        }
    }

    /// <summary>按逐 tile 模式图统计整幅图的残差直方图（用于估算"共用一套码表"的真实代价）。</summary>
    private static void AccumulateResidualHistogramWithTileModes(uint[] pixels, int width, int height,
        int tilesX, int bits, int[] tileModes, int[] histogram)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int topRow = row - width;
            int modeRow = (y >> bits) * tilesX;
            for (int x = 0; x < width; x++)
            {
                uint predictor;
                if (y == 0)
                {
                    predictor = x == 0 ? 0xFF000000u : pixels[row + x - 1];
                }
                else if (x == 0)
                {
                    predictor = pixels[topRow + x];
                }
                else
                {
                    predictor = Vp8LTransforms.Predictor(tileModes[modeRow + (x >> bits)],
                        pixels[row + x - 1], pixels[topRow + x], pixels[topRow + x - 1],
                        pixels[topRow + x + 1]);
                }
                uint residual = ModSubPixels(pixels[row + x], predictor);
                histogram[residual & 0xFF]++;
                histogram[NumLiteralCodes + ((residual >> 8) & 0xFF)]++;
                histogram[2 * NumLiteralCodes + ((residual >> 16) & 0xFF)]++;
                histogram[3 * NumLiteralCodes + (residual >> 24)]++;
            }
        }
    }

    private static void AccumulateResidualHistogram(uint[] pixels, int width,
        int x0, int x1, int y0, int y1, int mode, int[] histogram)
    {
        for (int y = y0; y < y1; y++)
        {
            int row = y * width;
            int topRow = row - width;
            for (int x = x0; x < x1; x++)
            {
                uint predictor;
                if (y == 0)
                {
                    predictor = x == 0 ? 0xFF000000u : pixels[row + x - 1];
                }
                else if (x == 0)
                {
                    predictor = pixels[topRow + x];
                }
                else
                {
                    predictor = Vp8LTransforms.Predictor(mode,
                        pixels[row + x - 1], pixels[topRow + x], pixels[topRow + x - 1],
                        pixels[topRow + x + 1]);
                }
                uint residual = ModSubPixels(pixels[row + x], predictor);
                histogram[residual & 0xFF]++;
                histogram[NumLiteralCodes + ((residual >> 8) & 0xFF)]++;
                histogram[2 * NumLiteralCodes + ((residual >> 16) & 0xFF)]++;
                histogram[3 * NumLiteralCodes + (residual >> 24)]++;
            }
        }
    }

    /// <summary>
    /// 四个通道的熵 + 各自码长表开销。predictor 选择必须按"熵 + 表"估算：
    /// 逐 tile 模式要为每个 tile 各付一套表，只比熵会系统性低估它的代价，
    /// 在强规律图（渐变/纯色）上选出明显更差的逐 tile 方案。
    /// </summary>
    private static double ChannelsEntropyAndTables(int[] histogram)
    {
        double bits = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            int offset = channel * NumLiteralCodes;
            bits += EntropyBits(histogram, offset, NumLiteralCodes);
            bits += TableBits(histogram, offset, NumLiteralCodes);
        }
        return bits;
    }

    /// <summary>tile 模式图像自身的编码开销估计（模式种类越杂，这张子图像越大）。</summary>
    private static double EstimateModeImageCost(int[] modes)
    {
        var histogram = new int[256];
        for (int i = 0; i < modes.Length; i++) histogram[modes[i]]++;
        return EntropyBits(histogram) + TableBits(histogram);
    }
}
