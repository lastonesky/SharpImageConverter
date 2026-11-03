using System;
using System.Buffers.Binary;

namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// LSB-first 位流读取器，VP8L 无损格式使用。
///
/// 实现要点：内部维护一个 64 位缓冲，按 4 字节批量 refill；常见路径
/// （<see cref="PeekBits"/> + <see cref="Advance"/>）不逐位循环，也不在每次取位时
/// 重复做边界判断，把每个 bit 的摊销成本压到几条指令。
/// </summary>
internal ref struct LsbBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private ulong _buffer;       // 低位是下一个待读 bit
    private int _bitsInBuffer;   // _buffer 中的有效位数（0~64）
    private int _bytePos;        // 尚未装入缓冲的字节偏移
    private long _totalBitsRead;

    public LsbBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _buffer = 0;
        _bitsInBuffer = 0;
        _bytePos = 0;
        _totalBitsRead = 0;
    }

    /// <summary>当前已读位数（方便调试与测试）。</summary>
    public int TotalBitsRead => (int)_totalBitsRead;

    /// <summary>剩余未读位数（含缓冲内尚未消费的位）。</summary>
    public long RemainingBits => _bitsInBuffer + (long)(_data.Length - _bytePos) * 8;

    /// <summary>是否已读完全部数据。</summary>
    public bool IsEnd => RemainingBits <= 0;

    /// <summary>返回剩余的未读字节数（粗略估计，最后一个字节的剩余 bit 不计入）。</summary>
    public int RemainingBytes => (int)(RemainingBits >> 3);

    /// <summary>把数据搬进 64 位缓冲。4 字节批量优先，尾部逐字节补齐。</summary>
    private void Refill()
    {
        while (_bitsInBuffer <= 32 && _bytePos + 4 <= _data.Length)
        {
            _buffer |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_bytePos, 4)) << _bitsInBuffer;
            _bytePos += 4;
            _bitsInBuffer += 32;
        }
        while (_bitsInBuffer <= 56 && _bytePos < _data.Length)
        {
            _buffer |= (ulong)_data[_bytePos++] << _bitsInBuffer;
            _bitsInBuffer += 8;
        }
    }

    /// <summary>
    /// 预读 n 个 bit（1~32）但不消费。数据不足时高位补 0；
    /// 调用方若要求严格边界，应先检查 <see cref="RemainingBits"/>。
    /// </summary>
    public uint PeekBits(int n)
    {
        if (n <= 0) return 0;
        if (_bitsInBuffer < n) Refill();
        ulong mask = (1UL << n) - 1;
        return (uint)(_buffer & mask);
    }

    /// <summary>丢弃前 n 个 bit（1~32），不检查边界。</summary>
    public void SkipBits(int n)
    {
        if (n <= 0) return;
        if (_bitsInBuffer < n) Refill();
        int take = Math.Min(n, _bitsInBuffer);
        _buffer >>= take;
        _bitsInBuffer -= take;
        _totalBitsRead += take;
    }

    /// <summary>消费前 n 个 bit；越界抛 <see cref="WebpBitstreamException"/>。</summary>
    public void Advance(int n)
    {
        if (n <= 0) return;
        if (n > RemainingBits)
            throw new WebpBitstreamException("LSB 位流意外结束");
        SkipBits(n);
    }

    /// <summary>读取 1 bit。返回 0 或 1。到末尾抛 <see cref="WebpBitstreamException"/>。</summary>
    public int ReadBit()
    {
        if (RemainingBits < 1)
            throw new WebpBitstreamException("LSB 位流意外结束");

        if (_bitsInBuffer == 0) Refill();
        int bit = (int)(_buffer & 1);
        _buffer >>= 1;
        _bitsInBuffer--;
        _totalBitsRead++;
        return bit;
    }

    /// <summary>读取 n 个 bit（LSB 先入），拼成一个整数返回。</summary>
    public uint ReadBits(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n > 32) throw new ArgumentOutOfRangeException(nameof(n), "最多读取 32 位");
        if (n == 0) return 0;

        if (n > RemainingBits)
            throw new WebpBitstreamException("LSB 位流意外结束");

        uint result = 0;
        int done = 0;
        while (done < n)
        {
            if (_bitsInBuffer == 0) Refill();
            int take = Math.Min(n - done, _bitsInBuffer);
            ulong mask = (1UL << take) - 1;
            result |= (uint)((_buffer & mask) << done);
            _buffer >>= take;
            _bitsInBuffer -= take;
            _totalBitsRead += take;
            done += take;
        }
        return result;
    }

    /// <summary>跳过到下一个字节对齐位置（丢弃当前字节剩余 bit）。</summary>
    public void AlignToByte()
    {
        int rem = (int)(_totalBitsRead & 7);
        if (rem != 0) SkipBits(8 - rem);
    }

    /// <summary>
    /// 读取一个有符号数值，使用 VP8L 的 "sign-extended" 方案：
    /// 先读 (nbits - 1) 位绝对值，再读 1 位符号位（1 = 负）。
    /// </summary>
    public int ReadSigned(int nbits)
    {
        if (nbits <= 1)
            throw new ArgumentOutOfRangeException(nameof(nbits), "signed 至少需要 2 位");
        int value = (int)ReadBits(nbits - 1);
        int sign = ReadBit();
        return sign == 1 ? -value : value;
    }
}
