using System;
using System.Collections.Generic;

namespace SharpImageConverter.Formats.Webp;

/// <summary>待写入 RIFF 容器的一个 chunk：四字节类型 + 负载。</summary>
internal readonly struct WebpOutputChunk
{
    public WebpOutputChunk(WebpChunkId id, ReadOnlyMemory<byte> data)
    {
        Id = id;
        Data = data;
    }

    public readonly WebpChunkId Id;
    public readonly ReadOnlyMemory<byte> Data;
}

/// <summary>
/// 纯 C# WebP 容器写出工具：按 RIFF/WEBP 规范拼装 chunk（奇数长度自动补 0）。
/// 供 ALPH 与动图编码等需要多 chunk 容器的路径复用。
/// </summary>
internal static class WebpChunkWriter
{
    /// <summary>拼装完整 RIFF/WEBP 文件（12 字节文件头 + 依次写出的 chunk）。</summary>
    public static byte[] Assemble(IReadOnlyList<WebpOutputChunk> chunks)
    {
        long fileSize = 4L; // "WEBP"
        foreach (var chunk in chunks)
            fileSize += 8L + chunk.Data.Length + (chunk.Data.Length & 1);

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
        foreach (var chunk in chunks)
        {
            WriteFourCc(result, pos, chunk.Id);
            WriteLittleEndian32(result, pos + 4, (uint)chunk.Data.Length);
            pos += 8;

            chunk.Data.Span.CopyTo(result.AsSpan(pos, chunk.Data.Length));
            pos += chunk.Data.Length;
            if ((chunk.Data.Length & 1) != 0) result[pos++] = 0;
        }
        return result;
    }

    /// <summary>
    /// 构建 VP8X chunk 数据：flags(1) + reserved(3) + canvas_width-1(3) + canvas_height-1(3)。
    /// </summary>
    public static byte[] BuildVp8X(byte flags, int width, int height)
    {
        if (width < 1 || width > 16777216 || height < 1 || height > 16777216)
            throw new WebpContainerException($"画布尺寸 {width}x{height} 超出 VP8X 可表示范围");

        var data = new byte[10];
        data[0] = flags;
        // data[1..3] 为保留字段，保持 0。
        WriteUInt24(data, 4, width - 1);
        WriteUInt24(data, 7, height - 1);
        return data;
    }

    /// <summary>构建 ANIM chunk 数据：background color（BGRA 小端 4 字节）+ loop count（2 字节）。</summary>
    public static byte[] BuildAnim(int loopCount, uint backgroundColorBgra = 0)
    {
        if (loopCount < 0 || loopCount > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(loopCount), "循环次数必须在 0..65535 之间");

        var data = new byte[6];
        WriteLittleEndian32(data, 0, backgroundColorBgra);
        data[4] = (byte)loopCount;
        data[5] = (byte)(loopCount >> 8);
        return data;
    }

    /// <summary>
    /// 构建 ANMF 的 16 字节帧头。
    /// x / y 以 2 像素为单位存储（调用方必须传入偶数偏移）；w / h 存为宽高减 1。
    /// flags：bit0 = dispose to background，bit1 = no blend。
    /// </summary>
    public static byte[] BuildAnmfHeader(int x, int y, int width, int height, int durationMs,
        bool disposeToBackground, bool noBlend)
    {
        if ((x & 1) != 0 || (y & 1) != 0)
            throw new ArgumentException($"ANMF 偏移必须为偶数：({x},{y})");
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "ANMF 帧尺寸必须为正");
        if (durationMs < 0 || durationMs > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "帧时长必须在 0..16777215 毫秒之间");

        var header = new byte[16];
        WriteUInt24(header, 0, x >> 1);
        WriteUInt24(header, 3, y >> 1);
        WriteUInt24(header, 6, width - 1);
        WriteUInt24(header, 9, height - 1);
        WriteUInt24(header, 12, durationMs);
        header[15] = (byte)((disposeToBackground ? 0x01 : 0) | (noBlend ? 0x02 : 0));
        return header;
    }

    /// <summary>把一个 chunk（type + size + 负载 + 对齐填充）追加到内存缓冲区。</summary>
    public static void AppendChunk(List<byte> buffer, WebpChunkId id, ReadOnlySpan<byte> data)
    {
        uint raw = (uint)id;
        buffer.Add((byte)raw);
        buffer.Add((byte)(raw >> 8));
        buffer.Add((byte)(raw >> 16));
        buffer.Add((byte)(raw >> 24));
        buffer.Add((byte)data.Length);
        buffer.Add((byte)(data.Length >> 8));
        buffer.Add((byte)(data.Length >> 16));
        buffer.Add((byte)(data.Length >> 24));
        foreach (byte b in data) buffer.Add(b);
        if ((data.Length & 1) != 0) buffer.Add(0);
    }

    private static void WriteUInt24(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
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
}
