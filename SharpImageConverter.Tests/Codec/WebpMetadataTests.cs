using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Webp.Managed;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// WebP 元数据（ICCP / EXIF / XMP）透传：
/// 解码时从容器原样取出，编码时原样写回，VP8X flags 与 chunk 一一对应。
/// 覆盖静态有损 / 静态无损 / 动图三条路径。
/// </summary>
[Collection("WebpEncodePolicy")]
public class WebpMetadataTests
{
    private static readonly int[] ThreeFrameDurations = { 40, 60, 80 };
    private static readonly int[] TwoFrameDurations = { 30, 30 };

    // ==================================================================
    //  静态无损（托管编码器）
    // ==================================================================

    [Fact]
    public void StaticLossless_Managed_RoundTripsMetadata_AndFlagsMatchChunks()
    {
        int w = 64, h = 48;
        var rgba = Photo(w, h, 123);
        for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = (byte)(i % 3 == 0 ? 128 : 255);

        var metadata = CreateMetadata();
        byte[] webp = ManagedWebpEncoder.EncodeRgba(rgba, w, h, metadata);

        AssertContainer(webp, metadata, expectAnimation: false, expectAlpha: true, "managed lossless");

        // 真实 libwebp 解码校验：带扩展 chunk 的文件仍是合法 WebP，像素 bit-exact。
        byte[] lib = WebpCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        Assert.Equal(rgba, lib);

        // 解码 API 暴露原始字节（不解析内容）。
        var decoded = new WebpDecoderAdapterRgba().DecodeRgba32(new MemoryStream(webp));
        Assert.Equal(metadata.IccProfile, decoded.Metadata.IccProfile);
        Assert.Equal(metadata.ExifRaw, decoded.Metadata.ExifRaw);
        Assert.Equal(metadata.XmpRaw, decoded.Metadata.XmpRaw);

        // 再编码 -> chunk 字节完全一致，且仍能解码。
        byte[] reencoded = ManagedWebpEncoder.EncodeRgba(decoded.Buffer, decoded.Width, decoded.Height, decoded.Metadata);
        AssertContainer(reencoded, metadata, expectAnimation: false, expectAlpha: true, "managed lossless re-encode");
        Assert.Equal(rgba, WebpCodec.DecodeRgba(reencoded, out _, out _));
    }

    [Fact]
    public void StaticLossless_StreamOverload_WritesMetadata()
    {
        int w = 33, h = 17;
        var rgba = Photo(w, h, 7);
        for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = 255;

        var metadata = CreateMetadata();
        var ms = new MemoryStream();
        ManagedWebpEncoder.EncodeRgbaToStream(ms, rgba, w, h, metadata);
        byte[] webp = ms.ToArray();

        AssertContainer(webp, metadata, expectAnimation: false, expectAlpha: false, "managed stream");
        Assert.Equal(rgba, WebpCodec.DecodeRgba(webp, out _, out _));
    }

    [Fact]
    public void StaticLossless_LibWebpBackend_RoundTripsMetadata()
    {
        int w = 40, h = 30;
        var rgba = Photo(w, h, 5);
        for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = 255;

        var metadata = CreateMetadata();
        byte[] webp = WithEncodeBackend(WebpEncodeBackend.LibWebp, () =>
        {
            var options = new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3, lossless: true);
            var encoder = new WebpEncoderAdapterRgba();
            encoder.Options = options;
            var image = new Image<Rgba32>(w, h, rgba, metadata);
            var ms = new MemoryStream();
            encoder.EncodeRgba32(ms, image);
            return ms.ToArray();
        });

        AssertContainer(webp, metadata, expectAnimation: false, expectAlpha: false, "libwebp lossless");
        Assert.Equal(rgba, WebpCodec.DecodeRgba(webp, out _, out _));
    }

    // ==================================================================
    //  静态有损（libwebp 编码 + 托管容器重写）
    // ==================================================================

    [Fact]
    public void StaticLossyRgba_RoundTripsMetadata()
    {
        int w = 32, h = 24;
        var rgba = Photo(w, h, 11);
        for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = (byte)(i % 2 == 0 ? 200 : 255);

        var metadata = CreateMetadata();
        byte[] webp = WithEncodeBackend(WebpEncodeBackend.LibWebp, () =>
        {
            var options = new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3);
            var encoder = new WebpEncoderAdapterRgba();
            encoder.Options = options;
            var image = new Image<Rgba32>(w, h, rgba, metadata);
            var ms = new MemoryStream();
            encoder.EncodeRgba32(ms, image);
            return ms.ToArray();
        });

        AssertContainer(webp, metadata, expectAnimation: false, expectAlpha: true, "libwebp lossy rgba");
        // 有损无法 bit-exact，只要求真实 libwebp 能解码。
        byte[] decoded = WebpCodec.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        Assert.Equal(w * h * 4, decoded.Length);
    }

    [Fact]
    public void StaticLossyRgb_RoundTripsMetadata_AddsVp8X()
    {
        int w = 24, h = 16;
        var rgb = PhotoRgb(w, h, 13);

        var metadata = CreateMetadata();
        byte[] webp = WithEncodeBackend(WebpEncodeBackend.LibWebp, () =>
        {
            var options = new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3);
            var encoder = new WebpEncoderAdapter();
            encoder.Options = options;
            var image = new Image<Rgb24>(w, h, rgb, metadata);
            var ms = new MemoryStream();
            encoder.EncodeRgb24(ms, image);
            return ms.ToArray();
        });

        AssertContainer(webp, metadata, expectAnimation: false, expectAlpha: false, "libwebp lossy rgb");
        byte[] decoded = WebpCodec.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        Assert.Equal(w * h * 4, decoded.Length);
    }

    // ==================================================================
    //  动图：元数据只在 VP8X 层
    // ==================================================================

    [Fact]
    public void Animated_MetadataStaysAtVp8X_NotInsideAnmf()
    {
        int w = 24, h = 16;
        var metadata = CreateMetadata();
        var frames = new List<Image<Rgb24>>
        {
            new(w, h, PhotoRgb(w, h, 1), metadata),
            new(w, h, PhotoRgb(w, h, 2)),
            new(w, h, PhotoRgb(w, h, 3)),
        };

        var ms = new MemoryStream();
        WebpAnimationEncoder.EncodeRgb24(ms, frames, ThreeFrameDurations, loopCount: 0,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3));
        byte[] webp = ms.ToArray();

        var container = WebpContainer.Load(webp);
        Assert.True(container.IsAnimation);
        Assert.True(container.HasIccProfileFlag);
        Assert.True(container.HasExifFlag);
        Assert.True(container.HasXmpFlag);
        Assert.True(container.HasIccProfile);
        Assert.True(container.HasExif);
        Assert.True(container.HasXmp);
        AssertBytesEqual(metadata.IccProfile!, container.IccProfile.ToArray(), "animated ICCP");
        AssertBytesEqual(metadata.ExifRaw!, container.Exif.ToArray(), "animated EXIF");
        AssertBytesEqual(metadata.XmpRaw!, container.Xmp.ToArray(), "animated XMP");

        var ids = new List<WebpChunkId>();
        foreach (var c in container.Chunks) ids.Add(c.Id);
        Assert.Equal(WebpChunkId.Vp8X, ids[0]);
        int icc = ids.IndexOf(WebpChunkId.Iccp);
        int anim = ids.IndexOf(WebpChunkId.Anim);
        int exif = ids.IndexOf(WebpChunkId.Exif);
        int xmp = ids.IndexOf(WebpChunkId.Xmp);
        Assert.True(icc > 0 && icc < anim, "ICCP 必须位于 ANIM/帧数据之前");
        Assert.True(exif > LastAnmfIndex(ids), "EXIF 必须位于全部 ANMF 之后");
        Assert.True(xmp > exif, "XMP 必须位于 EXIF 之后");

        // 元数据不得写入 ANMF 内部。
        foreach (var anmf in container.FindAllChunks(WebpChunkId.Anmf))
        {
            foreach (var sub in container.GetAnmfSubChunks(anmf))
            {
                Assert.NotEqual(WebpChunkId.Iccp, sub.Id);
                Assert.NotEqual(WebpChunkId.Exif, sub.Id);
                Assert.NotEqual(WebpChunkId.Xmp, sub.Id);
            }
        }

        // 动图的单图解码 API 不支持（既有行为）；把首帧 VP8 负载重新包成单帧 WebP，
        // 再用真实 libwebp 解码，确认帧数据本身合法。
        var firstAnmf = container.FindAllChunks(WebpChunkId.Anmf)[0];
        var vp8 = container.GetAnmfSubChunks(firstAnmf).First(c => c.Id == WebpChunkId.Vp8);
        byte[] frameWebp = WrapSingleChunk(WebpChunkId.Vp8, vp8.GetData(container.RawData).ToArray());
        byte[] decoded = WebpCodec.DecodeRgba(frameWebp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        Assert.Equal(w * h * 4, decoded.Length);
    }

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

        byte[] webp = ManagedWebpEncoder.EncodeRgba(rgba, w, h);
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
        Assert.Equal(webp, ManagedWebpEncoder.EncodeRgba(rgba, w, h, empty));
    }

    [Fact]
    public void NoMetadata_LossyRgb_WritesSimpleVp8()
    {
        int w = 20, h = 10;
        var rgb = PhotoRgb(w, h, 31);

        byte[] webp = WithEncodeBackend(WebpEncodeBackend.LibWebp, () =>
        {
            var encoder = new WebpEncoderAdapter();
            encoder.Options = new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 3);
            var image = new Image<Rgb24>(w, h, rgb);
            var ms = new MemoryStream();
            encoder.EncodeRgb24(ms, image);
            return ms.ToArray();
        });

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8, container.Kind);
        Assert.False(container.HasIccProfile);
        Assert.False(container.HasExif);
        Assert.False(container.HasXmp);
    }

    [Fact]
    public void NoMetadata_DecodeReturnsEmptyMetadata()
    {
        int w = 16, h = 12;
        var rgba = Photo(w, h, 41);
        byte[] webp = ManagedWebpEncoder.EncodeRgba(rgba, w, h);

        var decoded = new WebpDecoderAdapterRgba().DecodeRgba32(new MemoryStream(webp));
        Assert.Null(decoded.Metadata.IccProfile);
        Assert.Null(decoded.Metadata.ExifRaw);
        Assert.Null(decoded.Metadata.XmpRaw);
    }

    // ==================================================================
    //  辅助
    // ==================================================================

    private static ImageMetadata CreateMetadata()
    {
        // 刻意使用奇数长度，覆盖 RIFF chunk 的 1 字节 padding。
        var icc = new byte[33];
        for (int i = 0; i < icc.Length; i++) icc[i] = (byte)(i * 7 + 3);

        return new ImageMetadata
        {
            IccProfile = icc,
            ExifRaw = new byte[] { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0x4D, 0x4D, 0x00, 0x2A, 0x00 },
            XmpRaw = Encoding.UTF8.GetBytes(
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>"),
        };
    }

    private static void AssertContainer(byte[] webp, ImageMetadata expected,
        bool expectAnimation, bool expectAlpha, string label)
    {
        Assert.Equal((uint)webp.Length - 8, BitConverter.ToUInt32(webp, 4));

        var container = WebpContainer.Load(webp);
        Assert.True(container.HasIccProfileFlag, label + ": ICCP flag");
        Assert.True(container.HasExifFlag, label + ": EXIF flag");
        Assert.True(container.HasXmpFlag, label + ": XMP flag");
        Assert.True(container.HasIccProfile, label + ": ICCP chunk");
        Assert.True(container.HasExif, label + ": EXIF chunk");
        Assert.True(container.HasXmp, label + ": XMP chunk");
        Assert.Equal(expectAnimation, container.IsAnimation);
        Assert.Equal(expectAlpha, container.HasAlpha);

        AssertBytesEqual(expected.IccProfile!, container.IccProfile.ToArray(), label + " ICCP");
        AssertBytesEqual(expected.ExifRaw!, container.Exif.ToArray(), label + " EXIF");
        AssertBytesEqual(expected.XmpRaw!, container.Xmp.ToArray(), label + " XMP");

        var ids = new List<WebpChunkId>();
        foreach (var c in container.Chunks) ids.Add(c.Id);

        Assert.Equal(WebpChunkId.Vp8X, ids[0]);
        int icc = ids.IndexOf(WebpChunkId.Iccp);
        int image = ids.FindIndex(id =>
            id is WebpChunkId.Vp8 or WebpChunkId.Vp8L or WebpChunkId.Alph or WebpChunkId.Anim);
        int exif = ids.IndexOf(WebpChunkId.Exif);
        int xmp = ids.IndexOf(WebpChunkId.Xmp);

        Assert.True(icc > 0 && icc < image, label + ": ICCP 必须位于图像数据之前");
        Assert.True(exif > image, label + ": EXIF 必须位于图像数据之后");
        Assert.True(xmp > exif, label + ": XMP 必须位于 EXIF 之后");
    }

    private static int LastAnmfIndex(List<WebpChunkId> ids)
    {
        int last = -1;
        for (int i = 0; i < ids.Count; i++)
            if (ids[i] == WebpChunkId.Anmf) last = i;
        return last;
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"{label}: 第 {i} 字节不同（expected 0x{expected[i]:X2}, actual 0x{actual[i]:X2}）");
        }
    }

    private static byte[] WrapSingleChunk(WebpChunkId id, byte[] payload)
    {
        int padded = payload.Length + (payload.Length & 1);
        int fileSize = 4 + 8 + padded;
        var result = new byte[8 + fileSize];
        result[0] = (byte)'R';
        result[1] = (byte)'I';
        result[2] = (byte)'F';
        result[3] = (byte)'F';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)fileSize);
        result[8] = (byte)'W';
        result[9] = (byte)'E';
        result[10] = (byte)'B';
        result[11] = (byte)'P';
        BitConverter.TryWriteBytes(result.AsSpan(12, 4), (uint)id);
        BitConverter.TryWriteBytes(result.AsSpan(16, 4), (uint)payload.Length);
        payload.CopyTo(result.AsSpan(20));
        return result;
    }

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

    private static T WithEncodeBackend<T>(WebpEncodeBackend backend, Func<T> body)
    {
        var previous = WebpEncodePolicy.Backend;
        try
        {
            WebpEncodePolicy.Backend = backend;
            return body();
        }
        finally
        {
            WebpEncodePolicy.Backend = previous;
        }
    }
}
