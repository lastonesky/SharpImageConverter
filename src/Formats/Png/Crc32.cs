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
    private static readonly uint[][] Tables; // 8x256 的大表

    static Crc32Optimized()
    {
        Tables = new uint[8][];
        for (int i = 0; i < 8; i++) Tables[i] = new uint[256];

        uint poly = 0xedb88320;
        // 生成第一张表 (同标准表)
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) == 1 ? (crc >> 1) ^ poly : crc >> 1;
            Tables[0][i] = crc;
        }

        // 基于第一张表生成后续 7 张表
        for (int i = 0; i < 256; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                uint prev = Tables[j - 1][i];
                Tables[j][i] = (prev >> 8) ^ Tables[0][prev & 0xFF];
            }
        }
    }

    public static uint Compute(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        int i = 0;
        ref byte dataRef = ref MemoryMarshal.GetReference(data);

        // --- 核心优化：一次处理 8 字节 ---
        while (data.Length - i >= 8)
        {
            // 读取两个 32 位整数（按 little-endian 解释字节序）
            // 通过 ReadUInt32LittleEndian 保证跨平台一致性
            uint one = ReadUInt32LittleEndian(ref Unsafe.Add(ref dataRef, i)) ^ c;
            uint two = ReadUInt32LittleEndian(ref Unsafe.Add(ref dataRef, i + 4));

            c = Tables[7][one & 0xFF] ^
                Tables[6][(one >> 8) & 0xFF] ^
                Tables[5][(one >> 16) & 0xFF] ^
                Tables[4][one >> 24] ^
                Tables[3][two & 0xFF] ^
                Tables[2][(two >> 8) & 0xFF] ^
                Tables[1][(two >> 16) & 0xFF] ^
                Tables[0][two >> 24];
            i += 8;
        }

        // 处理剩余的字节 (少于 8 字节的部分)
        while (i < data.Length)
        {
            c = (c >> 8) ^ Tables[0][(byte)(c ^ Unsafe.Add(ref dataRef, i++))];
        }

        return ~c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32LittleEndian(ref byte src)
    {
        uint value = Unsafe.ReadUnaligned<uint>(ref src);
        if (!BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        return value;
    }
}
