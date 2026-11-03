using System;
using System.Collections.Generic;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 纯 C# 动图编码（VP8X + ANIM + ANMF）对拍测试。
///
/// 验收要点：
/// - 产物可被真实 libwebp（libwebpdemux）解码，帧数、画布、循环次数、每帧时长正确；
/// - 无损模式编码→解码往返后每帧与源快照逐像素完全一致（本实现与 libwebp 双方）；
/// - 有损模式本实现与 libwebp 解码同一码流，alpha 完全一致、RGB 仅 ±1；
/// - 覆盖不同帧数、不同 dispose、透明背景与奇数尺寸/奇数偏移。
/// </summary>
public class WebpAnimationEncodeTests
{
    [Fact]
    public void Lossless_ThreeFrames_WithMixedDispose_RoundTripsExactly()
    {
        int w = 32, h = 24;
        var frames = new[]
        {
            Frame(w, h, 4, 3, 10, 8, 0xE03020, 255),
            Frame(w, h, 12, 9, 14, 10, 0x20A040, 180),
            Frame(w, h, 20, 2, 8, 6, 0x2040E0, 255),
        };

        var input = new List<WebpAnimationFrameInput>
        {
            new(frames[0], 40, Blend: true, Dispose: WebpAnimationDispose.None),
            new(frames[1], 60, Blend: true, Dispose: WebpAnimationDispose.Background),
            new(frames[2], 80, Blend: false, Dispose: WebpAnimationDispose.None),
        };

        byte[] webp = WebpAnimationWriter.Encode(input, w, h, loopCount: 2,
            new WebpAnimationEncodeOptions(Lossless: true));

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8X, container.Kind);
        Assert.True(container.IsAnimation);
        Assert.True(container.HasAlpha);
        Assert.Equal(w, container.CanvasWidth);
        Assert.Equal(h, container.CanvasHeight);
        Assert.Equal(2, container.LoopCount);

        // 纯 C# 解码：每帧与源快照逐像素一致，时长正确。
        var managed = WebpDecoder.DecodeAnimation(webp);
        Assert.Equal(w, managed.CanvasWidth);
        Assert.Equal(h, managed.CanvasHeight);
        Assert.Equal(2, managed.LoopCount);
        Assert.Equal(3, managed.Frames.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(input[i].DurationMs, managed.Frames[i].DurationMs);
            Assert.Equal(frames[i], managed.Frames[i].Rgba);
        }

        // libwebp demux 解码：帧数 / 时长 / 像素同样必须一致（无损编码应完全还原）。
        Assert.True(LibWebpAnimationDecoder.TryDecode(webp, out var lib), "libwebp 无法解码该动图");
        Assert.Equal(w, lib.Width);
        Assert.Equal(h, lib.Height);
        Assert.Equal(2, lib.LoopCount);
        Assert.Equal(3, lib.Frames.Length);
        int prevTs = 0;
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(input[i].DurationMs, lib.Timestamps[i] - prevTs);
            prevTs = lib.Timestamps[i];
            Assert.Equal(frames[i], lib.Frames[i]);
        }
    }

    [Theory]
    [InlineData(35, 23, 3, 5)]   // 变化矩形左上角为奇数，检验 ANMF 偏移偶数对齐
    [InlineData(17, 13, 9, 7)]   // 奇数画布
    public void Lossless_OddRectOffset_And_OddCanvas_RoundTrip(int w, int h, int x0, int y0)
    {
        var f1 = Frame(w, h, x0, y0, 5, 4, 0xCC0088, 255);
        var f2 = Frame(w, h, x0 + 2, y0 + 1, 4, 3, 0x00CC88, 120);
        var input = new List<WebpAnimationFrameInput>
        {
            new(f1, 30, Dispose: WebpAnimationDispose.None),
            new(f2, 30, Blend: true, Dispose: WebpAnimationDispose.Background),
        };

        byte[] webp = WebpAnimationWriter.Encode(input, w, h, 0,
            new WebpAnimationEncodeOptions(Lossless: true));

        var managed = WebpDecoder.DecodeAnimation(webp);
        Assert.Equal(f1, managed.Frames[0].Rgba);
        Assert.Equal(f2, managed.Frames[1].Rgba);

        Assert.True(LibWebpAnimationDecoder.TryDecode(webp, out var lib), "libwebp 无法解码该动图");
        Assert.Equal(f1, lib.Frames[0]);
        Assert.Equal(f2, lib.Frames[1]);
        var parsed = WebpContainer.Load(webp);
        foreach (var anmf in parsed.FindAllChunks(WebpChunkId.Anmf))
        {
            var header = WebpContainer.ParseAnmfHeader(parsed.RawData, anmf);
            Assert.True(header.X % 2 == 0 && header.Y % 2 == 0,
                $"ANMF 偏移 ({header.X},{header.Y}) 未按 2 像素对齐");
            Assert.True(header.X + header.Width <= w && header.Y + header.Height <= h,
                "ANMF 帧矩形超出画布");
        }
    }

    [Fact]
    public void Lossy_FourFrames_MatchLibWebp_And_AlphaIsExact()
    {
        int w = 40, h = 30;
        var frames = new byte[4][];
        for (int i = 0; i < 4; i++)
            frames[i] = Frame(w, h, 3 + i * 6, 2 + i * 4, 12, 9,
                (uint)((i * 0x335577) | 0x200000), (byte)(i == 1 ? 128 : i == 3 ? 64 : 255));

        // 第 2 帧带全透明擦除块，覆盖 disposal/透明背景路径。
        ClearRect(frames[1], w, h, 5, 4, 10, 8);

        var durations = new[] { 40, 55, 70, 25 };
        var input = new List<WebpAnimationFrameInput>();
        for (int i = 0; i < 4; i++)
            input.Add(new WebpAnimationFrameInput(frames[i], durations[i],
                Blend: i != 2, Dispose: i == 1 ? WebpAnimationDispose.Background : WebpAnimationDispose.None));

        byte[] webp = WebpAnimationWriter.Encode(input, w, h, loopCount: 5);

        var container = WebpContainer.Load(webp);
        Assert.True(container.IsAnimation);
        Assert.True(container.HasAlpha);

        Assert.True(LibWebpAnimationDecoder.TryDecode(webp, out var lib), "libwebp 无法解码该动图");
        var managed = WebpDecoder.DecodeAnimation(webp);

        Assert.Equal(lib.Frames.Length, managed.Frames.Length);
        Assert.Equal(w, managed.CanvasWidth);
        Assert.Equal(h, managed.CanvasHeight);
        Assert.Equal(5, managed.LoopCount);
        Assert.Equal(4, lib.Frames.Length);
        Assert.Equal(5, lib.LoopCount);

        int prevTs = 0;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(durations[i], managed.Frames[i].DurationMs);
            Assert.Equal(durations[i], lib.Timestamps[i] - prevTs);
            prevTs = lib.Timestamps[i];

            byte[] a = managed.Frames[i].Rgba;
            byte[] b = lib.Frames[i];
            int maxRgbDiff = 0;
            for (int p = 0; p < w * h; p++)
            {
                Assert.True(a[p * 4 + 3] == b[p * 4 + 3],
                    $"第 {i} 帧像素 {p} alpha 不一致（本实现 {a[p * 4 + 3]}，libwebp {b[p * 4 + 3]}）");
                for (int c = 0; c < 3; c++)
                    maxRgbDiff = Math.Max(maxRgbDiff, Math.Abs(a[p * 4 + c] - b[p * 4 + c]));
            }
            Assert.True(maxRgbDiff <= 1, $"第 {i} 帧 RGB 最大差 {maxRgbDiff} 超过 ±1");
        }
    }

    [Fact]
    public void SingleFrame_TransparentCanvas_IsDecodable()
    {
        int w = 20, h = 16;
        var frame = Frame(w, h, 6, 4, 5, 5, 0x112233, 200);
        byte[] webp = WebpAnimationWriter.Encode(
            new[] { new WebpAnimationFrameInput(frame, 100) }, w, h, 0,
            new WebpAnimationEncodeOptions(Lossless: true));

        var managed = WebpDecoder.DecodeAnimation(webp);
        Assert.Single(managed.Frames);
        Assert.Equal(frame, managed.Frames[0].Rgba);

        Assert.True(LibWebpAnimationDecoder.TryDecode(webp, out var lib), "libwebp 无法解码单帧动图");
        Assert.Single(lib.Frames);
        Assert.Equal(frame, lib.Frames[0]);
    }

    [Fact]
    public void FrameSizeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            WebpAnimationWriter.Encode(
                new[] { new WebpAnimationFrameInput(new byte[10], 10) }, 8, 8, 0));
    }

    // ------------------------------------------------------------------
    //  帧构造辅助
    // ------------------------------------------------------------------

    private static byte[] Frame(int w, int h, int x, int y, int rw, int rh, uint rgb, byte alpha)
    {
        var rgba = new byte[w * h * 4];
        int r = (int)((rgb >> 16) & 0xFF), g = (int)((rgb >> 8) & 0xFF), b = (int)(rgb & 0xFF);
        for (int yy = y; yy < y + rh && yy < h; yy++)
        {
            for (int xx = x; xx < x + rw && xx < w; xx++)
            {
                int o = (yy * w + xx) * 4;
                rgba[o] = (byte)r;
                rgba[o + 1] = (byte)g;
                rgba[o + 2] = (byte)b;
                rgba[o + 3] = alpha;
            }
        }
        return rgba;
    }

    private static void ClearRect(byte[] rgba, int w, int h, int x, int y, int rw, int rh)
    {
        for (int yy = y; yy < y + rh && yy < h; yy++)
            Array.Clear(rgba, (yy * w + x) * 4, Math.Min(rw, w - x) * 4);
    }
}
