using System.Collections.Generic;
using System.IO;
using Xunit;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// WebP 元数据（ICCP / EXIF / XMP）透传：
/// 无元数据时不得写出多余扩展 chunk，解码端返回空元数据。
/// </summary>
[Collection("WebpEncodePolicy")]
public class WebpMetadataTests
{
    private static readonly int[] TwoFrameDurations = { 30, 30 };

    // ==================================================================
    //  动图：无元数据时不写扩展 chunk
    // ==================================================================

    [Fact]
    public void Animated_WithoutMetadata_WritesNoMetadataChunks()
    {
        int w = 16, h = 16;
        var frames = new List<Image<Rgb24>>
        {
            new(w, h, PhotoRgb(w, h, 4)),
            new(w, h, PhotoRgb(w, h, 5)),
        };

        var ms = new MemoryStream();
        WebpAnimationEncoder.EncodeRgb24(ms, frames, TwoFrameDurations, loopCount: 1,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3));

        var container = WebpContainer.Load(ms.ToArray());
        Assert.False(container.HasIccProfile);
        Assert.False(container.HasExif);
        Assert.False(container.HasXmp);
        Assert.False(container.HasIccProfileFlag);
        Assert.False(container.HasExifFlag);
        Assert.False(container.HasXmpFlag);
    }

    // ==================================================================
    //  无元数据时不写多余 chunk
    // ==================================================================

    [Fact]
    public void NoMetadata_WritesNoExtensionChunks()
    {
        int w = 48, h = 32;
        var rgba = Photo(w, h, 21);

        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        var container = WebpContainer.Load(webp);

        Assert.Equal(WebpContainerKind.Vp8L, container.Kind);
        Assert.DoesNotContain(container.Chunks, c => c.Id == WebpChunkId.Vp8X);
        Assert.False(container.HasIccProfile);
        Assert.False(container.HasExif);
        Assert.False(container.HasXmp);
        Assert.False(container.HasIccProfileFlag);
        Assert.False(container.HasExifFlag);
        Assert.False(container.HasXmpFlag);

        // 全空元数据对象与 null 必须产出完全相同的字节。
        var empty = new ImageMetadata();
        Assert.Equal(webp, WebpEncoder.EncodeRgba(rgba, w, h, empty));
    }

    [Fact]
    public void NoMetadata_DecodeReturnsEmptyMetadata()
    {
        int w = 16, h = 12;
        var rgba = Photo(w, h, 41);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);

        var decoded = new WebpDecoderAdapterRgba().DecodeRgba32(new MemoryStream(webp));
        Assert.Null(decoded.Metadata.IccProfile);
        Assert.Null(decoded.Metadata.ExifRaw);
        Assert.Null(decoded.Metadata.XmpRaw);
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    private static byte[] Photo(int w, int h, int seed)
    {
        var rgba = new byte[w * h * 4];
        uint state = (uint)(seed * 2654435761u + 1);
        for (int i = 0; i < w * h; i++)
        {
            state = state * 1664525u + 1013904223u;
            int x = i % w, y = i / w;
            rgba[i * 4] = (byte)((x * 7 + y * 3 + (state >> 24)) & 0xFF);
            rgba[i * 4 + 1] = (byte)((x * 2 + y * 5 + (state >> 16)) & 0xFF);
            rgba[i * 4 + 2] = (byte)((x * 5 + y * 2 + (state >> 8)) & 0xFF);
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private static byte[] PhotoRgb(int w, int h, int seed)
    {
        var rgba = Photo(w, h, seed);
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }
        return rgb;
    }
}
