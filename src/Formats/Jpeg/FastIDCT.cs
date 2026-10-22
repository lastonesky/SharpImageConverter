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

        // 第一趟沿列（与 libjpeg jidctint.c 一致：先列后行；两趟顺序影响舍入，必须对齐）。
        for (int col = 0; col < 8; col++)
        {
            int c1 = coefficients[col + 8];
            int c2 = coefficients[col + 16];
            int c3 = coefficients[col + 24];
            int c4 = coefficients[col + 32];
            int c5 = coefficients[col + 40];
            int c6 = coefficients[col + 48];
            int c7 = coefficients[col + 56];

            if ((c1 | c2 | c3 | c4 | c5 | c6 | c7) == 0)
            {
                int dcval = (coefficients[col] * quant[col]) << Pass1Bits;
                workspace[col * 8 + 0] = dcval;
                workspace[col * 8 + 1] = dcval;
                workspace[col * 8 + 2] = dcval;
                workspace[col * 8 + 3] = dcval;
                workspace[col * 8 + 4] = dcval;
                workspace[col * 8 + 5] = dcval;
                workspace[col * 8 + 6] = dcval;
                workspace[col * 8 + 7] = dcval;
                continue;
            }

            int q0 = quant[col];
            int q1 = quant[col + 8];
            int q2 = quant[col + 16];
            int q3 = quant[col + 24];
            int q4 = quant[col + 32];
            int q5 = quant[col + 40];
            int q6 = quant[col + 48];
            int q7 = quant[col + 56];

            int in0 = coefficients[col] * q0;
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

            workspace[col * 8 + 0] = Descale(tmp10 + tmp3o, ConstBits - Pass1Bits);
            workspace[col * 8 + 7] = Descale(tmp10 - tmp3o, ConstBits - Pass1Bits);
            workspace[col * 8 + 1] = Descale(tmp11 + tmp2o, ConstBits - Pass1Bits);
            workspace[col * 8 + 6] = Descale(tmp11 - tmp2o, ConstBits - Pass1Bits);
            workspace[col * 8 + 2] = Descale(tmp12 + tmp1o, ConstBits - Pass1Bits);
            workspace[col * 8 + 5] = Descale(tmp12 - tmp1o, ConstBits - Pass1Bits);
            workspace[col * 8 + 3] = Descale(tmp13 + tmp0o, ConstBits - Pass1Bits);
            workspace[col * 8 + 4] = Descale(tmp13 - tmp0o, ConstBits - Pass1Bits);
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
                // 第二趟遍历的是行 x：输出沿列展开。
                dest[(x * destStride) + 0] = b;
                dest[(x * destStride) + 1] = b;
                dest[(x * destStride) + 2] = b;
                dest[(x * destStride) + 3] = b;
                dest[(x * destStride) + 4] = b;
                dest[(x * destStride) + 5] = b;
                dest[(x * destStride) + 6] = b;
                dest[(x * destStride) + 7] = b;
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

            // 输出位置 k → dest[行x, 列k]；与 WriteOut 的 (x=列, y=行) 参数序对应。
            WriteOut(dest, destStride, 0, x, tmp10 + tmp3o);
            WriteOut(dest, destStride, 7, x, tmp10 - tmp3o);
            WriteOut(dest, destStride, 1, x, tmp11 + tmp2o);
            WriteOut(dest, destStride, 6, x, tmp11 - tmp2o);
            WriteOut(dest, destStride, 2, x, tmp12 + tmp1o);
            WriteOut(dest, destStride, 5, x, tmp12 - tmp1o);
            WriteOut(dest, destStride, 3, x, tmp13 + tmp0o);
            WriteOut(dest, destStride, 4, x, tmp13 - tmp0o);
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

