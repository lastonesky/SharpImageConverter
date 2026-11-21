using System;
using Xunit;
using SharpImageConverter.Core;
using SharpImageConverter;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 托管 VP8L 编码器（<see cref="WebpEncoder"/>）的合法性、round-trip 与压缩率回归。
/// </summary>
[Collection("WebpEncodePolicy")]
public class WebpLEncoderTests
{
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
    //  确定性
    // ==================================================================

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
    //  压缩率基线：阶段 B 必须至少不劣化于 literal-only
    // ==================================================================

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

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"{label}: 第 {i} 字节不同，期望 0x{expected[i]:X2}，实际 0x{actual[i]:X2}");
        }
    }

    // ----------------------------------------------------------------
    //  样本构造
    // ----------------------------------------------------------------

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
}

/// <summary>
/// 编码后端（<see cref="WebpEncodePolicy"/>）：纯 C# 为唯一实现，Auto 与 CSharp 等价。
/// </summary>
[Collection("WebpEncodePolicy")]
public class WebpEncodeBackendTests
{
    [Fact]
    public void Auto_Lossless_MatchesCSharpOutput()
    {
        var (rgba, w, h) = (PhotoSample(48, 40), 48, 40);

        byte[] csharp = WebpEncoder.EncodeRgba(rgba, w, h);
        byte[] auto = WebpCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));

        Assert.Equal(csharp, auto);
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
}

/// <summary>串行执行：这些测试会改全局 <see cref="WebpEncodePolicy.Backend"/>。</summary>
[CollectionDefinition("WebpEncodePolicy", DisableParallelization = true)]
public class WebpEncodePolicyCollection
{
}
