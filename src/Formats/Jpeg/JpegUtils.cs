using System;
using System.Runtime.CompilerServices;

namespace SharpImageConverter;

internal static class JpegUtils
{
    private static readonly byte[] ClampTable = CreateClampTable();
    private static byte[] CreateClampTable()
    {
        var table = new byte[768];
        for (int i = -256; i < 512; i++)
        {
            int index = i + 256;
            if (i < 0) table[index] = 0;
            else if (i > 255) table[index] = 255;
            else table[index] = (byte)i;
        }
        return table;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ClampToByte(int v) => ClampTable[v + 256];
    private static readonly byte[] ZigZagData =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63,
        63, 63, 63, 63, 63, 63, 63, 63
    };

    public static ReadOnlySpan<byte> ZigZag => ZigZagData;
}
