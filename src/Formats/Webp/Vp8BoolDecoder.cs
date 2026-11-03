using System.Numerics;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 布尔算术解码器（Bool Decoder），有损 VP8 的帧头与 token 分区均使用。
///
/// 实现严格遵循 RFC 6386 §7.3 的参考算法（split / renorm / value 窗口），
/// 与 libwebp 的 VP8BitReader（utils/bit_reader_utils + bit_reader_inl_utils）
/// 在数学上逐位等价，仅结构不同：
/// - value 为 16 位窗口，初始化时装入前两字节（大端）；renorm 左移，每累计 8 次移位
///   时把低 8 位（此时必为 0）填入下一个输入字节；
/// - 输入耗尽后以 0 填充并置 <see cref="Eof"/>，调用方据此识别截断流。
///
/// 之所以是 class 而非 ref struct：VP8 的 token 分区可有 1..8 个，且按宏块行轮流驱动，
/// 多个解码器实例必须跨行长期存活，无法放进数组（ref struct 不能装箱/入数组）。
///
/// 实例持有外部传入的只读字节缓冲区引用，不复制数据；构造后不可回退，
/// 但同一个实例不可被多线程并发使用（每帧/每分区各自独占一个实例）。
/// 概率 prob 语义：0..255，表示“下一个 bit 为 0”的概率（128 = 均匀）。
/// </summary>
internal sealed class Vp8BoolDecoder
{
    /// <summary>value 窗口的位宽：value 恒小于 2^16（见类型注释中的不变式）。</summary>
    private const uint kWindowMask = 0xFFFF;

    private readonly byte[] _data;
    private readonly int _start;    // 输入切片起始下标
    private readonly int _end;      // 输入切片结束下标（不含）
    private int _pos;               // 下一个待读字节下标
    private uint _range;            // 当前区间宽度，renorm 后保持在 [128, 255]
    private uint _value;            // 16 位值窗口
    private int _bitCount;          // 自上次装入字节以来 renorm 的移位次数（0..7）
    private bool _eof;              // 是否已读穿输入（此后以 0 填充）

    /// <summary>从独立缓冲区构造（会复制一次数据，仅供单测/短生命周期场景）。</summary>
    public Vp8BoolDecoder(ReadOnlySpan<byte> data)
        : this(data.ToArray(), 0, data.Length)
    {
    }

    /// <summary>
    /// 从大缓冲区中切出一段独立区间构造（不复制数据）。各 token 分区共用同一帧缓冲区时使用。
    /// </summary>
    public Vp8BoolDecoder(byte[] data, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, data.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, data.Length - offset);

        _data = data;
        _start = offset;
        _end = offset + length;
        _range = 255;
        _bitCount = 0;
        _eof = false;
        _pos = offset;

        // RFC 6386 §7.3：初始 value 为前两个字节（大端），不足 2 字节时高位补 0。
        if (length >= 2)
        {
            _value = (uint)((data[offset] << 8) | data[offset + 1]);
            _pos = offset + 2;
        }
        else if (length == 1)
        {
            _value = (uint)(data[offset] << 8);
            _pos = offset + 1;
        }
        else
        {
            _value = 0;
        }
    }

    /// <summary>是否已读穿输入缓冲区（截断流判定用）。</summary>
    public bool Eof => _eof;

    /// <summary>
    /// 流是否合法：RFC 6386 要求初始 value &lt; range，即首个有效字节不得为 0xFF。
    /// </summary>
    public bool IsValid => _start == _end || _data[_start] != 0xFF;

    /// <summary>
    /// 解码一个 bit。prob ∈ [0, 255] 为“结果为 0”的先验概率。
    /// </summary>
    public int GetBit(int prob)
    {
        // split ∈ [1, range-1]（prob 为 0/255 时取端点）；即 RFC 6386 §7.3 的 split。
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

        // Normalize in one shift. A range in [1,127] needs at most seven
        // bits, so at most one byte refill can be crossed in this operation.
        if (_range < 128)
        {
            int shift = BitOperations.LeadingZeroCount(_range) - 24;
            _range <<= shift;
            _value = (_value << shift) & kWindowMask;
            int count = _bitCount + shift;
            if (count >= 8)
            {
                count -= 8;
                if (_pos < _end)
                {
                    _value |= (uint)_data[_pos++] << count;
                }
                else
                {
                    // 输入耗尽：以 0 填充（value 低位自然为 0），并标记截断
                    _eof = true;
                }
            }
            _bitCount = count;
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
