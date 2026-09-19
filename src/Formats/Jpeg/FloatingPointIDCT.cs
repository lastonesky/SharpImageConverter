using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

    // CosTable 的转置视图：CosColumns[q * 8 + p] == CosTable[p * 8 + q]。
    // 第二趟的向量维度是空间列 x，需要按频率 u 取出整列 cos((2x + 1)uπ / 16)。
    private static readonly float[] CosColumns = CreateCosColumns();

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

        if (Vector256.IsHardwareAccelerated)
        {
            TransformVector256(coefficients, quant, dest, destStride);
            return;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            TransformVector128(coefficients, quant, dest, destStride);
            return;
        }

        TransformScalar(coefficients, quant, dest, destStride);
    }

    /// <summary>
    /// AVX2 路径，一行 8 个元素正好一个 256 位向量。
    /// 两趟的累加顺序与乘序均与 <see cref="TransformScalar"/> 一致，输出逐位相同。
    /// </summary>
    private static unsafe void TransformVector256(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> dest, int destStride)
    {
        // 第一趟对 v 求和，各 u 列互不影响，因此可整行并行。
        // deq[v] 已按 Cu[v] 预缩放，保持标量 ((Cu[v] * deq) * cos) 的乘序。
        Span<Vector256<float>> deq = stackalloc Vector256<float>[8];
        fixed (short* coefficientPtr = coefficients)
        fixed (ushort* quantPtr = quant)
        {
            for (int v = 0; v < 8; v++)
            {
                var c = Vector128.Load(coefficientPtr + (v * 8));
                var q = Vector128.Load(quantPtr + (v * 8));
                deq[v] = (ToSingle256(c) * ToSingle256(q)) * Vector256.Create(Cu[v]);
            }
        }

        Span<Vector256<float>> cosColumnVectors = stackalloc Vector256<float>[8];
        Span<float> tmp = stackalloc float[64];
        fixed (float* cosPtr = CosTable)
        fixed (float* cosColumnPtr = CosColumns)
        fixed (float* tmpPtr = tmp)
        {
            for (int u = 0; u < 8; u++)
            {
                cosColumnVectors[u] = Vector256.Load(cosColumnPtr + (u * 8));
            }

            for (int y = 0; y < 8; y++)
            {
                int cosRow = y * 8;
                var acc = Vector256<float>.Zero;
                for (int v = 0; v < 8; v++)
                {
                    acc += Vector256.Create(cosPtr[cosRow + v]) * deq[v];
                }

                Vector256.Store(acc, tmpPtr + (y * 8));
            }

            // 第二趟对 u 求和，向量维度为空间列 x。
            for (int y = 0; y < 8; y++)
            {
                int rowBase = y * 8;
                var acc = Vector256<float>.Zero;
                for (int u = 0; u < 8; u++)
                {
                    acc += cosColumnVectors[u] * Vector256.Create(tmpPtr[rowBase + u] * Cu[u]);
                }

                StoreRow(acc, dest, y * destStride);
            }
        }
    }

    /// <summary>
    /// SSE2 / AdvSimd 路径，一行 8 个元素拆成高低两个 128 位向量，结构与 256 位路径一致。
    /// </summary>
    private static unsafe void TransformVector128(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> dest, int destStride)
    {
        Span<Vector128<float>> deqLow = stackalloc Vector128<float>[8];
        Span<Vector128<float>> deqHigh = stackalloc Vector128<float>[8];
        fixed (short* coefficientPtr = coefficients)
        fixed (ushort* quantPtr = quant)
        {
            for (int v = 0; v < 8; v++)
            {
                var c = Vector128.Load(coefficientPtr + (v * 8));
                var q = Vector128.Load(quantPtr + (v * 8));
                Vector128<int> coefficientLow = Vector128.WidenLower(c);
                Vector128<int> coefficientHigh = Vector128.WidenUpper(c);
                Vector128<uint> quantLow = Vector128.WidenLower(q);
                Vector128<uint> quantHigh = Vector128.WidenUpper(q);
                var cu = Vector128.Create(Cu[v]);
                deqLow[v] = (Vector128.ConvertToSingle(coefficientLow) * Vector128.ConvertToSingle(quantLow)) * cu;
                deqHigh[v] = (Vector128.ConvertToSingle(coefficientHigh) * Vector128.ConvertToSingle(quantHigh)) * cu;
            }
        }

        Span<Vector128<float>> cosColumnLow = stackalloc Vector128<float>[8];
        Span<Vector128<float>> cosColumnHigh = stackalloc Vector128<float>[8];
        Span<float> tmp = stackalloc float[64];
        fixed (float* cosPtr = CosTable)
        fixed (float* cosColumnPtr = CosColumns)
        fixed (float* tmpPtr = tmp)
        {
            for (int u = 0; u < 8; u++)
            {
                cosColumnLow[u] = Vector128.Load(cosColumnPtr + (u * 8));
                cosColumnHigh[u] = Vector128.Load(cosColumnPtr + (u * 8) + 4);
            }

            for (int y = 0; y < 8; y++)
            {
                int cosRow = y * 8;
                var accLow = Vector128<float>.Zero;
                var accHigh = Vector128<float>.Zero;
                for (int v = 0; v < 8; v++)
                {
                    var m = Vector128.Create(cosPtr[cosRow + v]);
                    accLow += m * deqLow[v];
                    accHigh += m * deqHigh[v];
                }

                Vector128.Store(accLow, tmpPtr + (y * 8));
                Vector128.Store(accHigh, tmpPtr + (y * 8) + 4);
            }

            for (int y = 0; y < 8; y++)
            {
                int rowBase = y * 8;
                var accLow = Vector128<float>.Zero;
                var accHigh = Vector128<float>.Zero;
                for (int u = 0; u < 8; u++)
                {
                    var scaled = Vector128.Create(tmpPtr[rowBase + u] * Cu[u]);
                    accLow += cosColumnLow[u] * scaled;
                    accHigh += cosColumnHigh[u] * scaled;
                }

                StoreRow(accLow, accHigh, dest, y * destStride);
            }
        }
    }

    private static void TransformScalar(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quant, Span<byte> dest, int destStride)
    {
        ReadOnlySpan<float> cosTable = CosTable;

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
                    sum += Cu[v] * deq * cosTable[cy + v];
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
                    sum += Cu[u] * tmp[(y * 8) + u] * cosTable[cx + u];
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

    /// <summary>
    /// 标量使用 <c>(int)</c> 向零截断；这里用 Floor 后转 int，再统一钳位。
    /// 由于结果最终被钳位到 [0, 255]，两者对负数的差异（截断得 0 / 取整得 -1）都会被钳位消除。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(Vector256<float> values, Span<byte> dest, int offset)
    {
        var iv = Vector256.ConvertToInt32(Vector256.Floor((values * Vector256.Create(0.25f)) + Vector256.Create(128.5f)));
        iv = Vector256.Min(Vector256.Max(iv, Vector256<int>.Zero), Vector256.Create(255));

        var s16 = Vector128.Narrow(Vector256.GetLower(iv), Vector256.GetUpper(iv));
        var b16 = Vector128.Narrow(s16.AsUInt16(), Vector128<ushort>.Zero);
        StoreBytes(b16, dest, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(Vector128<float> low, Vector128<float> high, Span<byte> dest, int offset)
    {
        var lowInt = Vector128.ConvertToInt32(Vector128.Floor((low * Vector128.Create(0.25f)) + Vector128.Create(128.5f)));
        var highInt = Vector128.ConvertToInt32(Vector128.Floor((high * Vector128.Create(0.25f)) + Vector128.Create(128.5f)));
        lowInt = Vector128.Min(Vector128.Max(lowInt, Vector128<int>.Zero), Vector128.Create(255));
        highInt = Vector128.Min(Vector128.Max(highInt, Vector128<int>.Zero), Vector128.Create(255));

        var s16 = Vector128.Narrow(lowInt, highInt);
        var b16 = Vector128.Narrow(s16.AsUInt16(), Vector128<ushort>.Zero);
        StoreBytes(b16, dest, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBytes(Vector128<byte> packed, Span<byte> dest, int offset)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(dest.Slice(offset, 8)), Vector128.AsUInt64(packed).GetElement(0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ToSingle256(Vector128<short> value)
    {
        Vector128<int> low = Vector128.WidenLower(value);
        Vector128<int> high = Vector128.WidenUpper(value);
        return Vector256.Create(Vector128.ConvertToSingle(low), Vector128.ConvertToSingle(high));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ToSingle256(Vector128<ushort> value)
    {
        Vector128<uint> low = Vector128.WidenLower(value);
        Vector128<uint> high = Vector128.WidenUpper(value);
        return Vector256.Create(Vector128.ConvertToSingle(low), Vector128.ConvertToSingle(high));
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

    private static float[] CreateCosColumns()
    {
        float[] table = new float[64];
        for (int p = 0; p < 8; p++)
        {
            for (int q = 0; q < 8; q++)
            {
                table[(q * 8) + p] = CosTable[(p * 8) + q];
            }
        }

        return table;
    }
}
