using System;

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
