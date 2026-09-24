using System;
using System.Buffers;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpImageConverter.Core;

namespace SharpImageConverter.Processing
{
    /// <summary>
    /// 图像处理器接口，用于对图像执行处理操作。
    /// </summary>
    public interface IImageProcessor
    {
        /// <summary>
        /// 执行处理操作
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        void Execute(Image<Rgb24> image);
    }

    /// <summary>
    /// 图像处理上下文，提供 resize、灰度等处理方法。
    /// </summary>
    public sealed class ImageProcessingContext
    {
        private readonly Image<Rgb24> _image;
        /// <summary>
        /// 使用指定图像创建处理上下文
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        public ImageProcessingContext(Image<Rgb24> image) { _image = image; }
        /// <summary>
        /// 最近邻缩放到指定尺寸
        /// </summary>
        /// <param name="width">目标宽度</param>
        /// <param name="height">目标高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext Resize(int width, int height)
        {
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0) return this;
            if (sw == width && sh == height) return this;

            bool isUpscale = width > sw || height > sh;
            if (isUpscale)
            {
                return ResizeBicubicOptimized(width, height);
            }

            return ResizeArea(width, height);
        }

        // ---- ResizeBilinear SIMD 掩码 ----
        // 一次 8 字节载入可以拿到 x0 与 x1=x0+1 两个像素的完整 RGB
        // （布局 R0 G0 B0 R1 G1 B1 ? ?）。下面两组掩码把
        // v01=[L0(8B), L1(8B)] 与 v23=[L2(8B), L3(8B)] 拼成 8 个 short：
        //   [a0,0, b0,0, a1,0, b1,0, a2,0, b2,0, a3,0, b3,0]
        // 交给 pmaddwd 一次算出 4 个像素的水平插值。
        private static readonly Vector128<byte>[] BilinearPairMaskA =
        {
            Vector128.Create((byte)0, 0x80, 3, 0x80, 8, 0x80, 11, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80),
            Vector128.Create((byte)1, 0x80, 4, 0x80, 9, 0x80, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80),
            Vector128.Create((byte)2, 0x80, 5, 0x80, 10, 0x80, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80),
        };

        private static readonly Vector128<byte>[] BilinearPairMaskB =
        {
            Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 0x80, 3, 0x80, 8, 0x80, 11, 0x80),
            Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1, 0x80, 4, 0x80, 9, 0x80, 12, 0x80),
            Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 0x80, 5, 0x80, 10, 0x80, 13, 0x80),
        };

        /// <summary>
        /// 水平方向 4 抽头的双三次插值：一次算出 3 个通道（放在 128 位浮点的前 3 条 lane）。
        /// 结合顺序与标量路径完全一致：(((w0*p0) + (w1*p1)) + (w2*p2)) + (w3*p3)。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> BicubicRow(
            ref byte srcRef,
            int rowBase, int q0, int q1, int q2, int q3,
            Vector128<float> w0, Vector128<float> w1, Vector128<float> w2, Vector128<float> w3)
        {
            var acc = Sse2.Multiply(w0, BicubicLoadRgb(ref srcRef, rowBase + q0));
            acc = Sse2.Add(acc, Sse2.Multiply(w1, BicubicLoadRgb(ref srcRef, rowBase + q1)));
            acc = Sse2.Add(acc, Sse2.Multiply(w2, BicubicLoadRgb(ref srcRef, rowBase + q2)));
            return Sse2.Add(acc, Sse2.Multiply(w3, BicubicLoadRgb(ref srcRef, rowBase + q3)));
        }

        /// <summary>
        /// 一次 4 字节载入拿到某个源像素的 R/G/B：低 3 字节是我们要的通道，
        /// 第 4 字节是相邻像素的 R——它跟着一起算，但最后不会被写出。
        /// 需要 offset + 4 &lt;= 源长度，由调用方把落在最后一个源像素上的抽头排除来保证。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> BicubicLoadRgb(ref byte srcRef, int offset)
        {
            uint packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, offset));
            return Sse2.ConvertToVector128Single(Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte()));
        }

        // 把 4 个 int32 结果的低字节收成 4 个紧凑字节（第 4 个 lane 是多余读出，不参与写出）
        private static readonly Vector128<byte> BicubicStoreMask =
            Vector128.Create((byte)0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        // 双三次写出端的两个常量贵在重复构造，提到静态只读里：夹取上界与四舍五入偏移
        private static readonly Vector128<float> MaxByte = Vector128.Create(255f);
        private static readonly Vector128<float> Half = Vector128.Create(0.5f);

        // 4 个 int32 结果（值域 [0,255]）取其低字节，得到 4 个紧凑字节
        private static readonly Vector128<byte> BilinearExtractMask =
            Vector128.Create((byte)0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        // 双线性定点精度：权重放大 2^Shift，垂直/水平两级相乘后右移 2*Shift
        private const int Shift = 11;
        private const int Scale = 1 << Shift;
        private const int RoundingOffset = 1 << (2 * Shift - 1);

        // 3 个通道各自的 4 字节交错成 12 字节 RGB24（4 个像素）
        private static readonly Vector128<byte>[] BilinearInterleaveMask =
        {
            Vector128.Create((byte)0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80),
            Vector128.Create((byte)0x80, 0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 0x80, 0x80),
            Vector128.Create((byte)0x80, 0x80, 0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 0x80, 0x80),
        };

        /// <summary>
        /// 双线性插值的 SIMD 核心：一次算出 4 个输出像素，结果放在返回向量的低 12 字节。
        /// 与标量路径的数值完全相同（同样的 11 位定点拆分与同样的移位顺序）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> BilinearCore4(
            ref byte srcRef,
            ref int x0Ref,
            ref short qRef,
            ref short rRef,
            int row0, int row1, int x,
            Vector128<int> wy0Vec, Vector128<int> wy1Vec, Vector128<int> roundVec)
        {
            // 一次 8 字节载入同时拿到同一行的 x0 与 x1=x0+1 两个像素
            ulong a0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row0 + Unsafe.Add(ref x0Ref, x + 0)));
            ulong a1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row0 + Unsafe.Add(ref x0Ref, x + 1)));
            ulong a2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row0 + Unsafe.Add(ref x0Ref, x + 2)));
            ulong a3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row0 + Unsafe.Add(ref x0Ref, x + 3)));
            var v01 = Vector128.Create(a0, a1).AsByte();
            var v23 = Vector128.Create(a2, a3).AsByte();

            ulong b0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row1 + Unsafe.Add(ref x0Ref, x + 0)));
            ulong b1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row1 + Unsafe.Add(ref x0Ref, x + 1)));
            ulong b2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row1 + Unsafe.Add(ref x0Ref, x + 2)));
            ulong b3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref srcRef, row1 + Unsafe.Add(ref x0Ref, x + 3)));
            var w01 = Vector128.Create(b0, b1).AsByte();
            var w23 = Vector128.Create(b2, b3).AsByte();

            nuint qo = (nuint)(x * 2);
            var qVec = Vector128.LoadUnsafe(ref qRef, qo);
            var rVec = Vector128.LoadUnsafe(ref rRef, qo);

            var outBytes = Vector128<byte>.Zero;
            for (int c = 0; c < 3; c++)
            {
                var p0 = Sse2.Or(Ssse3.Shuffle(v01, BilinearPairMaskA[c]), Ssse3.Shuffle(v23, BilinearPairMaskB[c])).AsInt16();
                var p1 = Sse2.Or(Ssse3.Shuffle(w01, BilinearPairMaskA[c]), Ssse3.Shuffle(w23, BilinearPairMaskB[c])).AsInt16();

                // 水平：(a*q0 + b*q1) << 7 + (a*r0 + b*r1)
                var h0 = Sse2.Add(Sse2.ShiftLeftLogical(Sse2.MultiplyAddAdjacent(p0, qVec), 7), Sse2.MultiplyAddAdjacent(p0, rVec));
                var h1 = Sse2.Add(Sse2.ShiftLeftLogical(Sse2.MultiplyAddAdjacent(p1, qVec), 7), Sse2.MultiplyAddAdjacent(p1, rVec));

                var val = Sse2.ShiftRightLogical(
                    Sse2.Add(Sse2.Add(Sse41.MultiplyLow(h0, wy0Vec), Sse41.MultiplyLow(h1, wy1Vec)), roundVec),
                    2 * Shift);

                var chan = Ssse3.Shuffle(val.AsByte(), BilinearExtractMask);
                outBytes = Sse2.Or(outBytes, Ssse3.Shuffle(chan, BilinearInterleaveMask[c]));
            }

            return outBytes;
        }

        /// <summary>
        /// 双线性插值缩放到指定尺寸
        /// </summary>
        /// <param name="width">目标宽度</param>
        /// <param name="height">目标高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext ResizeBilinear(int width, int height)
        {
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0) return this;
            if (sw == width && sh == height) return this;
            var src = _image.Buffer;
            // 目标缓冲每个字节都会被覆写，跳过 new byte[] 的无谓清零
            var dst = GC.AllocateUninitializedArray<byte>(width * height * 3);
            float scaleX = sw <= 1 ? 0f : (float)(sw - 1) / Math.Max(1, width - 1);
            float scaleY = sh <= 1 ? 0f : (float)(sh - 1) / Math.Max(1, height - 1);
            var poolInt = ArrayPool<int>.Shared;
            var poolShort = ArrayPool<short>.Shared;

            int[] x0IndexArr = poolInt.Rent(width);
            int[] x1IndexArr = poolInt.Rent(width);
            int[] wx0Arr = poolInt.Rent(width);
            int[] wx1Arr = poolInt.Rent(width);
            short[]? qPairArr = null;
            short[]? rPairArr = null;
            try
            {
                var x0Index = x0IndexArr;
                var x1Index = x1IndexArr;
                for (int x = 0; x < width; x++)
                {
                    float sxf = x * scaleX;
                    int x0 = (int)sxf;
                    int x1 = x0 + 1;
                    if (x1 >= sw) x1 = sw - 1;
                    float tx = sxf - x0;
                    x0Index[x] = x0 * 3;
                    x1Index[x] = x1 * 3;
                    int wx1 = (int)(tx * Scale + 0.5f);
                    wx1Arr[x] = wx1;
                    wx0Arr[x] = Scale - wx1;
                }

                bool useSimd = Ssse3.IsSupported && Sse41.IsSupported && width >= 4;
                int simdXEnd = 0;
                if (useSimd)
                {
                    // SIMD 依赖 8 字节载入同时取到 x0 与 x0+1，右边缘把 x1 夹到 x0 的位置只能走标量
                    simdXEnd = width;
                    for (int x = 0; x < width; x++)
                    {
                        if (x1Index[x] != x0Index[x] + 3) { simdXEnd = x; break; }
                    }
                    simdXEnd &= ~3;

                    if (simdXEnd > 0)
                    {
                        // pmaddwd 只接受 16 位操作数，把 11 位权重拆成 q(=w>>7) 与 r(=w&127)：
                        //   a*w0 + b*w1 == ((a*q0 + b*q1) << 7) + (a*r0 + b*r1)
                        // 两步乘积都远小于 short 上限，结果与原标量路径逐位一致。
                        qPairArr = poolShort.Rent(width * 2);
                        rPairArr = poolShort.Rent(width * 2);
                        for (int x = 0; x < width; x++)
                        {
                            int w0 = wx0Arr[x], w1 = wx1Arr[x];
                            qPairArr[x * 2 + 0] = (short)(w0 >> 7);
                            qPairArr[x * 2 + 1] = (short)(w1 >> 7);
                            rPairArr[x * 2 + 0] = (short)(w0 & 127);
                            rPairArr[x * 2 + 1] = (short)(w1 & 127);
                        }
                    }
                    else
                    {
                        useSimd = false;
                    }
                }

                ref byte srcRef = ref MemoryMarshal.GetReference(src.AsSpan());
                ref byte dstRef = ref MemoryMarshal.GetReference(dst.AsSpan());
                ref short qRef = ref Unsafe.NullRef<short>();
                ref short rRef = ref Unsafe.NullRef<short>();
                if (useSimd)
                {
                    qRef = ref MemoryMarshal.GetReference(qPairArr!.AsSpan(0, width * 2));
                    rRef = ref MemoryMarshal.GetReference(rPairArr!.AsSpan(0, width * 2));
                }
                int srcLen = src.Length;

                for (int y = 0; y < height; y++)
                {
                    float syf = y * scaleY;
                    int y0 = (int)syf;
                    int y1 = y0 + 1; if (y1 >= sh) y1 = sh - 1;
                    float ty = syf - y0;
                    int wy1 = (int)(ty * Scale + 0.5f);
                    int wy0 = Scale - wy1;

                    int row0 = y0 * sw * 3;
                    int row1 = y1 * sw * 3;
                    int dRow = y * width * 3;

                    // 每行还要保证 8 字节载入不越界；row1 >= row0，用 row1 做保守判断
                    int maxX0Off = srcLen - 8 - row1;
                    int limit4 = 0;
                    int limit8 = 0;
                    if (useSimd)
                    {
                        limit4 = simdXEnd;
                        while (limit4 > 0 && x0Index[limit4 - 1] > maxX0Off) limit4 -= 4;
                        // x0Index 单调不减，只要整批的末位安全，批内其余下标也一定安全
                        limit8 = limit4 & ~7;
                    }

                    var wy0Vec = Vector128.Create(wy0);
                    var wy1Vec = Vector128.Create(wy1);
                    var roundVec = Vector128.Create(RoundingOffset);

                    ref int x0Ref = ref MemoryMarshal.GetReference(x0IndexArr.AsSpan(0, width));

                    int x = 0;
                    // 两个 4 像素批次之间没有数据依赖，配对成 8 像素一轮可以让两条依赖链并行发射
                    for (; x < limit8; x += 8)
                    {
                        var o0 = BilinearCore4(ref srcRef, ref x0Ref, ref qRef, ref rRef, row0, row1, x, wy0Vec, wy1Vec, roundVec);
                        var o1 = BilinearCore4(ref srcRef, ref x0Ref, ref qRef, ref rRef, row0, row1, x + 4, wy0Vec, wy1Vec, roundVec);

                        int d0 = dRow + x * 3;
                        // 每批 12 字节：8 字节 + 4 字节两次写入，不越界写到下一组
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d0), o0.AsUInt64().GetElement(0));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d0 + 8), o0.AsUInt32().GetElement(2));
                        int d1 = d0 + 12;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d1), o1.AsUInt64().GetElement(0));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d1 + 8), o1.AsUInt32().GetElement(2));
                    }

                    for (; x < limit4; x += 4)
                    {
                        var o = BilinearCore4(ref srcRef, ref x0Ref, ref qRef, ref rRef, row0, row1, x, wy0Vec, wy1Vec, roundVec);

                        int d = dRow + x * 3;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d), o.AsUInt64().GetElement(0));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d + 8), o.AsUInt32().GetElement(2));
                    }

                    for (; x < width; x++)
                    {
                        int s00 = row0 + x0Index[x];
                        int s10 = row0 + x1Index[x];
                        int s01 = row1 + x0Index[x];
                        int s11 = row1 + x1Index[x];
                        int d = dRow + x * 3;
                        int wx0 = wx0Arr[x];
                        int wx1 = wx1Arr[x];

                        int r0 = src[s00 + 0] * wx0 + src[s10 + 0] * wx1;
                        int r1 = src[s01 + 0] * wx0 + src[s11 + 0] * wx1;
                        dst[d + 0] = (byte)((r0 * wy0 + r1 * wy1 + RoundingOffset) >> (2 * Shift));

                        int g0 = src[s00 + 1] * wx0 + src[s10 + 1] * wx1;
                        int g1 = src[s01 + 1] * wx0 + src[s11 + 1] * wx1;
                        dst[d + 1] = (byte)((g0 * wy0 + g1 * wy1 + RoundingOffset) >> (2 * Shift));

                        int b0 = src[s00 + 2] * wx0 + src[s10 + 2] * wx1;
                        int b1 = src[s01 + 2] * wx0 + src[s11 + 2] * wx1;
                        dst[d + 2] = (byte)((b0 * wy0 + b1 * wy1 + RoundingOffset) >> (2 * Shift));
                    }
                }
            }
            finally
            {
                poolInt.Return(x0IndexArr);
                poolInt.Return(x1IndexArr);
                poolInt.Return(wx0Arr);
                poolInt.Return(wx1Arr);
                if (qPairArr != null) poolShort.Return(qPairArr);
                if (rPairArr != null) poolShort.Return(rPairArr);
            }
            _image.Update(width, height, dst);
            return this;
        }

        private ImageProcessingContext ResizeArea(int width, int height)
        {
            var src = _image.Buffer;
            int sw = _image.Width;
            int sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0) return this;

            // 目标缓冲每个字节都会被覆写，跳过 new byte[] 的无谓清零
            var dst = GC.AllocateUninitializedArray<byte>(width * height * 3);

            double scaleX = (double)sw / width;
            double scaleY = (double)sh / height;

            // x 方向的重叠区间与权重只取决于 dx，y 方向只取决于 dy。
            // 原先它们被放在最内层按 (dx,dy) 反复重算，这里全部提到循环外预计算，
            // 内层只剩下乘加累加。求和顺序与权重值保持不变，因此结果与原来逐位一致。
            var poolInt = ArrayPool<int>.Shared;
            var poolDouble = ArrayPool<double>.Shared;

            int[] sxStartArr = poolInt.Rent(width);
            int[] spanXArr = poolInt.Rent(width);
            int[] syStartArr = poolInt.Rent(height);
            int[] spanYArr = poolInt.Rent(height);

            int maxSpanX = 1;
            for (int dx = 0; dx < width; dx++)
            {
                double sx0 = dx * scaleX;
                double sx1 = (dx + 1) * scaleX;
                int sxStart = (int)Math.Floor(sx0);
                int sxEnd = (int)Math.Ceiling(sx1);
                if (sxStart < 0) sxStart = 0;
                if (sxEnd > sw) sxEnd = sw;
                if (sxEnd < sxStart) sxEnd = sxStart;
                sxStartArr[dx] = sxStart;
                spanXArr[dx] = sxEnd - sxStart;
                if (sxEnd - sxStart > maxSpanX) maxSpanX = sxEnd - sxStart;
            }

            int maxSpanY = 1;
            for (int dy = 0; dy < height; dy++)
            {
                double sy0 = dy * scaleY;
                double sy1 = (dy + 1) * scaleY;
                int syStart = (int)Math.Floor(sy0);
                int syEnd = (int)Math.Ceiling(sy1);
                if (syStart < 0) syStart = 0;
                if (syEnd > sh) syEnd = sh;
                if (syEnd < syStart) syEnd = syStart;
                syStartArr[dy] = syStart;
                spanYArr[dy] = syEnd - syStart;
                if (syEnd - syStart > maxSpanY) maxSpanY = syEnd - syStart;
            }

            double[] xwFlat = poolDouble.Rent(width * maxSpanX);
            for (int dx = 0; dx < width; dx++)
            {
                double sx0 = dx * scaleX;
                double sx1 = (dx + 1) * scaleX;
                int sxStart = sxStartArr[dx];
                int span = spanXArr[dx];
                int off = dx * maxSpanX;
                for (int k = 0; k < span; k++)
                {
                    double xLeft = sxStart + k;
                    double xRight = xLeft + 1;
                    double xOverlapLeft = sx0 > xLeft ? sx0 : xLeft;
                    double xOverlapRight = sx1 < xRight ? sx1 : xRight;
                    xwFlat[off + k] = xOverlapRight - xOverlapLeft;
                }
            }

            double[] ywFlat = poolDouble.Rent(height * maxSpanY);
            for (int dy = 0; dy < height; dy++)
            {
                double sy0 = dy * scaleY;
                double sy1 = (dy + 1) * scaleY;
                int syStart = syStartArr[dy];
                int span = spanYArr[dy];
                int off = dy * maxSpanY;
                for (int k = 0; k < span; k++)
                {
                    double yTop = syStart + k;
                    double yBottom = yTop + 1;
                    double yOverlapTop = sy0 > yTop ? sy0 : yTop;
                    double yOverlapBottom = sy1 < yBottom ? sy1 : yBottom;
                    ywFlat[off + k] = yOverlapBottom - yOverlapTop;
                }
            }

            void ProcessRow(int dy)
            {
                int syStart = syStartArr[dy];
                int spanY = spanYArr[dy];
                int yOff = dy * maxSpanY;
                int dRow = dy * width * 3;

                for (int dx = 0; dx < width; dx++)
                {
                    int sxStart = sxStartArr[dx];
                    int spanX = spanXArr[dx];
                    int xOff = dx * maxSpanX;

                    double sumR = 0;
                    double sumG = 0;
                    double sumB = 0;
                    double totalArea = 0;

                    for (int ky = 0; ky < spanY; ky++)
                    {
                        double yWeight = ywFlat[yOff + ky];
                        int rowBase = (syStart + ky) * sw * 3;
                        for (int kx = 0; kx < spanX; kx++)
                        {
                            double area = xwFlat[xOff + kx] * yWeight;
                            int s = rowBase + (sxStart + kx) * 3;
                            sumR += src[s + 0] * area;
                            sumG += src[s + 1] * area;
                            sumB += src[s + 2] * area;
                            totalArea += area;
                        }
                    }

                    int d = dRow + dx * 3;
                    if (totalArea > 0)
                    {
                        double invArea = 1.0 / totalArea;
                        dst[d + 0] = (byte)(sumR * invArea + 0.5);
                        dst[d + 1] = (byte)(sumG * invArea + 0.5);
                        dst[d + 2] = (byte)(sumB * invArea + 0.5);
                    }
                    else
                    {
                        dst[d + 0] = 0;
                        dst[d + 1] = 0;
                        dst[d + 2] = 0;
                    }
                }
            }

            bool useParallel = width * height >= 200000 && height >= 32 && Environment.ProcessorCount > 1;
            if (useParallel)
            {
                Parallel.For(0, height, ProcessRow);
            }
            else
            {
                for (int dy = 0; dy < height; dy++)
                {
                    ProcessRow(dy);
                }
            }

            poolInt.Return(sxStartArr);
            poolInt.Return(spanXArr);
            poolInt.Return(syStartArr);
            poolInt.Return(spanYArr);
            poolDouble.Return(xwFlat);
            poolDouble.Return(ywFlat);

            _image.Update(width, height, dst);
            return this;
        }

        /// <summary>
        /// 使用优化双三次插值将图像缩放到指定尺寸
        /// </summary>
        /// <param name="width">目标宽度</param>
        /// <param name="height">目标高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext ResizeBicubicOptimized(int width, int height)
        {
            var src = _image.Buffer;
            // 目标缓冲每个字节都会被覆写，跳过 new byte[] 的无谓清零
            var dst = GC.AllocateUninitializedArray<byte>(width * height * 3);
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0)
            {
                _image.Update(width, height, dst);
                return this;
            }

            float scaleX = (float)sw / width;
            float scaleY = (float)sh / height;

            var poolInt = ArrayPool<int>.Shared;
            var poolFloat = ArrayPool<float>.Shared;
            int[] xIndex = poolInt.Rent(width * 4);
            float[] xWeight = poolFloat.Rent(width * 4);
            int[] yIndex = poolInt.Rent(height * 4);
            float[] yWeight = poolFloat.Rent(height * 4);
            try
            {
                for (int x = 0; x < width; x++)
                {
                    float gx = (x + 0.5f) * scaleX - 0.5f;
                    int ix = (int)MathF.Floor(gx);
                    float t = gx - ix;
                    for (int k = -1; k <= 2; k++)
                    {
                        int idx = x * 4 + (k + 1);
                        int sx = ix + k;
                        if (sx < 0) sx = 0;
                        else if (sx >= sw) sx = sw - 1;
                        xIndex[idx] = sx;
                        xWeight[idx] = CubicF(t - k);
                    }
                }

                for (int y = 0; y < height; y++)
                {
                    float gy = (y + 0.5f) * scaleY - 0.5f;
                    int iy = (int)MathF.Floor(gy);
                    float t = gy - iy;
                    for (int k = -1; k <= 2; k++)
                    {
                        int idx = y * 4 + (k + 1);
                        int sy = iy + k;
                        if (sy < 0) sy = 0;
                        else if (sy >= sh) sy = sh - 1;
                        yIndex[idx] = sy;
                        yWeight[idx] = CubicF(t - k);
                    }
                }

                // SIMD 路径要求抽头只落在 [0, sw-2] 上（一次读 4 字节）；
                // xIndex[x*4+3] 是该像素的最大抽头，且随 x 单调不减，找到第一个越界位置即可。
                // 另外最后一个输出像素只能按 3 字节写，不参与 SIMD。
                bool useSimd = Sse2.IsSupported && Ssse3.IsSupported && Sse41.IsSupported;
                int simdEnd = width - 1;
                if (useSimd)
                {
                    for (int x = 0; x < simdEnd; x++)
                    {
                        if (xIndex[x * 4 + 3] > sw - 2) { simdEnd = x; break; }
                    }
                }
                else
                {
                    simdEnd = 0;
                }

                Parallel.For(0, height, y =>
                {
                    // ref 局部变量不能跨 lambda 边界捕获，因此每行开始时各取一次
                    ref byte srcRef = ref MemoryMarshal.GetReference(src.AsSpan());
                    ref byte dstRef = ref MemoryMarshal.GetReference(dst.AsSpan());

                    int yOff = y * 4;
                    int sy0 = yIndex[yOff + 0];
                    int sy1 = yIndex[yOff + 1];
                    int sy2 = yIndex[yOff + 2];
                    int sy3 = yIndex[yOff + 3];
                    float wy0 = yWeight[yOff + 0];
                    float wy1 = yWeight[yOff + 1];
                    float wy2 = yWeight[yOff + 2];
                    float wy3 = yWeight[yOff + 3];

                    int dBase = y * width * 3;
                    int base0 = sy0 * sw * 3;
                    int base1 = sy1 * sw * 3;
                    int base2 = sy2 * sw * 3;
                    int base3 = sy3 * sw * 3;

                    int x = 0;
                    for (; x < simdEnd; x++)
                    {
                        int xOff = x * 4;
                        int sx0 = xIndex[xOff + 0];
                        int sx1 = xIndex[xOff + 1];
                        int sx2 = xIndex[xOff + 2];
                        int sx3 = xIndex[xOff + 3];

                        // 16 个源偏移在三个通道间共享，只算一次
                        int q0 = sx0 * 3, q1 = sx1 * 3, q2 = sx2 * 3, q3 = sx3 * 3;

                        var w0 = Vector128.Create(xWeight[xOff + 0]);
                        var w1 = Vector128.Create(xWeight[xOff + 1]);
                        var w2 = Vector128.Create(xWeight[xOff + 2]);
                        var w3 = Vector128.Create(xWeight[xOff + 3]);

                        var row0 = BicubicRow(ref srcRef, base0, q0, q1, q2, q3, w0, w1, w2, w3);
                        var row1 = BicubicRow(ref srcRef, base1, q0, q1, q2, q3, w0, w1, w2, w3);
                        var row2 = BicubicRow(ref srcRef, base2, q0, q1, q2, q3, w0, w1, w2, w3);
                        var row3 = BicubicRow(ref srcRef, base3, q0, q1, q2, q3, w0, w1, w2, w3);

                        // 垂直方向同样是 (((wy0*row0) + (wy1*row1)) + ...) 的结合顺序
                        var val = Sse2.Multiply(Vector128.Create(wy0), row0);
                        val = Sse2.Add(val, Sse2.Multiply(Vector128.Create(wy1), row1));
                        val = Sse2.Add(val, Sse2.Multiply(Vector128.Create(wy2), row2));
                        val = Sse2.Add(val, Sse2.Multiply(Vector128.Create(wy3), row3));

                        // 先夹到 [0,255] 再 +0.5 截断——顺序必须和标量一致，否则边界取值会差 1
                        val = Sse2.Min(Sse2.Max(val, Vector128<float>.Zero), MaxByte);
                        // 必须用截断转换：标量路径是 (byte)(val + 0.5f)，协处理器语义等同于 cvtt。
                        // 若误用 cvtps2dq（最近取整）会整体偏 1，且 255.5 会取整成 256，取低字节时又绕回 0。
                        Vector128<int> iv = Sse2.ConvertToVector128Int32WithTruncation(Sse2.Add(val, Half));
                        var packed = Ssse3.Shuffle(iv.AsByte(), BicubicStoreMask);

                        int d = dBase + x * 3;
                        if (x + 1 < width)
                        {
                            // 写成 4 字节会顺带覆盖下一个像素的 R，那个像素随后会自己覆写回来
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d), packed.AsUInt32().GetElement(0));
                        }
                        else
                        {
                            dst[d + 0] = (byte)iv.GetElement(0);
                            dst[d + 1] = (byte)iv.GetElement(1);
                            dst[d + 2] = (byte)iv.GetElement(2);
                        }
                    }

                    for (; x < width; x++)
                    {
                        int xOff = x * 4;
                        int sx0 = xIndex[xOff + 0];
                        int sx1 = xIndex[xOff + 1];
                        int sx2 = xIndex[xOff + 2];
                        int sx3 = xIndex[xOff + 3];
                        float wx0 = xWeight[xOff + 0];
                        float wx1 = xWeight[xOff + 1];
                        float wx2 = xWeight[xOff + 2];
                        float wx3 = xWeight[xOff + 3];
                        int d = dBase + x * 3;

                        // 16 个源偏移在三个通道间共享，只算一次
                        int q0 = sx0 * 3, q1 = sx1 * 3, q2 = sx2 * 3, q3 = sx3 * 3;
                        int p00 = base0 + q0, p01 = base0 + q1, p02 = base0 + q2, p03 = base0 + q3;
                        int p10 = base1 + q0, p11 = base1 + q1, p12 = base1 + q2, p13 = base1 + q3;
                        int p20 = base2 + q0, p21 = base2 + q1, p22 = base2 + q2, p23 = base2 + q3;
                        int p30 = base3 + q0, p31 = base3 + q1, p32 = base3 + q2, p33 = base3 + q3;

                        for (int c = 0; c < 3; c++)
                        {
                            float row0 =
                                wx0 * src[p00 + c] +
                                wx1 * src[p01 + c] +
                                wx2 * src[p02 + c] +
                                wx3 * src[p03 + c];
                            float row1 =
                                wx0 * src[p10 + c] +
                                wx1 * src[p11 + c] +
                                wx2 * src[p12 + c] +
                                wx3 * src[p13 + c];
                            float row2 =
                                wx0 * src[p20 + c] +
                                wx1 * src[p21 + c] +
                                wx2 * src[p22 + c] +
                                wx3 * src[p23 + c];
                            float row3 =
                                wx0 * src[p30 + c] +
                                wx1 * src[p31 + c] +
                                wx2 * src[p32 + c] +
                                wx3 * src[p33 + c];

                            float val =
                                wy0 * row0 +
                                wy1 * row1 +
                                wy2 * row2 +
                                wy3 * row3;

                            if (val < 0f) val = 0f;
                            else if (val > 255f) val = 255f;
                            dst[d + c] = (byte)(val + 0.5f);
                        }
                    }
                });
            }
            finally
            {
                poolInt.Return(xIndex);
                poolInt.Return(yIndex);
                poolFloat.Return(xWeight);
                poolFloat.Return(yWeight);
            }

            _image.Update(width, height, dst);
            return this;

            static float CubicF(float x)
            {
                const float a = -0.5f;
                x = MathF.Abs(x);
                if (x <= 1f)
                {
                    return (a + 2f) * x * x * x - (a + 3f) * x * x + 1f;
                }
                if (x < 2f)
                {
                    return a * x * x * x - 5f * a * x * x + 8f * a * x - 4f * a;
                }
                return 0f;
            }
        }

        /// <summary>
        /// 将图像缩放到不超过指定最大宽高（保持宽高比）
        /// </summary>
        /// <param name="maxWidth">最大宽度</param>
        /// <param name="maxHeight">最大高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext ResizeToFit(int maxWidth, int maxHeight)
        {
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0) return this;
            if (maxWidth <= 0 || maxHeight <= 0) return this;

            double scaleW = (double)maxWidth / sw;
            double scaleH = (double)maxHeight / sh;
            double scale = Math.Min(scaleW, scaleH);

            int w = Math.Max(1, (int)Math.Round(sw * scale));
            int h = Math.Max(1, (int)Math.Round(sh * scale));

            return Resize(w, h);
        }
        /// <summary>
        /// 转换为灰度图（简单加权平均）
        /// </summary>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext Grayscale()
        {
            var buf = _image.Buffer;
            int width = _image.Width;
            int height = _image.Height;
            int n = buf.Length / 3;
            bool useParallel = n >= 200000 && height >= 32 && Environment.ProcessorCount > 1;
            if (useParallel)
            {
                int rowBytes = width * 3;
                Parallel.For(0, height, y =>
                {
                    SimdHelper.GrayscaleRgb24InPlace(buf.AsSpan(y * rowBytes, rowBytes));
                });
            }
            else
            {
                SimdHelper.GrayscaleRgb24InPlace(buf);
            }
            return this;
        }
    }

    /// <summary>
    /// 图像扩展方法
    /// </summary>
    public static class ImageExtensions
    {
        /// <summary>
        /// 克隆图像，应用处理上下文并返回新图像
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        /// <param name="action">处理操作</param>
        /// <returns>处理后的新图像（Rgb24）</returns>
        public static Image<Rgb24> Clone(this Image<Rgb24> image, Action<ImageProcessingContext> action)
        {
            var srcBuffer = image.Buffer;
            // 紧接着就要整块覆写，跳过 new byte[] 的无谓清零
            var clonedBuffer = GC.AllocateUninitializedArray<byte>(srcBuffer.Length);
            Buffer.BlockCopy(srcBuffer, 0, clonedBuffer, 0, srcBuffer.Length);
            var clonedImage = new Image<Rgb24>(image.Width, image.Height, clonedBuffer);
            var ctx = new ImageProcessingContext(clonedImage);
            action(ctx);
            return clonedImage;
        }

        /// <summary>
        /// 对图像应用处理上下文并执行指定操作
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        /// <param name="action">处理操作</param>
        public static void Mutate(this Image<Rgb24> image, Action<ImageProcessingContext> action)
        {
            var ctx = new ImageProcessingContext(image);
            action(ctx);
        }
    }
}
