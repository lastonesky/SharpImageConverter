using System.Numerics;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8 布尔熵编码器。
///
/// 逐句移植自 libwebp <c>src/utils/bit_writer_utils.c</c> 的 <c>VP8BitWriter</c>：
/// <list type="bullet">
/// <item><c>_range</c> 内部保存“实际 range - 1”（init 254），因此分割点
/// <c>split = (_range * prob) &gt;&gt; 8</c> 等价于解码端 <c>split = 1 + ((range - 1) * prob &gt;&gt; 8)</c>；</item>
/// <item>重归一化用前导零计数一次计算位移，等价于逐次执行 <c>(r &lt;&lt; 1) | 1</c>，
/// 也等价于 C 实现的 <c>kNorm[] / kNewRange[]</c> 查表；</item>
/// <item>输出端用 <c>_run</c> 延迟写出连续 0xFF 字节，等后续进位到来时再统一处理，避免回写已输出数据。</item>
/// </list>
/// 与 <see cref="Vp8BoolDecoder"/> 严格互逆；实例非线程安全，一次编码由单个线程独占。
/// </summary>
internal sealed class Vp8BoolEncoder
{
    /// <summary>分割概率的基准位宽（RFC 6386：概率 0..255 表示 0..1）。</summary>
    private const int ProbBits = 8;

    /// <summary>重归一化阈值：内部 range（实际 range - 1）小于该值时必须左移。</summary>
    private const int RenormThreshold = 127;

    private byte[] _buffer;
    private int _pos;

    /// <summary>实际 range - 1，取值范围 [0, 254]（重归一化后恒为 [127, 255)）。</summary>
    private int _range = 255 - 1;

    /// <summary>低值累加器（C 侧为 int32，按 32 位补码语义运算）。</summary>
    private int _value;

    /// <summary>自上次 <see cref="Flush"/> 以来累计的重归一化位数，初值 -8。</summary>
    private int _nbBits = -8;

    /// <summary>已判定为 0xFF、但因可能进位而延迟写出的字节数。</summary>
    private int _run;

    /// <summary>成本模式：为 true 时 <see cref="PutBit"/> 只累加 <see cref="_cost"/>，不改编码器状态。</summary>
    private bool _costOnly;

    /// <summary>成本模式下累计的位成本，单位 1/256 bit（见 <see cref="Vp8CostTable"/>）。</summary>
    private long _cost;

    /// <summary>
    /// 进入成本模式：之后的写位调用只累加成本、不产出字节。用于率失真决策时精确对比
    /// 「同一段语法按不同内容写」的码率——两段都能复用同一批写位函数，不必另写码率模型。
    /// </summary>
    internal void BeginCost()
    {
        _costOnly = true;
        _cost = 0;
    }

    /// <summary>退出成本模式并返回累计成本（1/256 bit）。</summary>
    internal long EndCost()
    {
        _costOnly = false;
        return _cost;
    }

    internal Vp8BoolEncoder(int expectedBytes = 1024)
    {
        _buffer = new byte[Math.Max(expectedBytes, 16)];
    }

    /// <summary>已完成的整字节数（不含未 <see cref="Finish"/> 的尾部）。</summary>
    internal int Length => _pos;

    /// <summary>
    /// 写入一个按 <paramref name="prob"/> 编码的位。对应 libwebp <c>VP8PutBit</c>。
    /// </summary>
    internal void PutBit(int bit, int prob)
    {
        if (_costOnly)
        {
            _cost += Vp8CostTable.Cost[prob, bit];
            return;
        }

        int split = (_range * prob) >> ProbBits;
        if (bit != 0)
        {
            _value += split + 1;
            _range -= split + 1;
        }
        else
        {
            _range = split;
        }

        if (_range < RenormThreshold)
        {
            // _range stores actualRange - 1; normalize the actual range
            // once instead of iterating over every missing high bit.
            int shift = BitOperations.LeadingZeroCount((uint)(_range + 1)) - 24;
            _range = ((_range + 1) << shift) - 1;
            _value <<= shift;
            _nbBits += shift;
            if (_nbBits > 0)
                Flush();
        }
    }

    /// <summary>写入一个等概率位（prob = 128）。对应 libwebp <c>VP8PutBitUniform</c>。</summary>
    internal void PutBitUniform(int bit) => PutBit(bit, 128);

    /// <summary>
    /// 以等概率按高位在前的顺序写入 <paramref name="bitCount"/> 个位。对应 libwebp <c>VP8PutBits</c>。
    /// </summary>
    internal void PutLiteral(int value, int bitCount)
    {
        for (int mask = 1 << (bitCount - 1); mask != 0; mask >>= 1)
            PutBitUniform((value & mask) != 0 ? 1 : 0);
    }

    /// <summary>
    /// 写入有符号整数：先写“是否非零”，再按 <paramref name="bitCount"/> 写符号 + 绝对值的位。
    /// 对应 libwebp <c>VP8PutSignedBits</c>。
    /// </summary>
    internal void PutSignedBits(int value, int bitCount)
    {
        if (!PutBool(value != 0))
            return;
        if (value < 0)
            PutLiteral(((-value) << 1) | 1, bitCount + 1);
        else
            PutLiteral(value << 1, bitCount + 1);
    }

    /// <summary>写一个等概率位并返回其布尔形式，便于对应 C 的 <c>if (!VP8PutBitUniform(...))</c> 写法。</summary>
    private bool PutBool(bool bit)
    {
        PutBitUniform(bit ? 1 : 0);
        return bit;
    }

    /// <summary>对应 libwebp <c>Flush</c>：把已确定的一个字节（可能带进位）输出到缓冲区。</summary>
    private void Flush()
    {
        int s = 8 + _nbBits;
        int bits = _value >> s;
        _value -= bits << s;
        _nbBits -= 8;

        if ((bits & 0xff) != 0xff)
        {
            int pos = _pos;
            if ((bits & 0x100) != 0 && pos > 0)
            {
                // 进位：把上一个已输出字节 +1。由于 0xFF 字节都被 _run 延迟，这里不会产生二次进位。
                _buffer[pos - 1]++;
            }

            if (_run > 0)
            {
                byte fill = (bits & 0x100) != 0 ? (byte)0x00 : (byte)0xff;
                EnsureCapacity(_run + 1);
                for (; _run > 0; _run--)
                    _buffer[pos++] = fill;
            }

            EnsureCapacity(1);
            _buffer[pos++] = (byte)(bits & 0xff);
            _pos = pos;
        }
        else
        {
            _run++; // 延迟输出 0xFF，等待可能的进位。
        }
    }

    /// <summary>
    /// 结束编码并返回完整字节。对应 libwebp <c>VP8BitWriterFinish</c>：
    /// 先补 <c>9 - nbBits</c> 个 0 位把累加器清空，再强制最后一次 Flush。
    /// </summary>
    internal byte[] Finish()
    {
        PutLiteral(0, 9 - _nbBits);
        _nbBits = 0;
        Flush();
        return ToArray();
    }

    /// <summary>返回当前已写出的字节副本（不结束编码）。</summary>
    internal byte[] ToArray()
    {
        var result = new byte[_pos];
        Array.Copy(_buffer, result, _pos);
        return result;
    }

    private void EnsureCapacity(int extra)
    {
        int needed = _pos + extra;
        if (needed <= _buffer.Length)
            return;

        int newSize = _buffer.Length * 2;
        if (newSize < needed)
            newSize = needed;
        Array.Resize(ref _buffer, newSize);
    }
}
