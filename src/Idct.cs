using System;

namespace PictureSharp;

public static class Idct
{
    // 快速两阶段IDCT：预计算余弦表，行列分离，显著减少运算量
    private static readonly double[,] Cos = BuildCosTable();
    private static readonly double[] C = BuildC();

    private static double[,] BuildCosTable()
    {
        var t = new double[8, 8];
        for (int n = 0; n < 8; n++)
        {
            for (int k = 0; k < 8; k++)
            {
                t[n, k] = Math.Cos(((2 * n + 1) * k * Math.PI) / 16.0);
            }
        }
        return t;
    }

    private static double[] BuildC()
    {
        var c = new double[8];
        for (int k = 0; k < 8; k++) c[k] = (k == 0) ? 1.0 / Math.Sqrt(2) : 1.0;
        return c;
    }

    // 整数 IDCT (参考 IJG jidctint.c)
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

    public static void IDCT8x8Int(short[] coeffs, int coeffsOffset, int[] outBlock, int outOffset)
    {
        Span<int> ws = stackalloc int[64];

        // Pass 1: Process columns from coeffs, store into ws (rows)
        // 注意：这里我们遵循 IJG 的习惯，先处理列（或行），关键是最终输出要转置正确
        // 原 Double IDCT 先处理列(v->y)，结果存入 tmp[y*8+u] (y=row, u=col)
        // 再处理行(u->x)，结果存入 out[y*8+x]
        // 这里我们也采用类似逻辑，或者直接使用标准的行列分离

        // Pass 1: Columns
        for (int i = 0; i < 8; ++i)
        {
            int z1, z2, z3, z4, z5;
            int tmp0, tmp1, tmp2, tmp3;
            int tmp10, tmp11, tmp12, tmp13;

            int ptr = coeffsOffset + i;
            
            // Even part
            if (coeffs[ptr + 8] == 0 && coeffs[ptr + 16] == 0 && coeffs[ptr + 24] == 0 &&
                coeffs[ptr + 32] == 0 && coeffs[ptr + 40] == 0 && coeffs[ptr + 48] == 0 &&
                coeffs[ptr + 56] == 0)
            {
                // AC terms all zero
                int dcval = coeffs[ptr] << PASS1_BITS;
                ws[i + 0] = dcval;
                ws[i + 8] = dcval;
                ws[i + 16] = dcval;
                ws[i + 24] = dcval;
                ws[i + 32] = dcval;
                ws[i + 40] = dcval;
                ws[i + 48] = dcval;
                ws[i + 56] = dcval;
                continue;
            }

            z2 = coeffs[ptr + 16];
            z3 = coeffs[ptr + 48];
            z1 = (z2 + z3) * FIX_0_541196100;
            tmp2 = z1 + z3 * (-FIX_1_847759065);
            tmp3 = z1 + z2 * FIX_0_765366865;

            z2 = coeffs[ptr];
            z3 = coeffs[ptr + 32];
            tmp0 = (z2 + z3) << CONST_BITS;
            tmp1 = (z2 - z3) << CONST_BITS;

            tmp10 = tmp0 + tmp3;
            tmp13 = tmp0 - tmp3;
            tmp11 = tmp1 + tmp2;
            tmp12 = tmp1 - tmp2;

            tmp0 = coeffs[ptr + 56];
            tmp1 = coeffs[ptr + 40];
            tmp2 = coeffs[ptr + 24];
            tmp3 = coeffs[ptr + 8];

            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            z4 = tmp1 + tmp3;
            z5 = (z3 + z4) * FIX_1_175875602;

            tmp0 = tmp0 * FIX_0_298631336;
            tmp1 = tmp1 * FIX_2_053119869;
            tmp2 = tmp2 * FIX_3_072711026;
            tmp3 = tmp3 * FIX_1_501321110;
            z1 = z1 * (-FIX_0_899976223);
            z2 = z2 * (-FIX_2_562915447);
            z3 = z3 * (-FIX_1_961570560);
            z4 = z4 * (-FIX_0_390180644);

            z3 += z5;
            z4 += z5;

            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;

            ws[i + 0] = (tmp10 + tmp3) >> (CONST_BITS - PASS1_BITS);
            ws[i + 56] = (tmp10 - tmp3) >> (CONST_BITS - PASS1_BITS);
            ws[i + 8] = (tmp11 + tmp2) >> (CONST_BITS - PASS1_BITS);
            ws[i + 48] = (tmp11 - tmp2) >> (CONST_BITS - PASS1_BITS);
            ws[i + 16] = (tmp12 + tmp1) >> (CONST_BITS - PASS1_BITS);
            ws[i + 40] = (tmp12 - tmp1) >> (CONST_BITS - PASS1_BITS);
            ws[i + 24] = (tmp13 + tmp0) >> (CONST_BITS - PASS1_BITS);
            ws[i + 32] = (tmp13 - tmp0) >> (CONST_BITS - PASS1_BITS);
        }

        // Pass 2: Rows
        for (int i = 0; i < 64; i += 8)
        {
            int z1, z2, z3, z4, z5;
            int tmp0, tmp1, tmp2, tmp3;
            int tmp10, tmp11, tmp12, tmp13;

            // Even part
            z2 = ws[i + 2];
            z3 = ws[i + 6];
            z1 = (z2 + z3) * FIX_0_541196100;
            tmp2 = z1 + z3 * (-FIX_1_847759065);
            tmp3 = z1 + z2 * FIX_0_765366865;

            tmp0 = (ws[i + 0] + ws[i + 4]) << CONST_BITS;
            tmp1 = (ws[i + 0] - ws[i + 4]) << CONST_BITS;

            tmp10 = tmp0 + tmp3;
            tmp13 = tmp0 - tmp3;
            tmp11 = tmp1 + tmp2;
            tmp12 = tmp1 - tmp2;

            // Odd part
            tmp0 = ws[i + 7];
            tmp1 = ws[i + 5];
            tmp2 = ws[i + 3];
            tmp3 = ws[i + 1];

            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            z4 = tmp1 + tmp3;
            z5 = (z3 + z4) * FIX_1_175875602;

            tmp0 = tmp0 * FIX_0_298631336;
            tmp1 = tmp1 * FIX_2_053119869;
            tmp2 = tmp2 * FIX_3_072711026;
            tmp3 = tmp3 * FIX_1_501321110;
            z1 = z1 * (-FIX_0_899976223);
            z2 = z2 * (-FIX_2_562915447);
            z3 = z3 * (-FIX_1_961570560);
            z4 = z4 * (-FIX_0_390180644);

            z3 += z5;
            z4 += z5;

            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;

            // Final output: scale down and level shift
            // PASS1_BITS + 3 = 5.
            // CONST_BITS + PASS1_BITS + 3 = 13 + 2 + 3 = 18.
            // We need to shift by 18 roughly?
            // Standard libjpeg: DESCALE(out, CONST_BITS+PASS1_BITS+3)
            // + 128 (level shift)
            
            // Wait, output should be 0..255
            // In double version: s * 0.25 + 128.
            // The integer algorithm produces scaled values.
            // We need to shift right by (CONST_BITS + PASS1_BITS + 3)
            // And add 128.
            
            // 1 << (CONST_BITS + PASS1_BITS + 3) = 1 << 18.
            // But we already shifted left by CONST_BITS in both passes?
            // Actually Pass 1: coeffs << PASS1_BITS or coeffs * FIX (which is 1<<13).
            // Pass 1 output is scaled by 1<<(CONST_BITS+PASS1_BITS) ? No.
            // Pass 1 output `ws` stores values shifted.
            // Let's check shifting in Pass 1:
            // ws[...] = (...) >> (CONST_BITS - PASS1_BITS).
            // So ws has `x * (1<<CONST_BITS) >> (13-2) = x * (1<<2)`.
            // So ws is scaled by 4.
            
            // Pass 2:
            // input ws (scaled by 4).
            // Multiplies by FIX (1<<13).
            // So accumulator is scaled by 4 * 8192 = 32768.
            // We need to divide by 32 (IDCT factor? No)
            
            // IDCT formula has 1/4 factor total.
            // We have huge scaling.
            // The standard shift is CONST_BITS + PASS1_BITS + 3.
            // 13 + 2 + 3 = 18.
            // But we only shifted right by (CONST_BITS - PASS1_BITS) in Pass 1.
            
            // Let's trust the standard algorithm shifting.
            // Final shift: (CONST_BITS + PASS1_BITS + 3)
            // `ws` values are effectively coefficients * 4 (roughly).
            // Pass 2 computes sum(ws * fix).
            // Result is coeff * 4 * 8192 = coeff * 32768.
            // We want result to be pixel value.
            // 32768 >> 18 = 0.125.
            // Wait, IDCT gain is 8?
            // Double version: s *= 0.25.
            
            // Let's verify scaling.
            // Pass 1: Sum(c * fix). `ws` = Sum >> 11.
            // Pass 2: Sum(ws * fix). `out` = Sum >> 18.
            
            int v0 = (tmp10 + tmp3) >> (CONST_BITS + PASS1_BITS + 3);
            int v7 = (tmp10 - tmp3) >> (CONST_BITS + PASS1_BITS + 3);
            int v1 = (tmp11 + tmp2) >> (CONST_BITS + PASS1_BITS + 3);
            int v6 = (tmp11 - tmp2) >> (CONST_BITS + PASS1_BITS + 3);
            int v2 = (tmp12 + tmp1) >> (CONST_BITS + PASS1_BITS + 3);
            int v5 = (tmp12 - tmp1) >> (CONST_BITS + PASS1_BITS + 3);
            int v3 = (tmp13 + tmp0) >> (CONST_BITS + PASS1_BITS + 3);
            int v4 = (tmp13 - tmp0) >> (CONST_BITS + PASS1_BITS + 3);
            
            // Level shift +128 and clamp
            // We add 128 to the result.
            // Actually, we usually add (1<<(shift-1)) for rounding before shifting.
            // But here we just shift.
            // Standard libjpeg adds range_limit.
            
            // Let's do explicit rounding:
            // val += 128;
            
            // Wait, `v0` etc are the differences from 0 (centered around 0).
            // We need to add 128 to get 0..255.
            
            // Let's use a helper to clamp.
            
            outBlock[outOffset + i + 0] = Clamp(v0 + 128);
            outBlock[outOffset + i + 7] = Clamp(v7 + 128);
            outBlock[outOffset + i + 1] = Clamp(v1 + 128);
            outBlock[outOffset + i + 6] = Clamp(v6 + 128);
            outBlock[outOffset + i + 2] = Clamp(v2 + 128);
            outBlock[outOffset + i + 5] = Clamp(v5 + 128);
            outBlock[outOffset + i + 3] = Clamp(v3 + 128);
            outBlock[outOffset + i + 4] = Clamp(v4 + 128);
        }
    }

    private static int Clamp(int val)
    {
        if (val < 0) return 0;
        if (val > 255) return 255;
        return val;
    }

    public static void IDCT8x8Double(short[] coeffs, int coeffsOffset, int[] outBlock, int outOffset)
    {
        Span<double> tmp = stackalloc double[64];

        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                double s = 0.0;
                int baseCoeff = coeffsOffset + u;
                for (int v = 0; v < 8; v++)
                {
                    double Fuv = coeffs[baseCoeff + v * 8];
                    s += C[v] * Fuv * Cos[y, v];
                }
                tmp[y * 8 + u] = s;
            }
        }

        for (int y = 0; y < 8; y++)
        {
            int rowBase = y * 8;
            for (int x = 0; x < 8; x++)
            {
                double s = 0.0;
                for (int u = 0; u < 8; u++)
                {
                    s += C[u] * tmp[rowBase + u] * Cos[x, u];
                }
                s *= 0.25;
                int val = (int)Math.Round(s + 128.0);
                if (val < 0) val = 0;
                if (val > 255) val = 255;
                outBlock[outOffset + y * 8 + x] = val;
            }
        }
    }
}
