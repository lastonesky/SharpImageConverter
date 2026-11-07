using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Streaming;

/// <summary>
/// 纯 C# WebP 流式逐行解码（<see cref="WebpRowSource"/>）验收：
/// 逐行结果与整图解码逐像素一致；短读流 / 截断流 / 并发场景行为正确。
/// </summary>
public class WebpStreamingDecodeTests
{
    private static string NewTemp(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);

    [Fact]
    public void LosslessWithAlpha_RowOutputMatchesFullDecode()
    {
        int w = 37, h = 29;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LossyWithAlpha_RowOutputMatchesFullDecode()
    {
        int w = 41, h = 27;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 80f);
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LossyOpaque_RowOutputMatchesFullDecode_AndUsesThreeChannels()
    {
        int w = 33, h = 25;
        byte[] rgba = GradientRgba(w, h, withAlpha: false);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 80f);
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void ShortReadNonSeekableStream_ProducesSameRows()
    {
        int w = 35, h = 23;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
        byte[] expected = WebpDecoder.DecodeRgba(webp, out int ew, out int eh);
        Assert.Equal(w, ew);
        Assert.Equal(h, eh);

        using var shortStream = new ShortReadStream(webp, maxPerRead: 3);
        using var rows = new WebpRowSource(shortStream);
        Assert.Equal(w, rows.Width);
        Assert.Equal(h, rows.Height);
        Assert.Equal(4, rows.Channels);

        var row = new byte[rows.Width * rows.Channels];
        for (int y = 0; y < h; y++)
        {
            Assert.True(rows.ReadRow(row));
            Assert.Equal(expected.AsSpan(y * w * 4, w * 4).ToArray(), row);
        }
        Assert.False(rows.ReadRow(row));
    }

    [Fact]
    public void TruncatedStream_ThrowsInvalidData()
    {
        int w = 20, h = 16;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
        byte[] truncated = webp.AsSpan(0, webp.Length / 2).ToArray();

        using var stream = new MemoryStream(truncated);
        Assert.Throws<InvalidDataException>(() => new WebpRowSource(stream));
    }

    [Fact]
    public void EmptyStream_ThrowsInvalidData()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => new WebpRowSource(stream));
    }

    [Fact]
    public void RowBufferTooSmall_ThrowsArgumentException()
    {
        int w = 8, h = 8;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        using var rows = new WebpRowSource(new MemoryStream(webp));
        var tooSmall = new byte[rows.Width * rows.Channels - 1];
        Assert.Throws<ArgumentException>(() => rows.ReadRow(tooSmall));
    }

    [Fact]
    public void ConcurrentInstances_AreIsolated()
    {
        int w = 30, h = 20;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        byte[] expected = WebpDecoder.DecodeRgba(webp, out _, out _);

        string path = NewTemp(".webp");
        try
        {
            File.WriteAllBytes(path, webp);

            var outcomes = new bool[8];
            Parallel.For(0, outcomes.Length, i =>
            {
                using var rows = new WebpRowSource(path);
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Assert.True(rows.ReadRow(row));
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        if (row[x * 4] != expected[o] || row[x * 4 + 1] != expected[o + 1]
                            || row[x * 4 + 2] != expected[o + 2] || row[x * 4 + 3] != expected[o + 3])
                        {
                            outcomes[i] = false;
                            return;
                        }
                    }
                }
                outcomes[i] = true;
            });

            Assert.All(outcomes, ok => Assert.True(ok, "并发流式解码结果与整图解码不一致"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnimatedWebp_IsRejectedExplicitly()
    {
        int w = 20, h = 16;
        var frame = new WebpAnimationFrameInput(new byte[w * h * 4], 50);
        byte[] webp = WebpAnimationWriter.Encode(new[] { frame }, w, h, 0,
            new WebpAnimationEncodeOptions(Lossless: true));

        using var stream = new MemoryStream(webp);
        Assert.Throws<WebpUnsupportedException>(() => new WebpRowSource(stream));
    }

    [Fact]
    public void IsSupported_DetectsWebpByHeader()
    {
        byte[] rgba = GradientRgba(12, 9, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, 12, 9);
        string path = NewTemp(".webp");
        string bogus = NewTemp(".bin");
        try
        {
            File.WriteAllBytes(path, webp);
            File.WriteAllBytes(bogus, new byte[] { 1, 2, 3, 4 });
            Assert.True(WebpRowSource.IsSupported(path));
            Assert.False(WebpRowSource.IsSupported(bogus));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(bogus)) File.Delete(bogus);
        }
    }

    [Fact]
    public void LibWebpLossless_PhotoLike_RowOutputMatchesFullDecode()
    {
        int w = 257, h = 129;
        byte[] rgba = PhotoLikeRgba(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LibWebpLossless_PaletteImage_RowOutputMatchesFullDecode()
    {
        int w = 97, h = 61;
        byte[] rgba = PaletteRgba(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    /// <summary>
    /// P0：窗口化 VP8 解码器（17/9 行重建窗口 + 32/16 行输出窗口）与整帧解码器
    /// 已在滤波阶段最终化的输出行必须逐字节一致。覆盖奇数/偶数尺寸、宏块行边界与非边界。
    /// </summary>
    [Fact]
    public void Vp8WindowedDecoder_OutputPlanesMatchFullDecode()
    {
        (int W, int H)[] sizes =
        [
            (1, 1), (2, 2), (3, 3), (15, 17), (16, 16), (17, 15),
            (33, 65), (64, 64), (65, 33), (127, 61),
        ];

        foreach (var (w, h) in sizes)
        {
            byte[] rgba = PhotoLikeRgba(w, h);
            byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
            byte[] payload = ExtractVp8Payload(webp);

            var full = Vp8Decoder.Decode(payload);
            var windowed = Vp8Decoder.CreateWindowed(payload);
            windowed.BeginRowStream();

            int chromaWidth = (w + 1) / 2;
            int chromaHeight = (h + 1) / 2;

            while (windowed.HasMoreMacroblockRows)
            {
                windowed.DecodeNextMacroblockRow();
                int k = windowed.CurrentMacroblockRow;

                // 本次滤波后新最终化的行：亮度 16(k-1)..16k、色度 8(k-1)..8k。
                // 更早的行在上一次迭代中已交付/校验，窗口平移后不再驻留。
                int lumaStart = k == 0 ? 0 : 16 * (k - 1);
                int lumaEnd = Math.Min(16 * k, h - 1);
                for (int r = lumaStart; r <= lumaEnd; r++)
                {
                    int windowRow = MapWindowRow(r, k, 16);
                    for (int x = 0; x < w; x++)
                    {
                        Assert.True(full.Y[r * w + x] == windowed.OutY[windowRow * windowed.StrideOutY + x],
                            $"Y mismatch w={w} h={h} k={k} r={r} x={x}");
                    }
                }

                int chromaStart = k == 0 ? 0 : 8 * (k - 1);
                int chromaEnd = Math.Min(8 * k, chromaHeight - 1);
                for (int c = chromaStart; c <= chromaEnd; c++)
                {
                    int windowRow = MapWindowRow(c, k, 8);
                    for (int x = 0; x < chromaWidth; x++)
                    {
                        Assert.True(full.U[c * chromaWidth + x] == windowed.OutU[windowRow * windowed.StrideOutUv + x],
                            $"U mismatch w={w} h={h} k={k} c={c} x={x}");
                        Assert.True(full.V[c * chromaWidth + x] == windowed.OutV[windowRow * windowed.StrideOutUv + x],
                            $"V mismatch w={w} h={h} k={k} c={c} x={x}");
                    }
                }

                if (windowed.HasMoreMacroblockRows)
                    windowed.SlideWindows();
            }
        }
    }

    /// <summary>P1：Vp8RowDecoder 逐行 RGBA 输出与整帧解码 + fancy upsampling 逐字节一致。</summary>
    [Fact]
    public void Vp8RowDecoder_RgbaMatchesFullDecode()
    {
        (int W, int H)[] sizes =
        [
            (1, 1), (2, 2), (3, 3), (15, 17), (16, 16), (17, 15),
            (33, 65), (64, 64), (65, 33), (127, 61), (256, 129),
        ];

        foreach (var (w, h) in sizes)
        {
            byte[] rgba = PhotoLikeRgba(w, h);
            byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
            VerifyVp8RowDecoderMatchesFull(webp, w, h);
        }
    }

    /// <summary>
    /// §9.1 质量矩阵：q ∈ {10, 30, 50, 75, 95} × 奇数/偶数/非 16 倍数/1×N/N×1 尺寸，
    /// 逐行 RGBA 输出与整帧路径必须 diff = 0。
    /// </summary>
    [Fact]
    public void Vp8RowDecoder_QualityMatrix_RgbaMatchesFullDecode()
    {
        (int W, int H)[] sizes =
        [
            (1, 1), (1, 3), (3, 1), (15, 17), (17, 15), (16, 16),
            (17, 16), (16, 17), (17, 17), (31, 33), (33, 31), (100, 37),
        ];
        float[] qualities = [10f, 30f, 50f, 75f, 95f];

        foreach (float q in qualities)
        {
            foreach (var (w, h) in sizes)
            {
                byte[] rgba = PhotoLikeRgba(w, h);
                byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, q);
                VerifyVp8RowDecoderMatchesFull(webp, w, h);
            }
        }
    }

    /// <summary>
    /// §9.1：simple / complex 环内滤波 × token 分区数 1/2/4/8 × 奇数与非 16 倍数尺寸。
    /// 码流由 libwebp 显式配置生成（自身编码器固定输出 complex + 单分区）。
    /// </summary>
    [Fact]
    public void Vp8RowDecoder_FilterAndPartitionMatrix_RgbaMatchesFullDecode()
    {
        (int W, int H)[][] sizes =
        [
            [(17, 17)], [(33, 65)], [(64, 48)], [(100, 37)],
        ];

        foreach (var group in sizes)
        {
            foreach (var (w, h) in group)
            {
                byte[] rgba = PhotoLikeRgba(w, h);
                foreach (int filterType in new[] { 0, 1 })            // 0 = simple, 1 = complex
                {
                    foreach (int partitions in new[] { 0, 1, 2, 3 })  // log2(分区数)
                    {
                        foreach (float q in new[] { 30f, 75f })
                        {
                            byte[] webp = LibWebpTestCodec.EncodeWithConfig(
                                rgba, w, h, w * 4, hasAlpha: true, q, method: 4, lossless: false, exact: false,
                                filterType: filterType, partitions: partitions);
                            VerifyVp8RowDecoderMatchesFull(webp, w, h);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// §9.2：高滤波强度 + 竖直强梯度（单像素条纹），强制 VFilter 改写上一宏块行最后 4 行，
    /// 验证滤波延迟足够。
    /// </summary>
    [Fact]
    public void Vp8RowDecoder_StrongEdgeGradient_RgbaMatchesFullDecode()
    {
        (int W, int H)[] sizes = [(65, 49), (17, 33), (3, 35)];
        foreach (var (w, h) in sizes)
        {
            byte[] rgba = StripeRgba(w, h);
            foreach (int sharpness in new[] { 0, 4, 7 })
            {
                byte[] webp = LibWebpTestCodec.EncodeWithConfig(
                    rgba, w, h, w * 4, hasAlpha: true, 20f, method: 4, lossless: false, exact: false,
                    filterType: 1, filterStrength: 63, filterSharpness: sharpness);
                VerifyVp8RowDecoderMatchesFull(webp, w, h);
            }
        }
    }

    /// <summary>§9.1：1080p 与 4K 的真实尺寸对拍（无 alpha）。</summary>
    [Fact]
    public void Vp8RowDecoder_LargeImages_RgbaMatchesFullDecode()
    {
        foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
        {
            byte[] rgba = PhotoLikeRgba(w, h);
            byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
            VerifyVp8RowDecoderMatchesFull(webp, w, h);
        }
    }

    /// <summary>§9.1：16383×1 与 1×16383 的极端长宽比。</summary>
    [Fact]
    public void Vp8RowDecoder_ExtremeThinImages_RgbaMatchesFullDecode()
    {
        foreach (var (w, h) in new[] { (16383, 1), (1, 16383) })
        {
            byte[] rgba = PhotoLikeRgba(w, h);
            byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
            VerifyVp8RowDecoderMatchesFull(webp, w, h);
        }
    }

    /// <summary>
    /// P3 容器级 alpha 矩阵：libwebp 的 alpha 压缩开关 × 四种滤波（none/horizontal/vertical/gradient
    /// 由 alpha_filtering 0/1/2 与预处理组合覆盖），行源输出与整图解码逐像素一致。
    /// </summary>
    [Fact]
    public void LossyAlpha_LibWebpFilterModes_RowOutputMatchesFullDecode()
    {
        int w = 129, h = 37;
        byte[] rgba = AlphaGradient4(w, h);

        foreach (int alphaCompression in new[] { 0, 1 })
        {
            foreach (int alphaFiltering in new[] { 0, 1, 2 })
            {
                foreach (int preprocessing in new[] { 0, 1 })
                {
                    byte[] webp = LibWebpTestCodec.EncodeWithConfig(
                        rgba, w, h, w * 4, hasAlpha: true, 60f, method: 4, lossless: false, exact: false,
                        alphaCompression: alphaCompression, alphaFiltering: alphaFiltering,
                        preprocessing: preprocessing);
                    VerifyStreamingMatchesFull(webp, rgba, w, h);
                }
            }
        }
    }

    private static void VerifyVp8RowDecoderMatchesFull(byte[] webp, int w, int h)
    {
        byte[] payload = ExtractVp8Payload(webp);

        var full = Vp8Decoder.Decode(payload);
        byte[] expected = Vp8YuvToRgb.ConvertToRgba(
            full.Y, full.U, full.V, w, h, w, (w + 1) / 2);

        var rowDecoder = new Vp8RowDecoder(payload);
        Assert.Equal(w, rowDecoder.Width);
        Assert.Equal(h, rowDecoder.Height);

        byte[] row = new byte[checked(w * 4)];
        for (int y = 0; y < h; y++)
        {
            Assert.True(rowDecoder.TryReadRgbaRow(row));
            for (int i = 0; i < w * 4; i++)
            {
                Assert.True(row[i] == expected[y * w * 4 + i],
                    $"RGBA mismatch w={w} h={h} row={y} offset={i} (x={i / 4}, c={i & 3})");
            }
        }
        Assert.False(rowDecoder.TryReadRgbaRow(row));
    }

    /// <summary>P3：ALPH 原始字节 + 四种逆滤波的行增量输出与整图解码一致。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AlphRowSource_RawFilters_MatchExpected(int filter)
    {
        (int W, int H)[] sizes = [(1, 1), (2, 3), (17, 9), (64, 33)];
        foreach (var (w, h) in sizes)
        {
            byte[] alpha = AlphaGradient(w, h);
            byte[] alphH = BuildRawAlph(alpha, w, h, filter);
            var expected = AlphDecoder.Decode(alphH, w, h);
            var source = new AlphRowSource(alphH, w, h);
            byte[] row = new byte[w];
            for (int y = 0; y < h; y++)
            {
                Assert.True(source.TryReadRow(row));
                for (int x = 0; x < w; x++)
                    Assert.True(row[x] == expected[y * w + x],
                        $"alpha mismatch filter={filter} w={w} h={h} y={y} x={x}");
            }
            Assert.False(source.TryReadRow(row));
        }
    }

    /// <summary>P3：真实 ALPH（VP8L 压缩 + 编码器选择的滤波）逐行输出与整图解码一致。</summary>
    [Fact]
    public void AlphRowSource_Vp8lAlphaRows_MatchFullDecoder()
    {
        (int W, int H)[] sizes = [(1, 1), (3, 2), (17, 33), (64, 65), (129, 47)];
        foreach (var (w, h) in sizes)
        {
            byte[] rgba = AlphaGradient4(w, h);
            byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
            byte[] alph = ExtractChunkPayload(webp, "ALPH", require: true);
            byte[] expected = AlphDecoder.Decode(alph, w, h);

            var source = new AlphRowSource(alph, w, h);
            byte[] row = new byte[w];
            for (int y = 0; y < h; y++)
            {
                Assert.True(source.TryReadRow(row));
                for (int x = 0; x < w; x++)
                    Assert.True(row[x] == expected[y * w + x],
                        $"alpha mismatch w={w} h={h} y={y} x={x}");
            }
            Assert.False(source.TryReadRow(row));
        }
    }

    private static byte[] AlphaGradient(int width, int height)
    {
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                alpha[y * width + x] = (byte)((x * 7 + y * 11) & 0xFF);
        return alpha;
    }

    private static byte[] AlphaGradient4(int width, int height)
    {
        byte[] rgb = PhotoLikeRgba(width, height);
        byte[] alpha = AlphaGradient(width, height);
        for (int i = 0; i < width * height; i++)
            rgb[i * 4 + 3] = alpha[i];
        return rgb;
    }

    /// <summary>按指定滤波方式对 alpha 做前向滤波，生成未压缩 ALPH chunk 数据。</summary>
    private static byte[] BuildRawAlph(byte[] alpha, int width, int height, int filter)
    {
        var data = new byte[1 + width * height];
        data[0] = (byte)(filter << 2); // compression=0, preprocessing=0, reserved=0
        Span<byte> residual = data.AsSpan(1);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            int prevStart = rowStart - width;

            if (filter == 0)
            {
                for (int x = 0; x < width; x++)
                    residual[rowStart + x] = alpha[rowStart + x];
                continue;
            }

            if (y == 0 || filter == 1)
            {
                // 首行（以及 horizontal 的每一行）都以「上一行首像素」为预测起点。
                int pred = y == 0 ? 0 : alpha[prevStart];
                for (int x = 0; x < width; x++)
                {
                    int idx = rowStart + x;
                    residual[idx] = (byte)(alpha[idx] - pred);
                    pred = alpha[idx];
                }
                continue;
            }

            if (filter == 2)
            {
                for (int x = 0; x < width; x++)
                    residual[rowStart + x] = (byte)(alpha[rowStart + x] - alpha[prevStart + x]);
                continue;
            }

            // gradient（y > 0）
            residual[rowStart] = (byte)(alpha[rowStart] - alpha[prevStart]);
            for (int x = 1; x < width; x++)
            {
                int idx = rowStart + x;
                int pred = alpha[idx - 1] + alpha[prevStart + x] - alpha[prevStart + x - 1];
                if (pred < 0) pred = 0;
                else if (pred > 255) pred = 255;
                residual[idx] = (byte)(alpha[idx] - pred);
            }
        }
        return data;
    }

    private static byte[] ExtractChunkPayload(byte[] webp, string id, bool require)
    {
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            uint size = BitConverter.ToUInt32(webp, pos + 4);
            int next = pos + 8 + (int)size + (int)(size & 1);
            if (webp[pos] == id[0] && webp[pos + 1] == id[1] &&
                webp[pos + 2] == id[2] && webp[pos + 3] == id[3])
            {
                return webp.AsSpan(pos + 8, (int)size).ToArray();
            }
            if (next <= pos || next > webp.Length) break;
            pos = next;
        }
        if (require) throw new InvalidDataException($"未找到 {id} chunk");
        return Array.Empty<byte>();
    }

    private static int MapWindowRow(int absoluteRow, int mbY, int rowsPerMacroblock) =>
        absoluteRow >= rowsPerMacroblock * mbY
            ? rowsPerMacroblock + absoluteRow - rowsPerMacroblock * mbY
            : absoluteRow - rowsPerMacroblock * (mbY - 1);

    private static byte[] ExtractVp8Payload(byte[] webp)
    {
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            uint size = BitConverter.ToUInt32(webp, pos + 4);
            int next = pos + 8 + (int)size + (int)(size & 1);
            if (webp[pos] == 'V' && webp[pos + 1] == 'P' && webp[pos + 2] == '8' && webp[pos + 3] == ' ')
                return webp.AsSpan(pos + 8, (int)size).ToArray();
            if (next <= pos || next > webp.Length) break;
            pos = next;
        }
        throw new InvalidDataException("未找到 VP8 chunk");
    }

    [Fact]
    public void LosslessTruncatedInsideVp8LChunk_ThrowsInvalidData()
    {
        int w = 33, h = 27;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        byte[] truncated = webp.AsSpan(0, webp.Length - 9).ToArray();

        using var stream = new MemoryStream(truncated);
        Assert.Throws<InvalidDataException>(() => new WebpRowSource(stream));
    }

    /// <summary>
    /// VP8L 行源只需读到 VP8L chunk 结束即可，不应消费随后的元数据/填充 chunk。
    /// 这里人为追加一个大 chunk，验证读取字节数停在主图像之后。
    /// </summary>
    [Fact]
    public void LosslessSource_StopsReadingAfterVp8LChunk()
    {
        int w = 32, h = 24;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] baseWebp = WebpEncoder.EncodeRgba(rgba, w, h);

        int imageEnd = FindVp8LEnd(baseWebp);
        Assert.True(imageEnd > 12, "未找到 VP8L chunk");

        byte[] combined = new byte[imageEnd + 8 + 4096];
        Array.Copy(baseWebp, combined, imageEnd);
        combined[imageEnd] = (byte)'J';
        combined[imageEnd + 1] = (byte)'U';
        combined[imageEnd + 2] = (byte)'N';
        combined[imageEnd + 3] = (byte)'K';
        BitConverter.TryWriteBytes(combined.AsSpan(imageEnd + 4, 4), 4096);
        var riffSize = combined.Length - 8;
        BitConverter.TryWriteBytes(combined.AsSpan(4, 4), riffSize);

        using var counting = new CountingStream(combined);
        using var rows = new WebpRowSource(counting);
        var row = new byte[rows.Width * rows.Channels];
        while (rows.ReadRow(row)) { }

        Assert.True(counting.BytesRead <= imageEnd,
            $"行源读取了 {counting.BytesRead} 字节，超过 VP8L chunk 结束位置 {imageEnd}");
    }

    /// <summary>
    /// 长图驻留验证：逐行解码 4096×4096 无损图时，托管分配量（压缩负载 + 环形历史 +
    /// 行缓冲）应显著小于整帧 RGBA 体积的 20%。测量用 GC.GetAllocatedBytesForCurrentThread，
    /// 编码与源图像分配都在测量区间之外。
    /// </summary>
    [Fact]
    public void LosslessLongImage_StreamingAllocationsUnderBudget()
    {
        const int w = 4096;
        const int h = 4096;
        byte[] rgba = GradientRgba(w, h, withAlpha: false);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));

        long frameBytes = (long)w * h * 4;

        // 对照组：整帧解码的托管分配（工作区 + 输出）。
        long beforeFull = GC.GetAllocatedBytesForCurrentThread();
        _ = WebpDecoder.DecodeRgba(webp, out _, out _);
        long fullAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeFull;

        long before = GC.GetAllocatedBytesForCurrentThread();

        int decodedRows;
        using (var rows = new WebpRowSource(new MemoryStream(webp)))
        {
            var row = new byte[rows.Width * rows.Channels];
            decodedRows = 0;
            while (rows.ReadRow(row)) decodedRows++;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[streaming-alloc] frame={frameBytes} full={fullAllocated} streaming={allocated} " +
                          $"ratio={((double)allocated / fullAllocated):P1} compressed={webp.Length}");
        Assert.Equal(h, decodedRows);
        Assert.True(allocated < frameBytes / 5,
            $"流式解码托管分配 {allocated} 字节，超过整帧 {frameBytes} 字节的 20%");
        Assert.True(allocated < fullAllocated / 5,
            $"流式解码托管分配 {allocated} 字节，未显著低于整帧解码的 {fullAllocated} 字节");
    }

    private static int FindVp8LEnd(byte[] webp)
    {
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            uint size = BitConverter.ToUInt32(webp, pos + 4);
            int next = pos + 8 + (int)size + (int)(size & 1);
            if (webp[pos] == 'V' && webp[pos + 1] == 'P' && webp[pos + 2] == '8' && webp[pos + 3] == 'L')
                return Math.Min(next, webp.Length);
            if (next <= pos || next > webp.Length) return -1;
            pos = next;
        }
        return -1;
    }

    /// <summary>确定性“照片类”内容：平滑基色 + 高频噪声 + 变化的 alpha。</summary>
    private static byte[] PhotoLikeRgba(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        uint seed = 0x12345678u;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int o = (y * w + x) * 4;
                int r = (x * 255) / Math.Max(1, w - 1);
                int g = (y * 255) / Math.Max(1, h - 1);
                int b = ((x + y) * 3) & 0xFF;
                rgba[o] = (byte)((r + (int)(seed >> 27)) & 0xFF);
                rgba[o + 1] = (byte)((g + (int)((seed >> 19) & 0x1F)) & 0xFF);
                rgba[o + 2] = (byte)((b + (int)((seed >> 11) & 0x1F)) & 0xFF);
                rgba[o + 3] = (byte)(255 - (int)((seed >> 3) & 0x0F));
            }
        }
        return rgba;
    }

    /// <summary>单像素黑白竖直条纹：在 4×4 块内产生最陡的垂直/水平边界，压测环内滤波。</summary>
    private static byte[] StripeRgba(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte v = (byte)((x & 1) == 0 ? 0 : 255);
                rgba[o] = v;
                rgba[o + 1] = v;
                rgba[o + 2] = v;
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    /// <summary>只有 8 种颜色的图，用于触发 color indexing / 调色板路径。</summary>
    private static byte[] PaletteRgba(int w, int h)
    {
        ReadOnlySpan<uint> colors = [0xFF000000u, 0xFF0000FFu, 0xFF00FF00u, 0xFFFF0000u,
                                     0xFFFFFFFFu, 0xFF808080u, 0xFF00FFFFu, 0xFFFF00FFu];
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                uint c = colors[((x >> 2) + (y >> 3)) & 7];
                rgba[o] = (byte)(c >> 16 & 0xFF);
                rgba[o + 1] = (byte)(c >> 8 & 0xFF);
                rgba[o + 2] = (byte)(c & 0xFF);
                rgba[o + 3] = (byte)(c >> 24);
            }
        }
        return rgba;
    }

    private static void VerifyStreamingMatchesFull(byte[] webp, byte[] sourceRgba, int w, int h)
    {
        byte[] expected = WebpDecoder.DecodeRgba(webp, out int ew, out int eh);
        Assert.Equal(w, ew);
        Assert.Equal(h, eh);

        using var rows = new WebpRowSource(new MemoryStream(webp));
        Assert.Equal(w, rows.Width);
        Assert.Equal(h, rows.Height);

        var row = new byte[rows.Width * rows.Channels];
        for (int y = 0; y < h; y++)
        {
            Assert.True(rows.ReadRow(row));
            for (int x = 0; x < w; x++)
            {
                int full = (y * w + x) * 4;
                int r = x * rows.Channels;
                Assert.True(row[r] == expected[full] && row[r + 1] == expected[full + 1]
                    && row[r + 2] == expected[full + 2],
                    $"y={y} x={x}: RGB 行输出与整图解码不一致");
                if (rows.Channels == 4)
                    Assert.True(row[r + 3] == expected[full + 3], $"y={y} x={x}: alpha 行输出不一致");
            }
        }
        Assert.False(rows.ReadRow(row));
    }

    private static byte[] GradientRgba(int w, int h, bool withAlpha)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                rgba[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                rgba[o + 2] = (byte)((x * 3 + y * 5) & 0xFF);
                rgba[o + 3] = withAlpha ? (byte)((x * 7 + y * 11 + 30) & 0xFF) : (byte)255;
            }
        }
        return rgba;
    }

    /// <summary>统计实际被消费字节数的可定位流（用于验证流式读取的边界）。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public CountingStream(byte[] data) => _data = data;

        public int BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int n = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>每次 Read 最多返回 maxPerRead 字节的非可定位流，用于验证短读处理。</summary>
    private sealed class ShortReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxPerRead;
        private int _position;

        public ShortReadStream(byte[] data, int maxPerRead)
        {
            _data = data;
            _maxPerRead = maxPerRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int n = Math.Min(Math.Min(count, _maxPerRead), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
