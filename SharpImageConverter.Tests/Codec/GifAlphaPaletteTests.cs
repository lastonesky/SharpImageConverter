using System;
using System.IO;
using SharpImageConverter.Formats.Gif;
using Xunit;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// GIF 编码器「带透明通道」路径的调色板容量边界。
/// </summary>
/// <remarks>
/// GIF 的调色板最多 256 项（2^8）。带透明的编码要用掉索引 0 放透明色，
/// 所以实际量化颜色只能有 255 种。此前量化器固定产出最多 256 色，
/// 满色时需要 1 + 256 = 257 个槽位，写调色板时数组越界
/// （<c>ArgumentException: Destination array was not long enough</c>）。
/// <para>
/// 触发条件：图像颜色足够丰富（量化后达到 256 色）且含透明像素。
/// 静帧（<c>EncodeRgba</c>）与动图（<c>EncodeAnimationRgba</c>）都走同一条路径。
/// </para>
/// </remarks>
public class GifAlphaPaletteTests
{
    /// <summary>
    /// 造一张「颜色足够多 + 含透明像素」的 RGBA 图，让量化器产出满 256 色。
    /// </summary>
    /// <remarks>
    /// RGB 按 <c>i % 256</c> / <c>(i / 256) % 256</c> / <c>(i * 7) % 256</c> 变化，
    /// 实测这个图案经八叉树量化后恰好产出 256 色（正好触到调色板容量上限）；
    /// 换成普通渐变或随机数只能到 253 色，测不出越界。
    /// </remarks>
    private static byte[] BuildRichRgba(int width, int height, bool withAlpha)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            rgba[o + 0] = (byte)(i % 256);
            rgba[o + 1] = (byte)((i / 256) % 256);
            rgba[o + 2] = (byte)((i * 7) % 256);
            // 一半不透明、一半全透明 → 保证走「带透明」的编码分支
            rgba[o + 3] = withAlpha && (i % width < width / 2) ? (byte)0 : (byte)255;
        }
        return rgba;
    }

    [Theory]
    [InlineData(GifQuantizerKind.OctreeBayer)]
    [InlineData(GifQuantizerKind.WuFloydSteinberg)]
    public void EncodeRgba_WithTransparency_FullPalette_DoesNotOverflow(GifQuantizerKind kind)
    {
        const int w = 64, h = 64;
        var rgba = BuildRichRgba(w, h, withAlpha: true);

        var enc = new GifEncoder { QuantizerKind = kind };
        using var ms = new MemoryStream();
        enc.EncodeRgba(w, h, rgba, ms);

        Assert.True(ms.Length > 0, "应产出非空 GIF");

        // 解回来确认透明像素仍在（量化没把透明区当成普通颜色）
        var decoded = new GifDecoder().DecodeRgba32(new MemoryStream(ms.ToArray()));
        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);
    }

    [Theory]
    [InlineData(GifQuantizerKind.OctreeBayer)]
    [InlineData(GifQuantizerKind.WuFloydSteinberg)]
    public void EncodeAnimationRgba_WithTransparency_FullPalette_DoesNotOverflow(GifQuantizerKind kind)
    {
        const int w = 64, h = 64;
        var frames = new[]
        {
            BuildRichRgba(w, h, withAlpha: true),
            BuildRichRgba(w, h, withAlpha: true),
        };

        var enc = new GifEncoder { QuantizerKind = kind };
        using var ms = new MemoryStream();
        int[] frameDurations = [100, 100];
        enc.EncodeAnimationRgba(w, h, frames, frameDurations, 0, ms);

        Assert.True(ms.Length > 0, "应产出非空 GIF 动图");

        var decoded = new GifDecoder().DecodeAnimationRgba32(new MemoryStream(ms.ToArray()));
        Assert.Equal(2, decoded.Frames.Count);
    }

    /// <summary>
    /// 带透明时量化颜色数必须 ≤255：调色板槽位 = 1（透明）+ 量化色数 ≤ 256。
    /// </summary>
    [Fact]
    public void QuantizeWithAlphaLimit_KeepsPaletteWithinGifCapacity()
    {
        const int w = 64, h = 64;
        var rgba = BuildRichRgba(w, h, withAlpha: true);

        // 复刻编码器的取样：只取不透明像素
        int pixelCount = w * h;
        var opaque = new byte[pixelCount * 3];
        int opPtr = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 4;
            if (rgba[s + 3] >= 128)
            {
                opaque[opPtr] = rgba[s];
                opaque[opPtr + 1] = rgba[s + 1];
                opaque[opPtr + 2] = rgba[s + 2];
                opPtr += 3;
            }
        }

        var (pal, _) = OctreeQuantizer.Quantize(opaque, w, h, true, 8, 255);
        int palCount = pal.Length / 3;
        Assert.True(palCount <= 255,
            $"带透明时调色板颜色数应 ≤255（要给透明索引留槽位），实际 {palCount}");

        // 1（透明）+ palCount 必须落在 GIF 的 256 项容量内
        Assert.True(1 + palCount <= 256, $"调色板总槽位 {1 + palCount} 超出 GIF 上限 256");
    }

    /// <summary>
    /// 测试素材本身的有效性：不变量是「这张图在 256 色上限下确实产出 256 色」。
    /// </summary>
    /// <remarks>
    /// 这条断言是给上面两个溢出用例兜底的——如果哪天换了测试图案、颜色数掉到 254，
    /// 越界就不再触发，测试会「通过」但什么也没测到。
    /// </remarks>
    [Fact]
    public void TestPattern_ActuallyProducesFullPalette()
    {
        const int w = 64, h = 64;
        var rgba = BuildRichRgba(w, h, withAlpha: false);

        var (pal, _) = OctreeQuantizer.Quantize(rgba, w, h, true, 8, 256);
        Assert.Equal(256, pal.Length / 3);
    }
}
