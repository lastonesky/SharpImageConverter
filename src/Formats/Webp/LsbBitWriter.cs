using System;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// LSB-first 位流写入器，与 <see cref="LsbBitReader"/> 对称，供 VP8L 编码器使用。
/// <c>WriteBits</c> 按低位先写；Huffman 码字按规范以 MSB-first 写出，故用 <c>WriteCode</c>。
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

        while (_bitsInBuffer >= 8) FlushByte();
    }

    /// <summary>写入 1 个 bit。</summary>
    public void WriteBit(int bit) => WriteBits((uint)(bit & 1), 1);

    /// <summary>
    /// 写入 Huffman 码字：规范中码字以 MSB-first 排列，而位流按字节 LSB-first 存放，
    /// 因此这里先反转码字再按位写入。
    /// </summary>
    public void WriteCode(int code, int length)
    {
        if (length <= 0) return;
        WriteBits((uint)ReverseBits(code, length), length);
    }

    /// <summary>补齐到字节边界（不足一字节的尾部补 0）。</summary>
    public void AlignToByte()
    {
        if (_bitsInBuffer > 0)
        {
            EmitByte((byte)(_buffer & 0xFF));
            _buffer >>= 8;
            _bitsInBuffer = 0;
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

    private void FlushByte()
    {
        EmitByte((byte)(_buffer & 0xFF));
        _buffer >>= 8;
        _bitsInBuffer -= 8;
    }

    private void EmitByte(byte value)
    {
        if (_byteCount == _bytes.Length)
        {
            int newSize = checked(_bytes.Length * 2);
            var grown = new byte[newSize];
            Array.Copy(_bytes, grown, _byteCount);
            _bytes = grown;
        }
        _bytes[_byteCount++] = value;
    }

    private static int ReverseBits(int value, int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }
        return result;
    }
}
