using System;
using System.IO;

public class BitReader
{
    private readonly Stream _s;
    private int _bitBuf;
    private int _bitCount;
    private bool _eof;

    public BitReader(Stream s)
    {
        _s = s;
        _bitBuf = 0;
        _bitCount = 0;
        _eof = false;
    }

    public void ResetBits()
    {
        _bitBuf = 0;
        _bitCount = 0;
    }

    private int ReadByteStuffed()
    {
        int b = _s.ReadByte();
        if (b == -1) { _eof = true; return -1; }
        if (b == 0xFF)
        {
            int n = _s.ReadByte();
            if (n == -1) { _eof = true; return -1; }
            if (n == 0x00)
            {
                // stuffed 0xFF00 => data 0xFF
                return 0xFF;
            }
            if (n >= 0xD0 && n <= 0xD7)
            {
                // RSTn: reset bit buffer and continue to next byte
                ResetBits();
                return ReadByteStuffed();
            }
            // other marker: step back by 2 bytes for external logic
            _s.Position -= 2;
            return -2; // signal marker
        }
        return b;
    }

    public int GetBits(int n)
    {
        while (_bitCount < n && !_eof)
        {
            int b = ReadByteStuffed();
            if (b == -1 || b == -2) { _eof = true; break; }
            _bitBuf = (_bitBuf << 8) | b;
            _bitCount += 8;
        }
        if (_bitCount < n)
            throw new EndOfStreamException("位流不足");
        int res = (_bitBuf >> (_bitCount - n)) & ((1 << n) - 1);
        _bitCount -= n;
        _bitBuf &= (1 << _bitCount) - 1;
        return res;
    }

    public int GetBit() => GetBits(1);

    public bool IsEOF => _eof;

    // 确保位缓冲中至少有 n 位（不消耗），用于快速霍夫曼查表
    public bool EnsureBits(int n)
    {
        while (_bitCount < n && !_eof)
        {
            int b = ReadByteStuffed();
            if (b == -1 || b == -2) { _eof = true; break; }
            _bitBuf = (_bitBuf << 8) | b;
            _bitCount += 8;
        }
        return _bitCount >= n;
    }

    // 查看高位的 n 位，不消耗
    public int PeekBits(int n)
    {
        if (!EnsureBits(n)) throw new EndOfStreamException("位流不足");
        return (_bitBuf >> (_bitCount - n)) & ((1 << n) - 1);
    }

    // 丢弃高位的 n 位
    public void DropBits(int n)
    {
        if (n > _bitCount) throw new ArgumentOutOfRangeException(nameof(n), "丢弃位数超过缓冲");
        _bitCount -= n;
        _bitBuf &= (1 << _bitCount) - 1;
    }
}
