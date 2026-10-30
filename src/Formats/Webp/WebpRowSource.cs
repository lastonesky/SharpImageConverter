using System;
using System.Buffers.Binary;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>
    /// WebP 逐行源：用 libwebp 的增量解码 API 边喂数据边取行。
    /// </summary>
    /// <remarks>
    /// libwebp 只有“整幅编码”入口，没有行/块编码 API；因此这条路径只覆盖
    /// <c>WebP → BMP/PNG</c>，不能用于 <c>→ WebP</c>。
    /// 增量解码仍会把解码结果放在 libwebp 内部缓冲里，但调用方无需再持有一整幅 RGBA。
    /// </remarks>
    internal sealed unsafe class WebpRowSource : IRowImageSource
    {
        private readonly FileStream _stream;
        private readonly byte[] _chunk = new byte[64 * 1024];
        private readonly IntPtr _output;
        private readonly int _outputStride;
        private IntPtr _decoder;
        private int _lastY;
        private int _stride;
        private int _nextRow;
        private bool _finished;

        internal WebpRowSource(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            if (!WebpHeader.TryRead(_stream, out int width, out int height, out bool hasAlpha))
            {
                throw new InvalidDataException("Invalid WebP header");
            }
            Width = width;
            Height = height;
            HasAlpha = hasAlpha;
            Channels = hasAlpha ? 4 : 3;
            _stream.Seek(0, SeekOrigin.Begin);
            _outputStride = Width * 4;
            _output = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)((long)Width * Height * 4));
            if (_output == IntPtr.Zero)
            {
                throw new InsufficientMemoryException(
                    $"无法分配 WebP 流式输出缓冲（{Width}x{Height}x4 字节）");
            }
            _decoder = WebpCodec.CreateIncrementalRgbaDecoder(Width, Height, _output, _outputStride);
            if (_decoder == IntPtr.Zero)
            {
                System.Runtime.InteropServices.NativeMemory.Free((void*)_output);
                throw new InvalidOperationException("WebP incremental decoder creation failed");
            }
            Pump();
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
            if (_nextRow >= Height) return false;
            if (row.Length < Width * Channels) throw new ArgumentException("Row buffer too small", nameof(row));

            while (_nextRow >= _lastY)
            {
                if (_finished)
                {
                    if (_nextRow >= Height) return false;
                    throw new InvalidDataException("WebP stream ended before all rows were decoded");
                }
                Pump();
            }

            IntPtr pointer = WebpCodec.GetIncrementalRgba(_decoder, out _lastY, out int width, out int height, out _stride);
            if (pointer == IntPtr.Zero || width != Width || height != Height || _nextRow >= _lastY)
            {
                throw new InvalidDataException("WebP incremental decode returned no row");
            }

            int sourceOffset = _nextRow * _stride;
            byte* source = (byte*)pointer + sourceOffset;
            if (Channels == 4)
            {
                new ReadOnlySpan<byte>(source, Width * 4).CopyTo(row);
            }
            else
            {
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

        private void Pump()
        {
            while (!_finished && _nextRow >= _lastY)
            {
                int read = _stream.Read(_chunk, 0, _chunk.Length);
                if (read == 0)
                {
                    _finished = true;
                    // 最后一轮：再取一次已解码行
                    IntPtr last = WebpCodec.GetIncrementalRgba(_decoder, out _lastY, out _, out _, out _stride);
                    _ = last;
                    return;
                }

                var status = WebpCodec.AppendIncremental(_decoder, _chunk.AsSpan(0, read));
                if (status is not (VP8StatusCode.VP8_STATUS_OK
                    or VP8StatusCode.VP8_STATUS_SUSPENDED
                    or VP8StatusCode.VP8_STATUS_NOT_ENOUGH_DATA))
                {
                    throw new InvalidDataException($"WebP incremental decode failed: {status}");
                }

                IntPtr pointer = WebpCodec.GetIncrementalRgba(_decoder, out _lastY, out int width, out int height, out _stride);
                if (pointer != IntPtr.Zero)
                {
                    if (width != Width || height != Height) throw new InvalidDataException("WebP dimensions changed during decode");
                }
                if (status == VP8StatusCode.VP8_STATUS_OK)
                {
                    // 若 libwebp 一次喂完就返回 OK，last_y 已可用；继续循环让 ReadRow 取行。
                    if (_lastY >= Height) _finished = true;
                }
            }
        }

        public void Dispose()
        {
            WebpCodec.DeleteIncrementalDecoder(_decoder);
            _decoder = IntPtr.Zero;
            if (_output != IntPtr.Zero) System.Runtime.InteropServices.NativeMemory.Free((void*)_output);
            _stream.Dispose();
        }

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
}
