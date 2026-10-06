using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpImageConverter.Core;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Png;

/// <summary>
/// PNG 行滤波策略。
/// </summary>
public enum PngFilterMode
{
    /// <summary>全部行使用 Up 滤波（默认，速度最快）。</summary>
    Up = 0,

    /// <summary>逐行在 5 种滤波器中选代价最小者，压缩率最好。</summary>
    Adaptive = 1,

    /// <summary>不做滤波。</summary>
    None = 2,
}

/// <summary>
/// 简单的 PNG 写入器，支持灰度、RGB24、RGBA32 与调色板（颜色类型 3）。
/// </summary>
public static class PngWriter
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] TypeIHDR = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    private static readonly byte[] TypeIDAT = [(byte)'I', (byte)'D', (byte)'A', (byte)'T'];
    private static readonly byte[] TypeIEND = [(byte)'I', (byte)'E', (byte)'N', (byte)'D'];
    private static readonly byte[] TypePLTE = [(byte)'P', (byte)'L', (byte)'T', (byte)'E'];
    private static readonly byte[] TypeTRNS = [(byte)'t', (byte)'R', (byte)'N', (byte)'S'];
    private static readonly byte[] TypeEXIF = [(byte)'e', (byte)'X', (byte)'I', (byte)'f'];
    private static readonly byte[] TypeICCP = [(byte)'i', (byte)'C', (byte)'C', (byte)'P'];
    private static readonly byte[] TypeSRGB = [(byte)'s', (byte)'R', (byte)'G', (byte)'B'];

    public static void WriteGray(string path, int width, int height, byte[] gray, ImageMetadata? metadata = null)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            WriteGray(fs, width, height, gray, metadata);
        }
    }

    public static void WriteGray(Stream stream, int width, int height, byte[] gray, ImageMetadata? metadata = null)
        => WriteGray(stream, width, height, gray, metadata, PngFilterMode.Up, CompressionLevel.Optimal);

    /// <summary>
    /// 写入灰度 PNG 流（颜色类型 0）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="gray">灰度像素数据</param>
    /// <param name="metadata">可选的元数据</param>
    /// <param name="filter">行滤波策略</param>
    /// <param name="level">Deflate 压缩级别</param>
    public static void WriteGray(Stream stream, int width, int height, byte[] gray, ImageMetadata? metadata, PngFilterMode filter, CompressionLevel level)
    {
        stream.Write(PngSignature, 0, PngSignature.Length);
        WriteChunk(stream, TypeIHDR, CreateIHDRGray(width, height));
        WriteMetadata(stream, metadata);
        WriteIdat(stream, gray, width, height, 1, filter, level);
        WriteChunk(stream, TypeIEND, []);
    }
    /// <summary>
    /// 写入 RGB24 PNG 文件（颜色类型 2）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write(string path, int width, int height, byte[] rgb, ImageMetadata? metadata = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(fs, width, height, rgb, metadata);
    }

    /// <summary>
    /// 写入 RGB24 PNG 流（颜色类型 2）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    public static void Write(Stream stream, int width, int height, byte[] rgb, ImageMetadata? metadata = null)
        => Write(stream, width, height, rgb, metadata, PngFilterMode.Up, CompressionLevel.Optimal);

    /// <summary>
    /// 写入 RGB24 PNG 流（颜色类型 2），可指定滤波策略与压缩级别。
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgb">RGB24 像素数据</param>
    /// <param name="metadata">可选的元数据</param>
    /// <param name="filter">行滤波策略</param>
    /// <param name="level">Deflate 压缩级别</param>
    public static void Write(Stream stream, int width, int height, byte[] rgb, ImageMetadata? metadata, PngFilterMode filter, CompressionLevel level)
    {
        stream.Write(PngSignature, 0, PngSignature.Length);
        WriteChunk(stream, TypeIHDR, CreateIHDR(width, height));
        WriteMetadata(stream, metadata);
        WriteIdat(stream, rgb, width, height, 3, filter, level);
        WriteChunk(stream, TypeIEND, []);
    }

    /// <summary>
    /// 写入 RGBA32 PNG 文件（颜色类型 6）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    public static void WriteRgba(string path, int width, int height, byte[] rgba, ImageMetadata? metadata = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteRgba(fs, width, height, rgba, metadata);
    }

    /// <summary>
    /// 写入 RGBA32 PNG 流（颜色类型 6）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    public static void WriteRgba(Stream stream, int width, int height, byte[] rgba, ImageMetadata? metadata = null)
        => WriteRgba(stream, width, height, rgba, metadata, PngFilterMode.Up, CompressionLevel.Optimal);

    /// <summary>
    /// 写入 RGBA32 PNG 流（颜色类型 6），可指定滤波策略与压缩级别。
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    /// <param name="metadata">可选的元数据</param>
    /// <param name="filter">行滤波策略</param>
    /// <param name="level">Deflate 压缩级别</param>
    public static void WriteRgba(Stream stream, int width, int height, byte[] rgba, ImageMetadata? metadata, PngFilterMode filter, CompressionLevel level)
    {
        stream.Write(PngSignature, 0, PngSignature.Length);
        WriteChunk(stream, TypeIHDR, CreateIHDRRgba(width, height));
        WriteMetadata(stream, metadata);
        WriteIdat(stream, rgba, width, height, 4, filter, level);
        WriteChunk(stream, TypeIEND, []);
    }

    /// <summary>
    /// 写入调色板 PNG 文件（颜色类型 3，8 位索引）。
    /// 每像素只占 1 字节，是 PNG 有损压缩的主要手段。
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="indices">每像素 1 字节的调色板索引，长度 width*height</param>
    /// <param name="paletteRgb">调色板 RGB，长度 3*颜色数（≤768）</param>
    /// <param name="paletteAlpha">调色板 alpha（每项对应一个颜色）；为 null 表示全部不透明</param>
    public static void WritePalette(string path, int width, int height, byte[] indices, byte[] paletteRgb, byte[]? paletteAlpha = null, ImageMetadata? metadata = null)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WritePalette(fs, width, height, indices, paletteRgb, paletteAlpha, metadata, PngFilterMode.Adaptive, CompressionLevel.Optimal);
    }

    /// <summary>
    /// 写入调色板 PNG 流（颜色类型 3，8 位索引）。
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="indices">每像素 1 字节的调色板索引</param>
    /// <param name="paletteRgb">调色板 RGB</param>
    /// <param name="paletteAlpha">调色板 alpha；为 null 表示全部不透明</param>
    /// <param name="metadata">可选的元数据</param>
    /// <param name="filter">行滤波策略</param>
    /// <param name="level">Deflate 压缩级别</param>
    public static void WritePalette(
        Stream stream,
        int width,
        int height,
        byte[] indices,
        byte[] paletteRgb,
        byte[]? paletteAlpha = null,
        ImageMetadata? metadata = null,
        PngFilterMode filter = PngFilterMode.Adaptive,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(paletteRgb);
        if (paletteRgb.Length == 0 || paletteRgb.Length % 3 != 0 || paletteRgb.Length > 768)
        {
            throw new ArgumentException("调色板长度必须是 3 的倍数且不超过 768 字节", nameof(paletteRgb));
        }
        int colors = paletteRgb.Length / 3;
        if (paletteAlpha != null && paletteAlpha.Length < colors)
        {
            throw new ArgumentException("alpha 表长度不得小于颜色数", nameof(paletteAlpha));
        }
        if (indices.Length < width * height)
        {
            throw new ArgumentException("索引缓冲区长度与宽高不匹配", nameof(indices));
        }

        stream.Write(PngSignature, 0, PngSignature.Length);
        WriteChunk(stream, TypeIHDR, CreateIHDRPalette(width, height));
        WriteChunk(stream, TypePLTE, paletteRgb);
        if (paletteAlpha != null)
        {
            byte[] trns = TrimTrailingOpaque(paletteAlpha, colors);
            if (trns.Length > 0) WriteChunk(stream, TypeTRNS, trns);
        }
        WriteMetadata(stream, metadata);
        WriteIdat(stream, indices, width, height, 1, filter, level);
        WriteChunk(stream, TypeIEND, []);
    }

    /// <summary>
    /// 打包并写出 IDAT：按指定滤波策略生成扫描行，再做 zlib 压缩。
    /// </summary>
    private static void WriteIdat(Stream stream, byte[] src, int width, int height, int bytesPerPixel, PngFilterMode filter, CompressionLevel level)
    {
        int stride = width * bytesPerPixel;
        int rawSize = (stride + 1) * height;
        using var ms = new PooledMemoryStream(rawSize);
        ZlibHelper.CompressRaw(s =>
        {
            switch (filter)
            {
                case PngFilterMode.Adaptive:
                    PngAdaptiveFilter.WriteFiltered(s, src, width, height, bytesPerPixel);
                    break;
                case PngFilterMode.None:
                    WriteUnfilteredScanlines(s, src, width, height, bytesPerPixel);
                    break;
                default:
                    WriteUpFilteredScanlines(s, src, width, height, bytesPerPixel);
                    break;
            }
        }, ms, level);
        ArraySegment<byte> segment = ms.GetBuffer();
        WriteChunk(stream, TypeIDAT, segment.Array, segment.Offset, segment.Count);
    }

    /// <summary>
    /// tRNS 允许省略尾部的不透明项，这里裁掉尾部 255 以省字节。
    /// </summary>
    private static byte[] TrimTrailingOpaque(byte[] alpha, int colors)
    {
        int len = colors;
        while (len > 0 && alpha[len - 1] == 255) len--;
        if (len == 0) return [];
        var trimmed = new byte[len];
        Buffer.BlockCopy(alpha, 0, trimmed, 0, len);
        return trimmed;
    }

    private static void WriteUnfilteredScanlines(Stream s, byte[] src, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        byte zero = 0;
        int srcIdx = 0;
        for (int y = 0; y < height; y++)
        {
            s.WriteByte(zero);
            s.Write(src, srcIdx, stride);
            srcIdx += stride;
        }
    }

    private static void WriteChunk(Stream s, byte[] type, byte[] data)
    {
        WriteChunk(s, type, data, 0, data.Length);
    }

    private static void WriteChunk(Stream s, byte[] type, byte[] data, int offset, int count)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        WriteBigEndian((uint)count, lenBytes);
        s.Write(lenBytes);
        s.Write(type, 0, 4);
        if (count > 0)
        {
            s.Write(data, offset, count);
        }
        uint crc = Crc32.Compute(type);
        crc = Crc32.Update(crc, data, offset, count);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crc, crcBytes);
        s.Write(crcBytes);
    }

    private static void WriteMetadata(Stream stream, ImageMetadata? metadata)
    {
        if (metadata == null) return;
        if (metadata.ExifRaw != null && metadata.ExifRaw.Length > 0)
        {
            byte[] exif = metadata.ExifRaw;
            if (exif.Length >= 6 &&
                exif[0] == (byte)'E' && exif[1] == (byte)'x' && exif[2] == (byte)'i' && exif[3] == (byte)'f' &&
                exif[4] == 0 && exif[5] == 0)
            {
                var raw = new byte[exif.Length - 6];
                Buffer.BlockCopy(exif, 6, raw, 0, raw.Length);
                exif = raw;
            }
            WriteChunk(stream, TypeEXIF, exif);
        }
        if (metadata.IccProfile != null && metadata.IccProfile.Length > 0)
        {
            byte[] compressed = ZlibHelper.Compress(metadata.IccProfile);
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes("ICC Profile");
            int payloadLen = nameBytes.Length + 1 + 1 + compressed.Length;
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(nameBytes, 0, payload, 0, nameBytes.Length);
            payload[nameBytes.Length] = 0;
            payload[nameBytes.Length + 1] = 0;
            Buffer.BlockCopy(compressed, 0, payload, nameBytes.Length + 2, compressed.Length);
            WriteChunk(stream, TypeICCP, payload);
        }
        if (metadata.IccProfile == null && metadata.IccProfileKind == IccProfileKind.SRgb)
        {
            WriteChunk(stream, TypeSRGB, new byte[] { 0 });
        }
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

    private static byte[] CreateIHDRPalette(int width, int height)
    {
        byte[] data = new byte[13];
        Array.Copy(ToBigEndian((uint)width), 0, data, 0, 4);
        Array.Copy(ToBigEndian((uint)height), 0, data, 4, 4);
        data[8] = 8;
        data[9] = 3;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static byte[] CreateIHDRGray(int width, int height)
    {
        byte[] data = new byte[13];
        Array.Copy(ToBigEndian((uint)width), 0, data, 0, 4);
        Array.Copy(ToBigEndian((uint)height), 0, data, 4, 4);
        data[8] = 8;
        data[9] = 0;
        data[10] = 0;
        data[11] = 0;
        data[12] = 0;
        return data;
    }

    private static void WriteUpFilteredScanlines(Stream s, byte[] src, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        using var filtered = SimdHelper.AllocateAlignedBytes(stride + 1, alignment: SimdHelper.DefaultAlignment, clear: false, padToMultiple: Vector<byte>.Count);

        Span<byte> writeSpan = filtered.Span.Slice(0, stride + 1);
        Span<byte> filteredSpan = writeSpan.Slice(1, stride);
        int srcIdx = 0;
        for (int y = 0; y < height; y++)
        {
            writeSpan[0] = 2;
            ReadOnlySpan<byte> current = src.AsSpan(srcIdx, stride);
            if (y == 0)
            {
                current.CopyTo(filteredSpan);
            }
            else
            {
                ReadOnlySpan<byte> prev = src.AsSpan(srcIdx - stride, stride);
                ApplyUpFilterSimd(current, prev, filteredSpan);
            }
            s.Write(writeSpan);
            srcIdx += stride;
        }
    }
    private static void ApplyUpFilterSimd(ReadOnlySpan<byte> current, ReadOnlySpan<byte> prevRow, Span<byte> destination)
    {
        int length = current.Length;
        int i = 0;
        if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            // 直接按元素偏移载入/回写，避免每步 Slice(i) 的重复边界检查。
            // Up 滤波是回绕减法（与 PNG 规范一致），不能用带饱和的减法。
            int simdCount = Vector<byte>.Count;
            ref byte curRef = ref MemoryMarshal.GetReference(current);
            ref byte prevRef = ref MemoryMarshal.GetReference(prevRow);
            ref byte dstRef = ref MemoryMarshal.GetReference(destination);
            for (; i <= length - simdCount; i += simdCount)
            {
                var diff = Vector.Subtract(
                    Vector.LoadUnsafe(ref curRef, (nuint)i),
                    Vector.LoadUnsafe(ref prevRef, (nuint)i));
                diff.StoreUnsafe(ref dstRef, (nuint)i);
            }
        }
        for (; i < length; i++)
        {
            destination[i] = (byte)(current[i] - prevRow[i]);
        }
    }
    private static byte[] ToBigEndian(uint val)
    {
        return
        [
            (byte)((val >> 24) & 0xFF),
            (byte)((val >> 16) & 0xFF),
            (byte)((val >> 8) & 0xFF),
            (byte)(val & 0xFF)
        ];
    }

    private static void WriteBigEndian(uint val, Span<byte> dest)
    {
        dest[0] = (byte)((val >> 24) & 0xFF);
        dest[1] = (byte)((val >> 16) & 0xFF);
        dest[2] = (byte)((val >> 8) & 0xFF);
        dest[3] = (byte)(val & 0xFF);
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
                _buffer = [];
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
