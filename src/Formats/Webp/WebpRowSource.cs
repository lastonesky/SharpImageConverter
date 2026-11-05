using System;
using System.Buffers.Binary;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// 纯 C# WebP 逐行源（替代原先基于 libwebp 增量解码的实现）。
///
/// 语义与 <see cref="IRowImageSource"/> 保持一致：调用方按行取像素，无需持有整幅 RGBA。
/// 数据读取按块进行并容忍短读；网络中断 / 流提前结束会明确抛出 <see cref="InvalidDataException"/>。
/// 每个实例只持有自己的解码状态与行游标，多个实例可并发使用。
///
/// 流式策略：
/// - VP8L（无损）走行增量解码器 <see cref="Vp8LRowDecoder"/>：只顺序读取到 VP8L chunk，
///   之后按输出行推进位流，常驻像素只有约 4.2 MB 的 LZ77 环形历史缓冲。
/// - VP8（有损，含 ALPH）暂时保留整帧解码回退（行级化需要窗口化 VP8 解码器，见 spec 4B），
///   容器解析本身仍是顺序读取。
/// </summary>
internal sealed class WebpRowSource : IRowImageSource
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[]? _rgba;          // 有损回退路径的整帧 RGBA
    private readonly Vp8LRowDecoder? _rowDecoder;
    private readonly uint[]? _argbRow;       // 行路径的当前行 ARGB 工作区
    private readonly object _lock = new();
    private int _nextRow;
    private bool _disposed;

    internal WebpRowSource(string path)
        : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan), ownsStream: true)
    {
    }

    /// <summary>供测试/内存流使用；实例接管流的释放。</summary>
    internal WebpRowSource(Stream stream)
        : this(stream, ownsStream: true)
    {
    }

    private WebpRowSource(Stream stream, bool ownsStream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ownsStream = ownsStream;

        int width, height;
        bool hasAlpha;
        byte[]? rgba = null;
        Vp8LRowDecoder? rowDecoder;
        try
        {
            try
            {
                ParseStaticImage(stream, out width, out height, out hasAlpha, out rowDecoder, out rgba);
            }
            catch (WebpUnsupportedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is WebpContainerException or WebpBitstreamException
                or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException("无效或不完整的 WebP 数据流", ex);
            }
        }
        catch
        {
            if (ownsStream) stream.Dispose();
            throw;
        }

        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
        Channels = hasAlpha ? 4 : 3;
        _rowDecoder = rowDecoder;
        _rgba = rgba;
        _argbRow = rowDecoder != null ? new uint[width] : null;
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public bool HasAlpha { get; }

    internal static bool IsSupported(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return WebpHeader.TryRead(fs, out _, out _, out _);
        }
        catch
        {
            return false;
        }
    }

    public bool ReadRow(Span<byte> row)
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WebpRowSource));
            if (_nextRow >= Height) return false;

            int needed = checked(Width * Channels);
            if (row.Length < needed)
                throw new ArgumentException("Row buffer too small", nameof(row));

            if (_rowDecoder != null)
            {
                var argb = _argbRow!;
                if (!_rowDecoder.TryReadArgbRow(argb)) return false;

                if (Channels == 4)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        uint p = argb[x];
                        int d = x * 4;
                        row[d] = (byte)(p >> 16);       // R
                        row[d + 1] = (byte)(p >> 8);    // G
                        row[d + 2] = (byte)p;           // B
                        row[d + 3] = (byte)(p >> 24);   // A
                    }
                }
                else
                {
                    for (int x = 0; x < Width; x++)
                    {
                        uint p = argb[x];
                        int d = x * 3;
                        row[d] = (byte)(p >> 16);
                        row[d + 1] = (byte)(p >> 8);
                        row[d + 2] = (byte)p;
                    }
                }
            }
            else
            {
                int sourceOffset = _nextRow * Width * 4;
                if (Channels == 4)
                {
                    _rgba!.AsSpan(sourceOffset, Width * 4).CopyTo(row);
                }
                else
                {
                    var source = _rgba!.AsSpan(sourceOffset, Width * 4);
                    for (int x = 0; x < Width; x++)
                    {
                        int s = x * 4;
                        int d = x * 3;
                        row[d] = source[s];
                        row[d + 1] = source[s + 1];
                        row[d + 2] = source[s + 2];
                    }
                }
            }

            _nextRow++;
            return true;
        }
    }

    /// <summary>
    /// 顺序解析 RIFF/WebP 容器并决定解码路径。VP8L 直接构造行增量解码器；
    /// VP8（有损）把已读字节与剩余流拼成完整文件后回退到整帧解码（保持既有行为）。
    /// </summary>
    private static void ParseStaticImage(Stream stream, out int width, out int height, out bool hasAlpha,
                                         out Vp8LRowDecoder? rowDecoder, out byte[]? rgba)
    {
        width = 0;
        height = 0;
        hasAlpha = false;
        rowDecoder = null;
        rgba = null;

        Span<byte> head = stackalloc byte[WebpMagic.HeaderSize];
        ReadExactly(stream, head, "WebP 流在 RIFF 头之前结束");
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != WebpMagic.Riff ||
            BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != WebpMagic.Webp)
        {
            throw new WebpContainerException("不是有效的 WebP 文件（RIFF/WEBP 标识不符）");
        }

        // RIFF size 覆盖 "WEBP" 标识之后的全部字节。此时已消费 12 字节，剩余应为 size-4。
        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]);
        if (riffSize < 4)
            throw new WebpContainerException("RIFF 声明长度非法（小于 WEBP 标识长度）");
        long declaredRemaining = (long)riffSize - 4;
        if (stream.CanSeek)
        {
            long actualRemaining = stream.Length - stream.Position;
            if (actualRemaining < declaredRemaining)
                throw new WebpContainerException(
                    $"WebP 数据被截断：RIFF 声明还需 {declaredRemaining} 字节，实际仅剩 {actualRemaining} 字节");
        }

        bool hasVp8X = false;
        bool vp8xAlpha = false;
        int canvasWidth = 0;
        int canvasHeight = 0;

        // 有损回退需要从头重建文件；只在找到主图像 chunk 之前缓存已消费的字节。
        using var capture = new MemoryStream();
        capture.Write(head);

        while (true)
        {
            Span<byte> chunkHeader = stackalloc byte[8];
            int got = ReadUpTo(stream, chunkHeader);
            if (got == 0) break; // 正常到达流末尾
            if (got < 8) throw new WebpContainerException("chunk 头被截断");

            WebpChunkId id = (WebpChunkId)BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            // 回退路径需要逐字节还原文件，chunk 头必须先入缓存（数据在各自分支里追加）。
            capture.Write(chunkHeader);

            if (size > int.MaxValue)
                throw new WebpContainerException($"chunk {id} 长度 {size} 超过 2GB");
            if ((long)size + 8 > declaredRemaining)
                throw new WebpContainerException(
                    $"chunk {id} 声明长度 {size} 超出 RIFF 剩余长度 {declaredRemaining}");

            switch (id)
            {
                case WebpChunkId.Vp8X:
                {
                    byte[] vp8x = ReadChunkData(stream, size, capture);
                    if (vp8x.Length < 10)
                        throw new WebpContainerException("VP8X chunk 过短（至少 10 字节）");

                    hasVp8X = true;
                    byte flags = vp8x[0];
                    if ((flags & (byte)Vp8XFlags.Animation) != 0)
                    {
                        throw new WebpUnsupportedException(
                            "流式逐行解码不支持动图 WebP（无需整帧缓冲的静态图像）");
                    }
                    vp8xAlpha = (flags & (byte)Vp8XFlags.Alpha) != 0;
                    canvasWidth = (vp8x[4] | (vp8x[5] << 8) | (vp8x[6] << 16)) + 1;
                    canvasHeight = (vp8x[7] | (vp8x[8] << 8) | (vp8x[9] << 16)) + 1;
                    break;
                }
                case WebpChunkId.Anim:
                case WebpChunkId.Anmf:
                    throw new WebpUnsupportedException(
                        "流式逐行解码不支持动图 WebP（无需整帧缓冲的静态图像）");

                case WebpChunkId.Alph:
                    // 独立 alpha 通道只能与有损 VP8 配合；内容随回退路径一起缓存。
                    ReadChunkData(stream, size, capture);
                    break;

                case WebpChunkId.Vp8L:
                {
                    byte[] payload = ReadChunkData(stream, size, capture: null);
                    if (payload.Length < 5)
                        throw new WebpContainerException("VP8L chunk 过短（至少 5 字节帧头）");

                    rowDecoder = new Vp8LRowDecoder(payload);
                    width = rowDecoder.Width;
                    height = rowDecoder.Height;

                    // 简单格式的 alpha 来自 VP8L 帧头 bit 28；VP8X 存在时与容器语义一致，以 flags 为准。
                    bool vp8lAlpha = (payload[4] & 0x10) != 0;
                    hasAlpha = hasVp8X ? vp8xAlpha : vp8lAlpha;

                    if (hasVp8X && (canvasWidth != width || canvasHeight != height))
                    {
                        throw new WebpBitstreamException(
                            $"VP8X 画布 {canvasWidth}x{canvasHeight} 与 VP8L 图像 {width}x{height} 不一致");
                    }
                    return;
                }

                case WebpChunkId.Vp8:
                {
                    // 有损 VP8：把已缓存的头部/ALPH 与当前 chunk 及剩余流拼接，走整帧解码。
                    CopyChunkToCapture(stream, size, capture);
                    SkipPadding(stream, size, capture);
                    CopyRemaining(stream, capture);

                    byte[] full = capture.ToArray();
                    rgba = WebpDecoder.DecodeRgba(full, out width, out height);
                    hasAlpha = hasVp8X && vp8xAlpha;

                    if (hasVp8X && (canvasWidth != width || canvasHeight != height))
                    {
                        throw new WebpBitstreamException(
                            $"VP8X 画布 {canvasWidth}x{canvasHeight} 与 VP8 图像 {width}x{height} 不一致");
                    }
                    return;
                }

                default:
                    // ICCP / EXIF / XMP 等元数据：跳过，但保留字节以备有损回退。
                    ReadChunkData(stream, size, capture);
                    break;
            }

            declaredRemaining -= (long)size + 8 + (size & 1);
        }

        throw new WebpContainerException("WebP 文件中未找到图像数据（VP8 / VP8L）");
    }

    /// <summary>读满 target 字节；短读允许，读到 0 视为截断。</summary>
    private static void ReadExactly(Stream stream, Span<byte> dst, string error)
    {
        int filled = 0;
        while (filled < dst.Length)
        {
            int read = stream.Read(dst[filled..]);
            if (read <= 0) throw new WebpContainerException(error);
            filled += read;
        }
    }

    /// <summary>尽量读满 dst，返回实际读到的字节数（0 表示流已结束）。</summary>
    private static int ReadUpTo(Stream stream, Span<byte> dst)
    {
        int filled = 0;
        while (filled < dst.Length)
        {
            int read = stream.Read(dst[filled..]);
            if (read <= 0) break;
            filled += read;
        }
        return filled;
    }

    /// <summary>读取一个 chunk 的全部数据（含 padding 处理），可选写入 capture。</summary>
    private static byte[] ReadChunkData(Stream stream, uint size, MemoryStream? capture)
    {
        var data = new byte[(int)size];
        int filled = 0;
        while (filled < data.Length)
        {
            int read = stream.Read(data, filled, data.Length - filled);
            if (read <= 0) throw new WebpContainerException("chunk 数据被截断");
            filled += read;
        }
        if (capture != null) capture.Write(data, 0, data.Length);

        SkipPadding(stream, size, capture);
        return data;
    }

    /// <summary>RIFF 偶数对齐：奇数长度的 chunk 后跟 1 字节 padding（截断时容忍缺失）。</summary>
    private static void SkipPadding(Stream stream, uint size, MemoryStream? capture)
    {
        if ((size & 1) == 0) return;
        Span<byte> pad = stackalloc byte[1];
        if (ReadUpTo(stream, pad) == 1 && capture != null)
            capture.Write(pad);
    }

    /// <summary>把 chunk 数据以流式方式追加到 capture（避免一次性再分配大缓冲）。</summary>
    private static void CopyChunkToCapture(Stream stream, uint size, MemoryStream capture)
    {
        var buffer = new byte[64 * 1024];
        while (size > 0)
        {
            int take = (int)Math.Min(size, (uint)buffer.Length);
            int filled = 0;
            while (filled < take)
            {
                int read = stream.Read(buffer, filled, take - filled);
                if (read <= 0) throw new WebpContainerException("chunk 数据被截断");
                filled += read;
            }
            capture.Write(buffer, 0, take);
            size -= (uint)take;
        }
    }

    private static void CopyRemaining(Stream stream, MemoryStream capture)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            capture.Write(buffer, 0, read);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_ownsStream) _stream.Dispose();
    }

    /// <summary>轻量头部嗅探：只扫描到第一个 VP8X / VP8 / VP8L 头即返回，用于格式分发。</summary>
    private static class WebpHeader
    {
        internal static bool TryRead(Stream stream, out int width, out int height, out bool hasAlpha)
        {
            width = height = 0;
            hasAlpha = false;
            Span<byte> head = stackalloc byte[12];
            if (stream.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12) return false;
            if (head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F'
                || head[8] != 'W' || head[9] != 'E' || head[10] != 'B' || head[11] != 'P') return false;

            Span<byte> chunk = stackalloc byte[8];
            Span<byte> data = stackalloc byte[10];
            while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                if (chunk[0] == 'V' && chunk[1] == 'P' && chunk[2] == '8' && chunk[3] == 'X')
                {
                    if (size < 10 || stream.ReadAtLeast(data, 10, throwOnEndOfStream: false) < 10) return false;
                    hasAlpha = (data[0] & 0x10) != 0;
                    width = (data[4] | (data[5] << 8) | (data[6] << 16)) + 1;
                    height = (data[7] | (data[8] << 8) | (data[9] << 16)) + 1;
                    return true;
                }
                if (chunk[0] == 'V' && chunk[1] == 'P' && chunk[2] == '8' && chunk[3] == ' ')
                {
                    if (size < 10 || stream.ReadAtLeast(data, 10, throwOnEndOfStream: false) < 10) return false;
                    if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A) return false;
                    width = (data[6] | (data[7] << 8)) & 0x3FFF;
                    height = (data[8] | (data[9] << 8)) & 0x3FFF;
                    return width > 0 && height > 0;
                }
                if (chunk[0] == 'V' && chunk[1] == 'P' && chunk[2] == '8' && chunk[3] == 'L')
                {
                    if (size < 5 || stream.ReadAtLeast(data[..5], 5, throwOnEndOfStream: false) < 5) return false;
                    if (data[0] != 0x2F) return false;
                    uint bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                    hasAlpha = ((bits >> 28) & 1) != 0;
                    return true;
                }
                stream.Seek((long)size + (size & 1), SeekOrigin.Current);
            }
            return false;
        }
    }
}
