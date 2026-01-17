using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace SharpImageConverter;

public unsafe class BitReader
{
    private readonly FastBufferedStream _stream;
    private ulong _bitBuffer;      // 64位位缓冲区（寄存器）
    private int _bitsRemaining;    // 寄存器中剩余的有效位数量

    public BitReader(FastBufferedStream stream)
    {
        _stream = stream;
        _bitsRemaining = 0;
        _bitBuffer = 0;
    }

    /// <summary>
    /// 读取 n 个比特，但不从缓冲区中移除它们（用于查表匹配）
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count)
    {
        if (_bitsRemaining < count)
        {
            FillBitBuffer();
        }
        
        // 从高位截取 count 个比特
        return (uint)(_bitBuffer >> (64 - count));
    }

    /// <summary>
    /// 从缓冲区中丢弃 n 个比特（匹配成功后移动指针）
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count)
    {
        _bitBuffer <<= count;
        _bitsRemaining -= count;
    }

    /// <summary>
    /// 读取并弹出 n 个比特
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        uint result = PeekBits(count);
        SkipBits(count);
        return result;
    }

    /// <summary>
    /// 核心优化：利用指针一次性拉取 8 字节填充寄存器
    /// </summary>
    private void FillBitBuffer()
    {
        // 只要寄存器里还能装得下至少一个字节，就尝试去读
        while (_bitsRemaining <= 56)
        {
            int b = _stream.ReadByte();
            if (b == -1) break; // 文件结束

            // ⚠️ JPEG 特殊处理：0xFF 后面如果是 0x00，代表真正的 0xFF 数据（Byte Stuffing）
            if (b == 0xFF)
            {
                int next = _stream.ReadByte();
                if (next != 0x00)
                {
                    // 遇到了标记（Marker），处理逻辑略...
                }
            }

            // 将新读取的字节放入 ulong 的低位，然后靠左对齐
            _bitBuffer |= (ulong)b << (56 - _bitsRemaining);
            _bitsRemaining += 8;
        }
    }
}