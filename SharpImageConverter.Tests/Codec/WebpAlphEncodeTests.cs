using System;
using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 有损 VP8 编码的 ALPH chunk（纯 C# 实现）回归测试。
/// </summary>
public class WebpAlphEncodeTests
{
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
}
