namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// LSB-first 位流读取器，VP8L 无损格式使用。
/// 从字节数组的当前位置按 little-endian 顺序逐个读取位。
/// </summary>
internal ref struct LsbBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;    // 当前字节偏移
    private int _bitPos; // 当前 bit 偏移（0~7）

    /// <summary>当前已读位数（方便调试）。</summary>
    public int TotalBitsRead { get; private set; }

    public LsbBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        _bitPos = 0;
        TotalBitsRead = 0;
    }

    /// <summary>是否已读完全部数据。</summary>
    public bool IsEnd => _pos >= _data.Length;

    /// <summary>读取 1 bit。返回 0 或 1。到末尾抛 <see cref="WebpBitstreamException"/>。</summary>
    public int ReadBit()
    {
        if (_pos >= _data.Length)
            throw new WebpBitstreamException("LSB 位流意外结束");

        int bit = (_data[_pos] >> _bitPos) & 1;
        _bitPos++;
        TotalBitsRead++;
        if (_bitPos == 8)
        {
            _bitPos = 0;
            _pos++;
        }
        return bit;
    }

    /// <summary>读取 n 个 bit（LSB 先入），拼成一个整数返回。</summary>
    public uint ReadBits(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n > 32) throw new ArgumentOutOfRangeException(nameof(n), "最多读取 32 位");

        uint result = 0;
        for (int i = 0; i < n; i++)
        {
            if (_pos >= _data.Length)
                throw new WebpBitstreamException("LSB 位流意外结束");

            int bit = (_data[_pos] >> _bitPos) & 1;
            result |= (uint)bit << i;
            _bitPos++;
            TotalBitsRead++;
            if (_bitPos == 8)
            {
                _bitPos = 0;
                _pos++;
            }
        }
        return result;
    }

    /// <summary>
    /// 跳过到下一个字节对齐位置（丢弃当前字节剩余 bit）。
    /// </summary>
    public void AlignToByte()
    {
        if (_bitPos != 0)
        {
            _bitPos = 0;
            _pos++;
        }
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

    /// <summary>
    /// 返回剩余的未读字节数（粗略估计，最后一个字节的剩余 bit 不计入）。
    /// </summary>
    public int RemainingBytes => _data.Length - _pos;
}
