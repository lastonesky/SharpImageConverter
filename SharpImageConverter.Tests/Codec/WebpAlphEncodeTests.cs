using System;
using System.Collections.Generic;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 有损 VP8 编码的 ALPH chunk（纯 C# 实现）对拍测试。
///
/// 验收要点：
/// - 带非不透明 alpha 的输入编码成 VP8X（alpha 标志）+ ALPH + VP8 的规范容器；
/// - 真实 libwebp 能解码该文件；
/// - alpha 平面由 VP8L 无损压缩，解码后必须与源 alpha 逐像素完全一致；
/// - 同一码流在纯 C# 解码器与 libwebp 下 RGB 仅允许 ±1 取整差异。
/// </summary>
public class WebpAlphEncodeTests
{
    public static IEnumerable<object[]> Cases()
    {
        (string name, int w, int h, Func<int, int, byte[]> alphaGen)[] cases =
        {
            ("all-transparent", 32, 24, (w, h) => new byte[w * h]),
            ("semi-gradient", 64, 48, (w, h) => GradientAlpha(w, h)),
            ("binary", 33, 21, (w, h) => BinaryAlpha(w, h)),
            ("odd-size", 17, 13, (w, h) => GradientAlpha(w, h)),
            ("mixed-opaque", 40, 25, (w, h) => MixedAlpha(w, h)),
        };
        foreach (var c in cases)
            yield return new object[] { c.name, c.w, c.h, c.alphaGen };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LossyAlpha_WritesAlphChunk_AndDecodesIdentically(
        string name, int w, int h, Func<int, int, byte[]> alphaGen)
    {
        byte[] rgba = MakePhoto(w, h, alphaGen(w, h));
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);

        // ---- 容器结构：VP8X(alpha) + ALPH + VP8，ALPH 必须在图像数据之前 ----
        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8X, container.Kind);
        Assert.True(container.HasAlpha, $"{name}: VP8X alpha 标志未置位");
        Assert.True(container.HasAlphaChunk, $"{name}: 缺少 ALPH chunk");
        Assert.True(container.CanvasWidth == w && container.CanvasHeight == h,
            $"{name}: VP8X 画布尺寸 {container.CanvasWidth}x{container.CanvasHeight} 与输入 {w}x{h} 不符");

        int alphIndex = -1, vp8Index = -1;
        var chunks = container.Chunks;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Id == WebpChunkId.Alph) alphIndex = i;
            if (chunks[i].Id == WebpChunkId.Vp8) vp8Index = i;
        }
        Assert.True(alphIndex >= 0 && vp8Index >= 0 && alphIndex < vp8Index,
            $"{name}: ALPH 必须在 VP8 之前（ALPH={alphIndex}, VP8={vp8Index}）");

        // ---- 纯 C# 解码：alpha 与源逐像素一致（ALPH 为无损压缩）----
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int cw, out int ch);
        Assert.Equal(w, cw);
        Assert.Equal(h, ch);
        AssertAlphaEquals(rgba, csharp, name, "C#");

        // ---- 真实 libwebp 解码：同样能解、alpha 一致、RGB 与 C# 仅 ±1 ----
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        AssertAlphaEquals(rgba, lib, name, "libwebp");

        int maxRgbDiff = 0;
        for (int i = 0; i < lib.Length; i++)
        {
            if (i % 4 == 3) continue;
            maxRgbDiff = Math.Max(maxRgbDiff, Math.Abs(lib[i] - csharp[i]));
        }
        Assert.True(maxRgbDiff <= 1, $"{name}: 两解码器 RGB 最大差 {maxRgbDiff} 超过 ±1");
    }

    /// <summary>不透明输入不应写出 ALPH / VP8X（保持最小容器）。</summary>
    [Fact]
    public void OpaqueInput_DoesNotWriteAlphaChunks()
    {
        int w = 48, h = 36;
        byte[] rgba = MakePhoto(w, h, OpaqueAlpha(w, h));
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8, container.Kind);
        Assert.False(container.HasAlphaChunk);
        Assert.False(container.HasAlpha);
    }

    private static void AssertAlphaEquals(byte[] source, byte[] decoded, string name, string decoder)
    {
        for (int i = 0; i < source.Length / 4; i++)
        {
            if (source[i * 4 + 3] != decoded[i * 4 + 3])
            {
                Assert.Fail($"{name}: {decoder} 解码 alpha 在像素 {i} 不一致" +
                            $"（源 {source[i * 4 + 3]}，解码 {decoded[i * 4 + 3]}）");
            }
        }
    }

    private static byte[] MakePhoto(int w, int h, byte[] alpha)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)((x * 7 + y * 3) & 0xFF);
                rgba[o + 1] = (byte)((x * 2 + y * 11 + 40) & 0xFF);
                rgba[o + 2] = (byte)((x * 13 + y * 5 + 90) & 0xFF);
                rgba[o + 3] = alpha[y * w + x];
            }
        }
        return rgba;
    }

    private static byte[] OpaqueAlpha(int w, int h)
    {
        var a = new byte[w * h];
        Array.Fill(a, (byte)255);
        return a;
    }

    private static byte[] GradientAlpha(int w, int h)
    {
        var a = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                a[y * w + x] = (byte)((x * 255 / Math.Max(1, w - 1) + y * 37) & 0xFF);
        return a;
    }

    private static byte[] BinaryAlpha(int w, int h)
    {
        var a = new byte[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = (byte)(i % 2 == 0 ? 255 : 0);
        return a;
    }

    private static byte[] MixedAlpha(int w, int h)
    {
        var a = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                a[i] = x < w / 3 ? (byte)255 : x < 2 * w / 3 ? (byte)0 : (byte)((x * 9 + y * 5) & 0xFF);
            }
        return a;
    }
}
