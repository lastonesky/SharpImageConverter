using System;

public static class Idct
{
    // 朴素浮点IDCT，保证正确性（性能非目标）
    public static void IDCT8x8(short[] coeffs, int coeffsOffset, int[] outBlock, int outOffset)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0.0;
                for (int u = 0; u < 8; u++)
                {
                    for (int v = 0; v < 8; v++)
                    {
                        double Cu = (u == 0) ? 1.0 / Math.Sqrt(2) : 1.0;
                        double Cv = (v == 0) ? 1.0 / Math.Sqrt(2) : 1.0;
                        double coeff = coeffs[coeffsOffset + v * 8 + u];
                        sum += Cu * Cv * coeff *
                               Math.Cos(((2 * x + 1) * u * Math.PI) / 16.0) *
                               Math.Cos(((2 * y + 1) * v * Math.PI) / 16.0);
                    }
                }
                sum /= 4.0;
                int val = (int)Math.Round(sum + 128);
                if (val < 0) val = 0;
                if (val > 255) val = 255;
                outBlock[outOffset + y * 8 + x] = val;
            }
        }
    }
}