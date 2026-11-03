using System;
using System.Collections.Generic;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// 把 ICCP / EXIF / XMP 原始字节原样写入 WebP 容器，并维护 VP8X flags。
///
/// 规范要求：ICCP 位于图像数据之前，EXIF / XMP 位于图像数据之后；
/// 只要存在任一扩展 chunk，就必须写出 VP8X。动图的元数据同样只放在 VP8X 层，
/// 不写入 ANMF 帧内部。
/// </summary>
internal static class WebpMetadataWriter
{
    /// <summary>是否携带任何需要写入的元数据（空数组视为未提供）。</summary>
    public static bool HasMetadata(ImageMetadata? metadata)
        => metadata is not null
           && (IsPresent(metadata.IccProfile) || IsPresent(metadata.ExifRaw) || IsPresent(metadata.XmpRaw));

    /// <summary>
    /// 若 <paramref name="metadata"/> 含元数据，则重建容器并写入这些 chunk；
    /// 否则原样返回 <paramref name="source"/>（不产生多余 VP8X / 额外 chunk）。
    /// </summary>
    /// <param name="source">已编码的完整 WebP 字节。</param>
    /// <param name="width">源容器未显式记录画布尺寸时使用的宽度。</param>
    /// <param name="height">源容器未显式记录画布尺寸时使用的高度。</param>
    public static byte[] Apply(byte[] source, int width, int height, ImageMetadata? metadata)
    {
        if (!HasMetadata(metadata)) return source;

        var container = WebpContainer.Load(source);
        int canvasWidth = container.CanvasWidth > 0 ? container.CanvasWidth : width;
        int canvasHeight = container.CanvasHeight > 0 ? container.CanvasHeight : height;
        if (canvasWidth <= 0 || canvasHeight <= 0)
            throw new WebpContainerException("无法确定画布尺寸，不能写入 VP8X 元数据");

        byte flags = 0;
        if (container.IsAnimation) flags |= (byte)Vp8XFlags.Animation;
        if (container.HasAlpha) flags |= (byte)Vp8XFlags.Alpha;
        if (IsPresent(metadata!.IccProfile)) flags |= (byte)Vp8XFlags.Iccp;
        if (IsPresent(metadata.ExifRaw)) flags |= (byte)Vp8XFlags.Exif;
        if (IsPresent(metadata.XmpRaw)) flags |= (byte)Vp8XFlags.Xmp;

        var pieces = new List<Piece>();
        // VP8X 必须位于文件最前；即使源文件已有 VP8X，也按新 flags 重写一份。
        pieces.Add(new Piece(WebpChunkId.Vp8X, BuildVp8X(flags, canvasWidth, canvasHeight)));

        // ICCP 必须在图像数据之前。
        if (IsPresent(metadata.IccProfile))
            pieces.Add(new Piece(WebpChunkId.Iccp, metadata.IccProfile!));

        // 保留原有 chunk 的相对顺序，去掉旧的 VP8X 与旧元数据（避免重复）。
        foreach (var chunk in container.Chunks)
        {
            switch (chunk.Id)
            {
                case WebpChunkId.Vp8X:
                case WebpChunkId.Iccp:
                case WebpChunkId.Exif:
                case WebpChunkId.Xmp:
                    continue;
                default:
                    pieces.Add(new Piece(chunk.Id, source.AsMemory(chunk.DataOffset, chunk.DataSize)));
                    break;
            }
        }

        // EXIF / XMP 位于图像数据之后。
        if (IsPresent(metadata.ExifRaw))
            pieces.Add(new Piece(WebpChunkId.Exif, metadata.ExifRaw!));
        if (IsPresent(metadata.XmpRaw))
            pieces.Add(new Piece(WebpChunkId.Xmp, metadata.XmpRaw!));

        return Assemble(pieces);
    }

    private static bool IsPresent(byte[]? data) => data is { Length: > 0 };

    private static byte[] BuildVp8X(byte flags, int width, int height)
    {
        if (width < 1 || width > 16777216 || height < 1 || height > 16777216)
            throw new WebpContainerException($"画布尺寸 {width}x{height} 超出 VP8X 可表示范围");

        var data = new byte[10];
        data[0] = flags;
        // data[1..3] 为 reserved，保持 0。
        WriteUInt24(data, 4, width - 1);
        WriteUInt24(data, 7, height - 1);
        return data;
    }

    private static void WriteUInt24(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }

    private static byte[] Assemble(List<Piece> pieces)
    {
        long fileSize = 4L; // "WEBP"
        foreach (var piece in pieces)
            fileSize += 8L + piece.Data.Length + (piece.Data.Length & 1);

        int total = checked((int)(fileSize + 8));
        var result = new byte[total];

        result[0] = (byte)'R';
        result[1] = (byte)'I';
        result[2] = (byte)'F';
        result[3] = (byte)'F';
        WriteLittleEndian32(result, 4, (uint)fileSize);
        result[8] = (byte)'W';
        result[9] = (byte)'E';
        result[10] = (byte)'B';
        result[11] = (byte)'P';

        int pos = 12;
        foreach (var piece in pieces)
        {
            WriteFourCc(result, pos, piece.Id);
            WriteLittleEndian32(result, pos + 4, (uint)piece.Data.Length);
            pos += 8;

            piece.Data.Span.CopyTo(result.AsSpan(pos, piece.Data.Length));
            pos += piece.Data.Length;
            if ((piece.Data.Length & 1) != 0) result[pos++] = 0;
        }
        return result;
    }

    private static void WriteFourCc(byte[] buffer, int offset, WebpChunkId id)
    {
        uint raw = (uint)id;
        buffer[offset] = (byte)raw;
        buffer[offset + 1] = (byte)(raw >> 8);
        buffer[offset + 2] = (byte)(raw >> 16);
        buffer[offset + 3] = (byte)(raw >> 24);
    }

    private static void WriteLittleEndian32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private readonly struct Piece
    {
        public Piece(WebpChunkId id, ReadOnlyMemory<byte> data)
        {
            Id = id;
            Data = data;
        }

        public readonly WebpChunkId Id;
        public readonly ReadOnlyMemory<byte> Data;
    }
}
