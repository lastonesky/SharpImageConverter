namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 有损关键帧（intra-only）解码结果：三个 YUV420 平面，按紧凑行距存放（无 padding）。
/// </summary>
internal sealed class Vp8DecodedImage
{
    internal Vp8DecodedImage(int width, int height, byte[] y, byte[] u, byte[] v)
    {
        Width = width;
        Height = height;
        Y = y;
        U = u;
        V = v;
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>亮度平面，行距即 <see cref="Width"/>。</summary>
    internal byte[] Y { get; }

    /// <summary>色度 U 平面，行距即 <see cref="StrideUV"/>。</summary>
    internal byte[] U { get; }

    /// <summary>色度 V 平面，行距即 <see cref="StrideUV"/>。</summary>
    internal byte[] V { get; }

    internal int StrideY => Width;

    internal int StrideUV => (Width + 1) / 2;
}

/// <summary>
/// VP8 关键帧解码器（RFC 6386 / libwebp src/dec 的托管移植）。
///
/// 覆盖范围：
/// <list type="bullet">
/// <item>帧标签、关键帧图像头（start code / 宽高 / scale）、颜色空间与裁剪标志；</item>
/// <item>分段头、环内滤波头、量化头、token 概率更新头与 skip 概率；</item>
/// <item>1..8 个 token 分区（按宏块行 <c>mb_y &amp; num_parts_minus_one</c> 选择）；</item>
/// <item>宏块层：segment / skip / i4x4 判定、16×16 与 4×4 帧内模式、色度模式；</item>
/// <item>残差 token 解码（含分类码表）、反量化、逆 WHT + 4×4 逆 DCT 重建；</item>
/// <item>simple / complex 环内滤波；</item>
/// <item>输出 YUV420 平面（彩色转换由上层的 <see cref="Vp8YuvToRgb"/> 完成）。</item>
/// </list>
///
/// 内存布局说明：解码需要同时保存"未滤波重建（供帧内预测读取）"与"滤波后输出"两套平面，
/// 因为 VP8 规定帧内预测必须使用未滤波的相邻像素（libwebp 用 yuv_b 缓存 + cache_y 输出平面实现）。
/// 这里改为持久化全平面：
/// <list type="bullet">
/// <item>重建平面四周留 <see cref="Pad"/> 像素 padding，左边界填 129、上空行填 127，与 libwebp 的
/// 边界初始化语义一致；这样可省略 libwebp 每宏块的左列 4 像素搬运（Copy32b）与顶行缓存（yuv_t）；</item>
/// <item>输出平面无 padding，只覆盖完整的宏块网格，最终按真实宽高裁剪。</item>
/// </list>
///
/// 实例持有整帧私有状态，不可跨线程共享；<see cref="Decode"/> 每次调用创建独立实例，线程安全。
/// </summary>
internal sealed class Vp8Decoder
{
    /// <summary>重建平面的四周 padding（左 1 列、上 1 行是预测边界所需，右 4 列供 4×4 右上样本复制）。</summary>
    private const int Pad = 8;

    /// <summary>
    /// 4×4 子块在宏块内的扫描顺序（对应 libwebp frame_dec.c 的 kScan）。C 版本按固定的
    /// 暂存区行距 BPS=32 展开，本实现的平面行距随宏块数变化，因此在分配平面时按实际
    /// 行距生成该表。
    /// </summary>
    private int[] _kScan = Array.Empty<int>();

    private readonly byte[] _data;

    private int _offset;
    private int _width;
    private int _height;
    private int _mbW;
    private int _mbH;

    private Vp8BoolDecoder _partition0 = null!;
    private Vp8BoolDecoder[] _parts = Array.Empty<Vp8BoolDecoder>();
    private int _numPartsMinusOne;

    // ---- 分段头（RFC 6386 §9.3）----
    private bool _useSegment;
    private bool _updateMap;
    private bool _absoluteDelta;
    private readonly int[] _segQuantizer = new int[4];
    private readonly int[] _segFilterStrength = new int[4];
    private readonly byte[] _segProbs = { 255, 255, 255 };

    // ---- 滤波头（RFC 6386 §9.4）----
    private bool _filterSimple;
    private bool _useLfDelta;
    private int _filterLevel;
    private int _sharpness;
    private readonly int[] _refLfDelta = new int[4];
    private readonly int[] _modeLfDelta = new int[4];
    private int _filterType; // 0=不滤波, 1=simple, 2=complex

    // ---- 量化矩阵（RFC 6386 §9.6 / §14.1）----
    private readonly int[] _y1Dc = new int[4];
    private readonly int[] _y1Ac = new int[4];
    private readonly int[] _y2Dc = new int[4];
    private readonly int[] _y2Ac = new int[4];
    private readonly int[] _uvDc = new int[4];
    private readonly int[] _uvAc = new int[4];

    // ---- token 概率与 skip 概率（RFC 6386 §13.3）----
    // 扁平表：[(type * 8 + band) * 3 + ctx] * 11 + p，与 CoeffsProba0 的四维结构等价。
    private readonly byte[] _proba = new byte[4 * 8 * 3 * 11];
    private bool _useSkipProba;
    private int _skipProb;

    // ---- 逐宏块（按行复用）----
    private int[] _mbNz = Array.Empty<int>();
    private int[] _mbNzDc = Array.Empty<int>();
    private int _leftNz;
    private int _leftNzDc;
    private byte[] _topModes = Array.Empty<byte>();
    private readonly byte[] _leftModes = new byte[4];

    private byte[] _segmentOfMb = Array.Empty<byte>();
    private byte[] _skipOfMb = Array.Empty<byte>();
    private bool[] _isI4X4OfMb = Array.Empty<bool>();
    private byte[] _imodes = Array.Empty<byte>();
    private byte[] _uvModeOfMb = Array.Empty<byte>();
    private uint[] _nonZeroY = Array.Empty<uint>();
    private uint[] _nonZeroUv = Array.Empty<uint>();
    private bool[] _fInnerOfMb = Array.Empty<bool>();
    private readonly short[] _coeffs = new short[384];
    private readonly short[] _dcTmp = new short[16];

    // ---- 预计算的滤波强度 [segment][i4x4]----
    private readonly int[,] _fLimit = new int[4, 2];
    private readonly int[,] _fILevel = new int[4, 2];
    private readonly int[,] _fHev = new int[4, 2];
    private readonly bool[,] _fInner = new bool[4, 2];

    // ---- 平面（重建 / 输出）----
    private byte[] _reconY = Array.Empty<byte>();
    private byte[] _reconU = Array.Empty<byte>();
    private byte[] _reconV = Array.Empty<byte>();
    private int _strideReconY;
    private int _strideReconUv;

    private byte[] _outY = Array.Empty<byte>();
    private byte[] _outU = Array.Empty<byte>();
    private byte[] _outV = Array.Empty<byte>();
    private int _strideOutY;
    private int _strideOutUv;

    private Vp8Decoder(byte[] data) => _data = data;

    /// <summary>帧标签的最低有效位为 0 表示关键帧（RFC 6386 §9.1）。</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> chunk) =>
        chunk.Length >= 3 && (chunk[0] & 1) == 0;

    /// <summary>
    /// 解码一个完整的 VP8 关键帧 chunk（不含 RIFF 容器包装）。
    /// 非关键帧、不可显示帧等不受支持的形态抛 <see cref="WebpUnsupportedException"/>；
    /// 截断或损坏的位流抛 <see cref="WebpBitstreamException"/>。
    /// </summary>
    public static Vp8DecodedImage Decode(ReadOnlySpan<byte> chunk)
    {
        var decoder = new Vp8Decoder(chunk.ToArray());
        return decoder.Run();
    }

    private Vp8DecodedImage Run()
    {
        ParseFrameHeader();
        ParseGlobalHeader();
        AllocatePlanes();
        DecodeFrame();
        return CropOutput();
    }

    // ==================================================================
    //  帧头（RFC 6386 §9.1 / §9.2）
    // ==================================================================

    private void ParseFrameHeader()
    {
        if (_data.Length < 4)
            throw new WebpBitstreamException("VP8 帧数据过短，无法读取帧标签");

        uint bits = (uint)(_data[0] | (_data[1] << 8) | (_data[2] << 16));
        if ((bits & 1u) != 0)
        {
            // RFC 9649 §2.1：WebP 的有损压缩使用 VP8 帧内（intra-frame）编码，ANMF 帧数据
            // 必须是 VP8 关键帧。非关键帧不属于 WebP 的合法输入，libwebp 同样以
            // VP8_STATUS_UNSUPPORTED_FEATURE（"Not a key frame."）显式拒绝，故这里是规范要求的
            // 拒绝路径，而不是待实现的功能；回退 libwebp 也不会成功。
            throw new WebpUnsupportedException(
                "VP8 非关键帧（inter frame）不属于 WebP：RFC 9649 规定有损 WebP 仅使用 VP8 帧内编码、" +
                "ANMF 帧数据必须为 VP8 关键帧（libwebp 亦拒绝此类输入）");
        }

        int profile = (int)((bits >> 1) & 7u);
        if (profile > 3)
            throw new WebpBitstreamException($"VP8 profile {profile} 超出规范范围");

        if (((bits >> 4) & 1u) == 0)
            throw new WebpUnsupportedException("VP8 关键帧被标记为不显示（show_frame=0）");

        int partitionLength = (int)(bits >> 5);
        _offset = 3;

        if (_data.Length - _offset < 7)
            throw new WebpBitstreamException("VP8 关键帧图像头截断");
        if (_data[_offset] != 0x9D || _data[_offset + 1] != 0x01 || _data[_offset + 2] != 0x2A)
            throw new WebpBitstreamException("VP8 起始码 0x9D012A 校验失败");

        // 宽高各 14 位；高 2 位是显示比例（scale），不参与解码。
        _width = ((_data[_offset + 4] << 8) | _data[_offset + 3]) & 0x3FFF;
        _height = ((_data[_offset + 6] << 8) | _data[_offset + 5]) & 0x3FFF;
        _offset += 7;

        if (_width == 0 || _height == 0)
            throw new WebpBitstreamException("VP8 图像宽高为 0");

        if (partitionLength > _data.Length - _offset)
            throw new WebpBitstreamException("VP8 partition0 长度超出 chunk 数据范围");

        _mbW = (_width + 15) >> 4;
        _mbH = (_height + 15) >> 4;

        _partition0 = new Vp8BoolDecoder(_data, _offset, partitionLength);
        if (!_partition0.IsValid)
            throw new WebpBitstreamException("VP8 partition0 首字节为 0xFF，违反 value < range");
        _offset += partitionLength;
    }

    // ==================================================================
    //  全局头（§9.3 - §9.9、§13.3）
    // ==================================================================

    private void ParseGlobalHeader()
    {
        // 关键帧颜色空间 / 裁剪类型标志：解码流程不使用，仅消费掉。
        _partition0.GetBit(128);
        _partition0.GetBit(128);

        ParseSegmentHeader();
        ParseFilterHeader();
        ParsePartitions();
        ParseQuant();

        // §9.7 的 update_proba 标志：本实现每次都用完整概率表，故忽略其值。
        _partition0.GetBit(128);
        ParseProba();
    }

    private void ParseSegmentHeader()
    {
        _useSegment = _partition0.GetBit(128) != 0;
        if (_useSegment)
        {
            _updateMap = _partition0.GetBit(128) != 0;
            if (_partition0.GetBit(128) != 0)
            {
                _absoluteDelta = _partition0.GetBit(128) != 0;
                for (int s = 0; s < 4; s++)
                    _segQuantizer[s] = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(7) : 0;
                for (int s = 0; s < 4; s++)
                    _segFilterStrength[s] = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(6) : 0;
            }
            if (_updateMap)
            {
                for (int s = 0; s < 3; s++)
                    _segProbs[s] = _partition0.GetBit(128) != 0 ? (byte)_partition0.GetValue(8) : (byte)255;
            }
        }
        else
        {
            _updateMap = false;
        }

        if (_partition0.Eof)
            throw new WebpBitstreamException("VP8 分段头截断");
    }

    private void ParseFilterHeader()
    {
        _filterSimple = _partition0.GetBit(128) != 0;
        _filterLevel = (int)_partition0.GetValue(6);
        _sharpness = (int)_partition0.GetValue(3);
        _useLfDelta = _partition0.GetBit(128) != 0;
        if (_useLfDelta && _partition0.GetBit(128) != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_partition0.GetBit(128) != 0)
                    _refLfDelta[i] = _partition0.GetSignedValue(6);
            }
            for (int i = 0; i < 4; i++)
            {
                if (_partition0.GetBit(128) != 0)
                    _modeLfDelta[i] = _partition0.GetSignedValue(6);
            }
        }

        _filterType = _filterLevel == 0 ? 0 : _filterSimple ? 1 : 2;

        if (_partition0.Eof)
            throw new WebpBitstreamException("VP8 滤波头截断");
    }

    private void ParsePartitions()
    {
        _numPartsMinusOne = (1 << (int)_partition0.GetValue(2)) - 1;

        int remaining = _data.Length - _offset;
        if (remaining < 3 * _numPartsMinusOne)
            throw new WebpBitstreamException("VP8 token 分区大小表截断");

        _parts = new Vp8BoolDecoder[_numPartsMinusOne + 1];
        int szPos = _offset;
        int partStart = _offset + 3 * _numPartsMinusOne;
        int sizeLeft = remaining - 3 * _numPartsMinusOne;

        for (int p = 0; p < _numPartsMinusOne; p++)
        {
            int psize = _data[szPos] | (_data[szPos + 1] << 8) | (_data[szPos + 2] << 16);
            if (psize > sizeLeft)
                psize = sizeLeft;
            _parts[p] = new Vp8BoolDecoder(_data, partStart, psize);
            if (!_parts[p].IsValid)
                throw new WebpBitstreamException($"VP8 token 分区 {p} 首字节为 0xFF");
            partStart += psize;
            sizeLeft -= psize;
            szPos += 3;
        }

        if (partStart >= _data.Length)
            throw new WebpBitstreamException("VP8 最后一个 token 分区没有数据");

        _parts[_numPartsMinusOne] = new Vp8BoolDecoder(_data, partStart, sizeLeft);
        if (!_parts[_numPartsMinusOne].IsValid)
            throw new WebpBitstreamException("VP8 最后一个 token 分区首字节为 0xFF");
    }

    private static int Clip(int v, int max) => v < 0 ? 0 : (v > max ? max : v);

    private void ParseQuant()
    {
        int baseQ = (int)_partition0.GetValue(7);
        int dqY1Dc = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(4) : 0;
        int dqY2Dc = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(4) : 0;
        int dqY2Ac = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(4) : 0;
        int dqUvDc = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(4) : 0;
        int dqUvAc = _partition0.GetBit(128) != 0 ? _partition0.GetSignedValue(4) : 0;

        for (int i = 0; i < 4; i++)
        {
            int q;
            if (_useSegment)
            {
                q = _segQuantizer[i];
                if (!_absoluteDelta)
                    q += baseQ;
            }
            else
            {
                if (i > 0)
                {
                    _y1Dc[i] = _y1Dc[0];
                    _y1Ac[i] = _y1Ac[0];
                    _y2Dc[i] = _y2Dc[0];
                    _y2Ac[i] = _y2Ac[0];
                    _uvDc[i] = _uvDc[0];
                    _uvAc[i] = _uvAc[0];
                    continue;
                }
                q = baseQ;
            }

            _y1Dc[i] = Vp8Tables.DcQuantTable[Clip(q + dqY1Dc, 127)];
            _y1Ac[i] = Vp8Tables.AcQuantTable[Clip(q, 127)];

            _y2Dc[i] = Vp8Tables.DcQuantTable[Clip(q + dqY2Dc, 127)] * 2;
            // C 注释：对 x ∈ [0,284]，x*155/100 与 (x*101581)>>16 逐位等价。
            _y2Ac[i] = (Vp8Tables.AcQuantTable[Clip(q + dqY2Ac, 127)] * 101581) >> 16;
            if (_y2Ac[i] < 8)
                _y2Ac[i] = 8;

            _uvDc[i] = Vp8Tables.DcQuantTable[Clip(q + dqUvDc, 117)];
            _uvAc[i] = Vp8Tables.AcQuantTable[Clip(q + dqUvAc, 127)];
        }
    }

    private void ParseProba()
    {
        var dflt = Vp8Tables.CoeffsProba0;
        var update = Vp8Tables.CoeffsUpdateProba;
        for (int t = 0; t < 4; t++)
        {
            for (int b = 0; b < 8; b++)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int p = 0; p < 11; p++)
                    {
                        byte v = _partition0.GetBit(update[t, b, c, p]) != 0
                            ? (byte)_partition0.GetValue(8)
                            : dflt[t, b, c, p];
                        _proba[((t * 8 + b) * 3 + c) * 11 + p] = v;
                    }
                }
            }
        }

        _useSkipProba = _partition0.GetBit(128) != 0;
        _skipProb = _useSkipProba ? (int)_partition0.GetValue(8) : 0;
    }

    // ==================================================================
    //  平面分配与初始化
    // ==================================================================

    private void AllocatePlanes()
    {
        _strideReconY = Pad * 2 + _mbW * 16;
        int hReconY = Pad * 2 + _mbH * 16;
        _reconY = new byte[_strideReconY * hReconY];

        _strideReconUv = Pad * 2 + _mbW * 8;
        int hReconUv = Pad * 2 + _mbH * 8;
        _reconU = new byte[_strideReconUv * hReconUv];
        _reconV = new byte[_strideReconUv * hReconUv];

        // 图像上边界：libwebp 用 127（RFC 6386 约定"不可用样本"）。整个上空行统一填 127 即可，
        // 因为只在首个宏块行被读取，且 libwebp 在该行也始终为 127。
        for (int row = 0; row < Pad; row++)
        {
            int rowStart = row * _strideReconY;
            Array.Fill(_reconY, (byte)127, rowStart, _strideReconY);
        }
        for (int row = 0; row < Pad; row++)
        {
            int rowStart = row * _strideReconUv;
            Array.Fill(_reconU, (byte)127, rowStart, _strideReconUv);
            Array.Fill(_reconV, (byte)127, rowStart, _strideReconUv);
        }

        // 图像左边界：libwebp 用 129。只对第 0 个宏块列的左邻样本可见。
        for (int row = Pad; row < hReconY; row++)
            _reconY[row * _strideReconY + Pad - 1] = 129;
        for (int row = Pad; row < hReconUv; row++)
        {
            _reconU[row * _strideReconUv + Pad - 1] = 129;
            _reconV[row * _strideReconUv + Pad - 1] = 129;
        }

        _strideOutY = _mbW * 16;
        _outY = new byte[_strideOutY * (_mbH * 16)];
        _strideOutUv = _mbW * 8;
        _outU = new byte[_strideOutUv * (_mbH * 8)];
        _outV = new byte[_strideOutUv * (_mbH * 8)];

        // 4×4 子块偏移按重建平面实际行距展开（对应 frame_dec.c 的 kScan，但那里行距固定为 BPS=32）。
        _kScan = new int[16];
        for (int n = 0; n < 16; n++)
            _kScan[n] = (n & 3) * 4 + (n >> 2) * 4 * _strideReconY;

        _mbNz = new int[_mbW];
        _mbNzDc = new int[_mbW];
        _topModes = new byte[_mbW * 4];
        _segmentOfMb = new byte[_mbW];
        _skipOfMb = new byte[_mbW];
        _isI4X4OfMb = new bool[_mbW];
        _imodes = new byte[_mbW * 16];
        _uvModeOfMb = new byte[_mbW];
        _nonZeroY = new uint[_mbW];
        _nonZeroUv = new uint[_mbW];
        _fInnerOfMb = new bool[_mbW];
    }

    /// <summary>
    /// 预计算每个 segment × i4x4/i16x16 组合的滤波强度（frame_dec.c PrecomputeFilterStrengths）。
    /// </summary>
    private void PrecomputeFilterStrengths()
    {
        for (int s = 0; s < 4; s++)
        {
            int baseLevel;
            if (_useSegment)
            {
                baseLevel = _segFilterStrength[s];
                if (!_absoluteDelta)
                    baseLevel += _filterLevel;
            }
            else
            {
                baseLevel = _filterLevel;
            }

            for (int i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                int level = baseLevel;
                if (_useLfDelta)
                {
                    level += _refLfDelta[0];
                    if (i4x4 != 0)
                        level += _modeLfDelta[0];
                }
                level = Math.Clamp(level, 0, 63);

                _fInner[s, i4x4] = i4x4 != 0;
                if (level > 0)
                {
                    int ilevel = level;
                    if (_sharpness > 0)
                    {
                        ilevel >>= _sharpness > 4 ? 2 : 1;
                        if (ilevel > 9 - _sharpness)
                            ilevel = 9 - _sharpness;
                    }
                    if (ilevel < 1)
                        ilevel = 1;
                    _fILevel[s, i4x4] = ilevel;
                    _fLimit[s, i4x4] = 2 * level + ilevel;
                    _fHev[s, i4x4] = level >= 40 ? 2 : (level >= 15 ? 1 : 0);
                }
                else
                {
                    _fLimit[s, i4x4] = 0;
                }
            }
        }
    }

    // ==================================================================
    //  帧级解码循环
    // ==================================================================

    private void DecodeFrame()
    {
        PrecomputeFilterStrengths();

        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            Vp8BoolDecoder tokenBr = _parts[mbY & _numPartsMinusOne];

            ParseIntraModeRow(mbY);
            if (_partition0.Eof)
                throw new WebpBitstreamException("VP8 partition0 提前结束（intra 模式行截断）");

            for (int mbX = 0; mbX < _mbW; mbX++)
            {
                DecodeMacroblock(tokenBr, mbX, mbY);
                if (tokenBr.Eof)
                    throw new WebpBitstreamException(
                        $"VP8 token 分区提前结束（宏块 {mbX},{mbY}）");
            }

            // 对应 VP8InitScanline：清空左侧上下文。
            _leftNz = 0;
            _leftNzDc = 0;
            Array.Clear(_leftModes);

            CopyRowToOutput(mbY);
            FilterRow(mbY);
        }
    }

    private void ParseIntraModeRow(int mbY)
    {
        for (int mbX = 0; mbX < _mbW; mbX++)
            ParseIntraMode(mbX);
    }

    /// <summary>
    /// 解析单个宏块的头信息（tree_dec.c ParseIntraMode）：segment、skip、i4x4 判定与帧内模式。
    /// </summary>
    private void ParseIntraMode(int mbX)
    {
        int segment = 0;
        if (_updateMap)
        {
            segment = _partition0.GetBit(_segProbs[0]) == 0
                ? _partition0.GetBit(_segProbs[1])
                : _partition0.GetBit(_segProbs[2]) + 2;
        }
        _segmentOfMb[mbX] = (byte)segment;

        if (_useSkipProba)
            _skipOfMb[mbX] = (byte)_partition0.GetBit(_skipProb);

        bool isI4X4 = _partition0.GetBit(145) == 0;
        _isI4X4OfMb[mbX] = isI4X4;
        int modesBase = mbX * 16;

        if (!isI4X4)
        {
            // 16×16 亮度模式的硬编码树（12.2）：DC / V / H / TM。
            int ymode = _partition0.GetBit(156) != 0
                ? (_partition0.GetBit(128) != 0 ? 1 : 3)
                : (_partition0.GetBit(163) != 0 ? 2 : 0);
            _imodes[modesBase] = (byte)ymode;
            Array.Fill(_topModes, (byte)ymode, mbX * 4, 4);
            Array.Fill(_leftModes, (byte)ymode);
        }
        else
        {
            int topBase = mbX * 4;
            for (int y = 0; y < 4; y++)
            {
                int ymode = _leftModes[y];
                for (int x = 0; x < 4; x++)
                {
                    ymode = DecodeBmode(_partition0, _topModes[topBase + x], ymode);
                    _topModes[topBase + x] = (byte)ymode;
                    _imodes[modesBase + y * 4 + x] = (byte)ymode;
                }
                _leftModes[y] = (byte)ymode;
            }
        }

        // 色度模式硬编码树（12.2）：DC / V / H / TM。
        _uvModeOfMb[mbX] = (byte)(_partition0.GetBit(142) == 0
            ? 0
            : _partition0.GetBit(114) == 0
                ? 2
                : (_partition0.GetBit(183) != 0 ? 1 : 3));
    }

    /// <summary>
    /// 4×4 亮度模式树（tree_dec.c 的 kBModesProba[top][left] 硬编码分支）。
    /// 返回值即 B_DC_PRED..B_HU_PRED（0..9）。
    /// </summary>
    private static int DecodeBmode(Vp8BoolDecoder br, int top, int left)
    {
        byte[,,] p = Vp8Tables.BModesProba;
        if (br.GetBit(p[top, left, 0]) == 0) return 0; // B_DC_PRED
        if (br.GetBit(p[top, left, 1]) == 0) return 1; // B_TM_PRED
        if (br.GetBit(p[top, left, 2]) == 0) return 2; // B_VE_PRED
        if (br.GetBit(p[top, left, 3]) == 0)
        {
            return br.GetBit(p[top, left, 4]) == 0
                ? 3  // B_HE_PRED
                : (br.GetBit(p[top, left, 5]) == 0 ? 4 : 5); // B_RD_PRED / B_VR_PRED
        }
        return br.GetBit(p[top, left, 6]) == 0
            ? 6  // B_LD_PRED
            : br.GetBit(p[top, left, 7]) == 0
                ? 7  // B_VL_PRED
                : (br.GetBit(p[top, left, 8]) == 0 ? 8 : 9); // B_HD_PRED / B_HU_PRED
    }

    // ==================================================================
    //  残差解码（§13.2 / §13.3）
    // ==================================================================

    private static uint NzCodeBits(uint nzCoeffs, int nz, bool dcNonZero)
    {
        nzCoeffs <<= 2;
        nzCoeffs |= nz > 3 ? 3u : (nz > 1 ? 2u : (dcNonZero ? 1u : 0u));
        return nzCoeffs;
    }

    private int GetLargeValue(Vp8BoolDecoder br, ReadOnlySpan<byte> p)
    {
        if (br.GetBit(p[3]) == 0)
        {
            if (br.GetBit(p[4]) == 0)
                return 2;
            return 3 + br.GetBit(p[5]);
        }

        if (br.GetBit(p[6]) == 0)
        {
            if (br.GetBit(p[7]) == 0)
                return 5 + br.GetBit(159);
            int v = 7 + 2 * br.GetBit(165);
            return v + br.GetBit(145);
        }

        int bit1 = br.GetBit(p[8]);
        int bit0 = br.GetBit(p[9 + bit1]);
        int cat = 2 * bit1 + bit0;
        ReadOnlySpan<byte> tab = cat switch
        {
            0 => Vp8Tables.Cat3Probs,
            1 => Vp8Tables.Cat4Probs,
            2 => Vp8Tables.Cat5Probs,
            _ => Vp8Tables.Cat6Probs,
        };
        int value = 0;
        foreach (byte prob in tab)
            value += value + br.GetBit(prob);
        return value + 3 + (8 << cat);
    }

    /// <summary>
    /// 解码单个 4×4 块的系数（vp8_dec.c GetCoeffsFast）。返回"最后一个非零系数位置 + 1"。
    /// </summary>
    /// <param name="type">0=i16x16 的 AC，1=Y2 的 DC，2=色度，3=i4x4</param>
    /// <param name="ctx">非零上下文（0..2）</param>
    /// <param name="first">起始扫描位置（i16x16 为 1，其余为 0）</param>
    private int GetCoeffs(Vp8BoolDecoder br, int type, int ctx, int dqDc, int dqAc, int first, Span<short> dstCoeffs)
    {
        ReadOnlySpan<byte> bands = Vp8Tables.Bands;
        int n = first;
        ReadOnlySpan<byte> p = ProbCell(type, bands[n], ctx);

        for (; n < 16; ++n)
        {
            if (br.GetBit(p[0]) == 0)
                return n; // 上一个系数就是最后一个非零系数

            while (br.GetBit(p[1]) == 0)
            {
                ++n;
                if (n == 16)
                    return 16;
                p = ProbCell(type, bands[n], 0);
            }

            int v;
            if (br.GetBit(p[2]) == 0)
            {
                v = 1;
                p = ProbCell(type, bands[n + 1], 1);
            }
            else
            {
                v = GetLargeValue(br, p);
                p = ProbCell(type, bands[n + 1], 2);
            }

            int signedValue = br.GetSigned(v);
            int dq = n > 0 ? dqAc : dqDc;
            dstCoeffs[Vp8Tables.Zigzag[n]] = unchecked((short)(signedValue * dq));
        }
        return 16;
    }

    private ReadOnlySpan<byte> ProbCell(int type, byte band, int ctx) =>
        _proba.AsSpan(((type * 8 + band) * 3 + ctx) * 11, 11);

    /// <summary>
    /// 解析一个宏块的全部残差（vp8_dec.c ParseResiduals），返回 C 语义的 skip 标志
    /// （1 表示整块无非零系数）。
    /// </summary>
    private int ParseResiduals(Vp8BoolDecoder br, int mbX, int segment, bool isI4X4)
    {
        short[] coeffs = _coeffs;
        Array.Clear(coeffs, 0, coeffs.Length);

        int first;
        int acType;
        if (!isI4X4)
        {
            // Y2 块的 16 个 DC 系数（§13.2）。
            Span<short> dc = _dcTmp;
            dc.Clear();
            int ctx = _mbNzDc[mbX] + _leftNzDc;
            int nz = GetCoeffs(br, 1, ctx, _y2Dc[segment], _y2Ac[segment], 0, dc);
            _mbNzDc[mbX] = _leftNzDc = nz > 0 ? 1 : 0;

            if (nz > 1)
            {
                Vp8Idct.TransformWHT(dc, coeffs);
            }
            else
            {
                // 只有 DC：内联简化变换。
                int dc0 = (dc[0] + 3) >> 3;
                for (int i = 0; i < 16 * 16; i += 16)
                    coeffs[i] = (short)dc0;
            }
            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3;
        }

        int tnz = _mbNz[mbX] & 0x0f;
        int lnz = _leftNz & 0x0f;
        uint nonZeroY = 0;
        int block = 0;

        for (int y = 0; y < 4; y++)
        {
            int l = lnz & 1;
            uint nzCoeffs = 0;
            for (int x = 0; x < 4; x++)
            {
                int ctx = l + (tnz & 1);
                int nz = GetCoeffs(br, acType, ctx, _y1Dc[segment], _y1Ac[segment], first,
                                  coeffs.AsSpan(block * 16, 16));
                l = nz > first ? 1 : 0;
                tnz = (tnz >> 1) | (l << 7);
                nzCoeffs = NzCodeBits(nzCoeffs, nz, coeffs[block * 16] != 0);
                block++;
            }
            tnz >>= 4;
            lnz = (lnz >> 1) | (l << 7);
            nonZeroY = (nonZeroY << 8) | nzCoeffs;
        }

        int outTopNz = tnz;
        int outLeftNz = lnz >> 4;

        uint nonZeroUv = 0;
        block = 16;
        for (int ch = 0; ch < 4; ch += 2)
        {
            uint nzCoeffs = 0;
            tnz = _mbNz[mbX] >> (4 + ch);
            lnz = _leftNz >> (4 + ch);
            for (int y = 0; y < 2; y++)
            {
                int l = lnz & 1;
                for (int x = 0; x < 2; x++)
                {
                    int ctx = l + (tnz & 1);
                    int nz = GetCoeffs(br, 2, ctx, _uvDc[segment], _uvAc[segment], 0,
                                      coeffs.AsSpan(block * 16, 16));
                    l = nz > 0 ? 1 : 0;
                    tnz = (tnz >> 1) | (l << 3);
                    nzCoeffs = NzCodeBits(nzCoeffs, nz, coeffs[block * 16] != 0);
                    block++;
                }
                tnz >>= 2;
                lnz = (lnz >> 1) | (l << 5);
            }
            nonZeroUv |= nzCoeffs << (4 * ch);
            outTopNz |= (tnz << 4) << ch;
            outLeftNz |= (lnz & 0xf0) << ch;
        }

        _mbNz[mbX] = outTopNz;
        _leftNz = outLeftNz;
        _nonZeroY[mbX] = nonZeroY;
        _nonZeroUv[mbX] = nonZeroUv;

        return (nonZeroY | nonZeroUv) == 0 ? 1 : 0;
    }

    // ==================================================================
    //  宏块解码与重建
    // ==================================================================

    private void DecodeMacroblock(Vp8BoolDecoder tokenBr, int mbX, int mbY)
    {
        bool isI4X4 = _isI4X4OfMb[mbX];
        int segment = _segmentOfMb[mbX];
        int skip = _useSkipProba ? _skipOfMb[mbX] : 0;
        int skipped;

        if (skip != 0)
        {
            _leftNz = _mbNz[mbX] = 0;
            if (!isI4X4)
                _leftNzDc = _mbNzDc[mbX] = 0;
            _nonZeroY[mbX] = 0;
            _nonZeroUv[mbX] = 0;
            skipped = 1;
        }
        else
        {
            skipped = ParseResiduals(tokenBr, mbX, segment, isI4X4);
        }

        // f_inner = i4x4 || 本宏块存在非零系数
        _fInnerOfMb[mbX] = isI4X4 || skipped == 0;

        ReconstructMacroblock(mbX, mbY);
    }

    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode == 0) // B_DC_PRED
        {
            if (mbX == 0)
                return mbY == 0 ? 6 : 5; // NOTOPLEFT / NOLEFT
            return mbY == 0 ? 4 : 0;     // NOTOP / 正常 DC
        }
        return mode;
    }

    /// <summary>
    /// 重建一个宏块（frame_dec.c ReconstructRow 中单个宏块的部分）。
    /// 在持久化的 recon 平面上做帧内预测 + 逆变换；预测读取的相邻像素始终是未滤波的重建值。
    /// </summary>
    private void ReconstructMacroblock(int mbX, int mbY)
    {
        int strideY = _strideReconY;
        int yOff = (Pad + mbY * 16) * strideY + Pad + mbX * 16;
        short[] coeffs = _coeffs;
        uint bits = _nonZeroY[mbX];

        if (_isI4X4OfMb[mbX])
        {
            PrepareTopRight(mbX, mbY, yOff);
            for (int n = 0; n < 16; n++, bits <<= 2)
            {
                int off = yOff + _kScan[n];
                Vp8IntraPred.Pred4(_imodes[mbX * 16 + n], _reconY, off, strideY);
                DoTransform(bits, coeffs, n * 16, _reconY, off, strideY);
            }
        }
        else
        {
            int pred = CheckMode(mbX, mbY, _imodes[mbX * 16]);
            int mode = pred >= 4 ? 0 : pred;
            bool hasTop = pred != 4 && pred != 6;
            bool hasLeft = pred != 5 && pred != 6;
            Vp8IntraPred.Pred16(mode, hasTop, hasLeft, _reconY, yOff, strideY);
            if (bits != 0)
            {
                for (int n = 0; n < 16; n++, bits <<= 2)
                    DoTransform(bits, coeffs, n * 16, _reconY, yOff + _kScan[n], strideY);
            }
        }

        int strideUv = _strideReconUv;
        int uvOff = (Pad + mbY * 8) * strideUv + Pad + mbX * 8;
        int uvPred = CheckMode(mbX, mbY, _uvModeOfMb[mbX]);
        int uvMode = uvPred >= 4 ? 0 : uvPred;
        bool uvHasTop = uvPred != 4 && uvPred != 6;
        bool uvHasLeft = uvPred != 5 && uvPred != 6;
        Vp8IntraPred.Pred8(uvMode, uvHasTop, uvHasLeft, _reconU, uvOff, strideUv);
        Vp8IntraPred.Pred8(uvMode, uvHasTop, uvHasLeft, _reconV, uvOff, strideUv);

        uint bitsUv = _nonZeroUv[mbX];
        DoUvTransform(bitsUv & 0xff, coeffs, 256, _reconU, uvOff, strideUv);
        DoUvTransform((bitsUv >> 8) & 0xff, coeffs, 320, _reconV, uvOff, strideUv);
    }

    /// <summary>
    /// 对应 frame_dec.c 中 i4x4 分支对 <c>top_right</c> 的处理：
    /// 右上 4 个样本取自上一宏块行的对应像素（最右列宏块则复制本行第 15 个样本），
    /// 并把这 4 个样本复制到宏块内第 3、7、11 行（4×4 子块右上边界）的同一列偏移处。
    /// </summary>
    private void PrepareTopRight(int mbX, int mbY, int yOff)
    {
        int strideY = _strideReconY;
        byte v0, v1, v2, v3;
        if (mbY > 0)
        {
            int src = yOff - strideY + 16;
            if (mbX >= _mbW - 1)
            {
                byte edge = _reconY[yOff - strideY + 15];
                v0 = v1 = v2 = v3 = edge;
            }
            else
            {
                v0 = _reconY[src];
                v1 = _reconY[src + 1];
                v2 = _reconY[src + 2];
                v3 = _reconY[src + 3];
            }

            // C 版本先写 top_right[0..3]（上一行第 16..19 列）。非末尾宏块写回的是同一批像素，
            // 幂等；末尾宏块则把上一行右侧 padding 归一化为边缘像素，否则会读到陈旧值。
            _reconY[src] = v0;
            _reconY[src + 1] = v1;
            _reconY[src + 2] = v2;
            _reconY[src + 3] = v3;
        }
        else
        {
            // 首个宏块行：C 依赖上边界统一 memset 的 127（AllocatePlanes 已填充）。
            v0 = v1 = v2 = v3 = 127;
        }

        // C 版本：top_right[BPS] = top_right[2*BPS] = top_right[3*BPS] = top_right[0]，
        // top_right 指向“上一行第 16 列”，uint32 下标 BPS 步进即跨 4 行，
        // 因此目标是本宏块的第 3、7、11 行（不是第 0、4、8 行）。
        for (int k = 1; k <= 3; k++)
        {
            int dst = yOff + (4 * k - 1) * strideY + 16;
            _reconY[dst + 0] = v0; _reconY[dst + 1] = v1; _reconY[dst + 2] = v2; _reconY[dst + 3] = v3;
        }
    }

    /// <summary>对应 frame_dec.c DoTransform：按 non_zero 码的两位选择逆变换变体。</summary>
    private static void DoTransform(uint bits, short[] coeffs, int coeffOff, byte[] dst, int off, int stride)
    {
        switch (bits >> 30)
        {
            case 3:
                Vp8Idct.TransformOne(coeffs.AsSpan(coeffOff, 16), dst, off, stride);
                break;
            case 2:
                Vp8Idct.TransformAc3(coeffs.AsSpan(coeffOff, 16), dst, off, stride);
                break;
            case 1:
                Vp8Idct.TransformDc(coeffs.AsSpan(coeffOff, 16), dst, off, stride);
                break;
            default:
                break;
        }
    }

    /// <summary>对应 frame_dec.c DoUVTransform：8 位 non_zero 码决定是否以及如何叠加色度残差。</summary>
    private static void DoUvTransform(uint bits, short[] coeffs, int coeffOff, byte[] dst, int off, int stride)
    {
        if ((bits & 0xff) == 0)
            return;
        if ((bits & 0xaa) != 0)
            Vp8Idct.TransformUv(coeffs.AsSpan(coeffOff, 64), dst, off, stride);
        else
            Vp8Idct.TransformDcUv(coeffs.AsSpan(coeffOff, 64), dst, off, stride);
    }

    private void CopyRowToOutput(int mbY)
    {
        int srcY = (Pad + mbY * 16) * _strideReconY + Pad;
        int dstY = mbY * 16 * _strideOutY;
        int rowBytes = _mbW * 16;
        for (int j = 0; j < 16; j++)
            Array.Copy(_reconY, srcY + j * _strideReconY, _outY, dstY + j * _strideOutY, rowBytes);

        int srcUv = (Pad + mbY * 8) * _strideReconUv + Pad;
        int dstUv = mbY * 8 * _strideOutUv;
        int uvRowBytes = _mbW * 8;
        for (int j = 0; j < 8; j++)
        {
            Array.Copy(_reconU, srcUv + j * _strideReconUv, _outU, dstUv + j * _strideOutUv, uvRowBytes);
            Array.Copy(_reconV, srcUv + j * _strideReconUv, _outV, dstUv + j * _strideOutUv, uvRowBytes);
        }
    }

    /// <summary>
    /// 对刚输出的宏块行做环内滤波（frame_dec.c FilterRow / DoFilter）。
    /// 在 out 平面上原位滤波：上方相邻行已是上一宏块行滤波后的结果，与 libwebp 的
    /// cache_y 环形缓存语义等价。
    /// </summary>
    private void FilterRow(int mbY)
    {
        if (_filterType == 0)
            return;

        int yBase = mbY * 16 * _strideOutY;
        int uvBase = mbY * 8 * _strideOutUv;

        for (int mbX = 0; mbX < _mbW; mbX++)
        {
            int isI4X4 = _isI4X4OfMb[mbX] ? 1 : 0;
            int segment = _segmentOfMb[mbX];
            int limit = _fLimit[segment, isI4X4];
            if (limit == 0)
                continue;

            int ilevel = _fILevel[segment, isI4X4];
            int hevThresh = _fHev[segment, isI4X4];
            bool inner = _fInner[segment, isI4X4] || _fInnerOfMb[mbX];

            int offY = yBase + mbX * 16;
            int offUv = uvBase + mbX * 8;

            if (_filterType == 1)
            {
                if (mbX > 0)
                    Vp8LoopFilter.HFilter16Simple(_outY, offY, _strideOutY, limit + 4);
                if (inner)
                    Vp8LoopFilter.HFilter16Simplei(_outY, offY, _strideOutY, limit);
                if (mbY > 0)
                    Vp8LoopFilter.VFilter16Simple(_outY, offY, _strideOutY, limit + 4);
                if (inner)
                    Vp8LoopFilter.VFilter16Simplei(_outY, offY, _strideOutY, limit);
            }
            else
            {
                if (mbX > 0)
                {
                    Vp8LoopFilter.HFilter16(_outY, offY, _strideOutY, limit + 4, ilevel, hevThresh);
                    Vp8LoopFilter.HFilter8(_outU, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
                    Vp8LoopFilter.HFilter8(_outV, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
                }
                if (inner)
                {
                    Vp8LoopFilter.HFilter16i(_outY, offY, _strideOutY, limit, ilevel, hevThresh);
                    Vp8LoopFilter.HFilter8i(_outU, offUv, _strideOutUv, limit, ilevel, hevThresh);
                    Vp8LoopFilter.HFilter8i(_outV, offUv, _strideOutUv, limit, ilevel, hevThresh);
                }
                if (mbY > 0)
                {
                    Vp8LoopFilter.VFilter16(_outY, offY, _strideOutY, limit + 4, ilevel, hevThresh);
                    Vp8LoopFilter.VFilter8(_outU, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
                    Vp8LoopFilter.VFilter8(_outV, offUv, _strideOutUv, limit + 4, ilevel, hevThresh);
                }
                if (inner)
                {
                    Vp8LoopFilter.VFilter16i(_outY, offY, _strideOutY, limit, ilevel, hevThresh);
                    Vp8LoopFilter.VFilter8i(_outU, offUv, _strideOutUv, limit, ilevel, hevThresh);
                    Vp8LoopFilter.VFilter8i(_outV, offUv, _strideOutUv, limit, ilevel, hevThresh);
                }
            }
        }
    }

    private Vp8DecodedImage CropOutput()
    {
        byte[] y = new byte[_width * _height];
        for (int r = 0; r < _height; r++)
            Array.Copy(_outY, r * _strideOutY, y, r * _width, _width);

        int cw = (_width + 1) / 2;
        int ch = (_height + 1) / 2;
        byte[] u = new byte[cw * ch];
        byte[] v = new byte[cw * ch];
        for (int r = 0; r < ch; r++)
        {
            Array.Copy(_outU, r * _strideOutUv, u, r * cw, cw);
            Array.Copy(_outV, r * _strideOutUv, v, r * cw, cw);
        }

        return new Vp8DecodedImage(_width, _height, y, u, v);
    }
}
