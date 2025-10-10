using System;

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

    public static void IDCT8x8Fast(short[] coeffs, int coeffsOffset, int[] outBlock, int outOffset)
    {
        // 使用池化的一维临时缓冲替代每次分配二维数组，索引为 y*8 + u
        var tmp = System.Buffers.ArrayPool<double>.Shared.Rent(64);
        try
        {
            // tmp[y, u] = sum_v C(v) * F[u,v] * cos((2y+1)v*pi/16)
            for (int y = 0; y < 8; y++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double s = 0.0;
                    int baseCoeff = coeffsOffset + u; // column u
                    for (int v = 0; v < 8; v++)
                    {
                        double Fuv = coeffs[baseCoeff + v * 8];
                        s += C[v] * Fuv * Cos[y, v];
                    }
                    tmp[y * 8 + u] = s;
                }
            }

            // f[x,y] = 1/4 * sum_u C(u) * tmp[y,u] * cos((2x+1)u*pi/16)
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
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(tmp);
        }
    }
}