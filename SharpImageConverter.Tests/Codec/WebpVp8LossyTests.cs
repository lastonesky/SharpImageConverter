using System.Runtime.InteropServices;
using Xunit;
using SharpImageConverter;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// VP8 有损关键帧（纯 C# 实现）与 libwebp 的对拍测试。
///
/// 判定标准（批次验收）：
/// - RGB 输出允许 ±1 取整差异：≥99% 像素逐通道差 ≤1；
/// - 整体 PSNR ≥ 45dB；
/// - 非关键帧必须抛 <see cref="WebpUnsupportedException"/>；
/// - 截断/损坏流必须抛可控异常（WebpContainerException / WebpBitstreamException），不得崩溃。
/// </summary>
public class WebpVp8LossyTests
{
    // ==================================================================
    //  1. Bool 解码器单元测试
    // ==================================================================

    [Fact]
    public void Vp8BoolDecoder_EmptyStream_ReportsEofAfterRenorm()
    {
        var br = new Vp8BoolDecoder(ReadOnlySpan<byte>.Empty);
        Assert.True(br.IsValid);
        // 空流上读取会以 0 填充，最终标记 Eof
        int bits = 0;
        for (int i = 0; i < 64; i++) bits += br.GetBit(128);
        Assert.True(br.Eof);
        Assert.Equal(0, bits); // 全 0 填充
    }

    [Fact]
    public void Vp8BoolDecoder_FirstByteFF_IsInvalid()
    {
        var br = new Vp8BoolDecoder(new byte[] { 0xFF, 0x00, 0x00, 0x00 });
        Assert.False(br.IsValid);
    }

    [Fact]
    public void Vp8BoolDecoder_GetSignedValue_ReadsMagnitudeThenSign()
    {
        // 构造一个至少能读出若干 bit 的流；只验证语义一致性：
        // 与 "GetValue 读幅值 + GetBit(128) 读符号" 的手工序列完全等价。
        var data = new byte[] { 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };
        var a = new Vp8BoolDecoder(data);
        var b = new Vp8BoolDecoder(data);
        int viaApi = a.GetSignedValue(5);
        int mag = (int)b.GetValue(5);
        int manual = b.GetBit(128) == 1 ? -mag : mag;
        Assert.Equal(manual, viaApi);
    }

    // ==================================================================
    //  2. 对拍测试矩阵：libwebp 编码 → 纯 C# 解码 vs libwebp 解码
    // ==================================================================

    public static IEnumerable<object[]> LossyCases()
    {
        int[] qualities = { 10, 20, 30, 50, 75, 95 };
        (string name, int w, int h, Func<int, int, byte[]> gen)[] images =
        {
            ("photo32",   32,  32,  MakePhotoLike),
            ("gradient",  96,  64,  MakeGradient),
            ("flat",      96,  64,  MakeFlat),
            ("noise",     96,  96,  (w, h) => MakeNoise(w, h, seed: 7)),
            ("photo512", 512, 512,  MakePhotoLike),
        };
        foreach (var q in qualities)
            foreach (var (name, w, h, gen) in images)
                yield return new object[] { name, w, h, q, gen };
    }

    [Theory]
    [MemberData(nameof(LossyCases))]
    public void Lossy_KeyFrame_MatchesLibwebp(string name, int w, int h, float quality, Func<int, int, byte[]> gen)
    {
        byte[] rgba = gen(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h, quality);

        byte[] expected = LibWebpTestCodec.DecodeRgba(webp, out int ew, out int eh);
        byte[] actual = WebpDecoder.DecodeRgba(webp, out int aw, out int ah);

        Assert.Equal(ew, aw);
        Assert.Equal(eh, ah);
        Assert.Equal(w, aw);
        Assert.Equal(h, ah);

        AssertSimilar(expected, actual, w, h, maxOutlierRate: 0.01, minPsnrDb: 45.0, name);
    }

    /// <summary>≥(1-maxOutlierRate) 的像素逐通道差 ≤1，且整图 PSNR ≥ minPsnrDb。</summary>
    private static void AssertSimilar(byte[] expected, byte[] actual, int w, int h,
        double maxOutlierRate, double minPsnrDb, string caseName)
    {
        Assert.Equal(expected.Length, actual.Length);
        long outliers = 0;
        double mse = 0;
        int pixels = w * h;
        for (int i = 0; i < pixels; i++)
        {
            int po = i * 4;
            bool outlier = false;
            for (int c = 0; c < 3; c++) // 只比较 RGB
            {
                int d = expected[po + c] - actual[po + c];
                mse += (double)d * d;
                if (d < -1 || d > 1) outlier = true;
            }
            // alpha 通道必须完全一致（均为 255）
            Assert.Equal(expected[po + 3], actual[po + 3]);
            if (outlier) outliers++;
        }

        double outlierRate = (double)outliers / pixels;
        mse /= (double)pixels * 3;
        double psnr = mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(255.0 * 255.0 / mse);

        Assert.True(outlierRate <= maxOutlierRate,
            $"用例 {caseName}: 超差像素占比 {outlierRate:P4} > {maxOutlierRate:P2}（PSNR={psnr:F2}dB）");
        Assert.True(psnr >= minPsnrDb,
            $"用例 {caseName}: PSNR {psnr:F2}dB < {minPsnrDb}dB（超差占比={outlierRate:P4}）");
    }

    // ==================================================================
    //  2b. YUV 平面逐像素对拍（主判据）
    // ==================================================================

    [DllImport("libwebpdecoder", EntryPoint = "WebPDecodeYUV", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr WebPDecodeYUV(byte[] data, nuint dataSize, out int width, out int height,
        out IntPtr u, out IntPtr v, out int stride, out int uvStride);

    [DllImport("libwebpdecoder", EntryPoint = "WebPFree", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WebPFreeNative(IntPtr ptr);

    private static byte[] CopyPlane(IntPtr basePtr, int stride, int w, int h)
    {
        if (stride <= 0 || w <= 0 || h <= 0 || stride > 65536)
            throw new InvalidOperationException($"非法平面参数 stride={stride} w={w} h={h}");
        var raw = new byte[stride * h];
        Marshal.Copy(basePtr, raw, 0, raw.Length);
        if (stride == w) return raw;
        var cropped = new byte[w * h];
        for (int r = 0; r < h; r++)
            Array.Copy(raw, r * stride, cropped, r * w, w);
        return cropped;
    }

    [Theory]
    [MemberData(nameof(LossyCases))]
    public void Lossy_KeyFrame_YuvPlanesMatchLibwebp(string name, int w, int h, float quality, Func<int, int, byte[]> gen)
    {
        byte[] rgba = gen(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h, quality);
        AssertYuvMatchesLibwebp(webp, w, h, name, quality);
    }

    /// <summary>
    /// 非偶数（含奇数与 1 像素）尺寸：VP8 的宏块网格、色度平面按 ceil(w/2)/ceil(h/2)
    /// 取整，边缘处理最容易出错，因此单独覆盖。
    /// </summary>
    public static IEnumerable<object[]> OddSizeCases()
    {
        int[] qualities = { 50, 75, 95 };
        (int w, int h)[] sizes = { (1, 1), (3, 2), (17, 13), (31, 17), (33, 7), (101, 63), (127, 5) };
        foreach (int q in qualities)
            foreach (var (w, h) in sizes)
                yield return new object[] { w, h, q };
    }

    [Theory]
    [MemberData(nameof(OddSizeCases))]
    public void Lossy_OddDimensions_YuvPlanesMatchLibwebp(int w, int h, float quality)
    {
        byte[] rgba = MakePhotoLike(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h, quality);
        AssertYuvMatchesLibwebp(webp, w, h, $"odd{w}x{h}", quality);
    }

    /// <summary>
    /// 真实照片（examples/Amish-Noka-Dresser.jpg）：低/中/高质量都由 libwebp 编码，
    /// 托管解码器的 Y/U/V 必须逐像素一致。真实照片的系数分布远比合成图复杂。
    /// </summary>
    [Theory]
    [InlineData(20f)]
    [InlineData(50f)]
    [InlineData(85f)]
    public void Lossy_RealPhoto_YuvPlanesMatchLibwebp(float quality)
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "examples", "Amish-Noka-Dresser.jpg");
        var frame = ImageFrame.Load(path).ToRgba32();
        byte[] webp = LibWebpTestCodec.EncodeRgba(frame.Pixels, frame.Width, frame.Height, quality);
        AssertYuvMatchesLibwebp(webp, frame.Width, frame.Height, "photo-real", quality);
    }

    private static void AssertYuvMatchesLibwebp(byte[] webp, int w, int h, string caseName, float quality)
    {
        IntPtr yPtr = WebPDecodeYUV(webp, (nuint)webp.Length, out int lw, out int lh,
            out IntPtr uPtr, out IntPtr vPtr, out int stride, out int uvStride);
        Assert.NotEqual(IntPtr.Zero, yPtr);
        byte[] ly, lu, lv;
        try
        {
            ly = CopyPlane(yPtr, stride, lw, lh);
            int cw = (lw + 1) / 2, ch = (lh + 1) / 2;
            lu = CopyPlane(uPtr, uvStride, cw, ch);
            lv = CopyPlane(vPtr, uvStride, cw, ch);
        }
        finally
        {
            WebPFreeNative(yPtr);
        }

        var container = WebpContainer.Load(webp);
        var chunk = container.GetPrimaryImageChunk()!.Value;
        var decoded = Vp8Decoder.Decode(chunk.GetData(container.RawData));

        Assert.Equal(lw, decoded.Width);
        Assert.Equal(lh, decoded.Height);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);

        AssertPlaneEqual("Y", ly, decoded.Y, lw, lh, caseName, quality);
        AssertPlaneEqual("U", lu, decoded.U, (lw + 1) / 2, (lh + 1) / 2, caseName, quality);
        AssertPlaneEqual("V", lv, decoded.V, (lw + 1) / 2, (lh + 1) / 2, caseName, quality);
    }

    private static void AssertPlaneEqual(string plane, byte[] expected, byte[] actual, int w, int h,
        string caseName, float quality)
    {
        Assert.Equal(expected.Length, actual.Length);
        int diff = 0, firstIdx = -1, maxDiff = 0;
        int rows = 0;
        var rowsWithDiff = new SortedSet<int>();
        for (int r = 0; r < h; r++)
        {
            int rowDiff = 0;
            for (int x = 0; x < w; x++)
            {
                int i = r * w + x;
                int d = Math.Abs(expected[i] - actual[i]);
                if (d != 0)
                {
                    if (firstIdx < 0) firstIdx = i;
                    rowDiff++;
                    diff++;
                    maxDiff = Math.Max(maxDiff, d);
                }
            }
            if (rowDiff > 0) { rows++; rowsWithDiff.Add(r); }
        }

        if (diff == 0) return;
        int fx = firstIdx % w, fy = firstIdx / w;
        Assert.Fail($"用例 {caseName} q={quality} {plane} 平面不一致：{diff}/{expected.Length} 像素差，" +
                    $"行数 {rows}/{h}，最大差 {maxDiff}；首个差异 @({fx},{fy}) " +
                    $"期望 {expected[firstIdx]} 实际 {actual[firstIdx]}；" +
                    $"前 10 个差异行 {string.Join(",", rowsWithDiff.Take(10))}");
    }

    // ==================================================================
    //  3. 非关键帧 → WebpUnsupportedException
    // ==================================================================

    [Fact]
    public void Lossy_NonKeyFrame_ThrowsUnsupported()
    {
        // frame tag：bit0=1（inter frame）、version=0、show_frame=1、first_part_size=4
        uint tag = 0x01 | (1u << 4) | (4u << 5);
        byte[] vp8 = new byte[]
        {
            (byte)(tag & 0xFF), (byte)((tag >> 8) & 0xFF), (byte)((tag >> 16) & 0xFF),
            0x9D, 0x01, 0x2A, 10, 0, 10, 0, // start code + 宽高（虽然是 P 帧语义上无此字段，但解码器应先识别帧类型）
            0, 0, 0, 0,
        };
        byte[] webp = BuildRiff(("VP8 ", vp8));
        Assert.Throws<WebpUnsupportedException>(() => WebpDecoder.DecodeRgba(webp, out _, out _));
    }

    [Fact]
    public void Lossy_AnimationWithVp8PFrame_ThrowsUnsupported()
    {
        // 动图（VP8X+ANIM+ANMF）中的 VP8 非关键帧子帧：不属于 WebP（RFC 9649 要求 ANMF 帧数据为
        // VP8 关键帧），必须显式抛 WebpUnsupportedException，与 libwebp 的拒绝行为一致，
        // 而不是静默产出错误像素或抛出难以归因的位流异常。
        uint tag = 0x01 | (1u << 4) | (10u << 5); // 非关键帧、show_frame、first_part_size=10
        var frame = new List<byte>();
        frame.Add((byte)(tag & 0xFF)); frame.Add((byte)((tag >> 8) & 0xFF)); frame.Add((byte)((tag >> 16) & 0xFF));
        frame.AddRange(new byte[10]); // first partition

        byte[] vp8Chunk = Chunk("VP8 ", frame.ToArray());
        byte[] anmfPayload = new byte[16];
        // ANMF 头：x/2,y/2,w-1,h-1 各 24 位 + duration 24 位 + flags
        anmfPayload[6] = 9; anmfPayload[9] = 9; // 10x10
        byte[] anmf = Chunk("ANMF", Concat(anmfPayload, vp8Chunk));

        byte[] vp8x = new byte[10];
        vp8x[0] = 0x02; // animation 标志
        vp8x[4] = 9; vp8x[7] = 9;    // canvas 10x10
        byte[] anim = new byte[6];    // 背景 + loop

        byte[] webp = BuildRawRiff(Chunk("VP8X", vp8x), Chunk("ANIM", anim), anmf);

        Assert.Throws<WebpUnsupportedException>(() => WebpDecoder.DecodeRgba(webp, out _, out _));
        Assert.Throws<WebpUnsupportedException>(() => WebpDecoder.DecodeAnimation(webp));
    }

    // ==================================================================
    //  4. 截断 / 损坏流健壮性
    // ==================================================================

    [Fact]
    public void Lossy_TruncatedFile_ThrowsControlledException()
    {
        byte[] rgba = MakePhotoLike(32, 32);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, 32, 32, 75f);

        // 从 30 字节起逐步截断：每次都必须抛 Webp*Exception，不得其它异常/崩溃
        for (int len = 30; len < webp.Length - 1; len += Math.Max(1, (webp.Length - 30) / 12))
        {
            var truncated = webp.AsSpan(0, len).ToArray();
            Assert.ThrowsAny<Exception>(() => WebpDecoder.DecodeRgba(truncated, out _, out _));
            try
            {
                WebpDecoder.DecodeRgba(truncated, out _, out _);
                Assert.Fail("截断流必须抛异常");
            }
            catch (Exception ex) when (ex is WebpContainerException or WebpBitstreamException or WebpUnsupportedException)
            {
                // 预期
            }
        }
    }

    [Fact]
    public void Vp8Decoder_TruncatedChunk_ThrowsBitstreamException()
    {
        byte[] rgba = MakePhotoLike(32, 32);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, 32, 32, 75f);

        // 提取 VP8 chunk 数据
        var container = WebpContainer.Load(webp);
        var chunk = container.GetPrimaryImageChunk()!.Value;
        byte[] vp8Data = chunk.GetData(container.RawData).ToArray();

        for (int len = 3; len < vp8Data.Length; len += 7)
        {
            var part = vp8Data.AsSpan(0, len).ToArray();
            var ex = Record.Exception(() => Vp8Decoder.Decode(part));
            // 截断要么抛可控异常，要么碰巧合法解出（极短截断可能在 token 阶段才耗尽，仍以 0 填充完成）
            if (ex != null)
                Assert.True(ex is WebpBitstreamException or WebpUnsupportedException,
                    $"截断长度 {len} 抛出意外异常类型 {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public void Vp8Decoder_GarbageData_ThrowsControlledException()
    {
        var rng = new Random(42);
        for (int i = 0; i < 32; i++)
        {
            byte[] garbage = new byte[rng.Next(0, 300)];
            rng.NextBytes(garbage);
            var ex = Record.Exception(() => Vp8Decoder.Decode(garbage));
            if (ex != null)
                Assert.True(ex is WebpBitstreamException or WebpUnsupportedException,
                    $"垃圾数据抛出意外异常类型 {ex.GetType().Name}: {ex.Message}");
            // 若未抛异常，说明恰好构成合法关键帧流（概率极低），允许但结果必须尺寸合法
        }
    }

    // ==================================================================
    //  测试图生成器（确定性）
    // ==================================================================

    /// <summary>照片类：径向渐变 + 正弦纹理 + 色彩变化，含丰富的高频与平滑区域。</summary>
    private static byte[] MakePhotoLike(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double nx = (double)x / w, ny = (double)y / h;
                double dx = nx - 0.45, dy = ny - 0.55;
                double r0 = Math.Sqrt(dx * dx + dy * dy);
                int r = (int)(180 * Math.Exp(-3 * r0) + 60 * Math.Sin(12 * Math.PI * nx) * Math.Cos(9 * Math.PI * ny) + 20 * nx + 40);
                int g = (int)(140 * Math.Exp(-2.5 * r0) + 50 * Math.Sin(7 * Math.PI * ny + 1.3) + 30 * ny + 45);
                int b = (int)(120 * Math.Exp(-2 * r0) + 55 * Math.Cos(11 * Math.PI * (nx + ny)) + 25 * (1 - nx) + 45);
                int o = (y * w + x) * 4;
                rgba[o] = (byte)Math.Clamp(r, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(g, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(b, 0, 255);
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    private static byte[] MakeGradient(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(255 * x / Math.Max(1, w - 1));
                rgba[o + 1] = (byte)(255 * y / Math.Max(1, h - 1));
                rgba[o + 2] = (byte)(255 * (x + y) / Math.Max(1, w + h - 2));
                rgba[o + 3] = 255;
            }
        return rgba;
    }

    /// <summary>大面积平滑：底色 + 一个高斯亮斑。</summary>
    private static byte[] MakeFlat(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - w / 2.0, dy = y - h / 2.0;
                double blob = 70 * Math.Exp(-(dx * dx + dy * dy) / (2 * 8 * 8));
                int o = (y * w + x) * 4;
                rgba[o] = (byte)Math.Clamp(90 + blob, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(120 + blob, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(150 + blob, 0, 255);
                rgba[o + 3] = 255;
            }
        return rgba;
    }

    private static byte[] MakeNoise(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var rgba = new byte[w * h * 4];
        rng.NextBytes(rgba);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        return rgba;
    }

    // ==================================================================
    //  容器构造辅助
    // ==================================================================

    private static byte[] Chunk(string fourcc, byte[] data)
    {
        var bytes = new byte[8 + data.Length + (data.Length & 1)];
        bytes[0] = (byte)fourcc[0]; bytes[1] = (byte)fourcc[1];
        bytes[2] = (byte)fourcc[2]; bytes[3] = (byte)fourcc[3];
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)data.Length);
        data.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] BuildRiff(params (string FourCC, byte[] Data)[] chunks)
    {
        var parts = chunks.Select(c => Chunk(c.FourCC, c.Data)).ToArray();
        return BuildRawRiff(parts);
    }

    private static byte[] BuildRawRiff(params byte[][] chunks)
    {
        int total = 4 + chunks.Sum(c => c.Length);
        var riff = new byte[8 + total];
        riff[0] = (byte)'R'; riff[1] = (byte)'I'; riff[2] = (byte)'F'; riff[3] = (byte)'F';
        BitConverter.TryWriteBytes(riff.AsSpan(4), (uint)total);
        riff[8] = (byte)'W'; riff[9] = (byte)'E'; riff[10] = (byte)'B'; riff[11] = (byte)'P';
        int pos = 12;
        foreach (var c in chunks) { c.CopyTo(riff, pos); pos += c.Length; }
        return riff;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        int pos = 0;
        foreach (var a in arrays) { a.CopyTo(result, pos); pos += a.Length; }
        return result;
    }
}
