using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Jpeg;

internal static class FloatingPointIDCT
{
    private static readonly float[] Cu =
    [
        0.7071067811865476f,
        1f,
        1f,
        1f,
        1f,
        1f,
        1f,
        1f,
    ];

    private static readonly float[] CosTable = CreateCosTable();

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
            float dc = (float)coefficients[0] * quant[0];
            int iv = (int)((dc * 0.125f) + 128.5f);
            if ((uint)iv > 255u)
            {
                iv = iv < 0 ? 0 : 255;
            }

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

        Span<float> tmp = stackalloc float[64];
        for (int y = 0; y < 8; y++)
        {
            int cy = y * 8;
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    int idx = (v * 8) + u;
                    float deq = (float)coefficients[idx] * quant[idx];
                    sum += Cu[v] * deq * CosTable[cy + v];
                }

                tmp[(y * 8) + u] = sum;
            }
        }

        for (int y = 0; y < 8; y++)
        {
            int row = y * destStride;
            for (int x = 0; x < 8; x++)
            {
                int cx = x * 8;
                float sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    sum += Cu[u] * tmp[(y * 8) + u] * CosTable[cx + u];
                }

                int iv = (int)((sum * 0.25f) + 128.5f);
                if ((uint)iv > 255u)
                {
                    iv = iv < 0 ? 0 : 255;
                }

                dest[row + x] = (byte)iv;
            }
        }
    }

    private static float[] CreateCosTable()
    {
        float[] table = new float[64];
        for (int p = 0; p < 8; p++)
        {
            for (int q = 0; q < 8; q++)
            {
                table[(p * 8) + q] = MathF.Cos(((2 * p + 1) * q * MathF.PI) / 16f);
            }
        }

        return table;
    }
}

