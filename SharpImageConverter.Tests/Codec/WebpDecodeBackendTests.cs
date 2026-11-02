using System;
using System.IO;
using Xunit;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Webp.Managed;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// WebP 解码后端分派（<see cref="WebpDecodePolicy"/>）与零拷贝入口
/// （<see cref="ManagedWebpDecoder.DecodeRgbaInto(ReadOnlySpan{byte}, Span{byte}, out int, out int)}）的回归测试。
///
/// 覆盖：
/// - Auto 模式下无损走托管、有损回退 libwebp、动图回退 libwebp；
/// - Managed 模式下有损明确抛 <see cref="WebpUnsupportedException"/>（不静默回退）；
/// - DecodeRgbaInto 与 DecodeRgba 逐字节一致，且不越界写调用方缓冲区。
/// </summary>
[Collection("WebpDecodePolicy")]
public class WebpDecodeBackendTests
{
    private static readonly int[] s_twoFrames = { 100, 100 };
    // ==================================================================
    //  零拷贝入口
    // ==================================================================

    [Fact]
    public void DecodeRgbaInto_MatchesDecodeRgba_BitExact()
    {
        var (rgba, w, h) = SampleImage(64, 48, seed: 1);
        byte[] webp = EncodeLossless(rgba, w, h);

        byte[] expected = ManagedWebpDecoder.DecodeRgba(webp, out int ew, out int eh);
        var dst = new byte[w * h * 4];
        ManagedWebpDecoder.DecodeRgbaInto(webp, dst, out int aw, out int ah);

        Assert.Equal(ew, aw);
        Assert.Equal(eh, ah);
        Assert.Equal(expected, dst);
    }

    [Fact]
    public void DecodeRgbaInto_LargerBuffer_LeavesTailUntouched()
    {
        var (rgba, w, h) = SampleImage(16, 16, seed: 2);
        byte[] webp = EncodeLossless(rgba, w, h);

        int needed = w * h * 4;
        var dst = new byte[needed + 64];
        dst.AsSpan(needed).Fill(0xAB);

        ManagedWebpDecoder.DecodeRgbaInto(webp, dst, out _, out _);

        for (int i = needed; i < dst.Length; i++)
            Assert.Equal(0xAB, dst[i]);
    }

    [Fact]
    public void DecodeRgbaInto_TooSmallBuffer_Throws()
    {
        var (rgba, w, h) = SampleImage(32, 32, seed: 3);
        byte[] webp = EncodeLossless(rgba, w, h);

        var dst = new byte[w * h * 4 - 1];
        Assert.Throws<ArgumentException>(() => ManagedWebpDecoder.DecodeRgbaInto(webp, dst, out _, out _));
    }

    // ==================================================================
    //  后端分派
    // ==================================================================

    [Fact]
    public void Auto_Lossless_UsesManaged_AndMatchesLibWebp()
    {
        var (rgba, w, h) = SampleImage(48, 40, seed: 4);
        byte[] webp = EncodeLossless(rgba, w, h);

        byte[] expected = WithBackend(WebpDecodeBackend.LibWebp, () => DecodeViaAdapter(webp, out _, out _));

        int aw = 0, ah = 0;
        byte[] actual = WithBackend(WebpDecodeBackend.Auto, () =>
        {
            var px = DecodeViaAdapter(webp, out int w2, out int h2);
            aw = w2;
            ah = h2;
            return px;
        });

        Assert.Equal(w, aw);
        Assert.Equal(h, ah);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Auto_Lossy_FallsBackToLibWebp()
    {
        var (rgba, w, h) = SampleImage(64, 64, seed: 5);
        byte[] webp = EncodeLossy(rgba, w, h, quality: 75f);

        byte[] expected = WithBackend(WebpDecodeBackend.LibWebp, () => DecodeViaAdapter(webp, out _, out _));
        byte[] actual = WithBackend(WebpDecodeBackend.Auto, () => DecodeViaAdapter(webp, out _, out _));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Managed_Lossy_ThrowsUnsupported_NoSilentFallback()
    {
        var (rgba, w, h) = SampleImage(32, 32, seed: 6);
        byte[] webp = EncodeLossy(rgba, w, h, quality: 75f);

        WithBackend(WebpDecodeBackend.Managed, () =>
        {
            Assert.Throws<WebpUnsupportedException>(() => DecodeViaAdapter(webp, out _, out _));
            return 0;
        });
    }

    [Fact]
    public void Auto_Animation_KeepsExistingLibWebpBehavior()
    {
        // 两帧纯色动图（libwebp 动图编码默认走有损 VP8 帧）
        var frames = new[]
        {
            new Image<Rgb24>(16, 16, Solid(16, 16, 200, 30, 30)),
            new Image<Rgb24>(16, 16, Solid(16, 16, 30, 200, 30)),
        };
        using var enc = new MemoryStream();
        WebpAnimationEncoder.EncodeRgb24(enc, frames, s_twoFrames, loopCount: 0, quality: 80f);
        byte[] webp = enc.ToArray();

        Assert.True(WebpContainer.Load(webp).IsAnimation);

        // 动图不参与 Auto 的托管分派：托管实现会把所有帧解成整幅快照（内存/语义都与既有单图解码不同），
        // 因此 Auto 必须与 LibWebp 保持完全一致的行为——包括"单图 API 解不了动图"这个既有结果。
        var libwebpError = Record.Exception(() => WithBackend(WebpDecodeBackend.LibWebp,
            () => DecodeViaAdapter(webp, out _, out _)));
        var autoError = Record.Exception(() => WithBackend(WebpDecodeBackend.Auto,
            () => DecodeViaAdapter(webp, out _, out _)));

        Assert.NotNull(libwebpError);
        Assert.NotNull(autoError);
        Assert.Equal(libwebpError.GetType(), autoError.GetType());
    }

    [Fact]
    public void LibWebpBackend_StillDecodesLossless()
    {
        var (rgba, w, h) = SampleImage(20, 20, seed: 7);
        byte[] webp = EncodeLossless(rgba, w, h);

        byte[] px = WithBackend(WebpDecodeBackend.LibWebp, () => DecodeViaAdapter(webp, out _, out _));
        Assert.Equal(w * h * 4, px.Length);
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    private static T WithBackend<T>(WebpDecodeBackend backend, Func<T> body)
    {
        var previous = WebpDecodePolicy.Backend;
        try
        {
            WebpDecodePolicy.Backend = backend;
            return body();
        }
        finally
        {
            WebpDecodePolicy.Backend = previous;
        }
    }

    private static byte[] DecodeViaAdapter(byte[] webp, out int w, out int h)
    {
        using var ms = new MemoryStream(webp);
        var img = new WebpDecoderAdapterRgba().DecodeRgba32(ms);
        w = img.Width;
        h = img.Height;
        return img.Buffer;
    }

    private static byte[] EncodeLossless(byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream();
        var img = new Image<Rgba32>(w, h, rgba);
        new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3, lossless: true) }
            .EncodeRgba32(ms, img);
        return ms.ToArray();
    }

    private static byte[] EncodeLossy(byte[] rgba, int w, int h, float quality)
    {
        using var ms = new MemoryStream();
        var img = new Image<Rgba32>(w, h, rgba);
        new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Serial, 3) }
            .EncodeRgba32(ms, img);
        return ms.ToArray();
    }

    private static (byte[] Rgba, int W, int H) SampleImage(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                px[o + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[o + 2] = (byte)rnd.Next(256);
                px[o + 3] = 255;
            }
        return (px, w, h);
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 3 + 0] = r;
            px[i * 3 + 1] = g;
            px[i * 3 + 2] = b;
        }
        return px;
    }
}

/// <summary>串行执行：这些测试会改全局 <see cref="WebpDecodePolicy.Backend"/>。</summary>
[CollectionDefinition("WebpDecodePolicy", DisableParallelization = true)]
public class WebpDecodePolicyCollection
{
}
