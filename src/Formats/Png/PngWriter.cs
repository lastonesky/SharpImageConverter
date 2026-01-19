using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Numerics;

namespace SharpImageConverter;

/// <summary>
/// 简单的 PNG 写入器，支持 RGB24 与 RGBA32。
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// 写入 RGB24 PNG 文件（颜色类型 2）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write(string path, int width, int height, byte[] rgb)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            Write(fs, width, height, rgb);
        }
    }

    /// <summary>
    /// 写入 RGB24 PNG 流（颜色类型 2）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write(Stream stream, int width, int height, byte[] rgb)
    {
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        WriteChunk(stream, "IHDR", CreateIHDR(width, height));
        int stride = width * 3;
        int rawSize = (stride + 1) * height;
        using (var ms = new PooledMemoryStream(rawSize))
        {
            ZlibHelper.CompressRaw(s =>
            {
                WriteUpFilteredScanlines(s, rgb, width, height, 3);
            }, ms);
            ArraySegment<byte> segment = ms.GetBuffer();
            WriteChunk(stream, "IDAT", segment.Array, segment.Offset, segment.Count);
        }
        WriteChunk(stream, "IEND", new byte[0]);
    }

    /// <summary>
    /// 写入 RGBA32 PNG 文件（颜色类型 6）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            WriteRgba(fs, width, height, rgba);
        }
    }

    /// <summary>
    /// 写入 RGBA32 PNG 流（颜色类型 6）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    public static void WriteRgba(Stream stream, int width, int height, byte[] rgba)
    {
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        WriteChunk(stream, "IHDR", CreateIHDRRgba(width, height));
        int stride = width * 4;
        int rawSize = (stride + 1) * height;
        using (var ms = new PooledMemoryStream(rawSize))
        {
            ZlibHelper.CompressRaw(s =>
            {
                WriteUpFilteredScanlines(s, rgba, width, height, 4);
            }, ms);
            ArraySegment<byte> segment = ms.GetBuffer();
            WriteChunk(stream, "IDAT", segment.Array, segment.Offset, segment.Count);
        }
        WriteChunk(stream, "IEND", new byte[0]);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        WriteChunk(s, type, data, 0, data.Length);
    }

    private static void WriteChunk(Stream s, string type, byte[] data, int offset, int count)
    {
        byte[] lenBytes = ToBigEndian((uint)count);
        s.Write(lenBytes, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        if (count > 0)
        {
            s.Write(data, offset, count);
        }
        uint crc = Crc32.Compute(typeBytes);
        crc = Crc32.Update(crc, data, offset, count);
        byte[] crcBytes = ToBigEndian(crc);
        s.Write(crcBytes, 0, 4);
    }

    private static byte[] CreateIHDR(int width, int height)
    {
        byte[] data = new byte[13];
        Array.Copy(ToBigEndian((uint)width), 0, data, 0, 4);
        Array.Copy(ToBigEndian((uint)height), 0, data, 4, 4);
        data[8] = 8;
        data[9] = 2;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CreateIHDRRgba(int width, int height)
    {
        byte[] data = new byte[13];
        Array.Copy(ToBigEndian((uint)width), 0, data, 0, 4);
        Array.Copy(ToBigEndian((uint)height), 0, data, 4, 4);
        data[8] = 8;
        data[9] = 6;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CreateIDAT(int width, int height, byte[] rgb)
    {
        int stride = width * 3;
        int rawSize = (stride + 1) * height;
        byte[] rawData = new byte[rawSize];
        int rawIdx = 0;
        int rgbIdx = 0;
        byte[] prevRow = new byte[stride];
        byte[] filtered = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            rawData[rawIdx++] = 2;
            ApplyUpFilterSimd(rgb.AsSpan(rgbIdx, stride), prevRow, filtered);
            Array.Copy(filtered, 0, rawData, rawIdx, stride);
            rgbIdx += stride;
            rawIdx += stride;
            Array.Copy(rgb, rgbIdx - stride, prevRow, 0, stride);
        }

        return ZlibHelper.Compress(rawData);
    }

    private static byte[] CreateIDATRgba(int width, int height, byte[] rgba)
    {
        int stride = width * 4;
        int rawSize = (stride + 1) * height;
        byte[] rawData = new byte[rawSize];
        int rawIdx = 0;
        int srcIdx = 0;
        byte[] prevRow = new byte[stride];
        byte[] filtered = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            rawData[rawIdx++] = 2;
            ApplyUpFilterSimd(rgba.AsSpan(srcIdx, stride), prevRow, filtered);
            Array.Copy(filtered, 0, rawData, rawIdx, stride);
            srcIdx += stride;
            rawIdx += stride;
            Array.Copy(rgba, srcIdx - stride, prevRow, 0, stride);
        }
        return ZlibHelper.Compress(rawData);
    }
    private static void WriteUpFilteredScanlines(Stream s, byte[] src, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        byte[] prevRow = new byte[stride];
        byte[] filtered = new byte[stride];
        int srcIdx = 0;
        for (int y = 0; y < height; y++)
        {
            s.WriteByte(2);
            ApplyUpFilterSimd(src.AsSpan(srcIdx, stride), prevRow, filtered);
            s.Write(filtered, 0, stride);
            Array.Copy(src, srcIdx, prevRow, 0, stride);
            srcIdx += stride;
        }
    }
    private static void ApplyUpFilterSimd(ReadOnlySpan<byte> current, byte[] prevRow, byte[] destination)
    {
        int length = current.Length;
        Span<byte> prevSpan = prevRow;
        Span<byte> destSpan = destination;
        int i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            int simdCount = Vector<byte>.Count;
            var mask = new Vector<ushort>(0xFF);
            for (; i <= length - simdCount; i += simdCount)
            {
                var curVec = new Vector<byte>(current.Slice(i));
                var prevVec = new Vector<byte>(prevSpan.Slice(i));
                Vector.Widen(curVec, out Vector<ushort> curLow, out Vector<ushort> curHigh);
                Vector.Widen(prevVec, out Vector<ushort> prevLow, out Vector<ushort> prevHigh);
                var resLow = (curLow - prevLow) & mask;
                var resHigh = (curHigh - prevHigh) & mask;
                var result = Vector.Narrow(resLow, resHigh);
                result.CopyTo(destSpan.Slice(i));
            }
        }
        for (; i < length; i++)
        {
            destSpan[i] = (byte)(current[i] - prevSpan[i]);
        }
    }
    private static byte[] ToBigEndian(uint val)
    {
        return new byte[]
        {
            (byte)((val >> 24) & 0xFF),
            (byte)((val >> 16) & 0xFF),
            (byte)((val >> 8) & 0xFF),
            (byte)(val & 0xFF)
        };
    }
}

internal sealed class PooledMemoryStream : Stream
{
    private byte[] _buffer;
    private int _length;
    private bool _disposed;

    public PooledMemoryStream(int initialCapacity)
    {
        if (initialCapacity < 1) initialCapacity = 1;
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _length = 0;
    }

    public ArraySegment<byte> GetBuffer()
    {
        return new ArraySegment<byte>(_buffer, 0, _length);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        EnsureCapacity(_length + count);
        buffer.AsSpan(offset, count).CopyTo(_buffer.AsSpan(_length));
        _length += count;
    }

    public override void WriteByte(byte value)
    {
        EnsureCapacity(_length + 1);
        _buffer[_length] = value;
        _length++;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                var buffer = _buffer;
                _buffer = Array.Empty<byte>();
                if (buffer.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length) return;
        int newSize = _buffer.Length * 2;
        if (newSize < required) newSize = required;
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _length).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
