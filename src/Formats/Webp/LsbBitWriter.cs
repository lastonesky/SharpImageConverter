using System;
using System.Buffers.Binary;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// LSB-first 位流写入器，与 <see cref="LsbBitReader"/> 对称，供 VP8L 编码器使用。
/// 对齐 libwebp src/utils/bit_writer_utils.h:147 VP8LPutBits()：
/// 64 bit 累加器，累计满 32 bit 时一次写出 4 字节，而不是每满 8 bit 刷一次。
/// Huffman 码字由 <see cref="Vp8LHuffmanCode"/> 在建表时预先反转为 LSB-first
/// （对齐 huffman_encode_utils.c:385-411 ConvertBitDepthsToSymbols）。
/// </summary>
internal sealed class LsbBitWriter
{
    private byte[] _bytes = new byte[256];
    private int _byteCount;
    private ulong _buffer;
    private int _bitsInBuffer;
    private long _totalBits;

    /// <summary>已写出的有效位数（含尚在缓冲中的位）。</summary>
    public long TotalBits => _totalBits;

    /// <summary>写入 n 个 bit（n ≤ 32），低位先写。</summary>
    public void WriteBits(uint value, int count)
    {
        if (count == 0) return;
        if (count < 0 || count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), "位宽必须在 0~32 之间");

        ulong mask = count == 32 ? 0xFFFFFFFFUL : (1UL << count) - 1;
        _buffer |= ((ulong)value & mask) << _bitsInBuffer;
        _bitsInBuffer += count;
        _totalBits += count;

        while (_bitsInBuffer >= 32) FlushWord();
    }

    /// <summary>写入 1 个 bit。</summary>
    public void WriteBit(int bit) => WriteBits((uint)(bit & 1), 1);

    /// <summary>写入 Huffman 码字：调用方已在建表时把规范码反转为 LSB-first。</summary>
    public void WriteCode(int code, int length)
    {
        if (length > 0) WriteBits((uint)code, length);
    }

    /// <summary>补齐到字节边界（不足一字节的尾部补 0）。</summary>
    public void AlignToByte()
    {
        while (_bitsInBuffer > 0)
        {
            EmitByte((byte)(_buffer & 0xFF));
            _buffer >>= 8;
            _bitsInBuffer = Math.Max(0, _bitsInBuffer - 8);
        }
    }

    /// <summary>输出已写出的全部字节（不足一字节的尾部补 0）。</summary>
    public byte[] ToArray()
    {
        AlignToByte();
        var result = new byte[_byteCount];
        Array.Copy(_bytes, result, _byteCount);
        return result;
    }

    private void FlushWord()
    {
        EmitUInt32((uint)_buffer);
        _buffer >>= 32;
        _bitsInBuffer -= 32;
    }

    private void EmitUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(_byteCount, 4), value);
        _byteCount += 4;
    }

    private void EmitByte(byte value)
    {
        EnsureCapacity(1);
        _bytes[_byteCount++] = value;
    }

    private void EnsureCapacity(int extra)
    {
        if (_byteCount + extra <= _bytes.Length) return;
        int newSize = _bytes.Length;
        while (newSize < _byteCount + extra) newSize = checked(newSize * 2);
        var grown = new byte[newSize];
        Array.Copy(_bytes, grown, _byteCount);
        _bytes = grown;
    }
}
