using System;
using System.IO;

namespace SharpImageConverter;

/// <summary>
/// 位流读取器，支持按位读取与 JPEG 的字节填充处理。
/// </summary>
public class BitReader
{
    private readonly Stream _s;
    private int _bitBuf;
    private int _bitCount;
    private bool _eof;
    private readonly byte[] _buf;
    private int _bufPos;
    private int _bufLen;
    private long _bufEndPos;

    /// <summary>
    /// 使用输入流创建位读取器
    /// </summary>
    /// <param name="s">输入数据流</param>
    public BitReader(Stream s)
    {
        _s = s;
        _bitBuf = 0;
        _bitCount = 0;
        _eof = false;
        _buf = new byte[16 * 1024];
        _bufPos = 0;
        _bufLen = 0;
        _bufEndPos = _s.CanSeek ? _s.Position : 0;
    }

    /// <summary>
    /// 重置位缓冲（清空已缓存的位）
    /// </summary>
    public void ResetBits()
    {
        _bitBuf = 0;
        _bitCount = 0;
    }

    private bool FillBuffer()
    {
        _bufLen = _s.Read(_buf, 0, _buf.Length);
        _bufPos = 0;
        if (_bufLen <= 0) return false;
        if (_s.CanSeek) _bufEndPos = _s.Position;
        return true;
    }

    private int ReadRawByte()
    {
        if (_bufPos >= _bufLen)
        {
            if (!FillBuffer()) return -1;
        }
        return _buf[_bufPos++];
    }

    private long LogicalPosition
        => _s.CanSeek ? (_bufEndPos - (_bufLen - _bufPos)) : 0;

    private int ReadByteStuffed()
    {
        while (true)
        {
            int b = ReadRawByte();
            if (b == -1) { _eof = true; return -1; }
            if (b != 0xFF) return b;

            int n = ReadRawByte();
            if (n == -1) { _eof = true; return -1; }
            if (n == 0x00) return 0xFF;
            if (n >= 0xD0 && n <= 0xD7)
            {
                ResetBits();
                continue;
            }

            if (_s.CanSeek)
            {
                long markerStartPos = LogicalPosition - 2;
                _s.Position = markerStartPos;
                _bufPos = 0;
                _bufLen = 0;
                _bufEndPos = markerStartPos;
            }
            return -2;
        }
    }

    /// <summary>
    /// 读取 n 位并消耗它们
    /// </summary>
    /// <param name="n">位数</param>
    /// <returns>读取到的值</returns>
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

    /// <summary>
    /// 读取 1 位并消耗
    /// </summary>
    public int GetBit() => GetBits(1);

    /// <summary>
    /// 是否已到位流末尾
    /// </summary>
    public bool IsEOF => _eof;

    // 确保位缓冲中至少有 n 位（不消耗），用于快速霍夫曼查表
    /// <summary>
    /// 确保位缓冲中至少有 n 位（不消耗）
    /// </summary>
    /// <param name="n">需要的位数</param>
    /// <returns>缓冲足够则为 true</returns>
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
    /// <summary>
    /// 查看高位的 n 位（不消耗）
    /// </summary>
    /// <param name="n">位数</param>
    /// <returns>读取的值</returns>
    public int PeekBits(int n)
    {
        if (!EnsureBits(n)) throw new EndOfStreamException("位流不足");
        return (_bitBuf >> (_bitCount - n)) & ((1 << n) - 1);
    }

    // 丢弃高位的 n 位
    /// <summary>
    /// 丢弃高位的 n 位
    /// </summary>
    /// <param name="n">位数</param>
    public void DropBits(int n)
    {
        if (n > _bitCount) throw new ArgumentOutOfRangeException(nameof(n), "丢弃位数超过缓冲");
        _bitCount -= n;
        _bitBuf &= (1 << _bitCount) - 1;
    }
}
