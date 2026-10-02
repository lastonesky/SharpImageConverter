using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImageConverter;

/// <summary>
/// CRC-32 校验算法实现，使用标准多项式 0xEDB88320。
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table;

    static Crc32()
    {
        uint poly = 0xedb88320;
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ poly;
                else
                    crc >>= 1;
            }
            Table[i] = crc;
        }
    }

    /// <summary>
    /// 计算整个字节数组的 CRC-32。
    /// </summary>
    public static uint Compute(byte[] bytes)
    {
        return Compute(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// 计算数组片段的 CRC-32。
    /// </summary>
    public static uint Compute(byte[] bytes, int offset, int count)
    {
        return Compute(0, bytes, offset, count);
    }

    /// <summary>
    /// 在现有 CRC 的基础上继续更新。
    /// </summary>
    public static uint Update(uint crc, byte[] bytes, int offset, int count)
    {
        return Compute(crc, bytes, offset, count);
    }

    /// <summary>
    /// 计算 CRC-32（可指定初始值）。
    /// </summary>
    public static uint Compute(uint crc, byte[] bytes, int offset, int count)
    {
        if (count >= 64)
        {
            return Crc32Optimized.Compute(crc, bytes.AsSpan(offset, count));
        }

        crc = ~crc;
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            byte index = (byte)(crc ^ bytes[i]);
            crc = (crc >> 8) ^ Table[index];
        }
        return ~crc;
    }
}
public static class Crc32Optimized
{
    // slice-by-8 的表展开成**扁平**一维数组：原先的 uint[8][256] 每次查表都要做两级
    // 指针跳转（先取子数组引用、再取元素），8 次查表 = 8 次潜在的非连续访存。
    // 扁平化后 8 张表在同一块连续内存里（8KB），只剩一次基址 + 常量偏移。
    private static readonly uint[] Tables = new uint[8 * 256];

    // 各 slice 表在扁平数组中的起始偏移（编译期常量，可直接折叠进地址计算）
    private const int T0 = 0 * 256;
    private const int T1 = 1 * 256;
    private const int T2 = 2 * 256;
    private const int T3 = 3 * 256;
    private const int T4 = 4 * 256;
    private const int T5 = 5 * 256;
    private const int T6 = 6 * 256;
    private const int T7 = 7 * 256;

    // 字节序判断提到静态字段：BitConverter.IsLittleEndian 虽是 JIT 常量，
    // 但放在每 8 字节调用一次的内联读取路径里，仍会阻碍该方法的进一步内联与优化。
    private static readonly bool IsLittleEndian = BitConverter.IsLittleEndian;

    static Crc32Optimized()
    {
        uint poly = 0xedb88320;
        // 生成第一张表 (同标准表)
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) == 1 ? (crc >> 1) ^ poly : crc >> 1;
            Tables[T0 + i] = crc;
        }

        // 基于第一张表生成后续 7 张表
        for (uint i = 0; i < 256; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                uint prev = Tables[(j - 1) * 256 + i];
                Tables[j * 256 + i] = (prev >> 8) ^ Tables[T0 + (prev & 0xFF)];
            }
        }
    }

    public static uint Compute(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        int i = 0;
        int length = data.Length;
        ref byte dataRef = ref MemoryMarshal.GetReference(data);
        ref uint tableRef = ref MemoryMarshal.GetArrayDataReference(Tables);

        // --- 核心优化：一次处理 8 字节 ---
        while (length - i >= 8)
        {
            // 读取两个 32 位整数（按 little-endian 解释字节序）
            uint one = ReadUInt32LittleEndian(ref Unsafe.Add(ref dataRef, i)) ^ c;
            uint two = ReadUInt32LittleEndian(ref Unsafe.Add(ref dataRef, i + 4));

            c = Unsafe.Add(ref tableRef, T7 + (int)(one & 0xFF)) ^
                Unsafe.Add(ref tableRef, T6 + (int)((one >> 8) & 0xFF)) ^
                Unsafe.Add(ref tableRef, T5 + (int)((one >> 16) & 0xFF)) ^
                Unsafe.Add(ref tableRef, T4 + (int)(one >> 24)) ^
                Unsafe.Add(ref tableRef, T3 + (int)(two & 0xFF)) ^
                Unsafe.Add(ref tableRef, T2 + (int)((two >> 8) & 0xFF)) ^
                Unsafe.Add(ref tableRef, T1 + (int)((two >> 16) & 0xFF)) ^
                Unsafe.Add(ref tableRef, T0 + (int)(two >> 24));
            i += 8;
        }

        // 处理剩余的字节 (少于 8 字节的部分)
        while (i < length)
        {
            c = (c >> 8) ^ Unsafe.Add(ref tableRef, T0 + (byte)(c ^ Unsafe.Add(ref dataRef, i++)));
        }

        return ~c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32LittleEndian(ref byte src)
    {
        uint value = Unsafe.ReadUnaligned<uint>(ref src);
        if (!IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        return value;
    }
}
