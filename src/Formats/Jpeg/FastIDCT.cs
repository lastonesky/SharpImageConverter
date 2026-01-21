using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Jpeg;

internal static class FastIDCT
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    private const int Fix_0_298631336 = 2446;
    private const int Fix_0_390180644 = 3196;
    private const int Fix_0_541196100 = 4433;
    private const int Fix_0_765366865 = 6270;
    private const int Fix_0_899976223 = 7373;
    private const int Fix_1_175875602 = 9633;
    private const int Fix_1_501321110 = 12299;
    private const int Fix_1_847759065 = 15137;
    private const int Fix_1_961570560 = 16069;
    private const int Fix_2_053119869 = 16819;
    private const int Fix_2_562915447 = 20995;
    private const int Fix_3_072711026 = 25172;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> dest, int destStride)
    {
        bool acZero = true;
        for (int i = 1; i < 64; i++)
        {
            if (coefficients[i] != 0)
            {
                acZero = false;
                break;
            }
        }

        if (acZero)
        {
            int dc = coefficients[0] * quant[0];
            int iv = Descale(dc, 3) + 128;
            if ((uint)iv > 255u) iv = iv < 0 ? 0 : 255;

            byte b = (byte)iv;
            for (int y = 0; y < 8; y++)
            {
                int row = y * destStride;
                dest[row + 0] = b;
                dest[row + 1] = b;
                dest[row + 2] = b;
                dest[row + 3] = b;
                dest[row + 4] = b;
                dest[row + 5] = b;
                dest[row + 6] = b;
                dest[row + 7] = b;
            }

            return;
        }

        Span<int> workspace = stackalloc int[64];

        for (int y = 0; y < 8; y++)
        {
            int row = y * 8;

            int c1 = coefficients[row + 1];
            int c2 = coefficients[row + 2];
            int c3 = coefficients[row + 3];
            int c4 = coefficients[row + 4];
            int c5 = coefficients[row + 5];
            int c6 = coefficients[row + 6];
            int c7 = coefficients[row + 7];

            if ((c1 | c2 | c3 | c4 | c5 | c6 | c7) == 0)
            {
                int dcval = (coefficients[row + 0] * quant[row + 0]) << Pass1Bits;
                workspace[row + 0] = dcval;
                workspace[row + 1] = dcval;
                workspace[row + 2] = dcval;
                workspace[row + 3] = dcval;
                workspace[row + 4] = dcval;
                workspace[row + 5] = dcval;
                workspace[row + 6] = dcval;
                workspace[row + 7] = dcval;
                continue;
            }

            int q0 = quant[row + 0];
            int q1 = quant[row + 1];
            int q2 = quant[row + 2];
            int q3 = quant[row + 3];
            int q4 = quant[row + 4];
            int q5 = quant[row + 5];
            int q6 = quant[row + 6];
            int q7 = quant[row + 7];

            int in0 = coefficients[row + 0] * q0;
            int in1 = c1 * q1;
            int in2 = c2 * q2;
            int in3 = c3 * q3;
            int in4 = c4 * q4;
            int in5 = c5 * q5;
            int in6 = c6 * q6;
            int in7 = c7 * q7;

            long z2 = in2;
            long z3 = in6;

            long z1 = (z2 + z3) * Fix_0_541196100;
            long tmp2 = z1 + (z3 * -Fix_1_847759065);
            long tmp3 = z1 + (z2 * Fix_0_765366865);

            long tmp0 = (long)(in0 + in4) << ConstBits;
            long tmp1 = (long)(in0 - in4) << ConstBits;

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;

            long tmp0o = in7;
            long tmp1o = in5;
            long tmp2o = in3;
            long tmp3o = in1;

            long z1o = tmp0o + tmp3o;
            long z2o = tmp1o + tmp2o;
            long z3o = tmp0o + tmp2o;
            long z4o = tmp1o + tmp3o;
            long z5o = (z3o + z4o) * Fix_1_175875602;

            tmp0o *= Fix_0_298631336;
            tmp1o *= Fix_2_053119869;
            tmp2o *= Fix_3_072711026;
            tmp3o *= Fix_1_501321110;

            z1o *= -Fix_0_899976223;
            z2o *= -Fix_2_562915447;
            z3o *= -Fix_1_961570560;
            z4o *= -Fix_0_390180644;

            z3o += z5o;
            z4o += z5o;

            tmp0o += z1o + z3o;
            tmp1o += z2o + z4o;
            tmp2o += z2o + z3o;
            tmp3o += z1o + z4o;

            workspace[row + 0] = Descale(tmp10 + tmp3o, ConstBits - Pass1Bits);
            workspace[row + 7] = Descale(tmp10 - tmp3o, ConstBits - Pass1Bits);
            workspace[row + 1] = Descale(tmp11 + tmp2o, ConstBits - Pass1Bits);
            workspace[row + 6] = Descale(tmp11 - tmp2o, ConstBits - Pass1Bits);
            workspace[row + 2] = Descale(tmp12 + tmp1o, ConstBits - Pass1Bits);
            workspace[row + 5] = Descale(tmp12 - tmp1o, ConstBits - Pass1Bits);
            workspace[row + 3] = Descale(tmp13 + tmp0o, ConstBits - Pass1Bits);
            workspace[row + 4] = Descale(tmp13 - tmp0o, ConstBits - Pass1Bits);
        }

        for (int x = 0; x < 8; x++)
        {
            int w1 = workspace[(1 * 8) + x];
            int w2 = workspace[(2 * 8) + x];
            int w3 = workspace[(3 * 8) + x];
            int w4 = workspace[(4 * 8) + x];
            int w5 = workspace[(5 * 8) + x];
            int w6 = workspace[(6 * 8) + x];
            int w7 = workspace[(7 * 8) + x];

            if ((w1 | w2 | w3 | w4 | w5 | w6 | w7) == 0)
            {
                int dcval = workspace[(0 * 8) + x];
                int v = Descale(dcval, Pass1Bits + 3) + 128;
                if ((uint)v > 255u) v = v < 0 ? 0 : 255;

                byte b = (byte)v;
                dest[(0 * destStride) + x] = b;
                dest[(1 * destStride) + x] = b;
                dest[(2 * destStride) + x] = b;
                dest[(3 * destStride) + x] = b;
                dest[(4 * destStride) + x] = b;
                dest[(5 * destStride) + x] = b;
                dest[(6 * destStride) + x] = b;
                dest[(7 * destStride) + x] = b;
                continue;
            }

            int w0 = workspace[(0 * 8) + x];

            long z2 = w2;
            long z3 = w6;

            long z1 = (z2 + z3) * Fix_0_541196100;
            long tmp2 = z1 + (z3 * -Fix_1_847759065);
            long tmp3 = z1 + (z2 * Fix_0_765366865);

            long tmp0 = (long)(w0 + w4) << ConstBits;
            long tmp1 = (long)(w0 - w4) << ConstBits;

            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;

            long tmp0o = w7;
            long tmp1o = w5;
            long tmp2o = w3;
            long tmp3o = w1;

            long z1o = tmp0o + tmp3o;
            long z2o = tmp1o + tmp2o;
            long z3o = tmp0o + tmp2o;
            long z4o = tmp1o + tmp3o;
            long z5o = (z3o + z4o) * Fix_1_175875602;

            tmp0o *= Fix_0_298631336;
            tmp1o *= Fix_2_053119869;
            tmp2o *= Fix_3_072711026;
            tmp3o *= Fix_1_501321110;

            z1o *= -Fix_0_899976223;
            z2o *= -Fix_2_562915447;
            z3o *= -Fix_1_961570560;
            z4o *= -Fix_0_390180644;

            z3o += z5o;
            z4o += z5o;

            tmp0o += z1o + z3o;
            tmp1o += z2o + z4o;
            tmp2o += z2o + z3o;
            tmp3o += z1o + z4o;

            WriteOut(dest, destStride, x, 0, tmp10 + tmp3o);
            WriteOut(dest, destStride, x, 7, tmp10 - tmp3o);
            WriteOut(dest, destStride, x, 1, tmp11 + tmp2o);
            WriteOut(dest, destStride, x, 6, tmp11 - tmp2o);
            WriteOut(dest, destStride, x, 2, tmp12 + tmp1o);
            WriteOut(dest, destStride, x, 5, tmp12 - tmp1o);
            WriteOut(dest, destStride, x, 3, tmp13 + tmp0o);
            WriteOut(dest, destStride, x, 4, tmp13 - tmp0o);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteOut(Span<byte> dest, int stride, int x, int y, long value)
    {
        int v = Descale(value, ConstBits + Pass1Bits + 3) + 128;
        if ((uint)v > 255u) v = v < 0 ? 0 : 255;
        dest[(y * stride) + x] = (byte)v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Descale(long x, int n)
    {
        return (int)((x + (1L << (n - 1))) >> n);
    }
}

