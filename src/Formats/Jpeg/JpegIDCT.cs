using System;

namespace SharpImageConverter;

internal static class JpegIDCT
{
    private const int CONST_BITS = 13;
    private const int PASS1_BITS = 2;

    private const int FIX_0_298631336 = 2446;
    private const int FIX_0_390180644 = 3196;
    private const int FIX_0_541196100 = 4433;
    private const int FIX_0_765366865 = 6270;
    private const int FIX_0_899976223 = 7373;
    private const int FIX_1_175875602 = 9633;
    private const int FIX_1_501321110 = 12299;
    private const int FIX_1_847759065 = 15137;
    private const int FIX_1_961570560 = 16069;
    private const int FIX_2_053119869 = 16819;
    private const int FIX_2_562915447 = 20995;
    private const int FIX_3_072711026 = 25172;

    private static int Descale(long x, int n) => (int)((x + (1L << (n - 1))) >> n);

    private static byte ClampToByte(int v)
    {
        if (v < 0) return 0;
        if (v > 255) return 255;
        return (byte)v;
    }

    public static void BlockIDCT(ReadOnlySpan<int> block, Span<byte> dest)
    {
        Span<long> ws = stackalloc long[64];

        for (int i = 0; i < 8; i++)
        {
            int ptr = i;
            if (block[ptr + 8] == 0 && block[ptr + 16] == 0 && block[ptr + 24] == 0 &&
                block[ptr + 32] == 0 && block[ptr + 40] == 0 && block[ptr + 48] == 0 &&
                block[ptr + 56] == 0)
            {
                long dc = (long)block[ptr] << PASS1_BITS;
                ws[ptr + 0] = dc;
                ws[ptr + 8] = dc;
                ws[ptr + 16] = dc;
                ws[ptr + 24] = dc;
                ws[ptr + 32] = dc;
                ws[ptr + 40] = dc;
                ws[ptr + 48] = dc;
                ws[ptr + 56] = dc;
                continue;
            }

            long z2 = block[ptr + 16];
            long z3 = block[ptr + 48];
            long z1 = (z2 + z3) * FIX_0_541196100;
            long tmp2 = z1 + z3 * (-FIX_1_847759065);
            long tmp3 = z1 + z2 * FIX_0_765366865;

            z2 = block[ptr + 0];
            z3 = block[ptr + 32];
            long tmp0 = (z2 + z3) << CONST_BITS;
            long tmp1 = (z2 - z3) << CONST_BITS;

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;

            tmp0 = block[ptr + 56];
            tmp1 = block[ptr + 40];
            tmp2 = block[ptr + 24];
            tmp3 = block[ptr + 8];

            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            long z4 = tmp1 + tmp3;
            long z5 = (z3 + z4) * FIX_1_175875602;

            tmp0 *= FIX_0_298631336;
            tmp1 *= FIX_2_053119869;
            tmp2 *= FIX_3_072711026;
            tmp3 *= FIX_1_501321110;
            z1 *= -FIX_0_899976223;
            z2 *= -FIX_2_562915447;
            z3 *= -FIX_1_961570560;
            z4 *= -FIX_0_390180644;

            z3 += z5;
            z4 += z5;

            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;

            ws[ptr + 0] = (tmp10 + tmp3) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 56] = (tmp10 - tmp3) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 8] = (tmp11 + tmp2) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 48] = (tmp11 - tmp2) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 16] = (tmp12 + tmp1) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 40] = (tmp12 - tmp1) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 24] = (tmp13 + tmp0) >> (CONST_BITS - PASS1_BITS);
            ws[ptr + 32] = (tmp13 - tmp0) >> (CONST_BITS - PASS1_BITS);
        }

        for (int i = 0; i < 64; i += 8)
        {
            long z2 = ws[i + 2];
            long z3 = ws[i + 6];
            long z1 = (z2 + z3) * FIX_0_541196100;
            long tmp2 = z1 + z3 * (-FIX_1_847759065);
            long tmp3 = z1 + z2 * FIX_0_765366865;

            long tmp0 = (ws[i + 0] + ws[i + 4]) << CONST_BITS;
            long tmp1 = (ws[i + 0] - ws[i + 4]) << CONST_BITS;

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;

            tmp0 = ws[i + 7];
            tmp1 = ws[i + 5];
            tmp2 = ws[i + 3];
            tmp3 = ws[i + 1];

            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            long z4 = tmp1 + tmp3;
            long z5 = (z3 + z4) * FIX_1_175875602;

            tmp0 *= FIX_0_298631336;
            tmp1 *= FIX_2_053119869;
            tmp2 *= FIX_3_072711026;
            tmp3 *= FIX_1_501321110;
            z1 *= -FIX_0_899976223;
            z2 *= -FIX_2_562915447;
            z3 *= -FIX_1_961570560;
            z4 *= -FIX_0_390180644;

            z3 += z5;
            z4 += z5;

            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;

            int shift = CONST_BITS + PASS1_BITS + 3;

            int v0 = Descale(tmp10 + tmp3, shift);
            int v7 = Descale(tmp10 - tmp3, shift);
            int v1 = Descale(tmp11 + tmp2, shift);
            int v6 = Descale(tmp11 - tmp2, shift);
            int v2 = Descale(tmp12 + tmp1, shift);
            int v5 = Descale(tmp12 - tmp1, shift);
            int v3 = Descale(tmp13 + tmp0, shift);
            int v4 = Descale(tmp13 - tmp0, shift);

            dest[i + 0] = ClampToByte(v0 + 128);
            dest[i + 7] = ClampToByte(v7 + 128);
            dest[i + 1] = ClampToByte(v1 + 128);
            dest[i + 6] = ClampToByte(v6 + 128);
            dest[i + 2] = ClampToByte(v2 + 128);
            dest[i + 5] = ClampToByte(v5 + 128);
            dest[i + 3] = ClampToByte(v3 + 128);
            dest[i + 4] = ClampToByte(v4 + 128);
        }
    }
}
