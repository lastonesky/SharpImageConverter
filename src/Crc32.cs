using System;

namespace PictureSharp;

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

    public static uint Compute(byte[] bytes)
    {
        return Compute(bytes, 0, bytes.Length);
    }

    public static uint Compute(byte[] bytes, int offset, int count)
    {
        return Compute(0, bytes, offset, count);
    }

    public static uint Update(uint crc, byte[] bytes, int offset, int count)
    {
        return Compute(crc, bytes, offset, count);
    }

    public static uint Compute(uint crc, byte[] bytes, int offset, int count)
    {
        crc = ~crc;
        for (int i = 0; i < count; i++)
        {
            byte index = (byte)(crc ^ bytes[offset + i]);
            crc = (crc >> 8) ^ Table[index];
        }
        return ~crc;
    }
}
