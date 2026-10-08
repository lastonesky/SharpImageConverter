using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Png;

/// <summary>
/// Adler-32 校验算法实现，用于 Zlib 校验。
/// </summary>
public static class Adler32
{
    // s2 的加权系数：处理 16 个字节 b0..b15 时，
    //   s2 的增量 = 16·s1_0 + Σ (16 - i)·b_i
    // 即 b 与 [16,15,...,1] 的点积。系数 ≤ 16，可安全放进 16 位通道做 pmaddwd。
    private static readonly Vector128<short> AdlerWeightLo = Vector128.Create((short)16, 15, 14, 13, 12, 11, 10, 9);
    private static readonly Vector128<short> AdlerWeightHi = Vector128.Create((short)8, 7, 6, 5, 4, 3, 2, 1);

    /// <summary>
    /// 计算指定缓冲区片段的 Adler-32 校验值。
    /// </summary>
    /// <param name="buffer">输入数据</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数量</param>
    /// <returns>Adler-32 校验值</returns>
    public static uint Compute(byte[] buffer, int offset, int count)
    {
        return Update(1u, buffer, offset, count);
    }

    // Allows updating an existing checksum
    /// <summary>
    /// 基于已有校验值继续更新 Adler-32 校验。
    /// </summary>
    /// <param name="adler">已有校验值</param>
    /// <param name="buffer">输入数据</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">字节数量</param>
    /// <returns>更新后的校验值</returns>
    public static uint Update(uint adler, byte[] buffer, int offset, int count)
    {
        uint s1 = adler & 0xFFFF;
        uint s2 = adler >> 16 & 0xFFFF;
        const uint MOD = 65521;
        // NMAX 是 zlib 的经典上界：保证一个分块内 s2 不会超过 uint 的表示范围。
        // 改成 SIMD 累加后每个分块的 s1/s2 增量与标量完全一致，因此该上界依然成立。
        const uint NMAX = 5552;

        int index = offset;
        int len = count;

        while (len > 0)
        {
            int k = len < NMAX ? len : (int)NMAX;
            len -= k;

            if (SimdCompat.VectorBytesSupported)
            {
                index = AccumulateSimd(buffer, index, k, ref s1, ref s2);
            }
            else
            {
                int end = index + k;
                while (index < end)
                {
                    s1 += buffer[index++];
                    s2 += s1;
                }
            }

            s1 %= MOD;
            s2 %= MOD;
        }

        return s2 << 16 | s1;
    }

    /// <summary>
    /// 一次 16 字节的 SIMD 累加，返回消耗后的 index。
    /// <para>
    /// s1 用 psadbw 求 8 字节一组的绝对差之和（与零做差 == 求字节和），两个 64 位 lane 各得一组和；
    /// s2 的加权和用 pmaddwd 一次完成 8 个 16 位×16 位乘加，再水平归约。
    /// 这样每个分块只剩 1 次 s2 更新，而不是每字节 1 次。
    /// </para>
    /// <para>
    /// 全部原语经 <see cref="SimdCompat"/> 分派：x86 与 arm64 走同一套公式，
    /// 每条内在函数在两侧逐位等价（NEON 用 <c>uabd+uaddlp×3</c> 合成 psadbw、
    /// <c>smull+addp</c> 合成 pmaddwd、<c>addv</c> 合成 pshufd 两级归约），
    /// 因此校验值跨架构逐位一致。原 x86 分支代码原样保留在 SimdCompat 内。
    /// </para>
    /// </summary>
    private static int AccumulateSimd(byte[] buffer, int index, int count, ref uint s1, ref uint s2)
    {
        ref byte b = ref MemoryMarshal.GetArrayDataReference(buffer);
        int i = 0;
        int limit = count - Vector128<byte>.Count;

        for (; i <= limit; i += Vector128<byte>.Count)
        {
            Vector128<byte> v = Vector128.LoadUnsafe(ref b, (nuint)(index + i));

            // s1: Σ b_i。psadbw 的结果是"每 64 位 lane 装一个 16 位和"，
            // 因此 .NET 把它建模为 Vector128<ushort>；两个 lane 相加即 16 个字节的总和。
            Vector128<ushort> sad = SimdCompat.SumAbsoluteDifferences(v, Vector128<byte>.Zero);
            uint blockSum = (uint)(sad.AsUInt64().GetElement(0) + sad.AsUInt64().GetElement(1));

            // s2: Σ (16 - i)·b_i
            Vector128<int> pLo = SimdCompat.MultiplyAddAdjacent(SimdCompat.WidenLowerBytes(v).AsInt16(), AdlerWeightLo);
            Vector128<int> pHi = SimdCompat.MultiplyAddAdjacent(SimdCompat.WidenUpperBytes(v).AsInt16(), AdlerWeightHi);
            Vector128<int> t = SimdCompat.AddInt32(pLo, pHi);
            uint weighted = (uint)SimdCompat.HorizontalSumInt32(t);

            s2 += 16u * s1 + weighted;
            s1 += blockSum;
        }

        // 不足 16 字节的尾部：与标量完全同式
        for (; i < count; i++)
        {
            s1 += Unsafe.Add(ref b, index + i);
            s2 += s1;
        }

        return index + count;
    }
}
