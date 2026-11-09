using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 托管 VP8 有损编码器的单元测试与一致性测试。
/// 主判据：同一码流在 libwebp 与托管解码器下必须逐像素完全一致（不接受近似阈值）。
/// </summary>
public class WebpVp8EncoderTests
{
    // ==================================================================
    //  回归：partition0 降级路径的量化档位
    // ==================================================================

    [Fact]
    public void Configure_DisabledSegmentation_UsesBaseQuantizers()
    {
        // 超大图（约 >6500² 宏块模式信息超 19 位 partition0）走降级配置：
        // Configure(false, false) 关闭分段后，所有宏块 _currentSegment=0，
        // 量化表必须回到基础 q。若仍叠加 _segDelta[0]（+2），整图比预期粗两档，
        // 色度 DC 预测在平坦区的偏差（约 ±10~12）落入量化死区被丢弃，残差无法
        // 修正，沿色度 DC 预测方向逐宏块传播，在白色背景上形成大面积半透明
        // 粉紫偏色（143MP 实测：右下角 V 均值 128→140）。
        var rgba = new byte[16 * 16 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
            rgba[i + 3] = 255;

        var encoder = new Vp8Encoder(rgba, 16, 16, baseQ: 88);
        var configure = typeof(Vp8Encoder).GetMethod("Configure",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        configure.Invoke(encoder, new object[] { false, false });

        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        int baseQ = (int)typeof(Vp8Encoder).GetField("_baseQ", flags)!.GetValue(encoder)!;
        var y1Dc = (int[])typeof(Vp8Encoder).GetField("_y1Dc", flags)!.GetValue(encoder)!;
        var y1Ac = (int[])typeof(Vp8Encoder).GetField("_y1Ac", flags)!.GetValue(encoder)!;
        var uvDc = (int[])typeof(Vp8Encoder).GetField("_uvDc", flags)!.GetValue(encoder)!;
        var uvAc = (int[])typeof(Vp8Encoder).GetField("_uvAc", flags)!.GetValue(encoder)!;

        int uvDcQ = Math.Clamp(baseQ + Vp8Encoder_UvDcDeltaQ, 0, 117);
        int uvAcQ = Math.Clamp(baseQ + Vp8Encoder_UvAcDeltaQ, 0, 127);
        Assert.Equal(Vp8Tables.DcQuantTable[baseQ], y1Dc[0]);
        Assert.Equal(Vp8Tables.AcQuantTable[baseQ], y1Ac[0]);
        Assert.Equal(Vp8Tables.DcQuantTable[uvDcQ], uvDc[0]);
        Assert.Equal(Vp8Tables.AcQuantTable[uvAcQ], uvAc[0]);
    }

    /// <summary>与 Vp8Encoder.UvDcDeltaQ / UvAcDeltaQ 常量保持一致（断言用）。</summary>
    private const int Vp8Encoder_UvDcDeltaQ = -2;
    private const int Vp8Encoder_UvAcDeltaQ = -4;

    // ==================================================================
    //  T0：bool 编码器 ↔ 解码器互逆
    // ==================================================================

    [Fact]
    public void BoolEncoder_RandomBits_RoundTripsWithDecoder()
    {
        var rng = new Random(1234567);
        const int count = 4096;
        int[] bits = new int[count];
        int[] probs = new int[count];
        int[] probSet = { 1, 8, 64, 128, 192, 250, 254, 255 };

        var enc = new Vp8BoolEncoder(256);
        for (int i = 0; i < count; i++)
        {
            bits[i] = rng.Next(2);
            probs[i] = probSet[rng.Next(probSet.Length)];
            enc.PutBit(bits[i], probs[i]);
        }

        byte[] bytes = enc.Finish();
        Assert.NotEmpty(bytes);

        var dec = new Vp8BoolDecoder(bytes);
        Assert.True(dec.IsValid);
        for (int i = 0; i < count; i++)
            Assert.Equal(bits[i], dec.GetBit(probs[i]));
        Assert.False(dec.Eof);
    }

    [Fact]
    public void BoolEncoder_LongRuns_RoundTrip()
    {
        // 全 0 / 全 1 的长游程会触发大量重归一化与 0xFF 延迟输出，专门覆盖进位路径。
        int[] probs = { 254, 1, 128, 250, 8 };
        foreach (int prob in probs)
        {
            foreach (int bit in new[] { 0, 1 })
            {
                var enc = new Vp8BoolEncoder(64);
                const int count = 8192;
                for (int i = 0; i < count; i++)
                    enc.PutBit(bit, prob);

                byte[] bytes = enc.Finish();
                var dec = new Vp8BoolDecoder(bytes);
                Assert.True(dec.IsValid);
                for (int i = 0; i < count; i++)
                    Assert.Equal(bit, dec.GetBit(prob));
            }
        }
    }

    [Fact]
    public void BoolEncoder_UniformLiterals_RoundTrip()
    {
        var rng = new Random(998877);
        for (int bitCount = 1; bitCount <= 12; bitCount++)
        {
            int[] values = new int[64];
            var enc = new Vp8BoolEncoder(64);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = rng.Next(1 << bitCount);
                enc.PutLiteral(values[i], bitCount);
            }

            byte[] bytes = enc.Finish();
            var dec = new Vp8BoolDecoder(bytes);
            for (int i = 0; i < values.Length; i++)
                Assert.Equal((uint)values[i], dec.GetValue(bitCount));
        }
    }

    // ==================================================================
    //  T0：正向变换定标与逆变换对齐
    // ==================================================================

    [Theory]
    [InlineData(7)]
    [InlineData(-7)]
    [InlineData(0)]
    [InlineData(33)]
    public void ForwardDct_ConstantBlock_DcIsEightTimesValue(int value)
    {
        Span<int> residual = stackalloc int[16];
        residual.Fill(value);
        Span<int> coeffs = stackalloc int[16];
        Vp8ForwardTransform.Dct4x4(residual, coeffs);

        // 伴随变换对常值块的 DC 定标：8×value，其余全 0。
        Assert.Equal(8 * value, coeffs[0]);
        for (int i = 1; i < 16; i++)
            Assert.Equal(0, coeffs[i]);

        // 逆变换的 DC 快路径：dc = 8·value + 4 → (dc >> 3) = value。
        byte[] dst = new byte[16];
        Array.Fill(dst, (byte)128);
        short[] c = new short[16];
        c[0] = (short)coeffs[0];
        Vp8Idct.TransformOne(c, dst, 0, 4);
        for (int i = 0; i < 16; i++)
            Assert.Equal(128 + value, dst[i]);
    }

    [Fact]
    public void ForwardWht_RandomDc_RoundTripsThroughInverseWht()
    {
        // 逆 WHT 的输出布局：block b 的 DC 槽位 = 64·(b>>2) + 16·(b&3)。
        // 正向 WHT 必须是逆变换的伴随，否则非零 AC 时 16 个块的 DC 会错位
        // （旧实现即在此处按块行 1↔2 互换，平图无法暴露）。
        var rng = new Random(20251103);
        for (int trial = 0; trial < 200; trial++)
        {
            int[] dc = new int[16];
            for (int i = 0; i < 16; i++)
                dc[i] = rng.Next(-1024, 1025);

            Span<int> coeffs = stackalloc int[16];
            Vp8ForwardTransform.Wht4x4(dc, coeffs);

            short[] input = new short[16];
            for (int i = 0; i < 16; i++)
                input[i] = (short)coeffs[i];
            short[] output = new short[256];
            Vp8Idct.TransformWHT(input, output);

            for (int block = 0; block < 16; block++)
            {
                int got = output[64 * (block >> 2) + 16 * (block & 3)];
                Assert.InRange(got, dc[block] - 1, dc[block] + 1);
            }
        }
    }

    [Fact]
    public void ForwardWht_SingleBlockDc_KeepsPosition()
    {
        // 只在块 (row=2, col=1) 有 DC：正向→逆向后该 DC 必须回到同一块，其它块为 0。
        int[] dc = new int[16];
        dc[2 * 4 + 1] = 96;
        Span<int> coeffs = stackalloc int[16];
        Vp8ForwardTransform.Wht4x4(dc, coeffs);

        short[] input = new short[16];
        for (int i = 0; i < 16; i++)
            input[i] = (short)coeffs[i];
        short[] output = new short[256];
        Vp8Idct.TransformWHT(input, output);

        for (int block = 0; block < 16; block++)
        {
            int got = output[64 * (block >> 2) + 16 * (block & 3)];
            Assert.Equal(block == 9 ? 96 : 0, got);
        }
    }

    [Fact]
    public void ForwardWht_ConstantDc_RoundTripsThroughInverseWht()
    {
        int[] values = { 37, -12, 5 };
        foreach (int d in values)
        {
            Span<int> dc = stackalloc int[16];
            dc.Fill(d);
            Span<int> coeffs = stackalloc int[16];
            Vp8ForwardTransform.Wht4x4(dc, coeffs);

            Assert.Equal(8 * d, coeffs[0]);
            for (int i = 1; i < 16; i++)
                Assert.Equal(0, coeffs[i]);

            short[] input = new short[16];
            for (int i = 0; i < 16; i++)
                input[i] = (short)coeffs[i];
            short[] output = new short[256];
            Vp8Idct.TransformWHT(input, output);

            for (int block = 0; block < 16; block++)
                Assert.Equal(d, (int)output[64 * (block >> 2) + 16 * (block & 3)]);
        }
    }

    [Fact]
    public void ForwardDct_RandomResidual_RoundTripsWithinIntegerRounding()
    {
        var rng = new Random(424242);
        for (int trial = 0; trial < 200; trial++)
        {
            int[] residual = new int[16];
            for (int i = 0; i < 16; i++)
                residual[i] = rng.Next(-28, 29);

            int[] coeffs = new int[16];
            Vp8ForwardTransform.Dct4x4(residual, coeffs);

            short[] c = new short[16];
            byte[] dst = new byte[16];
            Array.Fill(dst, (byte)128);
            for (int i = 0; i < 16; i++)
                c[i] = (short)coeffs[i];
            Vp8Idct.TransformOne(c, dst, 0, 4);

            for (int i = 0; i < 16; i++)
            {
                int expected = 128 + residual[i];
                Assert.InRange((int)dst[i], expected - 3, expected + 3);
            }
        }
    }

    // ==================================================================
    //  T1：托管 VP8 编码 → libwebp / 托管解码一致性
    // ==================================================================

    public static IEnumerable<object[]> EncodeCases()
    {
        (string name, int w, int h, float q)[] cases =
        {
            ("16x16", 16, 16, 75f),
            ("32x24", 32, 24, 75f),
            ("17x13", 17, 13, 75f),
            ("64x48-q95", 64, 48, 95f),
            ("64x48-q30", 64, 48, 30f),
            ("16x16-q20", 16, 16, 20f),
            ("16x16-q30", 16, 16, 30f),
            ("16x16-q40", 16, 16, 40f),
            ("16x16-q50", 16, 16, 50f),
            ("32x24-q30", 32, 24, 30f),
            ("32x24-q50", 32, 24, 50f),
        };
        foreach (var c in cases)
            yield return new object[] { c.name, c.w, c.h, c.q };
    }

    /// <summary>
    /// 主判据：托管编码器产出的码流必须能被真实 libwebp 解码，且同一码流在 libwebp 与托管
    /// 解码器下只允许 RGB ±1 的取整差异，alpha 必须一致。
    /// </summary>
    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void Vp8Encode_DecodesIdenticallyInLibWebpAndCSharp(
        string name, int w, int h, float quality)
    {
        byte[] rgba = MakePhoto(w, h);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, quality);

        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);

        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        Assert.Equal(w, mw);
        Assert.Equal(h, mh);

        int maxRgbDiff = 0, firstBad = -1;
        for (int i = 0; i < lib.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(lib[i], csharp[i]);
                continue;
            }

            int d = Math.Abs(lib[i] - csharp[i]);
            if (d > 1 && firstBad < 0) firstBad = i;
            maxRgbDiff = Math.Max(maxRgbDiff, d);
        }

        Assert.True(firstBad < 0,
            $"{name}: 同一码流两解码器 RGB 差超过 ±1（最大 {maxRgbDiff}，首个差异字节 {firstBad}）");

        // 保真下限：证明码流不是“两解码器一致地解出垃圾”。
        // 这一组是 16×16~64×48 的合成小图，色度被 4:2:0 降采样后本身就无法支撑高 PSNR
        // （真实照片的严格门槛见 Lossy_RealPhoto_MatchesLibWebpQualityAndSize，那里按
        //  q75/90/95 要求与 libwebp 的 PSNR 差 ≤2dB、体积 ≤1.5×）。
        double mse = 0;
        for (int i = 0; i < rgba.Length; i++)
        {
            if (i % 4 == 3) continue;
            double d = lib[i] - rgba[i];
            mse += d * d;
        }
        mse /= (w * h * 3);
        double psnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(mse, 1e-9));
        Assert.True(psnr >= 20.0, $"{name}: PSNR {psnr:F1}dB 低于 20dB，码流保真异常");
    }

    // ==================================================================
    //  T1b：真实照片与 libwebp 的质量/体积对拍（q75 / q90 / q95）
    // ==================================================================

    public static IEnumerable<object[]> RealPhotoLossyCases()
    {
        string[] files = { "Amish-Noka-Dresser.jpg", "5_star_base.jpg", "video-001.rgb.jpeg" };
        float[] qualities = { 75f, 90f, 95f };
        foreach (string file in files)
            foreach (float q in qualities)
                yield return new object[] { file, q };
    }

    /// <summary>
    /// 真实照片验收：同质量档位下，纯 C# 编码器的 PSNR 与 libwebp 差距 ≤2dB，体积 ≤1.5×。
    /// 质量一律用 libwebp 解码器测量（编码端问题不会被自家解码器掩盖）；
    /// 同一码流再用托管解码器解一次，要求与 libwebp 逐通道差 ≤1。
    /// </summary>
    [Theory]
    [MemberData(nameof(RealPhotoLossyCases))]
    public void Lossy_RealPhoto_MatchesLibWebpQualityAndSize(string file, float quality)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", file);
        var frame = ImageFrame.Load(path).ToRgba32();
        byte[] rgba = frame.Pixels;
        int w = frame.Width;
        int h = frame.Height;

        // 体积/画质对拍针对完整档（method ≥ 5）；快档的位流合法性由
        // Vp8Encode_DecodesIdenticallyInLibWebpAndCSharp 与下面的快档专项测试覆盖。
        byte[] ours = WebpEncoder.EncodeLossyRgba(rgba, w, h, quality, 6);
        byte[] reference = LibWebpTestCodec.EncodeRgba(rgba, w, h, quality);

        double ourPsnr = RgbPsnr(rgba, LibWebpTestCodec.DecodeRgba(ours, out _, out _), w, h);
        double libPsnr = RgbPsnr(rgba, LibWebpTestCodec.DecodeRgba(reference, out _, out _), w, h);

        Assert.True(libPsnr - ourPsnr <= 2.0,
            $"{file} q{quality}: PSNR {ourPsnr:F2}dB 比 libwebp {libPsnr:F2}dB 低 {libPsnr - ourPsnr:F2}dB（要求 ≤2dB）");
        Assert.True(ours.Length <= reference.Length * 1.5,
            $"{file} q{quality}: 体积 {ours.Length} 字节是 libwebp {reference.Length} 字节的 " +
            $"{(double)ours.Length / reference.Length:F2}×（要求 ≤1.5×）");

        // 自产码流的双解码器一致性：托管解码器与 libwebp 只允许 ±1 的取整差。
        byte[] csharpDecoded = WebpDecoder.DecodeRgba(ours, out int cw, out int ch);
        byte[] libDecoded = LibWebpTestCodec.DecodeRgba(ours, out int lw, out int lh);
        Assert.Equal(w, cw);
        Assert.Equal(h, ch);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        for (int i = 0; i < libDecoded.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(libDecoded[i], csharpDecoded[i]);
                continue;
            }
            Assert.InRange(csharpDecoded[i], libDecoded[i] - 1, libDecoded[i] + 1);
        }
    }

    /// <summary>
    /// 平滑渐变（q75）验收：这类内容在无环内滤波时块边界伪影显著（曾落后约 3.7dB），
    /// 用于锁定滤波写入帧头并被解码端应用的收益；判据与照片一致（≤2dB、≤1.5×）。
    /// </summary>
    [Fact]
    public void Lossy_SmoothGradient_MatchesLibWebpQuality()
    {
        const int w = 640;
        const int h = 480;
        const float quality = 75f;
        byte[] rgba = MakeSmoothGradient(w, h);

        byte[] ours = WebpEncoder.EncodeLossyRgba(rgba, w, h, quality, 6);
        byte[] reference = LibWebpTestCodec.EncodeRgba(rgba, w, h, quality);

        double ourPsnr = RgbPsnr(rgba, LibWebpTestCodec.DecodeRgba(ours, out _, out _), w, h);
        double libPsnr = RgbPsnr(rgba, LibWebpTestCodec.DecodeRgba(reference, out _, out _), w, h);

        Assert.True(libPsnr - ourPsnr <= 2.0,
            $"平滑渐变 q{quality}: PSNR {ourPsnr:F2}dB 比 libwebp {libPsnr:F2}dB 低 {libPsnr - ourPsnr:F2}dB（要求 ≤2dB）");
        Assert.True(ours.Length <= reference.Length * 1.5,
            $"平滑渐变 q{quality}: 体积 {ours.Length} 字节是 libwebp {reference.Length} 字节的 " +
            $"{(double)ours.Length / reference.Length:F2}×（要求 ≤1.5×）");

        byte[] csharpDecoded = WebpDecoder.DecodeRgba(ours, out int cw, out int ch);
        byte[] libDecoded = LibWebpTestCodec.DecodeRgba(ours, out int lw, out int lh);
        Assert.Equal(w, cw);
        Assert.Equal(h, ch);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        for (int i = 0; i < libDecoded.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(libDecoded[i], csharpDecoded[i]);
                continue;
            }
            Assert.InRange(csharpDecoded[i], libDecoded[i] - 1, libDecoded[i] + 1);
        }
    }

    /// <summary>
    /// 快档（method ≤ 4）只保证位流合法与画质，不承诺压缩率：
    /// 单遍 + 标量量化 + 查表滤波必须仍能被 libwebp 与托管解码器解出，
    /// 且 PSNR 与 libwebp 同档质量的差距不超过 2.1dB。
    ///
    /// 阈值 2.0 → 2.1 的记录：量化取整对齐 libwebp <c>kBiasMatrices</c> 后，这条
    /// 平滑渐变在 q75 的色度 DC 死区略增，实测 45.05 → 45.03dB（与 libwebp 差
    /// 2.02dB）。同一批改动换来的是平坦区色度不再逐块振荡：1024² 纯色/面板图
    /// q90 体积降到 1/4~1/27，见 docs/WebpEncoder.md §3.8。
    /// </summary>
    [Theory]
    [InlineData(75f)]
    [InlineData(90f)]
    public void Lossy_FastEffort_IsDecodableAndKeepsQuality(float quality)
    {
        const int w = 640, h = 480;
        byte[] rgba = MakeSmoothGradient(w, h);

        byte[] ours = WebpEncoder.EncodeLossyRgba(rgba, w, h, quality, 4);
        byte[] reference = LibWebpTestCodec.EncodeRgba(rgba, w, h, quality);

        double ourPsnr = RgbPsnr(rgba, LibWebpTestCodec.DecodeRgba(ours, out _, out _), w, h);
        double libPsnr = RgbPsnr(rgba, LibWebpTestCodec.DecodeRgba(reference, out _, out _), w, h);
        Assert.True(libPsnr - ourPsnr <= 2.1,
            $"快档 q{quality}: PSNR {ourPsnr:F2}dB 比 libwebp {libPsnr:F2}dB 低 {libPsnr - ourPsnr:F2}dB");

        byte[] csharpDecoded = WebpDecoder.DecodeRgba(ours, out int cw, out int ch);
        byte[] libDecoded = LibWebpTestCodec.DecodeRgba(ours, out int lw, out int lh);
        Assert.Equal(w, cw);
        Assert.Equal(h, ch);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        for (int i = 0; i < libDecoded.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(libDecoded[i], csharpDecoded[i]);
                continue;
            }
            Assert.InRange(csharpDecoded[i], libDecoded[i] - 1, libDecoded[i] + 1);
        }
    }

    // ==================================================================
    //  回归：平坦内容的色度 DC 极限环（逐块买 token 反向过冲）
    // ==================================================================

    /// <summary>
    /// 纯色图在若干档 q 上曾出现「每个 4×4 色度块都写一个 DC token」的振荡：
    /// 预测取相邻块的已重建值，残差落在量化死区边界时，trellis 的系数域失真
    /// 看不到「重建只能落在整数像素格上」，于是逐块反向过冲 ±1（或 ±2），
    /// 1024² 纯色可以从 1.8KB 涨到 4.2KB 且 PSNR 更低。
    ///
    /// 判据两条，都对准这个机制而不是体积阈值：
    /// 1. 色度非零系数只允许出现在「建立初始值」的首个宏块（8 个 4×4 块），
    ///    其余宏块必须全部 skip；
    /// 2. 解码结果必须有单一主值覆盖 ≥ 98% 的像素——振荡会把平坦区撕成两半
    ///    （覆盖率跌到 50% 量级），而量化粒度与环内滤波只影响边缘过渡（实测
    ///    99.4%+）。
    ///
    /// 这里**不用**整帧通道极差做判据：VP8 首宏块的绝对 DC 与环内滤波本来就会
    /// 在平坦区留下 4~5 个灰度级的过渡。实测 256² 纯色 RGB(176,96,64) 在 q78：
    /// 我方解码平面是 Y=116(源 116)、U=101(源 102)、V=166/163(源 165)，R 通道
    /// 极差 5 全部来自色度 DC 单电平（步长 25 ≈ 3 个灰度级）与 1.596× 的 R 放大；
    /// libwebp 在同一 q、同一输入下 R 极差**同样**是 5（主值 175，散点 172/174/177），
    /// 极差判据会把两者都判成回归。
    /// </summary>
    [Theory]
    [InlineData(70f)]
    [InlineData(74f)]
    [InlineData(78f)]
    [InlineData(90f)]
    [InlineData(92f)]
    [InlineData(95f)]
    public void Lossy_SolidColor_DoesNotOscillateInChroma(float quality)
    {
        const int w = 256, h = 256;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 176;
            rgba[i + 1] = 96;
            rgba[i + 2] = 64;
            rgba[i + 3] = 255;
        }

        var diagnostics = new Vp8Encoder.Vp8EncodeDiagnostics();
        Vp8Encoder.Encode(rgba, w, h, quality, 6, diagnostics);

        Assert.True(diagnostics.CoefficientsByType[2] <= 16,
            $"q{quality}: 色度非零系数 {diagnostics.CoefficientsByType[2]} 个（上限 16），" +
            $"纯色出现逐块振荡；诊断 {diagnostics}");

        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, quality, 6);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);

        Span<int> hist = stackalloc int[256];
        for (int c = 0; c < 3; c++)
        {
            hist.Clear();
            for (int i = c; i < decoded.Length; i += 4)
                hist[decoded[i]]++;

            int mode = 0;
            for (int v = 0; v < 256; v++)
                if (hist[v] > hist[mode]) mode = v;

            double coverage = hist[mode] * 100.0 / (w * h);
            Assert.True(coverage >= 98.0,
                $"q{quality}: 通道 {c} 主值 {mode} 只覆盖 {coverage:F2}% 的像素，" +
                $"平坦区被拆成了多个平台（色度逐块振荡）");
        }
    }

    private static byte[] MakeSmoothGradient(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / (w - 1));
                rgba[o + 1] = (byte)(y * 255 / (h - 1));
                rgba[o + 2] = (byte)((x + y) * 255 / (w + h - 2));
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    private static double RgbPsnr(byte[] a, byte[] b, int w, int h)
    {
        double mse = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            for (int c = 0; c < 3; c++)
            {
                double d = a[o + c] - b[o + c];
                mse += d * d;
            }
        }
        mse /= w * h * 3;
        return mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte[] MakePhoto(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                double nx = (double)x / Math.Max(1, w - 1);
                double ny = (double)y / Math.Max(1, h - 1);
                rgba[o] = (byte)Math.Clamp(90 + 130 * nx + 20 * Math.Sin(9 * nx * Math.PI), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(70 + 150 * ny + 25 * Math.Cos(7 * ny * Math.PI), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(120 + 80 * (nx + ny) / 2 + 30 * Math.Sin(5 * (nx + ny) * Math.PI), 0, 255);
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }
}
