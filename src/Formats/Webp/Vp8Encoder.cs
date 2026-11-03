namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 关键帧编码器（WebP 有损格式）。
///
/// 本阶段（baseline）实现：
/// <list type="bullet">
/// <item>16×16 帧内预测（DC / TM / V / H），按预测 SSE 选择模式；</item>
/// <item>4×4 正向 DCT + Y2 正向 WHT，标量与 <see cref="Vp8Idct"/> 严格互逆；</item>
/// <item>固定 q index（由外部 quality 单调映射），无概率更新；</item>
/// <item>intra 模式下全平面的残差 token 编码（EOB / 零游程 / 类别码）；</item>
/// <item>使用解码器完全相同的逆变换变体做重建，保证后续宏块预测与解码端一致；</item>
/// <item>环内滤波关闭（filter level = 0）。</item>
/// </list>
///
/// 位流布局：<c>[frame tag 3][start code 3][w 2][h 2][partition0][token partition]</c>，
/// 单 token 分区，因此不写分区大小表。输出为 <c>VP8 </c> chunk 的载荷（不含 chunk 头）。
///
/// 实例持有整帧缓冲区，非线程安全；每次编码创建独立实例。
/// </summary>
internal sealed class Vp8Encoder
{
    /// <summary>重建平面的四周 padding，与解码端一致（左 1 列 = 129，上空 8 行 = 127）。</summary>
    private const int Pad = 8;

    /// <summary>VP8 宽度/高度字段为 14 位。</summary>
    internal const int MaxDimension = 16383;

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

    private readonly int _baseQ;
    private readonly int _y1Dc;
    private readonly int _y1Ac;
    private readonly int _y2Dc;
    private readonly int _y2Ac;
    private readonly int _uvDc;
    private readonly int _uvAc;

    private readonly Vp8BoolEncoder _part0;
    private readonly Vp8BoolEncoder _tokens;

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

    private bool _anyCoefficient;
    private int _y2Nz;

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

        InitReconBoundaries();
        Vp8Yuv.ToYuv420(rgba, width, height, _srcY, _srcU, _srcV);

        int q = Math.Clamp(baseQ, 0, 127);
        _baseQ = q;
        _y1Dc = Vp8Tables.DcQuantTable[q];
        _y1Ac = Vp8Tables.AcQuantTable[q];
        _y2Dc = _y1Dc * 2;
        _y2Ac = Math.Max(8, (Vp8Tables.AcQuantTable[q] * 101581) >> 16);
        _uvDc = Vp8Tables.DcQuantTable[Math.Clamp(q, 0, 117)];
        _uvAc = Vp8Tables.AcQuantTable[q];

        _part0 = new Vp8BoolEncoder(1024);
        _tokens = new Vp8BoolEncoder(1024);

        _mbNz = new int[_mbW];
        _mbNzDc = new int[_mbW];
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
        return encoder.Run();
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

    private byte[] Run()
    {
        WriteGlobalHeader();

        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            for (int mbX = 0; mbX < _mbW; mbX++)
                EncodeMacroblock(mbX, mbY);

            // 对应解码端 VP8InitScanline：行末清空左侧 nz 上下文。
            _leftNz = 0;
            _leftNzDc = 0;
        }

        byte[] part0 = _part0.Finish();
        byte[] tokens = _tokens.Finish();
        return AssembleFrame(part0, tokens);
    }

    // ==================================================================
    //  全局头（RFC 6386 §9.2 - §9.9）
    // ==================================================================

    private void WriteGlobalHeader()
    {
        // key_frame=0, version=0, show_frame=0, first_partition_size 在拼帧时写入。
        _part0.PutBitUniform(0); // color_space
        _part0.PutBitUniform(0); // clamping_type

        // 分段：关闭（所有宏块使用 base_q）。
        _part0.PutBitUniform(0); // segmentation_enabled

        // 滤波：simple 标志位（level=0 时不生效），level=0，sharpness=0，无 delta。
        _part0.PutBitUniform(0); // filter_type
        _part0.PutLiteral(0, 6); // loop_filter_level
        _part0.PutLiteral(0, 3); // sharpness_level
        _part0.PutBitUniform(0); // loop_filter_adj_enable

        _part0.PutLiteral(0, 2); // log2_nbr_of_dct_partitions = 1 个分区

        // 量化：base_q + 5 个 delta 标志（全 0）。
        _part0.PutLiteral(_baseQ, 7);
        for (int i = 0; i < 5; i++)
            _part0.PutBitUniform(0);

        _part0.PutBitUniform(0); // refresh_entropy_probs = 0

        // 1056 个 token 概率更新位：全 0，沿用默认概率表。
        // 注意：每位必须用 CoeffsUpdateProba 对应的概率编码，不能用等概率 128，
        // 否则解码端的 range/value 从这一节开始就与编码端失步（RFC 6386 §13.4）。
        var updateProba = Vp8Tables.CoeffsUpdateProba;
        for (int t = 0; t < 4; t++)
            for (int b = 0; b < 8; b++)
                for (int c = 0; c < 3; c++)
                    for (int p = 0; p < 11; p++)
                        _part0.PutBit(0, updateProba[t, b, c, p]);

        _part0.PutBitUniform(1); // mb_no_coeff_skip
        _part0.PutLiteral(128, 8); // prob_skip_false
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
        int yOff = (Pad + mbY * 16) * _strideY + Pad + mbX * 16;
        int uvOff = (Pad + mbY * 8) * _strideUv + Pad + mbX * 8;

        int yMode = ChooseMode16(mbX, mbY, yOff);
        int uvMode = ChooseMode8(mbX, mbY, uvOff);

        _anyCoefficient = false;
        PrepareLumaResidual(mbX, mbY, yOff);
        PrepareChromaResidual(mbX, mbY, uvOff);

        bool skip = !_anyCoefficient;

        WriteMacroblockModes(yMode, uvMode, skip);
        if (skip)
        {
            _leftNz = 0;
            _mbNz[mbX] = 0;
            _leftNzDc = 0;
            _mbNzDc[mbX] = 0;
        }
        else
        {
            WriteResidualTokens(mbX);
        }
    }

    /// <summary>16×16 亮度预测模式选择（0=DC, 1=TM, 2=V, 3=H），结束时预测已写入重建平面。</summary>
    private int ChooseMode16(int mbX, int mbY, int yOff)
    {
        bool hasTop = mbY > 0;
        bool hasLeft = mbX > 0;
        int best = 0;
        long bestSse = long.MaxValue;

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
            ComputeResidualY(mbX, mbY, block, _residual);
            for (int i = 0; i < 16; i++)
                sse += (long)_residual[i] * _residual[i];
        }
        return sse;
    }

    private long SseChroma(int mbX, int mbY)
    {
        long sse = 0;
        for (int ch = 0; ch < 2; ch++)
        {
            for (int block = 0; block < 4; block++)
            {
                ComputeResidualUv(mbX, mbY, ch, block, _residual);
                for (int i = 0; i < 16; i++)
                    sse += (long)_residual[i] * _residual[i];
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
            int dq = n == 0 ? _y2Dc : _y2Ac;
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
                int q = Quantize(_lumaCoef[dst + raster], _y1Ac);
                _lumaQuant[dst + n] = q;
                _lumaSlots[dst + raster] = (short)(q * _y1Ac);
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
                    int dq = n == 0 ? _uvDc : _uvAc;
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

    private void WriteMacroblockModes(int yMode, int uvMode, bool skip)
    {
        _part0.PutBit(skip ? 1 : 0, 128); // prob_skip_false 固定 128

        // is_i4x4：写 1 表示 16×16。
        _part0.PutBit(1, 145);

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

        _mbNz[mbX] = outTopNz;
        _leftNz = outLeftNz;
    }

    /// <summary>
    /// 按解码端 GetCoeffs 的逆过程写一个 4×4 块的量化系数（扫描序）。
    /// </summary>
    private static void WriteCoeffs(Vp8BoolEncoder bw, int type, int ctx, int first, ReadOnlySpan<int> scan)
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
                bw.PutBit(0, Prob(type, band, curCtx, 0));
                return;
            }

            bw.PutBit(1, Prob(type, band, curCtx, 0));

            // 零游程：每遇到 0 写一个 p[1]=0；遇到非零写 p[1]=1。
            while (scan[n] == 0)
            {
                bw.PutBit(0, Prob(type, band, curCtx, 1));
                n++;
                band = bands[n];
                curCtx = 0;
            }
            bw.PutBit(1, Prob(type, band, curCtx, 1));

            int value = scan[n];
            int magnitude = Math.Abs(value);
            int nextCtx;
            if (magnitude == 1)
            {
                bw.PutBit(0, Prob(type, band, curCtx, 2));
                nextCtx = 1;
            }
            else
            {
                bw.PutBit(1, Prob(type, band, curCtx, 2));
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
    private static void PutLargeValue(Vp8BoolEncoder bw, int type, int band, int ctx, int magnitude)
    {
        if (magnitude <= 4)
        {
            bw.PutBit(0, Prob(type, band, ctx, 3));
            if (magnitude == 2)
            {
                bw.PutBit(0, Prob(type, band, ctx, 4));
            }
            else
            {
                bw.PutBit(1, Prob(type, band, ctx, 4));
                bw.PutBit(magnitude == 4 ? 1 : 0, Prob(type, band, ctx, 5));
            }
            return;
        }

        bw.PutBit(1, Prob(type, band, ctx, 3));

        if (magnitude <= 10)
        {
            bw.PutBit(0, Prob(type, band, ctx, 6));
            if (magnitude <= 6)
            {
                bw.PutBit(0, Prob(type, band, ctx, 7));
                bw.PutBit(magnitude == 6 ? 1 : 0, 159);
            }
            else
            {
                bw.PutBit(1, Prob(type, band, ctx, 7));
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

        bw.PutBit(1, Prob(type, band, ctx, 6));

        int cat = magnitude <= 18 ? 0 : magnitude <= 34 ? 1 : magnitude <= 66 ? 2 : 3;
        int bit1 = cat >= 2 ? 1 : 0;
        bw.PutBit(bit1, Prob(type, band, ctx, 8));
        bw.PutBit(cat & 1, Prob(type, band, ctx, 9 + bit1));

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

    private static int Prob(int type, int band, int ctx, int index) =>
        Vp8Tables.CoeffsProba0[type, band, ctx, index];

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

    private void ComputeResidualUv(int mbX, int mbY, int ch, int block, int[] residual)
    {
        int bx = (block & 1) * 4;
        int by = (block >> 1) * 4;
        int baseX = mbX * 8 + bx;
        int baseY = mbY * 8 + by;
        int reconOff = (Pad + mbY * 8) * _strideUv + Pad + mbX * 8 + by * _strideUv + bx;
        byte[] src = ch == 0 ? _srcU : _srcV;
        byte[] recon = ch == 0 ? _reconU : _reconV;

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
