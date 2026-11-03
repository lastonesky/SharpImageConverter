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
/// 每个实例只持有自己的解码结果与行游标，多个实例可并发使用。
/// </summary>
internal sealed class WebpRowSource : IRowImageSource
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _rgba;
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

        byte[] data = ReadAll(stream);
        bool hasAlpha;
        int width, height;
        try
        {
            var container = WebpContainer.Load(data);
            if (container.IsAnimation)
                throw new WebpUnsupportedException("流式逐行解码不支持动图 WebP（无需整帧缓冲的静态图像）");
            hasAlpha = container.HasAlpha;
            _rgba = WebpDecoder.DecodeRgba(data, out width, out height);
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

        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
        Channels = hasAlpha ? 4 : 3;
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

            int sourceOffset = _nextRow * Width * 4;
            if (Channels == 4)
            {
                _rgba.AsSpan(sourceOffset, Width * 4).CopyTo(row);
            }
            else
            {
                var source = _rgba.AsSpan(sourceOffset, Width * 4);
                for (int x = 0; x < Width; x++)
                {
                    int s = x * 4;
                    int d = x * 3;
                    row[d] = source[s];
                    row[d + 1] = source[s + 1];
                    row[d + 2] = source[s + 2];
                }
            }
            _nextRow++;
            return true;
        }
    }

    /// <summary>
    /// 分块读完整条流。可定位流按剩余长度预分配；不可定位流（网络/管道）逐块累积。
    /// 任一次短读都不视为失败，只有读到 0 且仍有数据缺口（可定位流）才判定为截断。
    /// </summary>
    private static byte[] ReadAll(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining < 0) remaining = 0;
            if (remaining > int.MaxValue) throw new InvalidDataException("WebP 数据超过 2GB，无法一次性解码");

            var buffer = new byte[(int)remaining];
            int filled = 0;
            while (filled < buffer.Length)
            {
                int read = stream.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0)
                    throw new InvalidDataException("WebP 流在读取完成前结束（数据被截断）");
                filled += read;
            }
            return buffer;
        }

        using var ms = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0) break;
            ms.Write(chunk, 0, read);
            if (ms.Length > int.MaxValue) throw new InvalidDataException("WebP 数据超过 2GB，无法一次性解码");
        }
        if (ms.Length == 0) throw new InvalidDataException("WebP 流为空");
        return ms.ToArray();
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
