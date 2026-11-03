using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 关键帧编码器（WebP 有损格式）。
///
/// 本阶段（baseline）实现：
/// <list type="bullet">
/// <item>16×16 帧内预测（DC / TM / V / H），按预测 SSE 选择模式；</item>
/// <item>4×4 正向 DCT + Y2 正向 WHT，标量与 <see cref="Vp8Idct"/> 严格互逆；</item>
/// <item>固定 q index（由外部 quality 单调映射）；token 概率按本帧统计量做一次更新（两遍编码）；</item>
/// <item>intra 模式下全平面的残差 token 编码（EOB / 零游程 / 类别码）；</item>
/// <item>使用解码器完全相同的逆变换变体做重建，保证后续宏块预测与解码端一致；</item>
/// <item>按 q index 估计环内滤波强度并写入帧头，重建输出行按解码端完全相同的顺序滤波
///   （仅宏块边界，16×16 模式不启用宏块内部边界）。</item>
/// </list>
///
/// 大图降级：VP8 帧头的 first partition 长度字段只有 19 位（≤512KB），而 partition0
/// 必须承载全部宏块的分段 + 跳过 + 帧内模式信息，超大图像会天然超限（libwebp 在同样
/// 场景直接报 <c>VP8_ENC_ERROR_PARTITION0_TOO_BIG</c>）。利用两遍架构：第一遍结束后
/// 测量 partition0 实际长度，超预算则改用降级配置重跑——关闭分段（省每宏块分段位）、
/// 强制 16×16 预测（省 i4x4 模式树，这是 partition0 的大头）、skip 概率按第一遍统计
/// 自适应（固定 128 时每宏块固定花 1 bit）。降级配置下若仍超限则抛出明确异常。
/// 默认配置的位流与降级前逐字节一致。
///
/// 位流布局：<c>[frame tag 3][start code 3][w 2][h 2][partition0][token partition]</c>，
/// 单 token 分区，因此不写分区大小表。输出为 <c>VP8 </c> chunk 的载荷（不含 chunk 头）。
///
/// 两遍编码：第一遍只统计 1056 个 token 概率节点的 0/1 次数（不写 token 位），
/// 第二遍用统计得到的概率写位流并在帧头写出更新项。两遍的量化与重建路径完全确定，
/// 符号序列因此一致；重建平面在第二遍开始前由宏块网格整体覆盖，无需清空。
///
/// 实例持有整帧缓冲区，非线程安全；每次编码创建独立实例。
/// </summary>
internal sealed class Vp8Encoder
{
    /// <summary>重建平面的四周 padding，与解码端一致（左 1 列 = 129，上空 8 行 = 127）。</summary>
    private const int Pad = 8;

    /// <summary>VP8 宽度/高度字段为 14 位。</summary>
    internal const int MaxDimension = 16383;

    /// <summary>VP8 帧头 first partition 长度字段为 19 位（RFC 6386 §9.1）。</summary>
    private const int MaxPartition0Bytes = 1 << 19;

    /// <summary>
    /// partition0 预算：两遍之间模式决策可能因概率表差异小幅漂移，留 32KB 余量。
    /// </summary>
    private const int Partition0BudgetBytes = MaxPartition0Bytes - 32 * 1024;

    /// <summary>VP8 系数幅值上限（类别码 Cat6 可表示的最大值）。</summary>
    private const int MaxCoefficient = 2114;

    private readonly int _width;
    private readonly int _height;
    private readonly int _mbW;
    private readonly int _mbH;
    private readonly int _cw;
    private readonly int _ch;

    private readonly int _strideY;
    private readonly int _strideUv;

    private readonly byte[] _srcY;
    private readonly byte[] _srcU;
    private readonly byte[] _srcV;

    private readonly byte[] _reconY;
    private readonly byte[] _reconU;
    private readonly byte[] _reconV;

    /// <summary>
    /// 滤波后的重建输出平面（与解码端 <c>_outY/_outU/_outV</c> 同布局、无 padding）。
    /// 与 <see cref="_reconY"/> 分离：帧内预测读取未滤波的重建，滤波器只作用于输出，
    /// 这与解码端的处理顺序一致，因此编码端输出与解码端逐像素相同。
    /// </summary>
    private readonly byte[] _outY;
    private readonly byte[] _outU;
    private readonly byte[] _outV;
    private readonly int _strideOutY;
    private readonly int _strideOutUv;

    private readonly int _baseQ;
    private readonly int[] _y1Dc = new int[4];
    private readonly int[] _y1Ac = new int[4];
    private readonly int[] _y2Dc = new int[4];
    private readonly int[] _y2Ac = new int[4];
    private readonly int[] _uvDc = new int[4];
    private readonly int[] _uvAc = new int[4];
    private int _currentSegment;

    private const int FlatSegmentQDelta = 6;
    private const int FlatSegmentThreshold = 256 * 256;

    private Vp8BoolEncoder _part0;
    private Vp8BoolEncoder _tokens;

    /// <summary>是否启用分段（平坦宏块用稍粗量化）。降级模式下关闭以省每宏块分段位。</summary>
    private bool _useSegmentation;

    /// <summary>是否允许 i4x4 预测。降级模式下强制 16×16 以省每宏块 16 个模式树。</summary>
    private bool _allowI4;

    /// <summary>
    /// mb_skip_coeff 位编码概率（帧头 prob_skip_false，8 位显式字面量）。
    /// 默认配置固定 128（与降级前一致）；降级模式按第一遍统计自适应。
    /// </summary>
    private int _skipProb = 128;

    /// <summary>第一遍统计：skip 宏块数 / 宏块总数（真实写位调用各计一次，成本试探不计）。</summary>
    private long _skipCount;
    private long _mbCount;

    /// <summary>当前生效的 token 概率表（第一遍为默认表，第二遍为统计更新后的表）。</summary>
    private byte[,,,] _probs;

    /// <summary>第一遍只统计符号频次：为 true 时 token 位不写入、只累加计数。</summary>
    private bool _statsOnly;

    /// <summary>1056 个 token 概率节点各自写 0 / 写 1 的次数（[type, band, ctx, node]）。</summary>
    private readonly int[,,,] _count0 = new int[4, 8, 3, 11];
    private readonly int[,,,] _count1 = new int[4, 8, 3, 11];

    /// <summary>概率估计的最小样本数：低于此值沿用默认表，避免小图过拟合。</summary>
    private const int MinProbSamples = 16;

    /// <summary>
    /// 色度 DC 量化 delta（相对 base_q）。色度块只有 8×8、且人眼对色度误差更敏感，
    /// 因此色度用比亮度更细的量化；取值与 libwebp 黑盒观测一致（dc=-2）。
    /// </summary>
    private const int UvDcDeltaQ = -2;

    /// <summary>色度 AC 量化 delta（相对 base_q），见 <see cref="UvDcDeltaQ"/>（ac=-4）。</summary>
    private const int UvAcDeltaQ = -4;

    /// <summary>环内滤波强度（0..63）。0 表示不滤波。</summary>
    private readonly int _filterLevel;

    // 非零上下文（与解码端 ParseResiduals 的簿记完全一致）。
    private readonly int[] _mbNz;
    private readonly int[] _mbNzDc;
    private int _leftNz;
    private int _leftNzDc;

    // 复用缓冲（单线程独占）。
    private readonly int[] _residual = new int[16];
    private readonly int[] _blockCoef = new int[16];
    private readonly int[] _lumaCoef = new int[256];      // 16 块 × 16，光栅序
    private readonly int[] _lumaDc = new int[16];
    private readonly int[] _y2Coef = new int[16];
    private readonly int[] _y2Quant = new int[16];        // 扫描序
    private readonly short[] _y2Dequant = new short[16];  // 光栅序
    private readonly short[] _lumaSlots = new short[256]; // 反量化后的光栅序系数（含 Y2 分发的 DC）
    private readonly int[] _lumaQuant = new int[256];     // 扫描序量化值，供 token 编码
    private readonly int[] _lumaCodes = new int[16];
    private readonly int[] _uvQuant = new int[128];       // U(4 块) + V(4 块)，扫描序
    private readonly short[] _uvSlots = new short[128];
    private readonly int[] _uvCodes = new int[8];

    // ---- i4x4（B_PRED）----
    /// <summary>本宏块 16 个子块的预测模式（0..9，光栅序）。</summary>
    private readonly int[] _i4Modes = new int[16];
    /// <summary>子块在重建平面内的偏移（跨行），与解码端 kScan 同构。</summary>
    private readonly int[] _i4Scan = new int[16];
    /// <summary>i4x4 模式树的上方上下文（每宏块列 4 项，每行清 0，与解码端 _topModes 一致）。</summary>
    private readonly byte[] _i4TopModes;
    /// <summary>i4x4 模式树的左方上下文（每个宏块行清 0）。</summary>
    private readonly byte[] _i4LeftModes = new byte[4];

    private bool _anyCoefficient;
    private int _y2Nz;

    // ---- 率失真决策 ----
    /// <summary>只累加位成本、不产出字节的编码器实例；与 <see cref="_part0"/> 共用同一批写位函数。</summary>
    private readonly Vp8BoolEncoder _costEnc = new(64);

    /// <summary>为 true 时 token 位只计价、不写位流（见 <see cref="PutCoeffBit"/>）。</summary>
    private bool _costMode;

    /// <summary>
    /// λ 的比例系数：<c>λQ8 = LambdaScale × y1Ac</c>。λ 随量化步长线性增长——
    /// 量化越粗，1 bit 码率能换到的失真越多，最优兑换率就越高。
    /// </summary>
    private const int LambdaScale = 300;



    /// <summary>i4x4 试排后的亮度重建块备份（16×16，按行 16 字节）。</summary>
    private readonly byte[] _i4ReconBak = new byte[16 * 16];
    private readonly int[] _i4QuantBak = new int[256];
    private readonly int[] _i4CodesBak = new int[16];

    private Vp8Encoder(ReadOnlySpan<byte> rgba, int width, int height, int baseQ)
    {
        _width = width;
        _height = height;
        _mbW = (width + 15) >> 4;
        _mbH = (height + 15) >> 4;
        _cw = (width + 1) >> 1;
        _ch = (height + 1) >> 1;

        _strideY = Pad * 2 + _mbW * 16;
        _strideUv = Pad * 2 + _mbW * 8;

        _srcY = new byte[width * height];
        _srcU = new byte[_cw * _ch];
        _srcV = new byte[_cw * _ch];

        _reconY = new byte[_strideY * (Pad * 2 + _mbH * 16)];
        _reconU = new byte[_strideUv * (Pad * 2 + _mbH * 8)];
        _reconV = new byte[_strideUv * (Pad * 2 + _mbH * 8)];

        _strideOutY = _mbW * 16;
        _strideOutUv = _mbW * 8;
        _outY = new byte[_strideOutY * (_mbH * 16)];
        _outU = new byte[_strideOutUv * (_mbH * 8)];
        _outV = new byte[_strideOutUv * (_mbH * 8)];

        InitReconBoundaries();
        Vp8Yuv.ToYuv420(rgba, width, height, _srcY, _srcU, _srcV);

        int q = Math.Clamp(baseQ, 0, 127);
        _baseQ = q;
        _filterLevel = EstimateFilterLevel(q);
        for (int segment = 0; segment < 4; segment++)
        {
            int segmentQ = Math.Min(127, q + (segment == 1 ? Math.Min(FlatSegmentQDelta, q / 8) : 0));
            _y1Dc[segment] = Vp8Tables.DcQuantTable[segmentQ];
            _y1Ac[segment] = Vp8Tables.AcQuantTable[segmentQ];
            _y2Dc[segment] = _y1Dc[segment] * 2;
            _y2Ac[segment] = Math.Max(8, (Vp8Tables.AcQuantTable[segmentQ] * 101581) >> 16);
            _uvDc[segment] = Vp8Tables.DcQuantTable[Math.Clamp(segmentQ + UvDcDeltaQ, 0, 117)];
            _uvAc[segment] = Vp8Tables.AcQuantTable[Math.Clamp(segmentQ + UvAcDeltaQ, 0, 127)];
        }

        _part0 = new Vp8BoolEncoder(1024);
        _tokens = new Vp8BoolEncoder(1024);

        _probs = CopyDefaultProbs();

        _mbNz = new int[_mbW];
        _mbNzDc = new int[_mbW];

        _i4TopModes = new byte[_mbW * 4];
        for (int n = 0; n < 16; n++)
            _i4Scan[n] = (n & 3) * 4 + (n >> 2) * 4 * _strideY;
    }

    /// <summary>
    /// 编码一个 RGBA 图像为 VP8 关键帧载荷（不含 <c>VP8 </c> chunk 头与 RIFF 包装）。
    /// </summary>
    /// <param name="rgba">像素数据，长度必须为 width·height·4，alpha 必须为 255。</param>
    /// <param name="width">宽度，1..16383。</param>
    /// <param name="height">高度，1..16383。</param>
    /// <param name="quality">0..100 的质量参数，单调映射为内部 q index。</param>
    internal static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, float quality)
    {
        if (width <= 0 || width > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"VP8 宽度必须在 1..{MaxDimension} 之间");
        if (height <= 0 || height > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(height), height,
                $"VP8 高度必须在 1..{MaxDimension} 之间");
        if (rgba.Length != (long)width * height * 4)
            throw new ArgumentException("RGBA 数据长度与宽高不匹配", nameof(rgba));

        var encoder = new Vp8Encoder(rgba, width, height, QualityToQIndex(quality));

        // 默认配置：第一遍统计后检查 partition0 长度，超预算则降级重跑（见类注释）。
        encoder.Configure(useSegmentation: true, allowI4: true);
        encoder.ResetForPass(encoder._probs, statsOnly: true);
        encoder.Run(assemble: false);

        if (encoder._part0.Length > Partition0BudgetBytes)
        {
            // 降级配置（关闭分段 + 强制 16×16）每宏块 partition0 开销有内容无关的硬上界
            // （skip ≤1 + i4x4 判定 ≤1.2 + 亮度模式树 ≤4 + 色度模式树 ≤3 ≈ 9 bit），
            // 但实际取值随内容波动大，直接用第一遍实测裁决，不做保守预筛——
            // 只有极端尺寸（约 16000×16000 以上）才会白跑一遍。
            encoder.Configure(useSegmentation: false, allowI4: false);
            encoder.ResetForPass(encoder._probs, statsOnly: true);
            encoder.Run(assemble: false);

            if (encoder._part0.Length > Partition0BudgetBytes)
                throw Partition0Overflow(width, height, encoder._part0.Length);

            encoder.AdaptSkipProb();
        }

        // 第二遍：用本帧统计得到的概率写位流。
        encoder.ResetForPass(encoder.AdaptProbabilities(), statsOnly: false);
        return encoder.Run(assemble: true);
    }

    /// <summary>partition0 即使在降级配置下仍超 19 位上限时给出可操作的错误说明。</summary>
    private static InvalidOperationException Partition0Overflow(int width, int height, long length)
        => new(
            $"VP8 partition0 长度 {length} 超出 19 位上限 {MaxPartition0Bytes} 字节：" +
            $"{width}×{height} 的宏块模式信息无法装入单一 partition（VP8 规范限制，" +
            "libwebp 同样无法编码）。有损 WebP 请缩小图像尺寸，或改用无损 WebP" +
            "（VP8L 位流没有 19 位分区限制）。");

    /// <summary>设定本帧的编码配置（默认 / 降级），必须在每遍 ResetForPass 之前调用。</summary>
    private void Configure(bool useSegmentation, bool allowI4)
    {
        _useSegmentation = useSegmentation;
        _allowI4 = allowI4;
        _skipProb = 128;
    }

    /// <summary>
    /// 按第一遍统计把 skip 概率从固定 128 调整为实际分布：
    /// <c>p = P(宏块 skip) = skipCount / mbCount</c> 映射到 1..255。
    /// 超大图像上绝大多数宏块非 skip，收益接近每宏块省 1 bit。
    /// </summary>
    private void AdaptSkipProb()
    {
        if (_mbCount > 0)
            _skipProb = Math.Clamp((int)((_skipCount * 255 + _mbCount / 2) / _mbCount), 1, 255);
    }

    private static byte[,,,] CopyDefaultProbs()
    {
        var src = Vp8Tables.CoeffsProba0;
        var dst = new byte[4, 8, 3, 11];
        for (int t = 0; t < 4; t++)
            for (int b = 0; b < 8; b++)
                for (int c = 0; c < 3; c++)
                    for (int n = 0; n < 11; n++)
                        dst[t, b, c, n] = src[t, b, c, n];
        return dst;
    }

    /// <summary>
    /// 由第一遍的 0/1 频次估计概率：<c>p(0) = count0 / total</c>，映射到 1..254。
    /// 样本不足 <see cref="MinProbSamples"/> 的节点沿用默认表。
    /// </summary>
    private byte[,,,] AdaptProbabilities()
    {
        var def = Vp8Tables.CoeffsProba0;
        var dst = new byte[4, 8, 3, 11];
        for (int t = 0; t < 4; t++)
            for (int b = 0; b < 8; b++)
                for (int c = 0; c < 3; c++)
                    for (int n = 0; n < 11; n++)
                    {
                        int c0 = _count0[t, b, c, n];
                        int c1 = _count1[t, b, c, n];
                        int total = c0 + c1;
                        int p = def[t, b, c, n];
                        if (total >= MinProbSamples)
                        {
                            int est = (int)(((long)c0 * 255 + total / 2) / total);
                            p = Math.Clamp(est, 1, 254);
                        }
                        dst[t, b, c, n] = (byte)p;
                    }
        return dst;
    }

    /// <summary>重置逐帧状态，准备新的一遍编码；重建网格会在编码时被整体覆盖。</summary>
    private void ResetForPass(byte[,,,] probs, bool statsOnly)
    {
        _probs = probs;
        _statsOnly = statsOnly;
        _part0 = new Vp8BoolEncoder(1024);
        _tokens = new Vp8BoolEncoder(1024);

        _leftNz = 0;
        _leftNzDc = 0;
        Array.Clear(_mbNz);
        Array.Clear(_mbNzDc);
        Array.Clear(_i4TopModes);
        Array.Clear(_i4LeftModes);
        _anyCoefficient = false;
        _y2Nz = 0;
        _skipCount = 0;
        _mbCount = 0;

        InitReconBoundaries();
    }

    /// <summary>
    /// quality（0..100）→ q index（0..127）的单调映射：高质量使用更小量化步长。
    /// 与 libwebp 的具体数值不追求一致，但必须可复现、单调。
    /// </summary>
    internal static int QualityToQIndex(float quality)
    {
        int q = (int)Math.Round(Math.Clamp(quality, 0f, 100f));
        int index = 127 - (q * 127 + 50) / 100;
        return Math.Clamp(index, 0, 127);
    }

    /// <summary>
    /// q index → 环内滤波强度（0..63）。量化越粗，块边界伪影越明显，滤波越强；
    /// 该映射只影响解码后的重建输出，不参与模式决策，因此可独立于 RD 标定。
    /// 标定依据（真实照片 + 合成渐变扫描）：q≈6（quality 95）取 2、q≈13（90）取 4、
    /// q≈32（75）取 16；更粗的量化收益饱和，上限 16 以免过度模糊细节。
    /// </summary>
    private static int EstimateFilterLevel(int q)
    {
        if (q >= 24)
            return Math.Min(16, (q + 1) / 2);
        return Math.Max(2, (q + 1) / 3);
    }

    /// <summary>
    /// 跑完一遍编码。<paramref name="assemble"/> 为 false 时（统计遍）不拼帧——
    /// partition0 超限时 <see cref="AssembleFrame"/> 会抛异常，而统计遍的产物本就要丢弃，
    /// 此时只需通过 <see cref="_part0"/>.Length 测量长度供降级决策使用。
    /// </summary>
    private byte[] Run(bool assemble)
    {
        WriteGlobalHeader();

        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            for (int mbX = 0; mbX < _mbW; mbX++)
                EncodeMacroblock(mbX, mbY);

            // 对应解码端 VP8InitScanline：行末清空左侧 nz 上下文与 i4x4 模式上下文。
            _leftNz = 0;
            _leftNzDc = 0;
            Array.Clear(_i4LeftModes);

            // 第一遍只统计概率，滤波不参与任何编码决策，跳过以免重复工作。
            if (!_statsOnly)
                FilterRow(mbY);
        }

        byte[] part0 = _part0.Finish();
        byte[] tokens = _tokens.Finish();
        return assemble ? AssembleFrame(part0, tokens) : part0;
    }

    /// <summary>
    /// 对刚编码完的宏块行做环内滤波，与解码端 <see cref="Vp8Decoder"/> 的 FilterRow 完全同序：
    /// 先把未滤波的重建行拷到输出平面，再按宏块从左到右依次处理水平（左邻）与垂直（上邻）边界。
    /// 16×16 帧内模式不启用宏块内部边界（解码端 <c>_fInner[s, 0]</c> 恒为 false），
    /// 因此这里只滤宏块边界，滤波后的输出与解码端逐像素一致。
    /// </summary>
    private void FilterRow(int mbY)
    {
        int yBase = mbY * 16 * _strideOutY;
        int uvBase = mbY * 8 * _strideOutUv;

        int srcY = (Pad + mbY * 16) * _strideY + Pad;
        int rowBytes = _mbW * 16;
        for (int j = 0; j < 16; j++)
            Array.Copy(_reconY, srcY + j * _strideY, _outY, yBase + j * _strideOutY, rowBytes);

        int srcUv = (Pad + mbY * 8) * _strideUv + Pad;
        int uvRowBytes = _mbW * 8;
        for (int j = 0; j < 8; j++)
        {
            Array.Copy(_reconU, srcUv + j * _strideUv, _outU, uvBase + j * _strideOutUv, uvRowBytes);
            Array.Copy(_reconV, srcUv + j * _strideUv, _outV, uvBase + j * _strideOutUv, uvRowBytes);
        }

        if (_filterLevel == 0)
            return;

        // PrecomputeFilterStrengths（sharpness=0、无 lf delta、无分段）：
        // ilevel = level，limit = 2·level + ilevel = 3·level，hev 阈值按 level 分档。
        int ilevel = _filterLevel;
        int limit = 3 * _filterLevel;
        int hevThresh = _filterLevel >= 40 ? 2 : (_filterLevel >= 15 ? 1 : 0);

        for (int mbX = 0; mbX < _mbW; mbX++)
        {
            int offY = yBase + mbX * 16;
            int offUv = uvBase + mbX * 8;

            if (mbX > 0)
            {
                Vp8LoopFilter.HFilter16(_outY, offY, _strideOutY, limit + 4, ilevel, hevThresh);
                Vp8LoopFilter.HFilter8(_outU, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
                Vp8LoopFilter.HFilter8(_outV, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
            }
            if (mbY > 0)
            {
                Vp8LoopFilter.VFilter16(_outY, offY, _strideOutY, limit + 4, ilevel, hevThresh);
                Vp8LoopFilter.VFilter8(_outU, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
                Vp8LoopFilter.VFilter8(_outV, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
            }
        }
    }

    // ==================================================================
    //  全局头（RFC 6386 §9.2 - §9.9）
    // ==================================================================

    private void WriteGlobalHeader()
    {
        // key_frame=0, version=0, show_frame=0, first_partition_size 在拼帧时写入。
        _part0.PutBitUniform(0); // color_space
        _part0.PutBitUniform(0); // clamping_type

        // 分段：默认配置启用（平坦宏块进入 segment 1 并使用稍粗量化）；
        // 降级配置写 segmentation_enabled=0，解码端不读分段位（ParseSegmentHeader 对应）。
        _part0.PutBitUniform(_useSegmentation ? 1 : 0); // segmentation_enabled
        if (_useSegmentation)
        {
            _part0.PutBitUniform(1); // update_mb_segmentation_map
            _part0.PutBitUniform(1); // update_segment_data
            _part0.PutBitUniform(0); // segment_feature_mode = delta values (keeps the common filter level)
            for (int s = 0; s < 4; s++)
            {
                _part0.PutSignedBits(s == 1 ? Math.Min(FlatSegmentQDelta, _baseQ / 8) : 0, 7);
            }
            // 不设置按段滤波 delta。
            for (int s = 0; s < 4; s++) _part0.PutBitUniform(0);
            // map tree：只输出 segment 0/1，segment 2/3 分支概率设为 0。
            _part0.PutBitUniform(1); _part0.PutLiteral(255, 8);
            _part0.PutBitUniform(1); _part0.PutLiteral(220, 8);
            _part0.PutBitUniform(1); _part0.PutLiteral(255, 8);
        }

        // 滤波：normal 型（simple=0），sharpness=0，不写 ref/mode lf delta。
        // 头部字段顺序依据 RFC 6386 §9.4，与解码端 ParseFilterHeader 逐项对应。
        _part0.PutBitUniform(0); // filter_type：0 = normal
        _part0.PutLiteral(_filterLevel, 6); // loop_filter_level
        _part0.PutLiteral(0, 3); // sharpness_level
        _part0.PutBitUniform(0); // loop_filter_adj_enable

        _part0.PutLiteral(0, 2); // log2_nbr_of_dct_partitions = 1 个分区

        // 量化：base_q + 5 个 delta（亮度全 0，色度按 UvDcDeltaQ / UvAcDeltaQ 变细）。
        // PutSignedBits 自身写“是否非零”标志，语义与解码端的 GetSignedValue(4) 一致。
        _part0.PutLiteral(_baseQ, 7);
        _part0.PutSignedBits(0, 4); // y1_dc_delta_q
        _part0.PutSignedBits(0, 4); // y2_dc_delta_q
        _part0.PutSignedBits(0, 4); // y2_ac_delta_q
        _part0.PutSignedBits(UvDcDeltaQ, 4);
        _part0.PutSignedBits(UvAcDeltaQ, 4);

        _part0.PutBitUniform(0); // refresh_entropy_probs = 0

        // 1056 个 token 概率更新项：与默认表相同的节点写 0（沿用默认），
        // 其余写 1 + 8 位新概率（RFC 6386 §13.4）。标志位必须用 CoeffsUpdateProba 编码，
        // 不能用等概率 128，否则解码端从这一节开始就与编码端失步。
        var updateProba = Vp8Tables.CoeffsUpdateProba;
        var defaultProba = Vp8Tables.CoeffsProba0;
        for (int t = 0; t < 4; t++)
            for (int b = 0; b < 8; b++)
                for (int c = 0; c < 3; c++)
                    for (int p = 0; p < 11; p++)
                    {
                        int np = _probs[t, b, c, p];
                        if (np == defaultProba[t, b, c, p])
                        {
                            _part0.PutBit(0, updateProba[t, b, c, p]);
                        }
                        else
                        {
                            _part0.PutBit(1, updateProba[t, b, c, p]);
                            _part0.PutLiteral(np, 8);
                        }
                    }

        _part0.PutBitUniform(1); // mb_no_coeff_skip
        _part0.PutLiteral(_skipProb, 8); // prob_skip_false（默认配置恒为 128，降级配置自适应）
    }

    private byte[] AssembleFrame(byte[] part0, byte[] tokens)
    {
        if (part0.Length >= (1 << 19))
            throw new InvalidOperationException(
                $"VP8 partition0 长度 {part0.Length} 超出 19 位上限；请降低质量参数或图像尺寸");

        long tag = 0x10 | ((long)part0.Length << 5);
        var frame = new byte[10 + part0.Length + tokens.Length];
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        frame[6] = (byte)(_width & 0xff);
        frame[7] = (byte)((_width >> 8) & 0x3f);
        frame[8] = (byte)(_height & 0xff);
        frame[9] = (byte)((_height >> 8) & 0x3f);
        Array.Copy(part0, 0, frame, 10, part0.Length);
        Array.Copy(tokens, 0, frame, 10 + part0.Length, tokens.Length);
        return frame;
    }

    // ==================================================================
    //  宏块编码
    // ==================================================================

    private void EncodeMacroblock(int mbX, int mbY)
    {
        _currentSegment = _useSegmentation && IsFlatMacroblock(mbX, mbY) ? 1 : 0;
        int yOff = (Pad + mbY * 16) * _strideY + Pad + mbX * 16;
        int uvOff = (Pad + mbY * 8) * _strideUv + Pad + mbX * 8;

        // 色度与亮度完全独立（色度预测只读色度平面的上/左邻居），先算定 uvMode 与
        // chromaAny，两条亮度候选路径共用同一份色度结果。
        int uvMode = ChooseMode8(mbX, mbY, uvOff);
        _anyCoefficient = false;
        PrepareChromaResidual(mbX, mbY, uvOff);
        bool chromaAny = _anyCoefficient;

        int yMode = ChooseMode16(mbX, mbY, yOff, out long sse16);

        // ---- 候选 A：i4x4 ----
        // 试排总会完整重建一份，但重建结果要备份：下面的 16×16 路径会把它整个覆盖掉。
        bool i4Ok = _allowI4 && sse16 > 0;
        long dist4 = 0, bits4 = 0;
        bool any4 = false;
        if (i4Ok)
        {
            _anyCoefficient = false;
            PrepareI4x4(mbX, mbY, yOff, out dist4);
            any4 = _anyCoefficient;
            bits4 = CostLuma(mbX, useI4: true, yMode, uvMode, skip: !(any4 || chromaAny));
            BackupI4x4(yOff);
        }

        // ---- 候选 B：16×16 ----
        Vp8IntraPred.Pred16(yMode, mbY > 0, mbX > 0, _reconY, yOff, _strideY);
        _anyCoefficient = false;
        PrepareLumaResidual(mbX, mbY, yOff);
        bool any16 = _anyCoefficient;
        long dist16 = SseLuma(mbX, mbY);
        long bits16 = CostLuma(mbX, useI4: false, yMode, uvMode, skip: !(any16 || chromaAny));

        // 率失真决胜：两条路都已算出「重建失真 + 精确码率」，比 SSE 门槛可靠得多。
        bool useI4 = i4Ok && Score(dist4, bits4) <= Score(dist16, bits16);
        if (useI4)
            RestoreI4x4(yOff);

        bool skip = !((useI4 ? any4 : any16) || chromaAny);

        WriteMacroblockModes(mbX, useI4, yMode, uvMode, skip);
        if (skip)
        {
            _leftNz = 0;
            _mbNz[mbX] = 0;
            if (!useI4)
            {
                _leftNzDc = 0;
                _mbNzDc[mbX] = 0;
            }
        }
        else if (useI4)
        {
            WriteI4ResidualTokens(mbX);
        }
        else
        {
            WriteResidualTokens(mbX);
        }
    }

    /// <summary>
    /// 率失真评分：<c>D + λ·R</c>，D 是重建后的亮度 SSE、R 以 bit 计。整体乘 65536 转成
    /// 整数运算——λ 用 Q8 定点（<c>λQ8 = LambdaScale × 当前段的 y1Ac</c>），则 <c>D·65536 + λQ8·R</c>
    /// 与 <c>D + λ·(R/256)</c> 同序（R 的单位是 1/256 bit）。
    /// <para>
    /// λ 的取值：<c>λQ8 = LambdaScale × y1Ac</c>。选「与量化步长成正比」是因为 1 bit 码率
    /// 能换回多少失真，直接取决于量化步长。标定结果（6 图 × q50/75/90/95，24 组，对照
    /// 旧的「SSE 压到 16×16 的 55% 以内」门槛）：
    /// </para>
    /// <list type="bullet">
    /// <item>LambdaScale 150/230/300/350/450/600 → 总体积 −3.04/−3.09/−3.11/−3.13/−3.10/−3.15%，</item>
    /// <item>平均 ΔPSNR +0.010/−0.003/−0.010/−0.014/−0.022/−0.026 dB。</item>
    /// </list>
    /// <para>
    /// 曲线在 150~600 之间几乎是平的（体积极差 0.11%），说明决策由数据本身决定，不再依赖
    /// 精调门槛——这与旧门槛的「最优值随内容漂移（照片 40、UI 70~100）」形成对比。取 300。
    /// </para>
    /// </summary>
    private long Score(long distortion, long bits)
        => distortion * 65536L + (long)(LambdaScale * _y1Ac[_currentSegment]) * bits;

    private bool IsFlatMacroblock(int mbX, int mbY)
    {
        const int sampleCount = 16 * 16;
        long sum = 0, sumSquares = 0;
        int baseX = mbX * 16, baseY = mbY * 16;
        for (int y = 0; y < 16; y++)
        {
            int sy = Math.Min(baseY + y, _height - 1);
            int row = sy * _width;
            for (int x = 0; x < 16; x++)
            {
                int sx = Math.Min(baseX + x, _width - 1);
                int value = _srcY[row + sx];
                sum += value;
                sumSquares += value * value;
            }
        }
        // n*Σx² - (Σx)² = n times the sum of squared deviations from the mean.
        long varianceNumerator = sampleCount * sumSquares - sum * sum;
        return varianceNumerator <= FlatSegmentThreshold;
    }

    /// <summary>
    /// 在不改动真实编码状态的前提下，精确算出「本宏块按这条路径写出去要多少 bit」：
    /// 临时把 <see cref="_part0"/><see cref="_tokens"/> 换成只累加成本的实例，
    /// 复用真实的模式写位与 token 写位函数，因此码率模型与位流生成永远同步。
    /// 结束后回滚所有逐宏块上下文（nz 与 i4x4 模式上下文）。
    /// </summary>
    private long CostLuma(int mbX, bool useI4, int yMode, int uvMode, bool skip)
    {
        int tb = mbX * 4;
        int saveLeftNz = _leftNz, saveLeftDc = _leftNzDc;
        int saveTopNz = _mbNz[mbX], saveTopDc = _mbNzDc[mbX];
        Span<byte> saveTop = stackalloc byte[4];
        Span<byte> saveLeft = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            saveTop[i] = _i4TopModes[tb + i];
            saveLeft[i] = _i4LeftModes[i];
        }

        Vp8BoolEncoder part0 = _part0, tokens = _tokens;
        bool costMode = _costMode;
        _costMode = true;
        _part0 = _costEnc;
        _tokens = _costEnc;
        _costEnc.BeginCost();

        WriteMacroblockModes(mbX, useI4, yMode, uvMode, skip);
        if (!skip)
        {
            if (useI4) WriteI4ResidualTokens(mbX);
            else WriteResidualTokens(mbX);
        }

        long bits = _costEnc.EndCost();
        _part0 = part0;
        _tokens = tokens;
        _costMode = costMode;

        _leftNz = saveLeftNz;
        _leftNzDc = saveLeftDc;
        _mbNz[mbX] = saveTopNz;
        _mbNzDc[mbX] = saveTopDc;
        for (int i = 0; i < 4; i++)
        {
            _i4TopModes[tb + i] = saveTop[i];
            _i4LeftModes[i] = saveLeft[i];
        }
        return bits;
    }

    private void BackupI4x4(int yOff)
    {
        for (int r = 0; r < 16; r++)
            Array.Copy(_reconY, yOff + r * _strideY, _i4ReconBak, r * 16, 16);
        Array.Copy(_lumaQuant, _i4QuantBak, 256);
        Array.Copy(_lumaCodes, _i4CodesBak, 16);
    }

    private void RestoreI4x4(int yOff)
    {
        for (int r = 0; r < 16; r++)
            Array.Copy(_i4ReconBak, r * 16, _reconY, yOff + r * _strideY, 16);
        Array.Copy(_i4QuantBak, _lumaQuant, 256);
        Array.Copy(_i4CodesBak, _lumaCodes, 16);
    }

    /// <summary>
    /// 16×16 亮度预测模式选择（0=DC, 1=TM, 2=V, 3=H），结束时预测已写入重建平面。
    /// </summary>
    private int ChooseMode16(int mbX, int mbY, int yOff, out long bestSse)
    {
        bool hasTop = mbY > 0;
        bool hasLeft = mbX > 0;
        int best = 0;
        bestSse = long.MaxValue;

        for (int mode = 0; mode < 4; mode++)
        {
            Vp8IntraPred.Pred16(mode, hasTop, hasLeft, _reconY, yOff, _strideY);
            long sse = SseLuma(mbX, mbY);
            if (sse < bestSse)
            {
                bestSse = sse;
                best = mode;
            }
        }

        Vp8IntraPred.Pred16(best, hasTop, hasLeft, _reconY, yOff, _strideY);
        return best;
    }

    /// <summary>
    /// 试走一遍 i4x4（B_PRED）编码本宏块：预测域 SSE 筛出前三种模式，再按重建失真和码率选择，
    /// 随即量化、反量化、重建，作为下一个子块的预测源（与解码端逐块推进的顺序一致）。
    /// </summary>
    /// <param name="distortion">
    /// 重建后的亮度失真（16 个子块的 SSE 之和）——这是 RD 比较用的真实失真，
    /// 不是模式选择时的预测域 SSE。
    /// </param>
    private void PrepareI4x4(int mbX, int mbY, int yOff, out long distortion)
    {
        PrepareTopRight(mbX, mbY, yOff);

        distortion = 0;
        bool any = false;
        Span<int> candidates = stackalloc int[3];
        Span<long> candidateSse = stackalloc long[3];
        Span<int> topNz = stackalloc int[4];
        Span<int> leftNz = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            topNz[i] = (_mbNz[mbX] >> i) & 1;
            leftNz[i] = (_leftNz >> i) & 1;
        }

        for (int n = 0; n < 16; n++)
        {
            int off = yOff + _i4Scan[n];
            int baseX = mbX * 16 + (n & 3) * 4;
            int baseY = mbY * 16 + (n >> 2) * 4;

            candidateSse.Fill(long.MaxValue);
            for (int mode = 0; mode <= I4MaxMode; mode++)
            {
                Vp8IntraPred.Pred4(mode, _reconY, off, _strideY);
                long sse = BlockSse4x4(_srcY, _width, _height, _reconY, off, _strideY, baseX, baseY);
                for (int rank = 0; rank < candidates.Length; rank++)
                {
                    if (sse >= candidateSse[rank]) continue;
                    for (int j = candidates.Length - 1; j > rank; j--)
                    {
                        candidateSse[j] = candidateSse[j - 1];
                        candidates[j] = candidates[j - 1];
                    }
                    candidateSse[rank] = sse;
                    candidates[rank] = mode;
                    break;
                }
            }

            int bx = n & 3, by = n >> 2;
            int topMode = by > 0 ? _i4Modes[n - 4] : _i4TopModes[mbX * 4 + bx];
            int leftMode = bx > 0 ? _i4Modes[n - 1] : _i4LeftModes[by];
            int context = topNz[bx] + leftNz[by];
            int bestMode = candidates[0];
            long bestScore = long.MaxValue;
            foreach (int mode in candidates)
            {
                PrepareI4Block(mbX, mbY, n, off, mode);
                long d = BlockSse4x4(_srcY, _width, _height, _reconY, off, _strideY, baseX, baseY);
                long bits = CostI4Block(mode, topMode, leftMode, context, n);
                long score = Score(d, bits);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMode = mode;
                }
            }

            bool nonZero = PrepareI4Block(mbX, mbY, n, off, bestMode);
            _i4Modes[n] = bestMode;
            topNz[bx] = leftNz[by] = nonZero ? 1 : 0;
            any |= nonZero;
            distortion += BlockSse4x4(_srcY, _width, _height, _reconY, off, _strideY, baseX, baseY);
        }

        _anyCoefficient = any;
    }

    private long CostI4Block(int mode, int topMode, int leftMode, int context, int block)
    {
        bool savedCostMode = _costMode;
        _costMode = true;
        try
        {
            _costEnc.BeginCost();
            Vp8ModeTree.Write(_costEnc, mode, topMode, leftMode);
            WriteCoeffs(_costEnc, 3, context, 0, _lumaQuant.AsSpan(block * 16, 16));
            return _costEnc.EndCost();
        }
        finally
        {
            _costMode = savedCostMode;
        }
    }

    private bool PrepareI4Block(int mbX, int mbY, int n, int off, int bestMode)
    {
        bool any = false;
        Vp8IntraPred.Pred4(bestMode, _reconY, off, _strideY);

        // 残差 → 正变换 → 量化（i4x4 的 DC 走 y1Dc，不汇入 Y2）→ 反量化 → 重建
        ComputeResidualY(mbX, mbY, n, _residual);
        Vp8ForwardTransform.Dct4x4(_residual, _blockCoef);

        int dst = n * 16;
        int last = -1;
        for (int i = 0; i < 16; i++)
        {
            int raster = Vp8Tables.Zigzag[i];
            int dq = i == 0 ? _y1Dc[_currentSegment] : _y1Ac[_currentSegment];
            int q = Quantize(_blockCoef[raster], dq);
            _lumaQuant[dst + i] = q;
            _lumaSlots[dst + raster] = (short)(q * dq);
            if (q != 0)
            {
                last = i;
                any = true;
            }
        }

        int nz = last < 0 ? 0 : last + 1;
        bool dcNonZero = _lumaSlots[dst] != 0;
        _lumaCodes[n] = NonZeroCode(nz, dcNonZero);
        ApplyTransform(_lumaSlots, dst, _reconY, off, _strideY, _lumaCodes[n]);

        return any;
    }

    /// <summary>i4x4 亮度子块模式搜索的上界（含）：0=B_DC … 9=B_HU 全开。</summary>
    private const int I4MaxMode = 9;

    /// <summary>
    /// 对应解码端 frame_dec.c 的 top_right 预处理：i4x4 的 4×4 子块右上边界取自上一宏块行，
    /// 最右列宏块复制本行第 15 个样本；并把结果复制到本宏块第 3/7/11 行右侧，
    /// 供下方子块读取。编码端必须与解码端逐字节相同，否则预测会分叉。
    /// </summary>
    private void PrepareTopRight(int mbX, int mbY, int yOff)
    {
        byte v0, v1, v2, v3;
        if (mbY > 0)
        {
            int src = yOff - _strideY + 16;
            if (mbX >= _mbW - 1)
            {
                byte edge = _reconY[yOff - _strideY + 15];
                v0 = v1 = v2 = v3 = edge;
            }
            else
            {
                v0 = _reconY[src];
                v1 = _reconY[src + 1];
                v2 = _reconY[src + 2];
                v3 = _reconY[src + 3];
            }

            _reconY[src] = v0;
            _reconY[src + 1] = v1;
            _reconY[src + 2] = v2;
            _reconY[src + 3] = v3;
        }
        else
        {
            // 首个宏块行：上边界统一为 127（InitReconBoundaries 已填）。
            v0 = v1 = v2 = v3 = 127;
        }

        for (int k = 1; k <= 3; k++)
        {
            int dst = yOff + (4 * k - 1) * _strideY + 16;
            _reconY[dst] = v0;
            _reconY[dst + 1] = v1;
            _reconY[dst + 2] = v2;
            _reconY[dst + 3] = v3;
        }
    }

    /// <summary>色度 8×8 预测模式选择（U/V 共用同一模式），结束时预测已写入重建平面。</summary>
    private int ChooseMode8(int mbX, int mbY, int uvOff)
    {
        bool hasTop = mbY > 0;
        bool hasLeft = mbX > 0;
        int best = 0;
        long bestSse = long.MaxValue;

        for (int mode = 0; mode < 4; mode++)
        {
            Vp8IntraPred.Pred8(mode, hasTop, hasLeft, _reconU, uvOff, _strideUv);
            Vp8IntraPred.Pred8(mode, hasTop, hasLeft, _reconV, uvOff, _strideUv);
            long sse = SseChroma(mbX, mbY);
            if (sse < bestSse)
            {
                bestSse = sse;
                best = mode;
            }
        }

        Vp8IntraPred.Pred8(best, hasTop, hasLeft, _reconU, uvOff, _strideUv);
        Vp8IntraPred.Pred8(best, hasTop, hasLeft, _reconV, uvOff, _strideUv);
        return best;
    }

    private long SseLuma(int mbX, int mbY)
    {
        long sse = 0;
        for (int block = 0; block < 16; block++)
        {
            int bx = (block & 3) * 4;
            int by = (block >> 2) * 4;
            int reconOff = (Pad + mbY * 16) * _strideY + Pad + mbX * 16 + by * _strideY + bx;
            sse += BlockSse4x4(_srcY, _width, _height, _reconY, reconOff, _strideY,
                               mbX * 16 + bx, mbY * 16 + by);
        }
        return sse;
    }

    private long SseChroma(int mbX, int mbY)
    {
        long sse = 0;
        for (int ch = 0; ch < 2; ch++)
        {
            byte[] src = ch == 0 ? _srcU : _srcV;
            byte[] recon = ch == 0 ? _reconU : _reconV;
            for (int block = 0; block < 4; block++)
            {
                int bx = (block & 1) * 4;
                int by = (block >> 1) * 4;
                int reconOff = (Pad + mbY * 8) * _strideUv + Pad + mbX * 8 + by * _strideUv + bx;
                sse += BlockSse4x4(src, _cw, _ch, recon, reconOff, _strideUv,
                                   mbX * 8 + bx, mbY * 8 + by);
            }
        }
        return sse;
    }

    /// <summary>
    /// 一个 4×4 块的残差平方和，直接从平面算，不再到 <c>_residual</c> 中转。
    /// 内侧块（4 行都在图内、行内起止都在图内）走向量路径：两行一次，把 8 个字节
    /// 宽成 ushort 后相减、相乘，再宽成 int 累加。边缘块回落到逐像素钳制的标量路径，
    /// 与 <see cref="ComputeResidualY"/> 的钳制规则完全一致。
    /// </summary>
    private static long BlockSse4x4(byte[] src, int srcWidth, int srcHeight,
                                    byte[] recon, int reconOff, int reconStride,
                                    int baseX, int baseY)
    {
        if (baseY + 3 < srcHeight && baseX + 3 < srcWidth)
        {
            ref byte srcRef = ref MemoryMarshal.GetArrayDataReference(src);
            ref byte recRef = ref MemoryMarshal.GetArrayDataReference(recon);
            Vector128<int> acc = Vector128<int>.Zero;
            for (int y = 0; y < 4; y += 2)
            {
                int s0 = (baseY + y) * srcWidth + baseX;
                ulong sp = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, s0))
                         | ((ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, s0 + srcWidth)) << 32);
                int r0 = reconOff + y * reconStride;
                ulong rp = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref recRef, r0))
                         | ((ulong)Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref recRef, r0 + reconStride)) << 32);

                Vector128<short> d =
                    Vector128.WidenLower(Vector128.CreateScalarUnsafe(sp).AsByte()).AsInt16()
                    - Vector128.WidenLower(Vector128.CreateScalarUnsafe(rp).AsByte()).AsInt16();
                Vector128<ushort> sq = Vector128.Multiply(d, d).AsUInt16();
                acc += Vector128.WidenLower(sq).AsInt32() + Vector128.WidenUpper(sq).AsInt32();
            }
            return Vector128.Sum(acc);
        }

        long sse = 0;
        for (int y = 0; y < 4; y++)
        {
            int gy = baseY + y;
            if (gy >= srcHeight) gy = srcHeight - 1;
            int srcRow = gy * srcWidth;
            int reconRow = reconOff + y * reconStride;
            for (int x = 0; x < 4; x++)
            {
                int gx = baseX + x;
                if (gx >= srcWidth) gx = srcWidth - 1;
                int diff = src[srcRow + gx] - recon[reconRow + x];
                sse += (long)diff * diff;
            }
        }
        return sse;
    }

    /// <summary>
    /// 计算亮度残差、正变换、量化/反量化，并按解码端的 non_zero 码选择逆变换重建。
    /// i16x16 的 16 个 DC 汇入 Y2 块统一编码。
    /// </summary>
    private void PrepareLumaResidual(int mbX, int mbY, int yOff)
    {
        // 1) 每块正 DCT，收集 DC 并保留 AC（光栅序）。
        for (int block = 0; block < 16; block++)
        {
            ComputeResidualY(mbX, mbY, block, _residual);
            Vp8ForwardTransform.Dct4x4(_residual, _blockCoef);
            _lumaDc[block] = _blockCoef[0];
            int dst = block * 16;
            for (int i = 0; i < 16; i++)
                _lumaCoef[dst + i] = _blockCoef[i];
            _lumaQuant[dst] = 0;
        }

        // 2) 正 WHT + 量化（DC 与 AC 分别用 y2Dc / y2Ac）。
        Vp8ForwardTransform.Wht4x4(_lumaDc, _y2Coef);
        int lastY2 = -1;
        for (int n = 0; n < 16; n++)
        {
            int raster = Vp8Tables.Zigzag[n];
            int dq = n == 0 ? _y2Dc[_currentSegment] : _y2Ac[_currentSegment];
            int q = Quantize(_y2Coef[raster], dq);
            _y2Quant[n] = q;
            _y2Dequant[raster] = (short)(q * dq);
            if (q != 0)
            {
                lastY2 = n;
                _anyCoefficient = true;
            }
        }
        _y2Nz = lastY2 < 0 ? 0 : lastY2 + 1;

        // 3) 把 Y2 的 DC 分发到 16 个亮度块（与解码端完全相同的两条路径）。
        if (_y2Nz > 1)
        {
            Vp8Idct.TransformWHT(_y2Dequant, _lumaSlots);
        }
        else
        {
            short dc0 = (short)((_y2Dequant[0] + 3) >> 3);
            for (int block = 0; block < 16; block++)
                _lumaSlots[block * 16] = dc0;
        }

        // 4) 量化各块 AC，记录扫描序量化值与反量化结果。
        for (int block = 0; block < 16; block++)
        {
            int dst = block * 16;
            int last = -1;
            for (int n = 1; n < 16; n++)
            {
                int raster = Vp8Tables.Zigzag[n];
                int q = Quantize(_lumaCoef[dst + raster], _y1Ac[_currentSegment]);
                _lumaQuant[dst + n] = q;
                _lumaSlots[dst + raster] = (short)(q * _y1Ac[_currentSegment]);
                if (q != 0)
                {
                    last = n;
                    _anyCoefficient = true;
                }
            }

            int nz = last < 0 ? 1 : last + 1;
            bool dcNonZero = _lumaSlots[dst] != 0;
            _lumaCodes[block] = NonZeroCode(nz, dcNonZero);
        }

        // 5) 逐块重建，变换变体与解码端 DoTransform 完全一致。
        for (int block = 0; block < 16; block++)
        {
            int off = yOff + (block & 3) * 4 + (block >> 2) * 4 * _strideY;
            ApplyTransform(_lumaSlots, block * 16, _reconY, off, _strideY, _lumaCodes[block]);
        }
    }

    /// <summary>色度残差：16 个系数全部进入 token（DC 不受 Y2 影响）。</summary>
    private void PrepareChromaResidual(int mbX, int mbY, int uvOff)
    {
        for (int ch = 0; ch < 2; ch++)
        {
            for (int block = 0; block < 4; block++)
            {
                ComputeResidualUv(mbX, mbY, ch, block, _residual);
                Vp8ForwardTransform.Dct4x4(_residual, _blockCoef);

                int index = ch * 4 + block;
                int dst = index * 16;
                int last = -1;
                for (int n = 0; n < 16; n++)
                {
                    int raster = Vp8Tables.Zigzag[n];
                    int dq = n == 0 ? _uvDc[_currentSegment] : _uvAc[_currentSegment];
                    int q = Quantize(_blockCoef[raster], dq);
                    _uvQuant[dst + n] = q;
                    _uvSlots[dst + raster] = (short)(q * dq);
                    if (q != 0)
                    {
                        last = n;
                        _anyCoefficient = true;
                    }
                }

                int nz = last < 0 ? 0 : last + 1;
                bool dcNonZero = _uvSlots[dst] != 0;
                int code = NonZeroCode(nz, dcNonZero);
                _uvCodes[index] = code;

                int off = uvOff + (block & 1) * 4 + (block >> 1) * 4 * _strideUv;
                ApplyTransform(_uvSlots, dst, ch == 0 ? _reconU : _reconV, off, _strideUv, code);
            }
        }
    }

    private void WriteMacroblockModes(int mbX, bool useI4, int yMode, int uvMode, bool skip)
    {
        // ParseIntraMode reads a 3-way tree: first choose segment 0/1, then choose the id.
        // 降级配置下 update_map=0，解码端不读这两个位，这里同样不写。
        if (_useSegmentation)
        {
            _part0.PutBit(0, 255);
            _part0.PutBit(_currentSegment, 220);
        }

        // skip 统计只认真实写位：成本试探（CostLuma）会重复调用本函数，第一遍之外的
        // 计入会翻倍，因此用 _statsOnly && !_costMode 双重门控。
        if (_statsOnly && !_costMode)
        {
            _mbCount++;
            if (skip)
                _skipCount++;
        }
        _part0.PutBit(skip ? 1 : 0, _skipProb);

        // is_i4x4：写 1 表示 16×16，写 0 表示 4×4。
        _part0.PutBit(useI4 ? 0 : 1, 145);

        if (useI4)
        {
            int topBase = mbX * 4;
            for (int y = 0; y < 4; y++)
            {
                int left = _i4LeftModes[y];
                for (int x = 0; x < 4; x++)
                {
                    int mode = _i4Modes[y * 4 + x];
                    Vp8ModeTree.Write(_part0, mode, _i4TopModes[topBase + x], left);
                    _i4TopModes[topBase + x] = (byte)mode;
                    left = mode;
                }
                _i4LeftModes[y] = (byte)left;
            }
        }
        else
        {
            // 解码端在 16×16 宏块里同样把该宏块的 4 列/4 行模式上下文填成这同一个模式
            // （ParseIntraMode 的 !isI4X4 分支），i4x4 宏块据此取上下文，必须同步。
            int topBase16 = mbX * 4;
            for (int i = 0; i < 4; i++)
                _i4TopModes[topBase16 + i] = (byte)yMode;
            for (int i = 0; i < 4; i++)
                _i4LeftModes[i] = (byte)yMode;

            // 16×16 亮度模式树（与解码端 ParseIntraMode 的读法互为逆序）。
            switch (yMode)
            {
                case 1: // TM
                    _part0.PutBit(1, 156);
                    _part0.PutBit(1, 128);
                    break;
                case 3: // H
                    _part0.PutBit(1, 156);
                    _part0.PutBit(0, 128);
                    break;
                case 2: // V
                    _part0.PutBit(0, 156);
                    _part0.PutBit(1, 163);
                    break;
                default: // DC
                    _part0.PutBit(0, 156);
                    _part0.PutBit(0, 163);
                    break;
            }
        }

        // 色度模式树（U/V 共用）。
        switch (uvMode)
        {
            case 2: // V
                _part0.PutBit(1, 142);
                _part0.PutBit(0, 114);
                break;
            case 1: // TM
                _part0.PutBit(1, 142);
                _part0.PutBit(1, 114);
                _part0.PutBit(1, 183);
                break;
            case 3: // H
                _part0.PutBit(1, 142);
                _part0.PutBit(1, 114);
                _part0.PutBit(0, 183);
                break;
            default: // DC
                _part0.PutBit(0, 142);
                break;
        }
    }

    // ==================================================================
    //  残差 token 编码（与解码端 ParseResiduals 的上下文簿记一致）
    // ==================================================================

    private void WriteResidualTokens(int mbX)
    {
        // Y2 块（type = 1，first = 0）。
        int ctx = _mbNzDc[mbX] + _leftNzDc;
        WriteCoeffs(_tokens, 1, ctx, 0, _y2Quant);
        _mbNzDc[mbX] = _leftNzDc = _y2Nz > 0 ? 1 : 0;

        int tnz = _mbNz[mbX] & 0x0f;
        int lnz = _leftNz & 0x0f;
        int l = 0;

        for (int y = 0; y < 4; y++)
        {
            l = lnz & 1;
            for (int x = 0; x < 4; x++)
            {
                int block = y * 4 + x;
                int bctx = l + (tnz & 1);
                WriteCoeffs(_tokens, 0, bctx, 1, _lumaQuant.AsSpan(block * 16, 16));
                int nz = NonZeroLength(_lumaQuant.AsSpan(block * 16, 16), 1);
                l = nz > 1 ? 1 : 0;
                tnz = (tnz >> 1) | (l << 7);
            }
            tnz >>= 4;
            lnz = (lnz >> 1) | (l << 7);
        }
        int outTopNz = tnz;
        int outLeftNz = lnz >> 4;

        WriteChromaTokens(mbX, ref outTopNz, ref outLeftNz);

        _mbNz[mbX] = outTopNz;
        _leftNz = outLeftNz;
    }

    /// <summary>
    /// i4x4 的亮度系数：没有 Y2，16 个块都以 first=0（DC 在块内）、类型 3 的上下文编码。
    /// 与解码端 <c>ParseResiduals(isI4X4=true)</c> 的簿记逐句对应。
    /// </summary>
    private void WriteI4ResidualTokens(int mbX)
    {
        int tnz = _mbNz[mbX] & 0x0f;
        int lnz = _leftNz & 0x0f;
        int l = 0;

        for (int y = 0; y < 4; y++)
        {
            l = lnz & 1;
            for (int x = 0; x < 4; x++)
            {
                int block = y * 4 + x;
                int bctx = l + (tnz & 1);
                WriteCoeffs(_tokens, 3, bctx, 0, _lumaQuant.AsSpan(block * 16, 16));
                int nz = NonZeroLength(_lumaQuant.AsSpan(block * 16, 16), 0);
                l = nz > 0 ? 1 : 0;
                tnz = (tnz >> 1) | (l << 7);
            }
            tnz >>= 4;
            lnz = (lnz >> 1) | (l << 7);
        }
        int outTopNz = tnz;
        int outLeftNz = lnz >> 4;

        WriteChromaTokens(mbX, ref outTopNz, ref outLeftNz);

        _mbNz[mbX] = outTopNz;
        _leftNz = outLeftNz;
    }

    /// <summary>色度的 4 个块（U/V 各 2×2）。两种亮度路径共用。</summary>
    private void WriteChromaTokens(int mbX, ref int outTopNz, ref int outLeftNz)
    {
        int tnz, lnz, l;
        int index = 0;
        for (int ch = 0; ch < 4; ch += 2)
        {
            // 关键：此处仍使用本宏块编码前的 _mbNz / _leftNz（与解码端一致）。
            tnz = _mbNz[mbX] >> (4 + ch);
            lnz = _leftNz >> (4 + ch);
            for (int y = 0; y < 2; y++)
            {
                l = lnz & 1;
                for (int x = 0; x < 2; x++)
                {
                    int bctx = l + (tnz & 1);
                    WriteCoeffs(_tokens, 2, bctx, 0, _uvQuant.AsSpan(index * 16, 16));
                    int nz = NonZeroLength(_uvQuant.AsSpan(index * 16, 16), 0);
                    l = nz > 0 ? 1 : 0;
                    tnz = (tnz >> 1) | (l << 3);
                    index++;
                }
                tnz >>= 2;
                lnz = (lnz >> 1) | (l << 5);
            }
            outTopNz |= (tnz << 4) << ch;
            outLeftNz |= (lnz & 0xf0) << ch;
        }
    }

    /// <summary>
    /// 按解码端 GetCoeffs 的逆过程写一个 4×4 块的量化系数（扫描序）。
    /// </summary>
    private void WriteCoeffs(Vp8BoolEncoder bw, int type, int ctx, int first, ReadOnlySpan<int> scan)
    {
        byte[] bands = Vp8Tables.Bands;
        int last = -1;
        for (int i = 15; i >= first; i--)
        {
            if (scan[i] != 0)
            {
                last = i;
                break;
            }
        }

        int n = first;
        int band = bands[n];
        int curCtx = ctx;

        while (true)
        {
            if (last < n)
            {
                PutCoeffBit(bw, type, band, curCtx, 0, 0);
                return;
            }

            PutCoeffBit(bw, type, band, curCtx, 0, 1);

            // 零游程：每遇到 0 写一个 p[1]=0；遇到非零写 p[1]=1。
            while (scan[n] == 0)
            {
                PutCoeffBit(bw, type, band, curCtx, 1, 0);
                n++;
                band = bands[n];
                curCtx = 0;
            }
            PutCoeffBit(bw, type, band, curCtx, 1, 1);

            int value = scan[n];
            int magnitude = Math.Abs(value);
            int nextCtx;
            if (magnitude == 1)
            {
                PutCoeffBit(bw, type, band, curCtx, 2, 0);
                nextCtx = 1;
            }
            else
            {
                PutCoeffBit(bw, type, band, curCtx, 2, 1);
                nextCtx = 2;
                PutLargeValue(bw, type, band, curCtx, magnitude);
            }

            bw.PutBit(value < 0 ? 1 : 0, 128);
            n++;
            if (n >= 16)
                return;

            band = bands[n];
            curCtx = nextCtx;
        }
    }

    /// <summary>对应解码端 GetLargeValue 的逆过程（幅值 ≥ 2）。</summary>
    private void PutLargeValue(Vp8BoolEncoder bw, int type, int band, int ctx, int magnitude)
    {
        if (magnitude <= 4)
        {
            PutCoeffBit(bw, type, band, ctx, 3, 0);
            if (magnitude == 2)
            {
                PutCoeffBit(bw, type, band, ctx, 4, 0);
            }
            else
            {
                PutCoeffBit(bw, type, band, ctx, 4, 1);
                PutCoeffBit(bw, type, band, ctx, 5, magnitude == 4 ? 1 : 0);
            }
            return;
        }

        PutCoeffBit(bw, type, band, ctx, 3, 1);

        if (magnitude <= 10)
        {
            PutCoeffBit(bw, type, band, ctx, 6, 0);
            if (magnitude <= 6)
            {
                PutCoeffBit(bw, type, band, ctx, 7, 0);
                bw.PutBit(magnitude == 6 ? 1 : 0, 159);
            }
            else
            {
                PutCoeffBit(bw, type, band, ctx, 7, 1);
                if (magnitude <= 8)
                {
                    bw.PutBit(0, 165);
                    bw.PutBit(magnitude == 8 ? 1 : 0, 145);
                }
                else
                {
                    bw.PutBit(1, 165);
                    bw.PutBit(magnitude == 10 ? 1 : 0, 145);
                }
            }
            return;
        }

        PutCoeffBit(bw, type, band, ctx, 6, 1);

        int cat = magnitude <= 18 ? 0 : magnitude <= 34 ? 1 : magnitude <= 66 ? 2 : 3;
        int bit1 = cat >= 2 ? 1 : 0;
        PutCoeffBit(bw, type, band, ctx, 8, bit1);
        PutCoeffBit(bw, type, band, ctx, 9 + bit1, cat & 1);

        int extra = magnitude - (3 + (8 << cat));
        ReadOnlySpan<byte> table = cat switch
        {
            0 => Vp8Tables.Cat3Probs,
            1 => Vp8Tables.Cat4Probs,
            2 => Vp8Tables.Cat5Probs,
            _ => Vp8Tables.Cat6Probs,
        };
        for (int i = table.Length - 1, k = 0; i >= 0; i--, k++)
            bw.PutBit((extra >> i) & 1, table[k]);
    }

    /// <summary>
    /// 写一个可更新的 token 概率节点：第一遍只累加频次；第二遍按当前概率表写位。
    /// </summary>
    private void PutCoeffBit(Vp8BoolEncoder bw, int type, int band, int ctx, int node, int bit)
    {
        if (_costMode)
        {
            // 用当前生效的概率表计价。第一遍用的是默认表、第二遍是统计更新后的表，
            // 因此两遍的评分可能略有差异；这只会让第二遍的重建与第一遍统计的符号分布
            // 有些偏差（编码效率的损失），不影响位流合法性——解码器用的是帧头里传的表。
            // 反过来，若固定用默认表，成本会被系统性放大（渐变图实测 4.5×），
            // 且放大倍数随内容漂移，i4x4 与 16×16 的相对比较会失真。
            bw.PutBit(bit, _probs[type, band, ctx, node]);
            return;
        }
        if (_statsOnly)
        {
            if (bit == 0) _count0[type, band, ctx, node]++;
            else _count1[type, band, ctx, node]++;
            return;
        }
        bw.PutBit(bit, _probs[type, band, ctx, node]);
    }

    private static int NonZeroLength(ReadOnlySpan<int> scan, int first)
    {
        for (int i = 15; i >= first; i--)
        {
            if (scan[i] != 0)
                return i + 1;
        }
        return first;
    }

    private static int NonZeroCode(int nz, bool dcNonZero)
    {
        if (nz > 3)
            return 3;
        if (nz > 1)
            return 2;
        return dcNonZero ? 1 : 0;
    }

    // ==================================================================
    //  变换 / 量化 / 重建
    // ==================================================================

    private static int Quantize(int coefficient, int dq)
    {
        if (coefficient == 0)
            return 0;

        int magnitude = coefficient < 0 ? -coefficient : coefficient;
        int quantized = (magnitude + (dq >> 1)) / dq;
        if (quantized > MaxCoefficient)
            quantized = MaxCoefficient;
        return coefficient < 0 ? -quantized : quantized;
    }

    private static void ApplyTransform(short[] slots, int offset, byte[] dst, int dstOff, int stride, int code)
    {
        switch (code)
        {
            case 3:
                Vp8Idct.TransformOne(slots.AsSpan(offset, 16), dst, dstOff, stride);
                break;
            case 2:
                Vp8Idct.TransformAc3(slots.AsSpan(offset, 16), dst, dstOff, stride);
                break;
            case 1:
                Vp8Idct.TransformDc(slots.AsSpan(offset, 16), dst, dstOff, stride);
                break;
            default:
                break;
        }
    }

    private void ComputeResidualY(int mbX, int mbY, int block, int[] residual)
    {
        int bx = (block & 3) * 4;
        int by = (block >> 2) * 4;
        int baseX = mbX * 16 + bx;
        int baseY = mbY * 16 + by;
        int reconOff = (Pad + mbY * 16) * _strideY + Pad + mbX * 16 + by * _strideY + bx;

        if (baseY + 3 < _height && baseX + 3 < _width)
        {
            ResidualBlock4x4(_srcY, _width, _reconY, reconOff, _strideY, baseX, baseY, residual);
            return;
        }

        for (int y = 0; y < 4; y++)
        {
            int gy = baseY + y;
            if (gy >= _height)
                gy = _height - 1;
            int srcRow = gy * _width;
            int reconRow = reconOff + y * _strideY;
            for (int x = 0; x < 4; x++)
            {
                int gx = baseX + x;
                if (gx >= _width)
                    gx = _width - 1;
                residual[y * 4 + x] = _srcY[srcRow + gx] - _reconY[reconRow + x];
            }
        }
    }

    /// <summary>
    /// 4×4 残差（内侧块专用，不做越界钳制）：一次取一行的 4 个字节，
    /// 宽成 short 相减后再宽成 int 存回。调用方必须保证 4 行都在图内、
    /// 且 baseX..baseX+3 不跨行——越界分支仍走标量钳制路径。
    /// </summary>
    private static void ResidualBlock4x4(byte[] src, int srcWidth,
                                         byte[] recon, int reconOff, int reconStride,
                                         int baseX, int baseY, int[] residual)
    {
        ref byte srcRef = ref MemoryMarshal.GetArrayDataReference(src);
        ref byte recRef = ref MemoryMarshal.GetArrayDataReference(recon);
        ref int resRef = ref MemoryMarshal.GetArrayDataReference(residual);

        for (int y = 0; y < 4; y++)
        {
            int s = (baseY + y) * srcWidth + baseX;
            int r = reconOff + y * reconStride;
            Vector128<short> sv = Vector128.WidenLower(
                Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, s))).AsByte()).AsInt16();
            Vector128<short> rv = Vector128.WidenLower(
                Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref recRef, r))).AsByte()).AsInt16();
            Vector128.StoreUnsafe(Vector128.WidenLower(sv - rv), ref resRef, (nuint)(y * 4));
        }
    }

    private void ComputeResidualUv(int mbX, int mbY, int ch, int block, int[] residual)
    {
        int bx = (block & 1) * 4;
        int by = (block >> 1) * 4;
        int baseX = mbX * 8 + bx;
        int baseY = mbY * 8 + by;
        int reconOff = (Pad + mbY * 8) * _strideUv + Pad + mbX * 8 + by * _strideUv + bx;
        byte[] src = ch == 0 ? _srcU : _srcV;
        byte[] recon = ch == 0 ? _reconU : _reconV;

        if (baseY + 3 < _ch && baseX + 3 < _cw)
        {
            ResidualBlock4x4(src, _cw, recon, reconOff, _strideUv, baseX, baseY, residual);
            return;
        }

        for (int y = 0; y < 4; y++)
        {
            int gy = baseY + y;
            if (gy >= _ch)
                gy = _ch - 1;
            int srcRow = gy * _cw;
            int reconRow = reconOff + y * _strideUv;
            for (int x = 0; x < 4; x++)
            {
                int gx = baseX + x;
                if (gx >= _cw)
                    gx = _cw - 1;
                residual[y * 4 + x] = src[srcRow + gx] - recon[reconRow + x];
            }
        }
    }

    private void InitReconBoundaries()
    {
        for (int row = 0; row < Pad; row++)
        {
            Array.Fill(_reconY, (byte)127, row * _strideY, _strideY);
            Array.Fill(_reconU, (byte)127, row * _strideUv, _strideUv);
            Array.Fill(_reconV, (byte)127, row * _strideUv, _strideUv);
        }

        for (int row = Pad; row < _reconY.Length / _strideY; row++)
            _reconY[row * _strideY + Pad - 1] = 129;
        int uvRows = _reconU.Length / _strideUv;
        for (int row = Pad; row < uvRows; row++)
        {
            _reconU[row * _strideUv + Pad - 1] = 129;
            _reconV[row * _strideUv + Pad - 1] = 129;
        }
    }
}
