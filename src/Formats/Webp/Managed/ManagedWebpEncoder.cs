using System;
using System.IO;

namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// 纯 C# WebP 编码器入口（仅无损 VP8L）。
///
/// 输出完整的 RIFF/WEBP 容器：<c>RIFF</c> + 文件大小 + <c>WEBP</c> +
/// <c>VP8L</c> chunk（chunk 大小为 VP8L 负载长度，奇数长度补 1 字节填充）。
///
/// 默认使用 <see cref="Vp8LEncoderOptions.Auto"/>：在空间变换（subtract-green /
/// cross-color / per-tile predictor）与调色板（color indexing）两条路线中、
/// 各自与 color cache / LZ77 组合的若干候选里选输出最小的一个，全部候选都可被
/// libwebp 解码且与原始 RGBA 逐像素 bit-exact。
/// </summary>
public static class ManagedWebpEncoder
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
    /// 把 RGBA 像素编码为完整 WebP 字节（无损 VP8L）。
    /// </summary>
    public static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        ValidateDimensions(width, height, rgba.Length);
        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, Vp8LEncoderOptions.Auto);
        return WrapInRiff(vp8l);
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
    /// 把 RGBA 像素编码为 WebP 并写入流（无损 VP8L）。
    /// </summary>
    public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateDimensions(width, height, rgba.Length);

        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, Vp8LEncoderOptions.Auto);
        var riff = WrapInRiff(vp8l);
        stream.Write(riff, 0, riff.Length);
    }

    /// <summary>
    /// 编码为完整 WebP（可指定编码特性，供测试与阶段对比使用）。
    /// </summary>
    internal static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int width, int height,
        Vp8LEncoderOptions options)
    {
        ValidateDimensions(width, height, rgba.Length);
        var vp8l = Vp8LEncoder.EncodeVp8L(rgba, width, height, options);
        return WrapInRiff(vp8l);
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
    /// 按 RIFF/WEBP 规范包装 VP8L chunk：
    /// riff_size = 4 ("WEBP") + 8 (chunk 头) + chunk_size + (chunk 奇数时为 1 字节 pad)。
    /// </summary>
    private static byte[] WrapInRiff(ReadOnlySpan<byte> vp8lPayload)
    {
        int chunkSize = vp8lPayload.Length;
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
        result[12] = (byte)'V';
        result[13] = (byte)'P';
        result[14] = (byte)'8';
        result[15] = (byte)'L';
        WriteLittleEndian32(result, 16, (uint)chunkSize);
        vp8lPayload.CopyTo(result.AsSpan(20));
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
