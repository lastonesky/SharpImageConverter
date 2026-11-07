using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using SharpImageConverter.Core;
using SharpImageConverter;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 托管 VP8L 编码器（<see cref="WebpEncoder"/>）的合法性、round-trip 与压缩率回归。
///
/// 合法性口径：编码结果必须能被真实 libwebp 解码（<see cref="LibWebpTestCodec.DecodeRgba"/>），
/// 且与原始 RGBA 逐像素 bit-exact；同时用自己的托管解码器再解一遍做交叉验证。
/// </summary>
[Collection("WebpEncodePolicy")]
public class WebpLEncoderTests
{
    // ==================================================================
    //  阶段 A：literal-only
    // ==================================================================

    [Fact]
    public void StageA_LiteralOnly_RoundTrip_BitExact()
    {
        foreach (var sample in Samples())
        {
            AssertRoundTrip(sample.Rgba, sample.W, sample.H, Vp8LEncoderOptions.LiteralOnly,
                sample.Name + " (literal-only)");
        }
    }

    [Fact]
    public void StageA_LiteralOnly_OddSizes_RoundTrip_BitExact()
    {
        foreach (var (w, h) in new[] { (1, 1), (3, 7), (17, 15), (31, 33), (65, 1) })
        {
            var rgba = Photo(w, h, seed: w * h);
            AssertRoundTrip(rgba, w, h, Vp8LEncoderOptions.LiteralOnly, $"odd {w}x{h} (literal-only)");
        }
    }

    // ==================================================================
    //  阶段 B：全特性
    // ==================================================================

    [Fact]
    public void StageB_Full_RoundTrip_BitExact()
    {
        foreach (var sample in Samples())
        {
            AssertRoundTrip(sample.Rgba, sample.W, sample.H, Vp8LEncoderOptions.Full,
                sample.Name + " (full)");
        }
    }

    [Fact]
    public void StageB_Full_OddSizes_RoundTrip_BitExact()
    {
        foreach (var (w, h) in new[] { (1, 1), (5, 1), (9, 9), (17, 15), (33, 33), (127, 63) })
        {
            AssertRoundTrip(Photo(w, h, seed: w + h), w, h, Vp8LEncoderOptions.Full, $"odd {w}x{h} (full)");
        }
    }

    [Fact]
    public void StageB_Full_MaxDimension16384_RoundTrip_BitExact()
    {
        // VP8L 宽/高各 14 位且解码为 ReadBits(14)+1，规范允许的最大边长是 16384
        //（与有损 VP8 的 16383 上限不同）。解码端曾误用 16383 拒绝合法流。
        AssertRoundTrip(Photo(16384, 1, seed: 7), 16384, 1, Vp8LEncoderOptions.Full, "16384x1 (full)");
        AssertRoundTrip(Photo(1, 16384, seed: 11), 1, 16384, Vp8LEncoderOptions.Full, "1x16384 (full)");
    }

    [Fact]
    public void StageB_Full_AlphaVariants_RoundTrip_BitExact()
    {
        var transparentGradient = AlphaGradient(64, 48);
        AssertRoundTrip(transparentGradient, 64, 48, Vp8LEncoderOptions.Full, "alpha gradient (full)");

        var sparseAlpha = Photo(80, 60, seed: 99);
        for (int i = 0; i < 80 * 60; i++) sparseAlpha[i * 4 + 3] = (byte)(i % 5 == 0 ? 0 : 255);
        AssertRoundTrip(sparseAlpha, 80, 60, Vp8LEncoderOptions.Full, "sparse alpha (full)");

        var opaque = Photo(64, 64, seed: 7);
        for (int i = 0; i < 64 * 64; i++) opaque[i * 4 + 3] = 255;
        AssertRoundTrip(opaque, 64, 64, Vp8LEncoderOptions.Full, "opaque (full)");
    }

    /// <summary>
    /// 大于 512 像素的强线性相关图像：cross-color 的"整幅共用一张系数表"候选在
    /// 1 个 tile 覆盖不了整幅图时必须把系数重复铺满系数图像，否则会越界。
    /// </summary>
    [Fact]
    public void CrossColor_FullImageCandidate_LargerThanSingleTile_RoundTrip()
    {
        var gradient = Gradient(640, 480);
        AssertRoundTrip(gradient, 640, 480, Vp8LEncoderOptions.Full, "640x480 gradient (full)");
    }

    [Fact]
    public void ColorIndexing_RoundTrip_BitExact()
    {
        var samples = new (string Name, byte[] Rgba, int W, int H)[]
        {
            ("2 colors", TwoColor(64, 32), 64, 32),
            ("17 colors", ManyColors(64, 48, 17), 64, 48),
            ("256 colors", ManyColors(64, 48, 256), 64, 48),
            ("palette image", Palette(96, 64), 96, 64),
            ("photo (palette infeasible)", Photo(64, 64, seed: 21), 64, 64),
        };
        var combos = new[]
        {
            ("palette-only", new Vp8LEncoderOptions(false, false, false, useColorIndexing: true)),
            ("palette+lz77", new Vp8LEncoderOptions(false, false, true, useColorIndexing: true)),
            ("palette+cache+lz77", new Vp8LEncoderOptions(false, true, true, useColorIndexing: true)),
        };

        foreach (var sample in samples)
        {
            foreach (var (name, options) in combos)
            {
                AssertRoundTrip(sample.Rgba, sample.W, sample.H, options, $"{sample.Name} ({name})");
            }
        }
    }

    [Fact]
    public void FeatureIsolation_RoundTrip()
    {
        var combos = new[]
        {
            ("transforms-only", new Vp8LEncoderOptions(true, false, false)),
            ("cache-only", new Vp8LEncoderOptions(false, true, false)),
            ("lz77-only", new Vp8LEncoderOptions(false, false, true)),
            ("cache+lz77", new Vp8LEncoderOptions(false, true, true)),
        };

        foreach (var sample in Samples())
        {
            foreach (var (name, options) in combos)
            {
                AssertRoundTrip(sample.Rgba, sample.W, sample.H, options, $"{sample.Name} ({name})");
            }
        }
    }

    // ==================================================================
    //  libwebp 编码 -> 托管解码（回归确认既有能力）
    // ==================================================================

    [Fact]
    public void LibWebpLossless_DecoderRegression()
    {
        foreach (var sample in Samples())
        {
            byte[] webp = LibWebpTestCodec.EncodeLosslessRgbaForDispatch(sample.Rgba, sample.W, sample.H);

            byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
            Assert.Equal(sample.W, mw);
            Assert.Equal(sample.H, mh);

            // libwebp 无损编码默认 exact=0，会把 alpha=0 像素的 RGB 清零，
            // 因此这里以“同一个流、libwebp 解码器输出”为基准，逐一字节对比托管解码器。
            byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out _, out _);
            AssertPixelExact(lib, csharp, sample.Name + " libwebp->csharp");
        }
    }

    // ==================================================================
    //  RIFF 容器与 VP8L 头
    // ==================================================================

    [Fact]
    public void RiffContainer_HeaderSizesAndPadding()
    {
        byte[] rgba = Palette(97, 65);
        int w = 97, h = 65;
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);

        Assert.Equal((byte)'R', webp[0]);
        Assert.Equal((byte)'I', webp[1]);
        Assert.Equal((byte)'F', webp[2]);
        Assert.Equal((byte)'F', webp[3]);

        int riffSize = BitConverter.ToInt32(webp, 4);
        Assert.Equal(webp.Length - 8, riffSize);

        Assert.Equal((byte)'W', webp[8]);
        Assert.Equal((byte)'E', webp[9]);
        Assert.Equal((byte)'B', webp[10]);
        Assert.Equal((byte)'P', webp[11]);
        Assert.Equal((byte)'V', webp[12]);
        Assert.Equal((byte)'P', webp[13]);
        Assert.Equal((byte)'8', webp[14]);
        Assert.Equal((byte)'L', webp[15]);

        int chunkSize = BitConverter.ToInt32(webp, 16);
        int padding = chunkSize & 1;
        Assert.Equal(webp.Length - 20 - padding, chunkSize);
        if (padding != 0) Assert.Equal(0, webp[webp.Length - 1]);

        // VP8L 5 字节头：signature 0x2F；width-1 / height-1 为 14bit。
        Assert.Equal(0x2F, webp[20]);
        Assert.Equal(w - 1, BitConverter.ToInt32(webp, 21) & 0x3FFF);
        Assert.Equal((h - 1) & 0x3FFF, (BitConverter.ToInt32(webp, 21) >> 14) & 0x3FFF);
    }

    // ==================================================================
    //  性能剪枝与确定性
    // ==================================================================

    /// <summary>
    /// 候选剪枝（低色数 + 调色板已压到 ≤0.5 字节/像素时跳过空间变换搜索）
    /// 不得改变最终选择：把 <see cref="WebpEncoder"/> 的自动路径与
    /// "逐个候选全量编码后取最小"逐一对比，要求体积完全相同。
    /// </summary>
    [Fact]
    public void AutoSelection_MatchesFullCandidateEnumeration()
    {
        var samples = new (string Name, byte[] Rgba, int W, int H)[]
        {
            ("1 color", SolidImage(128, 128, 200, 30, 90, 255), 128, 128),
            ("8 colors", ManyColors(128, 128, 8), 128, 128),
            ("16 colors dithered", Dithered(128, 128, 16), 128, 128),
            ("256 colors", ManyColors(96, 96, 256), 96, 96),
        };

        foreach (var (name, rgba, w, h) in samples)
        {
            // 完整候选择优只在 method ≥ 5 启用；默认快档（method=4）走单候选，
            // 体积本就不要求等于全量枚举的最小值，因此这里显式指定完整档。
            int auto = WebpEncoder.EncodeRgba(rgba, w, h, 6).Length;

            // 与 EncodeBest 的候选集合一一对应（顺序无关，取最小即可）。
            var candidates = new[]
            {
                new Vp8LEncoderOptions(true, true, true, autoPredictor: true),
                new Vp8LEncoderOptions(true, false, true, autoPredictor: true, autoCrossColor: false),
                new Vp8LEncoderOptions(true, false, true, autoPredictor: true),
                new Vp8LEncoderOptions(true, false, false, autoPredictor: true),
                Vp8LEncoderOptions.LiteralOnly,
                new Vp8LEncoderOptions(false, true, true, useColorIndexing: true),
                new Vp8LEncoderOptions(false, false, true, useColorIndexing: true),
                new Vp8LEncoderOptions(false, true, false, useColorIndexing: true),
            };

            int smallest = int.MaxValue;
            foreach (var options in candidates)
            {
                int size = WebpEncoder.EncodeRgba(rgba, w, h, options).Length;
                if (size < smallest) smallest = size;
            }

            Assert.Equal(smallest, auto);
            AssertRoundTrip(rgba, w, h, new Vp8LEncoderOptions(true, true, true, useColorIndexing: true,
                selectBest: true, useMetaHuffman: true, autoPredictor: true), name + " (auto)");
        }
    }

    /// <summary>
    /// 连续编码同一幅图必须逐字节一致（保证优化引入的复用/缓存没有跨调用状态泄漏）。
    /// </summary>
    [Fact]
    public void RepeatedEncoding_IsDeterministic()
    {
        byte[] rgba = Photo(96, 72, seed: 31);
        byte[] first = WebpEncoder.EncodeRgba(rgba, 96, 72);

        for (int i = 0; i < 100; i++)
        {
            byte[] again = WebpEncoder.EncodeRgba(rgba, 96, 72);
            Assert.Equal(first.Length, again.Length);
            AssertBytesEqual(first, again, "repeat #" + i);
        }
    }

    // ==================================================================
    //  压缩率基线：阶段 A vs 阶段 B（各特性必须真的带来收益或至少不劣化）
    // ==================================================================

    /// <summary>
    /// 压缩率对拍：无损体积不得明显劣于 libwebp。
    ///
    /// 背景：限长 Huffman 曾在 Kraft 校验失败后退化成扁平码（所有符号 9~10 位）。
    /// 那种位流**完全合法**——能解码、往返 bit-exact、Kraft 和也成立——只有体积异常
    /// （照片样本上曾达 libwebp 的 1.72 倍）。合法性与往返测试都抓不到，只有和 libwebp
    /// 比体积才能兜住，所以这条断言是那类回归的唯一护栏。
    /// </summary>
    [Fact]
    public void Full_SizeWithin35PercentOfLibWebp_OnStructuredImages()
    {
        // 样本必须够大、符号够多，限长 Huffman 才会真正被触发：96×72 这类小图上
        // 活跃符号少、树深不足 15，绕过限长逻辑，用它们做护栏会漏掉回归。
        // scene.png 是真实照片（长尾残差），修复前它的比值是 1.57x，正是本断言的靶子。
        var samples = new List<Sample>
        {
            new Sample("photo-96x72", Photo(96, 72, seed: 11), 96, 72),
            new Sample("photo-320x240", Photo(320, 240, seed: 17), 320, 240),
            new Sample("gradient-256x192", Gradient(256, 192), 256, 192),
            new Sample("alpha-192x128", AlphaGradient(192, 128), 192, 128),
        };
        string scenePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "scene.png");
        if (File.Exists(scenePath))
        {
            var scene = SharpImageConverter.Core.Image.LoadRgba32(scenePath);
            samples.Add(new Sample("scene.png", scene.Buffer, scene.Width, scene.Height));
        }
        foreach (var sample in samples)
        {
            // 这条是限长 Huffman / 码表退化的体积护栏，针对完整档（method ≥ 5）；
            // 快档以 effort 换速度，不承诺压缩率，其正确性由往返与双解码器一致性测试覆盖。
            int ours = WebpEncoder.EncodeRgba(sample.Rgba, sample.W, sample.H, 6).Length;
            int lib = LibWebpTestCodec.EncodeLosslessRgbaForDispatch(sample.Rgba, sample.W, sample.H).Length;

            // 小图（<10 万像素）里位流头 + 码表的固定开销占比高，比值天然偏高，
            // 因此额外放宽一个固定字节数；大图上这条余量可以忽略。
            long budget = (long)(lib * 1.35) + 256;
            Assert.True(ours <= budget,
                $"{sample.Name}: 我方 {ours} 字节 / libwebp {lib} 字节 = {(double)ours / lib:F2}x（上限 1.35x + 256B）");
        }
    }

    [Fact]
    public void StageB_ShouldNotBeLargerThanLiteralOnly_ForStructuredImages()
    {
        foreach (var sample in Samples())
        {
            int literal = WebpEncoder.EncodeRgba(sample.Rgba, sample.W, sample.H,
                Vp8LEncoderOptions.LiteralOnly).Length;
            int full = WebpEncoder.EncodeRgba(sample.Rgba, sample.W, sample.H).Length;

            // 结构化图像（照片/渐变/调色板）上，变换 + color cache + LZ77 必须比纯 literal 小。
            // 噪声/纯色图不保证：噪声没有可利用结构，纯色小图会被变换头与子图像开销反超。
            if (sample.Name is "photo" or "gradient" or "palette" or "alpha")
                Assert.True(full < literal, $"{sample.Name}: full={full} 应小于 literal={literal}");
        }
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    private sealed record Sample(string Name, byte[] Rgba, int W, int H);

    private static Sample[] Samples() => new[]
    {
        new Sample("photo", Photo(96, 72, seed: 11), 96, 72),
        new Sample("gradient", Gradient(96, 72), 96, 72),
        new Sample("palette", Palette(96, 72), 96, 72),
        new Sample("noise", Noise(64, 64, seed: 13), 64, 64),
        new Sample("alpha", AlphaGradient(64, 48), 64, 48),
        new Sample("solid", SolidImage(48, 32, 200, 30, 90, 128), 48, 32),
    };

    private static void AssertRoundTrip(byte[] rgba, int w, int h, Vp8LEncoderOptions options, string label)
    {
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h, options);

        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        AssertPixelExact(rgba, csharp, label + " csharp->csharp");

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        AssertPixelExact(rgba, lib, label + " csharp->libwebp");
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"{label}: 第 {i} 字节不同，期望 0x{expected[i]:X2}，实际 0x{actual[i]:X2}");
        }
    }

    private static void AssertPixelExact(byte[] expected, byte[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);

        int first = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i]) { first = i; break; }
        }

        if (first < 0) return;

        int pixel = first / 4;
        string channel = "RGBA"[first % 4].ToString();
        Assert.Fail($"{label}: 首个差异在像素 {pixel} 的 {channel} 通道，" +
                    $"期望 0x{expected[first]:X2}，实际 0x{actual[first]:X2}");
    }

    // ----------------------------------------------------------------
    //  样本构造
    // ----------------------------------------------------------------

    private static (byte[] Rgba, int W, int H) SampleImage(Sample sample)
        => (sample.Rgba, sample.W, sample.H);

    /// <summary>照片近似：平滑渐变 + 低频正弦 + 轻微噪声，全不透明。</summary>
    private static byte[] Photo(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                double fx = x / (double)Math.Max(1, w - 1);
                double fy = y / (double)Math.Max(1, h - 1);
                double wave = Math.Sin(fx * 6.28) * Math.Cos(fy * 3.14);
                px[o + 0] = ClampByte(128 + 90 * fx + 30 * wave + rnd.Next(-6, 7));
                px[o + 1] = ClampByte(96 + 110 * fy + 25 * wave + rnd.Next(-6, 7));
                px[o + 2] = ClampByte(150 + 40 * wave - 50 * fy + rnd.Next(-6, 7));
                px[o + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>纯线性渐变（R 横向、G 纵向、B 对角），高度结构化。</summary>
    private static byte[] Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o + 0] = ClampByte(x * 255 / Math.Max(1, w - 1));
                px[o + 1] = ClampByte(y * 255 / Math.Max(1, h - 1));
                px[o + 2] = ClampByte((x + y) * 255 / Math.Max(1, w + h - 2));
                px[o + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>块状调色板图（8 色），纹理重复度高。</summary>
    private static byte[] Palette(int w, int h)
    {
        var colors = new (byte R, byte G, byte B)[]
        {
            (220, 30, 30), (30, 200, 40), (40, 60, 220), (230, 210, 40),
            (30, 190, 200), (190, 40, 200), (240, 240, 240), (25, 25, 25),
        };
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                var c = colors[((x / 8) + (y / 8) * 3) % colors.Length];
                px[o + 0] = c.R;
                px[o + 1] = c.G;
                px[o + 2] = c.B;
                px[o + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>两色棋盘：覆盖 color indexing 的最短索引打包路径（bits=3，8 索引/字节）。</summary>
    private static byte[] TwoColor(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bool second = ((x / 4) + (y / 4)) % 2 == 1;
                px[o + 0] = (byte)(second ? 0 : 255);
                px[o + 1] = (byte)(second ? 0 : 0);
                px[o + 2] = (byte)(second ? 255 : 0);
                px[o + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>恰好 numColors 种颜色的序列图：覆盖 color indexing 的 4/16/256 色分档。</summary>
    private static byte[] ManyColors(int w, int h, int numColors)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                int idx = (y * w + x) % numColors;
                px[o + 0] = (byte)(idx * 7);
                px[o + 1] = (byte)(idx * 11);
                px[o + 2] = (byte)(idx * 13);
                px[o + 3] = 255;
            }
        }
        return px;
    }

    /// <summary>低色数但空间抖动：索引流本身不可压缩，用于确认剪枝不会误剪这类图。</summary>
    private static byte[] Dithered(int w, int h, int numColors)
    {
        var colors = new byte[numColors][];
        for (int i = 0; i < numColors; i++)
            colors[i] = new byte[] { (byte)(i * 17), (byte)(i * 29 + 40), (byte)(i * 43 + 80), 255 };

        var px = new byte[w * h * 4];
        var rnd = new Random(101);
        for (int i = 0; i < w * h; i++)
        {
            int idx = rnd.Next(numColors);
            Array.Copy(colors[idx], 0, px, i * 4, 4);
        }
        return px;
    }

    /// <summary>随机噪声：最差压缩场景，检验合法性而非压缩率。</summary>
    private static byte[] Noise(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(seed);
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i + 0] = (byte)rnd.Next(256);
            px[i + 1] = (byte)rnd.Next(256);
            px[i + 2] = (byte)rnd.Next(256);
            px[i + 3] = (byte)rnd.Next(256);
        }
        return px;
    }

    private static byte[] AlphaGradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o + 0] = (byte)((x * 3) & 0xFF);
                px[o + 1] = (byte)((y * 5) & 0xFF);
                px[o + 2] = (byte)(((x + y) * 2) & 0xFF);
                px[o + 3] = (byte)((x * 255 / Math.Max(1, w - 1)) ^ (y * 3));
            }
        }
        return px;
    }

    private static byte[] SolidImage(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i + 0] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = a;
        }
        return px;
    }

    private static byte ClampByte(double v) => v switch
    {
        < 0 => 0,
        > 255 => 255,
        _ => (byte)Math.Round(v),
    };

    /// <summary>
    /// 真实照片的 VP8L 往返：托管编码 → libwebp 解码必须逐像素等于原图。
    /// 真实照片会触发大调色板、长距离反向引用与各向异性残差，是合成图覆盖不到的路径。
    /// </summary>
    [Fact]
    public void CSharp_Lossless_RealPhoto_RoundTripsThroughLibWebP()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "examples", "Amish-Noka-Dresser.jpg");
        var frame = ImageFrame.Load(path).ToRgba32();

        AssertRoundTrip(frame.Pixels, frame.Width, frame.Height,
            new Vp8LEncoderOptions(true, true, true, useColorIndexing: true, autoPredictor: true),
            "real photo");
    }
}

/// <summary>
/// 编码后端（<see cref="WebpEncodePolicy"/>）：纯 C# 为唯一实现，Auto 与 CSharp 等价；
/// 产物继续用 libwebp 黑盒对拍。
/// </summary>
[Collection("WebpEncodePolicy")]
public class WebpEncodeBackendTests
{
    [Fact]
    public void CSharp_Lossless_ProducesLibWebpDecodableFile()
    {
        var (rgba, w, h) = (PhotoSample(64, 48), 64, 48);

        byte[] webp = WebpCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        Assert.Equal(rgba, lib);
    }

    [Fact]
    public void Auto_Lossless_MatchesCSharpOutput()
    {
        var (rgba, w, h) = (PhotoSample(48, 40), 48, 40);

        byte[] csharp = WebpEncoder.EncodeRgba(rgba, w, h);
        byte[] auto = WebpCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));

        Assert.Equal(csharp, auto);
    }

    [Fact]
    public void LibWebpOracle_Lossless_DecodesBitExact()
    {
        var (rgba, w, h) = (PhotoSample(48, 40), 48, 40);

        byte[] native = LibWebpTestCodec.EncodeLosslessRgbaForDispatch(rgba, w, h);
        byte[] csharp = WebpDecoder.DecodeRgba(native, out int mw, out int mh);

        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        Assert.Equal(rgba, csharp);
    }

    [Fact]
    public void CSharp_Lossy_Opaque_ProducesLibWebpDecodableFile()
    {
        var (rgba, w, h) = (PhotoSample(32, 32), 32, 32);

        byte[] webp = WebpCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: false));

        // 合法性判据：真实 libwebp 必须能解出正确尺寸。
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);

        // 同一码流在托管解码器下必须与 libwebp 只存在 ±1 取整差异（alpha 必须一致）。
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        AssertNearPixel(lib, csharp, "csharp lossy libwebp->csharp");
    }

    [Fact]
    public void CSharp_Lossy_Alpha_ProducesAlphChunk_AndRoundTrips()
    {
        var rgba = PhotoSample(16, 16);
        for (int i = 0; i < 16 * 16; i++)
            rgba[i * 4 + 3] = (byte)(i % 5 == 0 ? 0 : (i * 13 + 40) & 0xFF); // 非不透明输入

        byte[] webp = WebpCodec.EncodeRgba(rgba, 16, 16,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: false));

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8X, container.Kind);
        Assert.True(container.HasAlpha);
        Assert.True(container.HasAlphaChunk);

        // 真实 libwebp 解码：alpha 平面由 ALPH（VP8L 无损）承载，必须与源完全一致。
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(16, lw);
        Assert.Equal(16, lh);
        for (int i = 0; i < 16 * 16; i++)
            Assert.Equal(rgba[i * 4 + 3], lib[i * 4 + 3]);

        // 同一码流在纯 C# 解码器下与 libwebp 仅允许 RGB ±1（alpha 严格相等）。
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(16, mw);
        Assert.Equal(16, mh);
        AssertNearPixel(lib, csharp, "csharp lossy alpha libwebp->csharp");
    }

    private static byte[] PhotoSample(int w, int h)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(w * h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o + 0] = (byte)((x * 3 + y) & 0xFF);
                px[o + 1] = (byte)((y * 4 + x) & 0xFF);
                px[o + 2] = (byte)rnd.Next(256);
                px[o + 3] = 255;
            }
        return px;
    }

    /// <summary>
    /// 有损码流的两个解码器对比：RGB 允许 ±1 取整差异，alpha 必须一致。
    /// </summary>
    private static void AssertNearPixel(byte[] expected, byte[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        int maxDiff = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = Math.Abs(expected[i] - actual[i]);
            if (i % 4 == 3)
                Assert.Equal(expected[i], actual[i]);
            maxDiff = Math.Max(maxDiff, i % 4 == 3 ? 0 : d);
        }

        Assert.True(maxDiff <= 1, $"{label}: RGB 最大差 {maxDiff} 超过 ±1");
    }
}

/// <summary>串行执行：这些测试会改全局 <see cref="WebpEncodePolicy.Backend"/>。</summary>
[CollectionDefinition("WebpEncodePolicy", DisableParallelization = true)]
public class WebpEncodePolicyCollection
{
}
