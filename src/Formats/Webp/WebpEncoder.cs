using System;
using System.IO;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// 纯 C# WebP 编码器入口（无损 VP8L + 有损 VP8 关键帧）。
///
/// 输出完整的 RIFF/WEBP 容器：<c>RIFF</c> + 文件大小 + <c>WEBP</c> +
/// <c>VP8L</c> 或 <c>VP8 </c> chunk（chunk 大小为负载长度，奇数长度补 1 字节填充）。
///
/// 无损默认使用 <see cref="Vp8LEncoderOptions.Auto"/>：在空间变换（subtract-green /
/// cross-color / per-tile predictor）与调色板（color indexing）两条路线中、
/// 各自与 color cache / LZ77 组合的若干候选里选输出最小的一个，全部候选都可被
/// libwebp 解码且与原始 RGBA 逐像素 bit-exact。
///
/// 有损走 <see cref="Vp8Encoder"/>（VP8 关键帧）；输入带非不透明 alpha 时，
/// 额外用 <see cref="AlphEncoder"/> 把 alpha 平面以 VP8L 无损压缩写入 ALPH chunk，
/// 容器改为 VP8X（置 alpha 标志）+ ALPH + VP8 的规范顺序。
/// </summary>
public static class WebpEncoder
{
    private const uint Vp8LMaxPixelsPerDimension = 16384;

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（无损 VP8L）。
    /// </summary>
    /// <param name="rgba">像素数据，每像素 R,G,B,A 连续 4 字节。</param>
    /// <param name="width">图像宽度（1..16384）。</param>
    /// <param name="height">图像高度（1..16384）。</param>
    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        return EncodeRgba(rgba.AsSpan(), width, height);
    }

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（无损 VP8L），并原样写入元数据。
    /// </summary>
    /// <param name="rgba">像素数据，每像素 R,G,B,A 连续 4 字节。</param>
    /// <param name="width">图像宽度（1..16384）。</param>
    /// <param name="height">图像高度（1..16384）。</param>
    /// <param name="metadata">需要透传的 ICCP / EXIF / XMP 原始字节；null 表示不写扩展 chunk。</param>
    public static byte[] EncodeRgba(byte[] rgba, int width, int height, ImageMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        return EncodeRgba(rgba.AsSpan(), width, height, metadata);
    }

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（无损 VP8L）。
    /// </summary>
    public static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height)
        => EncodeRgba(rgba, width, height, metadata: null);

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（无损 VP8L），并原样写入元数据。
    /// </summary>
    public static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height, ImageMetadata? metadata)
    {
        ValidateDimensions(width, height, rgba.Length);
        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, Vp8LEncoderOptions.Auto);
        return WrapInRiff(vp8l, WebpChunkId.Vp8L, width, height, metadata);
    }

    /// <summary>
    /// 把 RGBA 像素编码为 WebP 并写入流（无损 VP8L）。
    /// </summary>
    public static void EncodeRgbaToStream(Stream stream, byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rgba);
        EncodeRgbaToStream(stream, rgba.AsSpan(), width, height);
    }

    /// <summary>
    /// 把 RGBA 像素编码为 WebP 并写入流（无损 VP8L），同时写入元数据。
    /// </summary>
    public static void EncodeRgbaToStream(Stream stream, byte[] rgba, int width, int height,
        ImageMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rgba);
        EncodeRgbaToStream(stream, rgba.AsSpan(), width, height, metadata);
    }

    /// <summary>
    /// 把 RGBA 像素编码为 WebP 并写入流（无损 VP8L）。
    /// </summary>
    public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
        => EncodeRgbaToStream(stream, rgba, width, height, metadata: null);

    /// <summary>
    /// 把 RGBA 像素编码为 WebP 并写入流（无损 VP8L），同时写入元数据。
    /// </summary>
    public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height,
        ImageMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateDimensions(width, height, rgba.Length);

        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, Vp8LEncoderOptions.Auto);
        var riff = WrapInRiff(vp8l, WebpChunkId.Vp8L, width, height, metadata);
        stream.Write(riff, 0, riff.Length);
    }

    /// <summary>
    /// 编码为完整 WebP（可指定编码特性，供测试与阶段对比使用）。
    /// </summary>
    internal static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height,
        Vp8LEncoderOptions options, ImageMetadata? metadata = null)
    {
        ValidateDimensions(width, height, rgba.Length);
        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, options);
        return WrapInRiff(vp8l, WebpChunkId.Vp8L, width, height, metadata);
    }

    // ==================================================================
    //  有损 VP8
    // ==================================================================

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（有损 VP8 关键帧）。
    /// </summary>
    /// <param name="rgba">像素数据，每像素 R,G,B,A 连续 4 字节；alpha 可包含任意值。</param>
    /// <param name="width">图像宽度（1..16383）。</param>
    /// <param name="height">图像高度（1..16383）。</param>
    /// <param name="quality">质量（0..100，越大越清晰、体积越大）。</param>
    public static byte[] EncodeLossyRgba(byte[] rgba, int width, int height, float quality)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        return EncodeLossyRgba(rgba.AsSpan(), width, height, quality, metadata: null);
    }

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（有损 VP8 关键帧）。
    /// </summary>
    public static byte[] EncodeLossyRgba(ReadOnlySpan<byte> rgba, int width, int height, float quality)
        => EncodeLossyRgba(rgba, width, height, quality, metadata: null);

    /// <summary>
    /// 把 RGBA 像素编码为完整 WebP 字节（有损 VP8 关键帧），并原样写入元数据。
    /// </summary>
    public static byte[] EncodeLossyRgba(ReadOnlySpan<byte> rgba, int width, int height, float quality,
        ImageMetadata? metadata)
    {
        ValidateLossyDimensions(width, height, rgba.Length);
        var vp8 = Vp8Encoder.Encode(rgba, width, height, quality);

        int pixelCount = width * height;
        byte[]? alph = null;
        for (int i = 0; i < pixelCount; i++)
        {
            if (rgba[i * 4 + 3] == 255) continue;
            var alpha = new byte[pixelCount];
            for (int p = 0; p < pixelCount; p++)
                alpha[p] = rgba[p * 4 + 3];
            alph = AlphEncoder.Encode(alpha, width, height);
            break;
        }

        byte[] result = alph is null
            ? WrapInRiff(vp8, WebpChunkId.Vp8)
            : WrapLossyWithAlpha(vp8, alph, width, height);
        return WebpMetadataWriter.Apply(result, width, height, metadata);
    }

    /// <summary>
    /// 组装 VP8X + ALPH + VP8 的有损带 alpha 容器。
    /// 规范顺序：VP8X（alpha 标志置位）→ ALPH（紧邻图像数据之前）→ VP8。
    /// </summary>
    private static byte[] WrapLossyWithAlpha(ReadOnlySpan<byte> vp8, ReadOnlySpan<byte> alph, int width, int height)
    {
        var chunks = new[]
        {
            new WebpOutputChunk(WebpChunkId.Vp8X,
                WebpChunkWriter.BuildVp8X((byte)Vp8XFlags.Alpha, width, height)),
            new WebpOutputChunk(WebpChunkId.Alph, alph.ToArray()),
            new WebpOutputChunk(WebpChunkId.Vp8, vp8.ToArray()),
        };
        return WebpChunkWriter.Assemble(chunks);
    }

    /// <summary>
    /// 把 RGBA 像素编码为 WebP 并写入流（有损 VP8 关键帧），同时写入元数据。
    /// </summary>
    public static void EncodeLossyRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height,
        float quality, ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] riff = EncodeLossyRgba(rgba, width, height, quality, metadata);
        stream.Write(riff, 0, riff.Length);
    }

    /// <summary>
    /// 校验有损编码的输入尺寸/像素长度。VP8 关键帧宽高为 14 位且取值范围 1..16383。
    /// </summary>
    private static void ValidateLossyDimensions(int width, int height, int rgbaLength)
    {
        if (width < 1 || width > Vp8Encoder.MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"VP8 宽度必须在 1..{Vp8Encoder.MaxDimension} 之间");
        if (height < 1 || height > Vp8Encoder.MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(height),
                $"VP8 高度必须在 1..{Vp8Encoder.MaxDimension} 之间");
        if (rgbaLength != checked(width * height * 4))
            throw new ArgumentException("RGBA 像素长度与尺寸不匹配", nameof(rgbaLength));
    }

    private static void ValidateDimensions(int width, int height, int rgbaLength)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "WebP 尺寸必须为正数");
        if (width > Vp8LMaxPixelsPerDimension || height > Vp8LMaxPixelsPerDimension)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"VP8L 单边最大 {Vp8LMaxPixelsPerDimension} 像素");
        if (rgbaLength != checked(width * height * 4))
            throw new ArgumentException("RGBA 像素长度与尺寸不匹配", nameof(rgbaLength));
    }

    /// <summary>
    /// 按 RIFF/WEBP 规范包装图像 chunk：
    /// riff_size = 4 ("WEBP") + 8 (chunk 头) + chunk_size + (chunk 奇数时为 1 字节 pad)。
    /// 存在元数据时改为输出 VP8X 容器（ICCP 前置、EXIF/XMP 后置）。
    /// </summary>
    private static byte[] WrapInRiff(ReadOnlySpan<byte> payload, WebpChunkId chunkId, int width, int height,
        ImageMetadata? metadata)
    {
        var simple = WrapInRiff(payload, chunkId);
        return WebpMetadataWriter.Apply(simple, width, height, metadata);
    }

    private static byte[] WrapInRiff(ReadOnlySpan<byte> payload, WebpChunkId chunkId)
    {
        int chunkSize = payload.Length;
        int paddedChunkSize = chunkSize + (chunkSize & 1);
        int fileSize = checked(4 + 8 + paddedChunkSize);
        int totalLength = fileSize + 8;

        var result = new byte[totalLength];
        result[0] = (byte)'R';
        result[1] = (byte)'I';
        result[2] = (byte)'F';
        result[3] = (byte)'F';
        WriteLittleEndian32(result, 4, (uint)fileSize);
        result[8] = (byte)'W';
        result[9] = (byte)'E';
        result[10] = (byte)'B';
        result[11] = (byte)'P';
        uint fourCc = (uint)chunkId;
        result[12] = (byte)fourCc;
        result[13] = (byte)(fourCc >> 8);
        result[14] = (byte)(fourCc >> 16);
        result[15] = (byte)(fourCc >> 24);
        WriteLittleEndian32(result, 16, (uint)chunkSize);
        payload.CopyTo(result.AsSpan(20));
        // 奇数长度时第 paddedChunkSize 字节保持 0，即 RIFF 规范要求的填充字节。
        return result;
    }

    private static void WriteLittleEndian32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
