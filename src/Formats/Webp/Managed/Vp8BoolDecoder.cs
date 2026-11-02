namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// VP8 布尔算术解码器（Bool Decoder），有损 VP8 的帧头与 token 分区均使用。
///
/// 实现严格遵循 RFC 6386 §7.3 的参考算法（split / renorm / value 窗口），
/// 与 libwebp 的 VP8BitReader（utils/bit_reader_utils + bit_reader_inl_utils）
/// 在数学上逐位等价，仅结构不同：
/// - value 为 32 位窗口，初始化时装入前两字节（大端），renorm 时低位补新字节；
/// - 输入耗尽后以 0 填充并置 <see cref="Eof"/>，调用方据此识别截断流。
///
/// 概率 prob 语义：0..255，表示“下一个 bit 为 0”的概率（128 = 均匀）。
/// </summary>
internal ref struct Vp8BoolDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;        // 下一个待读字节下标
    private uint _range;     // 当前区间宽度，renorm 后保持在 [128, 255]
    private uint _value;     // 32 位值窗口（有效位始终落在低 16 位内）
    private int _bitCount;   // 自上次装入字节以来 renorm 的移位次数（0..7）
    private bool _eof;       // 是否已读穿输入（此后以 0 填充）

    public Vp8BoolDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _range = 255;
        _bitCount = 0;
        _eof = false;
        if (data.Length >= 2)
        {
            _value = (uint)((data[0] << 8) | data[1]);
            _pos = 2;
        }
        else if (data.Length == 1)
        {
            _value = (uint)(data[0] << 8);
            _pos = 1;
        }
        else
        {
            _value = 0;
            _pos = 0;
        }
    }

    /// <summary>是否已读穿输入缓冲区（截断流判定用）。</summary>
    public readonly bool Eof => _eof;

    /// <summary>
    /// 流是否合法：RFC 6386 要求初始 value &lt; range，即首字节不得为 0xFF。
    /// </summary>
    public readonly bool IsValid => _data.Length == 0 || _data[0] != 0xFF;

    /// <summary>
    /// 解码一个 bit。prob ∈ [0, 255] 为“结果为 0”的先验概率。
    /// </summary>
    public int GetBit(int prob)
    {
        // split ∈ [1, range-1]（prob 为 0/255 时取端点，与 libwebp 一致）
        uint split = 1u + (((_range - 1u) * (uint)prob) >> 8);
        uint bigSplit = split << 8;
        int bit;
        if (_value >= bigSplit)
        {
            bit = 1;
            _value -= bigSplit;
            _range -= split;
        }
        else
        {
            bit = 0;
            _range = split;
        }

        // 归一化：range 保持 ≥128，value 随之左移，低位从输入补字节
        while (_range < 128)
        {
            _value <<= 1;
            _range <<= 1;
            if (++_bitCount == 8)
            {
                _bitCount = 0;
                if (_pos < _data.Length)
                {
                    _value |= _data[_pos++];
                }
                else
                {
                    // 输入耗尽：以 0 填充（value 低位自然为 0），并标记截断
                    _eof = true;
                }
            }
        }
        return bit;
    }

    /// <summary>
    /// 读取 nbits 位的无符号整数（MSB 在前，每位概率 128）。
    /// </summary>
    public uint GetValue(int nbits)
    {
        uint v = 0;
        for (int i = nbits - 1; i >= 0; i--)
            v |= (uint)GetBit(128) << i;
        return v;
    }

    /// <summary>
    /// 读取“值 + 符号位”：先读 nbits 位绝对值，再读 1 位符号（1 = 负）。
    /// 对应 libwebp 的 VP8GetSignedValue。
    /// </summary>
    public int GetSignedValue(int nbits)
    {
        int v = (int)GetValue(nbits);
        return GetBit(128) == 1 ? -v : v;
    }

    /// <summary>
    /// 对已读出的绝对值 v 读取 1 位符号（1 = 负），返回带符号结果。
    /// 对应 libwebp 内联的 VP8GetSigned(br, v)。
    /// </summary>
    public int GetSigned(int v) => GetBit(128) == 1 ? -v : v;
}
