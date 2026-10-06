using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SharpImageConverter.Formats.Png;

/// <summary>
/// PNG 自适应行滤波：逐行在 None / Sub / Up / Average / Paeth 五种滤波器中，
/// 按「滤波后字节的有符号绝对值之和最小」挑一个，通常能比固定 Up 滤波再省 5%~20%。
/// </summary>
internal static class PngAdaptiveFilter
{
    public const int FilterNone = 0;
    public const int FilterSub = 1;
    public const int FilterUp = 2;
    public const int FilterAverage = 3;
    public const int FilterPaeth = 4;

    /// <summary>
    /// 按自适应滤波把原始扫描行写入输出流（每行先写 1 字节滤波器类型，再写滤波后的行）。
    /// </summary>
    /// <param name="output">输出流</param>
    /// <param name="src">原始像素（行优先，无行填充）</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="bytesPerPixel">每像素字节数</param>
    public static void WriteFiltered(Stream output, byte[] src, int width, int height, int bytesPerPixel)
    {
        int stride = width * bytesPerPixel;
        byte[] prev = new byte[stride];
        byte[] candidate = new byte[stride];
        byte[] best = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> cur = src.AsSpan(y * stride, stride);
            long bestScore = long.MaxValue;
            int bestFilter = FilterNone;
            for (int f = FilterNone; f <= FilterPaeth; f++)
            {
                long score = FilterCore(cur, prev, candidate, bytesPerPixel, f);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = f;
                    candidate.AsSpan(0, stride).CopyTo(best);
                }
            }
            output.WriteByte((byte)bestFilter);
            output.Write(best, 0, stride);
            cur.CopyTo(prev);
        }
    }

    /// <summary>
    /// 用指定滤波器对一行做滤波，返回滤波结果的「有符号绝对值之和」（PNG 经典的代价度量）。
    /// </summary>
    private static long FilterCore(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, Span<byte> dst, int bpp, int filter)
    {
        int n = cur.Length;
        long total = 0;
        int i = 0;

        // 前 bpp 字节的 left 像素为 0，需要单独处理；None/Up 不依赖 left，可以直接走向量。
        int scalarEnd = filter is FilterNone or FilterUp ? 0 : Math.Min(bpp, n);
        for (; i < scalarEnd; i++)
        {
            byte v = cur[i];
            dst[i] = (byte)(v - PredictScalar(cur, prev, i, bpp, filter));
            total += AbsSigned(dst[i]);
        }

        if (Vector.IsHardwareAccelerated)
        {
            int simd = Vector<byte>.Count;
            var zero = Vector<byte>.Zero;
            var acc = Vector<ushort>.Zero;
            int blocks = 0;
            ref byte curRef = ref MemoryMarshal.GetReference(cur);
            ref byte prevRef = ref MemoryMarshal.GetReference(prev);
            ref byte dstRef = ref MemoryMarshal.GetReference(dst);

            for (; i <= n - simd; i += simd)
            {
                var v = Vector.LoadUnsafe(ref curRef, (nuint)i);
                Vector<byte> pred;
                switch (filter)
                {
                    case FilterNone:
                        pred = zero;
                        break;
                    case FilterSub:
                        pred = Vector.LoadUnsafe(ref curRef, (nuint)(i - bpp));
                        break;
                    case FilterUp:
                        pred = Vector.LoadUnsafe(ref prevRef, (nuint)i);
                        break;
                    case FilterAverage:
                        {
                            var left = Vector.LoadUnsafe(ref curRef, (nuint)(i - bpp));
                            var up = Vector.LoadUnsafe(ref prevRef, (nuint)i);
                            Vector.Widen(left, out Vector<ushort> lLo, out Vector<ushort> lHi);
                            Vector.Widen(up, out Vector<ushort> uLo, out Vector<ushort> uHi);
                            pred = Vector.Narrow(
                                Vector.ShiftRightLogical(Vector.Add(lLo, uLo), 1),
                                Vector.ShiftRightLogical(Vector.Add(lHi, uHi), 1));
                            break;
                        }
                    default:
                        {
                            var a = Vector.LoadUnsafe(ref curRef, (nuint)(i - bpp));
                            var b = Vector.LoadUnsafe(ref prevRef, (nuint)i);
                            var c = Vector.LoadUnsafe(ref prevRef, (nuint)(i - bpp));
                            pred = PaethPredictor(a, b, c);
                            break;
                        }
                }

                var f = Vector.Subtract(v, pred);
                f.StoreUnsafe(ref dstRef, (nuint)i);
                var abs = Vector.Min(f, Vector.Subtract(zero, f));
                Vector.Widen(abs, out Vector<ushort> aLo, out Vector<ushort> aHi);
                acc += aLo;
                acc += aHi;
                // 每个 lane 单块最多 255，累计 8 块远小于 ushort 上限
                if (++blocks == 8)
                {
                    total += HorizontalSum(acc);
                    acc = Vector<ushort>.Zero;
                    blocks = 0;
                }
            }
            total += HorizontalSum(acc);
        }

        for (; i < n; i++)
        {
            byte v = cur[i];
            dst[i] = (byte)(v - PredictScalar(cur, prev, i, bpp, filter));
            total += AbsSigned(dst[i]);
        }
        return total;
    }

    private static byte PredictScalar(ReadOnlySpan<byte> cur, ReadOnlySpan<byte> prev, int i, int bpp, int filter)
    {
        byte a = i >= bpp ? cur[i - bpp] : (byte)0;
        byte b = prev[i];
        switch (filter)
        {
            case FilterNone: return 0;
            case FilterSub: return a;
            case FilterUp: return b;
            case FilterAverage: return (byte)(((int)a + b) >> 1);
            default:
                byte c = i >= bpp ? prev[i - bpp] : (byte)0;
                return PaethScalar(a, b, c);
        }
    }

    private static Vector<byte> PaethPredictor(Vector<byte> a, Vector<byte> b, Vector<byte> c)
    {
        var zero = Vector<byte>.Zero;
        var p = Vector.Subtract(Vector.Add(a, b), c);
        var pa = Vector.Min(Vector.Subtract(p, a), Vector.Subtract(zero, Vector.Subtract(p, a)));
        var pb = Vector.Min(Vector.Subtract(p, b), Vector.Subtract(zero, Vector.Subtract(p, b)));
        var pc = Vector.Min(Vector.Subtract(p, c), Vector.Subtract(zero, Vector.Subtract(p, c)));
        var useA = Vector.BitwiseAnd(Vector.LessThanOrEqual(pa, pb), Vector.LessThanOrEqual(pa, pc));
        var useB = Vector.LessThanOrEqual(pb, pc);
        return Vector.ConditionalSelect(useA, a, Vector.ConditionalSelect(useB, b, c));
    }

    private static byte PaethScalar(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static int AbsSigned(byte v) => v >= 128 ? 256 - v : v;

    private static long HorizontalSum(Vector<ushort> acc)
    {
        long sum = 0;
        for (int k = 0; k < Vector<ushort>.Count; k++) sum += acc[k];
        return sum;
    }
}
