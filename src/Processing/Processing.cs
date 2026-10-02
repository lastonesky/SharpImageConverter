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

        // 双像素收成：把 [pxR,pxG,pxB,X | px1R,px1G,px1B,X]（128 位，低 4 字节来自像素 x、高 4 字节来自 x+1）
        // 取成连续 6 字节 [pxR,pxG,pxB, px1R,px1G,px1B, 0...]（一次写 8 字节，尾 2 字节落在 x+2 的 R/G）
        private static readonly Vector128<byte> BicubicPack6Mask =
            Vector128.Create((byte)0, 1, 2, 4, 5, 6, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        // vpdpwssd 水平卷积的去交错掩码：把 4 个连续源像素的 16 字节读取
        // [R0,G0,B0, R1,G1,B1, R2,G2,B2, R3,G3,B3, x,x,x,x]
        // 一次性去交错 *并* 零扩展成 256 位 int16 序列
        // [R0,R1,R2,R3, G0,G1,G2,G3, B0,B1,B2,B3, 0,0,0,0]（字节视图下每个值占 2 字节）。
        // 这样一条 vpdpwssd（每 dword 通道吃 2 个 word）就完成 4 抽头 × 3 通道 = 12 个 MAC。
        // 陷阱：Avx2.Shuffle 按 128 位通道独立工作，索引只能取 0..15，
        // 因此源 16 字节必须先广播到两个通道（BroadcastVector128ToVector256）。
        private static readonly Vector256<byte> BicubicDeinterleave16 = Vector256.Create(
            Vector128.Create((byte)0, 0x80, 3, 0x80, 6, 0x80, 9, 0x80, 1, 0x80, 4, 0x80, 7, 0x80, 10, 0x80),
            Vector128.Create((byte)2, 0x80, 5, 0x80, 8, 0x80, 11, 0x80,
                             0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80));

        // 收成阶段：把 [R,R,G,G,B,B,0,0] 的 dword 0/2/4 聚到低 128 位 [R,G,B,?]（vpermd，可跨通道）
        private static readonly Vector256<int> BicubicPickRgbIdx =
            Vector256.Create(0, 2, 4, 0, 0, 0, 0, 0);

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
            if (UseQuantizedVnni && EnableVnniBilinear && AvxVnni.IsSupported) return ResizeBilinearVnni(width, height);
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
            int sw = _image.Width, sh = _image.Height;

            // VNNI 分派必须排在输出缓冲分配之前：它自带目标缓冲，
            // 否则每次调用都会白白分配一个 width*height*3 的缓冲再丢弃（142MP 时是 428MB）。
            if (UseQuantizedVnni && AvxVnni.IsSupported && VnniWorthFor(sw, sh, width, height))
                return ResizeBicubicVnni(width, height);

            var src = _image.Buffer;
            // 目标缓冲每个字节都会被覆写，跳过 new byte[] 的无谓清零
            var dst = GC.AllocateUninitializedArray<byte>(width * height * 3);
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

        }

        #region VNNI 量化 resize（分批：权重瓦片预计算 + pshufb 转置 gather + 多像素批量）

        // 静态开关：开启后 ResizeBicubicOptimized 走 VNNI 量化路径（默认开）。
        // 双线性默认不走 VNNI，见下面的 EnableVnniBilinear。
        public static bool UseQuantizedVnni { get; set; } = true;

        // ---- VNNI 收益阈值（本机实测标定，可按目标机器调整）----
        //
        // 标定方法：关闭分层编译（DOTNET_TieredCompilation=0）、float 与 VNNI 交替测量、
        // 每格取 min(41)、三轮重复；合成图 768x768 ~ 3840x2160，外加 D:\progressive.jpg（142MP 实拍）。
        // ratio = float / vnni，>1 表示 VNNI 更快。
        //
        //   双三次 缩小：输出 ≥0.25MP 起稳定更快 —— 27/27 格 ratio ≥1.01，中位 1.19
        //   双三次 放大：<2MP 不稳定（768x768 三轮稳定 0.66~0.70x 慢）；
        //               ≥2MP 起 15/15 格 ≥1.00、多数 1.10~1.28；142MP 实拍 1.35~1.41x
        //   双线性     ：54 格全部落在 0.85~1.21、中位 1.02，任何分辨率都没有可测量的收益
        //
        // 注：早前"双线性 Down 快 2.5x / Up 慢 1.19x"是旧基准（1 次预热 + 7 次中位数）
        //     预热不足造成的假象，已被上述重复测量推翻，不要据此做路由。
        public static int VnniMinPixelsDownscale { get; set; } = 1 << 18;   // 0.25 MP
        public static int VnniMinPixelsUpscale { get; set; } = 2 << 20;     // 2 MP

        // 双线性 VNNI 实测为中性，默认不启用。两条路径逐字节一致（max|Δ|=0），需要时打开。
        public static bool EnableVnniBilinear { get; set; } = false;

        private static bool VnniWorthFor(int sw, int sh, int width, int height)
        {
            long outPx = (long)width * height;
            long srcPx = (long)sw * sh;
            return outPx >= (outPx >= srcPx ? VnniMinPixelsUpscale : VnniMinPixelsDownscale);
        }

        // 双三次量化精度：权重放大 2^BicubicWShift，两级相乘后右移 2*BicubicWShift 还原。
        // 两级共 16 抽头定点累加须落在 int32：255*16*Scale^2 < 2^31 → 选 W=9（Scale=512）。
        private const int BicubicWShift = 9;
        private const int BicubicWScale = 1 << BicubicWShift;
        private const int BicubicRound = 1 << (2 * BicubicWShift - 1);
        private const int BicubicMaxSum = 255 * BicubicWScale * BicubicWScale;

        // 上面三个 int 常量的向量广播版本：原先在每个核心里现场 Vector.Create，
        // 与同文件 :130-131 的 MaxByte/Half 静态只读写法不一致。提升后 JIT 可直接引用常量池。
        private static readonly Vector256<int> BicubicMaxSum256 = Vector256.Create(BicubicMaxSum);
        private static readonly Vector256<int> BicubicRound256 = Vector256.Create(BicubicRound);
        private static readonly Vector128<int> BicubicMaxSum128 = Vector128.Create(BicubicMaxSum);
        private static readonly Vector128<int> BicubicRound128 = Vector128.Create(BicubicRound);

        // 双三次核函数（与浮点路径同定义，供 VNNI 量化路径与标量回退共用）
        private static float CubicF(float x)
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

        /// <summary>
        /// 双线性核心（VNNI 路由走这里）：一次算 4 个输出像素，数值与浮点 BilinearCore4 完全一致。
        /// 双线性是 2 抽头点积，pmaddwd（MultiplyAddAdjacent）已经是最优指令；vpdpwssd 的 4 抽头分组
        /// 无法隔离 2 抽头，故这里复用浮点路径的 8 字节载入 + BilinearPairMask 转置 gather +
        /// q/r 11 位定点拆分，保证输出与浮点逐位一致（VNNI 对 2 抽头无加速收益）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> BilinearCore4Vnni(
            ref byte srcRef, ref int x0Ref, ref short qRef, ref short rRef, int row0, int row1, int x,
            Vector128<int> wy0Vec, Vector128<int> wy1Vec, Vector128<int> roundVec)
        {
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

                // 水平：(a*q0 + b*q1) << 7 + (a*r0 + b*r1)，与浮点 BilinearCore4 完全相同
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

        private ImageProcessingContext ResizeBilinearVnni(int width, int height)
        {
            int sw = _image.Width, sh = _image.Height;
            var src = _image.Buffer;
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
                for (int x = 0; x < width; x++)
                {
                    float sxf = x * scaleX;
                    int x0 = (int)sxf;
                    int x1 = x0 + 1; if (x1 >= sw) x1 = sw - 1;
                    float tx = sxf - x0;
                    x0IndexArr[x] = x0 * 3;
                    x1IndexArr[x] = x1 * 3;
                    int wx1 = (int)(tx * Scale + 0.5f);
                    wx1Arr[x] = wx1;
                    wx0Arr[x] = Scale - wx1;
                }

                bool useSimd = Ssse3.IsSupported && Sse41.IsSupported && width >= 4;
                int simdXEnd = 0;
                if (useSimd)
                {
                    // pmaddwd 只接受 16 位操作数，把 11 位权重拆成 q(=w>>7) 与 r(=w&127)
                    simdXEnd = width;
                    for (int x = 0; x < width; x++)
                    {
                        if (x1IndexArr[x] != x0IndexArr[x] + 3) { simdXEnd = x; break; }
                    }
                    simdXEnd &= ~3;
                    if (simdXEnd > 0)
                    {
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
                ref int x0Ref = ref MemoryMarshal.GetReference(x0IndexArr.AsSpan(0, width));
                int srcLen = src.Length;

                for (int y = 0; y < height; y++)
                {
                    float syf = y * scaleY;
                    int y0 = (int)syf; int y1 = y0 + 1; if (y1 >= sh) y1 = sh - 1;
                    float ty = syf - y0;
                    int wy1 = (int)(ty * Scale + 0.5f);
                    int wy0 = Scale - wy1;
                    int row0 = y0 * sw * 3, row1 = y1 * sw * 3, dRow = y * width * 3;

                    int limit4 = 0, limit8 = 0;
                    if (useSimd)
                    {
                        limit4 = simdXEnd;
                        while (limit4 > 0 && x0IndexArr[limit4 - 1] > srcLen - 8 - row1) limit4 -= 4;
                        limit8 = limit4 & ~7;
                    }

                    var wy0Vec = Vector128.Create(wy0);
                    var wy1Vec = Vector128.Create(wy1);
                    var roundVec = Vector128.Create(RoundingOffset);

                    int x = 0;
                    for (; x < limit8; x += 8)
                    {
                        var o0 = BilinearCore4Vnni(ref srcRef, ref x0Ref, ref qRef, ref rRef, row0, row1, x, wy0Vec, wy1Vec, roundVec);
                        var o1 = BilinearCore4Vnni(ref srcRef, ref x0Ref, ref qRef, ref rRef, row0, row1, x + 4, wy0Vec, wy1Vec, roundVec);
                        int d0 = dRow + x * 3;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d0), o0.AsUInt64().GetElement(0));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d0 + 8), o0.AsUInt32().GetElement(2));
                        int d1 = d0 + 12;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d1), o1.AsUInt64().GetElement(0));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d1 + 8), o1.AsUInt32().GetElement(2));
                    }
                    for (; x < limit4; x += 4)
                    {
                        var o = BilinearCore4Vnni(ref srcRef, ref x0Ref, ref qRef, ref rRef, row0, row1, x, wy0Vec, wy1Vec, roundVec);
                        int d = dRow + x * 3;
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d), o.AsUInt64().GetElement(0));
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, d + 8), o.AsUInt32().GetElement(2));
                    }
                    for (; x < width; x++)
                    {
                        int s00 = row0 + x0IndexArr[x], s10 = row0 + x1IndexArr[x];
                        int s01 = row1 + x0IndexArr[x], s11 = row1 + x1IndexArr[x];
                        int d = dRow + x * 3;
                        int wx0 = wx0Arr[x], wx1 = wx1Arr[x];
                        int r0 = src[s00] * wx0 + src[s10] * wx1, r1 = src[s01] * wx0 + src[s11] * wx1;
                        dst[d] = (byte)((r0 * wy0 + r1 * wy1 + RoundingOffset) >> (2 * Shift));
                        int g0 = src[s00 + 1] * wx0 + src[s10 + 1] * wx1, g1 = src[s01 + 1] * wx0 + src[s11 + 1] * wx1;
                        dst[d + 1] = (byte)((g0 * wy0 + g1 * wy1 + RoundingOffset) >> (2 * Shift));
                        int b0 = src[s00 + 2] * wx0 + src[s10 + 2] * wx1, b1 = src[s01 + 2] * wx0 + src[s11 + 2] * wx1;
                        dst[d + 2] = (byte)((b0 * wy0 + b1 * wy1 + RoundingOffset) >> (2 * Shift));
                    }
                }
            }
            finally
            {
                poolInt.Return(x0IndexArr); poolInt.Return(x1IndexArr); poolInt.Return(wx0Arr); poolInt.Return(wx1Arr);
                if (qPairArr != null) poolShort.Return(qPairArr);
                if (rPairArr != null) poolShort.Return(rPairArr);
            }
            _image.Update(width, height, dst);
            return this;
        }

        /// <summary>
        /// 双三次单像素 int32 核心：结构完全镜像浮点 ResizeBicubicOptimized（BicubicRow + 垂直广播权重累加），
        /// 但全程走 int32 定点（×BicubicWScale²），无转置。每行用"广播权重 × 逐元素乘 + 横向相加"直接算水平 4 抽头，
        /// 因为源字节是 RGB 交错、权重逐像素广播，与浮点 BicubicRow 一样天然逐通道、无需 pshufb 转置（消除双三次的转置开销）。
        /// 数值与浮点同量级（同为 9 位定点），最大差 1~3 级（视觉无损）。
        /// 说明：本机 .NET 10 的 AvxVnni.MultiplyWideningAndAdd 仍降级为 pmaddwd（2 抽头），且 Avx512Vnni 不可用，
        /// 故不依赖 4 抽头 vpdpwssd，改用与浮点同构的 int32 广播权重路径——这样 int 路径与 float 路径同构、可直接比速。
        /// </summary>
        /// <summary>
        /// 单行水平 4 抽头 × 3 通道：一条 vpdpwssd 完成 12 个 MAC。
        /// 4 个抽头在源里连续（q1=q0+3, q2=q0+6, q3=q0+9），故一次 16 字节读取即可覆盖全部 12 个有效字节，
        /// 再用一条 256 位 pshufb 同时完成"去交错 + 零扩展成 int16"。
        /// </summary>
        /// <returns>int32 [Ra,Rb,Ga,Gb,Ba,Bb,0,0]，其中 R = Ra + Rb（每通道的 4 抽头被 vpdpwssd 拆成两半）。
        /// 值域 × BicubicWScale。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> BicubicHorizRowVpdpwssd(
            ref byte srcRef, int rowBase, int q0, Vector256<short> w)
        {
            // 读 16 字节（末 4 字节多余但会被掩码丢弃）；合法性由 simdStart/simdEnd 保证
            var v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcRef, rowBase + q0));
            // pshufb 按 128 位通道独立，先把这 16 字节广播到两个通道
            var wide = Vector256.Create(v, v);
            var a = Avx2.Shuffle(wide, BicubicDeinterleave16).AsInt16();
            return AvxVnni.MultiplyWideningAndAdd(Vector256<int>.Zero, a, w);
        }

        /// <summary>
        /// 收成：相邻 dword 相加完成水平 4 抽头合并 -&gt; 夹取 -&gt; 四舍五入右移 -&gt; 取 R,G,B 三字节。
        /// 水平 reduce 与垂直加权都是线性的，故可交换：把 reduce 推迟到垂直之后只做一次。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> BicubicFinishV(Vector256<int> val)
        {
            // [Ra,Rb,Ga,Gb,Ba,Bb,0,0] -> [R,R,G,G,B,B,0,0]（vpshufd 交换相邻 dword 后相加）
            var t = Avx2.Add(val, Avx2.Shuffle(val, 0xB1));
            t = Avx2.Max(t, Vector256<int>.Zero);
            t = Avx2.Min(t, BicubicMaxSum256);
            var shifted = Avx2.ShiftRightLogical(Avx2.Add(t, BicubicRound256), 2 * BicubicWShift);
            // 跨通道取 dword 0/2/4 到低 128 位 [R,G,B,?]，再用现有 128 位掩码抽低字节
            var rgb = Avx2.PermuteVar8x32(shifted, BicubicPickRgbIdx);
            return Ssse3.Shuffle(rgb.GetLower().AsByte(), BicubicStoreMask);
        }

        /// <summary>
        /// 单输出像素的双三次核心（vpdpwssd 版）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> BicubicCoreVnniV(
            ref byte srcRef, int base0, int base1, int base2, int base3, int q0,
            Vector256<short> w,
            Vector256<int> wvy0, Vector256<int> wvy1, Vector256<int> wvy2, Vector256<int> wvy3)
        {
            var h0 = BicubicHorizRowVpdpwssd(ref srcRef, base0, q0, w);
            var h1 = BicubicHorizRowVpdpwssd(ref srcRef, base1, q0, w);
            var h2 = BicubicHorizRowVpdpwssd(ref srcRef, base2, q0, w);
            var h3 = BicubicHorizRowVpdpwssd(ref srcRef, base3, q0, w);

            // 垂直累加（×BicubicWScale²）；水平结果超 int16，垂直侧仍用 int32 乘
            var val = Avx2.MultiplyLow(h0, wvy0);
            val = Avx2.Add(val, Avx2.MultiplyLow(h1, wvy1));
            val = Avx2.Add(val, Avx2.MultiplyLow(h2, wvy2));
            val = Avx2.Add(val, Avx2.MultiplyLow(h3, wvy3));
            return BicubicFinishV(val);
        }

        /// <summary>
        /// 一次处理 2 个相邻输出像素（N=2 批量）：两条独立的 vpdpwssd 链，最后合成一次 8 字节写。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BicubicCoreVnniV2(
            ref byte srcRef, int base0, int base1, int base2, int base3, int q0, int r0,
            Vector256<short> wA, Vector256<short> wB,
            Vector256<int> wvy0, Vector256<int> wvy1, Vector256<int> wvy2, Vector256<int> wvy3,
            ref byte dstRef, int dBase, int x)
        {
            var a0 = BicubicHorizRowVpdpwssd(ref srcRef, base0, q0, wA);
            var a1 = BicubicHorizRowVpdpwssd(ref srcRef, base1, q0, wA);
            var a2 = BicubicHorizRowVpdpwssd(ref srcRef, base2, q0, wA);
            var a3 = BicubicHorizRowVpdpwssd(ref srcRef, base3, q0, wA);

            var b0 = BicubicHorizRowVpdpwssd(ref srcRef, base0, r0, wB);
            var b1 = BicubicHorizRowVpdpwssd(ref srcRef, base1, r0, wB);
            var b2 = BicubicHorizRowVpdpwssd(ref srcRef, base2, r0, wB);
            var b3 = BicubicHorizRowVpdpwssd(ref srcRef, base3, r0, wB);

            var va = Avx2.MultiplyLow(a0, wvy0);
            va = Avx2.Add(va, Avx2.MultiplyLow(a1, wvy1));
            va = Avx2.Add(va, Avx2.MultiplyLow(a2, wvy2));
            va = Avx2.Add(va, Avx2.MultiplyLow(a3, wvy3));

            var vb = Avx2.MultiplyLow(b0, wvy0);
            vb = Avx2.Add(vb, Avx2.MultiplyLow(b1, wvy1));
            vb = Avx2.Add(vb, Avx2.MultiplyLow(b2, wvy2));
            vb = Avx2.Add(vb, Avx2.MultiplyLow(b3, wvy3));

            var pa = BicubicFinishV(va);   // 低 4 字节 = [Ra,Ga,Ba,?]
            var pb = BicubicFinishV(vb);
            // 合成 [R,G,B,?, R1,G1,B1,?] 再收成连续 6 字节；一次写 8 字节，
            // 尾 2 字节落在像素 x+2 的 R/G，下一轮迭代会覆写（循环上界保证不越界）。
            var both = Sse2.Or(pa, Sse2.ShiftLeftLogical128BitLane(pb, 4));
            var out6 = Ssse3.Shuffle(both, BicubicPack6Mask);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, dBase + x * 3), out6.AsUInt64().GetElement(0));
        }

        // ============================================================================
        // 回退路径：vpmulld 核心（不依赖 AVX-VNNI，也不要求 4 个抽头连续）。
        // 用途：(a) CPU 无 AVX-VNNI 时作为唯一 SIMD 路径；
        //       (b) 边缘被 clamp 打断连续性的像素（vpdpwssd 去交错掩码假设抽头相距 3 字节）。
        // 只要求最后一个抽头的 4 字节读取不越界。
        // ============================================================================

        // 一次 4 字节载入取某源像素的 R/G/B（零扩展成 int32）；第 4 个 lane 是相邻像素的 R，写出时丢弃。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> LoadRgbInt(ref byte srcRef, int off)
        {
            uint packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, off));
            return Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> BicubicHorizRowVnni(
            ref byte srcRef, int rowBase, int q0, int q1, int q2, int q3,
            Vector128<int> w0, Vector128<int> w1, Vector128<int> w2, Vector128<int> w3)
        {
            var acc = Sse41.MultiplyLow(LoadRgbInt(ref srcRef, rowBase + q0), w0);
            acc = Sse2.Add(acc, Sse41.MultiplyLow(LoadRgbInt(ref srcRef, rowBase + q1), w1));
            acc = Sse2.Add(acc, Sse41.MultiplyLow(LoadRgbInt(ref srcRef, rowBase + q2), w2));
            acc = Sse2.Add(acc, Sse41.MultiplyLow(LoadRgbInt(ref srcRef, rowBase + q3), w3));
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> BicubicCoreVnni(
            ref byte srcRef, int base0, int base1, int base2, int base3,
            int q0, int q1, int q2, int q3,
            Vector128<short> wH,
            Vector128<int> wvy0, Vector128<int> wvy1, Vector128<int> wvy2, Vector128<int> wvy3)
        {
            var w0 = Vector128.Create((int)wH.GetElement(0));
            var w1 = Vector128.Create((int)wH.GetElement(1));
            var w2 = Vector128.Create((int)wH.GetElement(2));
            var w3 = Vector128.Create((int)wH.GetElement(3));

            var r0 = BicubicHorizRowVnni(ref srcRef, base0, q0, q1, q2, q3, w0, w1, w2, w3);
            var r1 = BicubicHorizRowVnni(ref srcRef, base1, q0, q1, q2, q3, w0, w1, w2, w3);
            var r2 = BicubicHorizRowVnni(ref srcRef, base2, q0, q1, q2, q3, w0, w1, w2, w3);
            var r3 = BicubicHorizRowVnni(ref srcRef, base3, q0, q1, q2, q3, w0, w1, w2, w3);

            var val = Sse41.MultiplyLow(r0, wvy0);
            val = Sse2.Add(val, Sse41.MultiplyLow(r1, wvy1));
            val = Sse2.Add(val, Sse41.MultiplyLow(r2, wvy2));
            val = Sse2.Add(val, Sse41.MultiplyLow(r3, wvy3));

            val = Sse41.Max(val, Vector128<int>.Zero);
            val = Sse41.Min(val, BicubicMaxSum128);
            var shifted = Sse2.ShiftRightLogical(Sse2.Add(val, BicubicRound128), 2 * BicubicWShift);
            return Ssse3.Shuffle(shifted.AsByte(), BicubicStoreMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> LoadRgbInt2(ref byte srcRef, int offA, int offB)
        {
            var a = Sse41.ConvertToVector128Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, offA))).AsByte());
            var b = Sse41.ConvertToVector128Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, offB))).AsByte());
            return Vector256.Create(a, b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<int> BicubicHorizRowVnni2(
            ref byte srcRef, int rowBase,
            int q0, int q1, int q2, int q3, int r0, int r1, int r2, int r3,
            Vector256<int> wv0, Vector256<int> wv1, Vector256<int> wv2, Vector256<int> wv3)
        {
            var acc = Avx2.MultiplyLow(LoadRgbInt2(ref srcRef, rowBase + q0, rowBase + r0), wv0);
            acc = Avx2.Add(acc, Avx2.MultiplyLow(LoadRgbInt2(ref srcRef, rowBase + q1, rowBase + r1), wv1));
            acc = Avx2.Add(acc, Avx2.MultiplyLow(LoadRgbInt2(ref srcRef, rowBase + q2, rowBase + r2), wv2));
            acc = Avx2.Add(acc, Avx2.MultiplyLow(LoadRgbInt2(ref srcRef, rowBase + q3, rowBase + r3), wv3));
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BicubicCoreVnni2(
            ref byte srcRef, int base0, int base1, int base2, int base3,
            int q0, int q1, int q2, int q3, int r0, int r1, int r2, int r3,
            Vector128<short> wHx, Vector128<short> wHx1,
            Vector256<int> wvy0, Vector256<int> wvy1, Vector256<int> wvy2, Vector256<int> wvy3,
            ref byte dstRef, int dBase, int x)
        {
            var wv0 = Vector256.Create(Vector128.Create((int)wHx.GetElement(0)), Vector128.Create((int)wHx1.GetElement(0)));
            var wv1 = Vector256.Create(Vector128.Create((int)wHx.GetElement(1)), Vector128.Create((int)wHx1.GetElement(1)));
            var wv2 = Vector256.Create(Vector128.Create((int)wHx.GetElement(2)), Vector128.Create((int)wHx1.GetElement(2)));
            var wv3 = Vector256.Create(Vector128.Create((int)wHx.GetElement(3)), Vector128.Create((int)wHx1.GetElement(3)));

            var h0 = BicubicHorizRowVnni2(ref srcRef, base0, q0, q1, q2, q3, r0, r1, r2, r3, wv0, wv1, wv2, wv3);
            var h1 = BicubicHorizRowVnni2(ref srcRef, base1, q0, q1, q2, q3, r0, r1, r2, r3, wv0, wv1, wv2, wv3);
            var h2 = BicubicHorizRowVnni2(ref srcRef, base2, q0, q1, q2, q3, r0, r1, r2, r3, wv0, wv1, wv2, wv3);
            var h3 = BicubicHorizRowVnni2(ref srcRef, base3, q0, q1, q2, q3, r0, r1, r2, r3, wv0, wv1, wv2, wv3);

            var val = Avx2.MultiplyLow(h0, wvy0);
            val = Avx2.Add(val, Avx2.MultiplyLow(h1, wvy1));
            val = Avx2.Add(val, Avx2.MultiplyLow(h2, wvy2));
            val = Avx2.Add(val, Avx2.MultiplyLow(h3, wvy3));

            val = Avx2.Max(val, Vector256<int>.Zero);
            val = Avx2.Min(val, BicubicMaxSum256);
            var shifted = Avx2.ShiftRightLogical(Avx2.Add(val, BicubicRound256), 2 * BicubicWShift);

            // 收成 2 像素：Avx2.Shuffle 按 128 位通道独立，不能跨通道 gather，
            // 故分别对低 128（像素 x）与高 128（像素 x+1）做 128 位 shuffle 取出 R/G/B，再合成连续 6 字节。
            var aP = Ssse3.Shuffle(shifted.GetLower().AsByte(), BicubicStoreMask);
            var bP = Ssse3.Shuffle(shifted.GetUpper().AsByte(), BicubicStoreMask);
            var both = Sse2.Or(aP, Sse2.ShiftLeftLogical128BitLane(bP, 4));
            var out6 = Ssse3.Shuffle(both, BicubicPack6Mask);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, dBase + x * 3), out6.AsUInt64().GetElement(0));
        }

        /// <summary>单个输出像素的统一回退：优先 128 位 vpmulld 核心，边界不安全时落标量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BicubicPixelFallback(
            ref byte srcRef, ref byte dstRef, int srcBytes,
            int base0, int base1, int base2, int base3, int d,
            int q0, int q1, int q2, int q3,
            float fxw0, float fxw1, float fxw2, float fxw3,
            float fyw0, float fyw1, float fyw2, float fyw3,
            Vector128<short> wH,
            Vector128<int> wvy0, Vector128<int> wvy1, Vector128<int> wvy2, Vector128<int> wvy3,
            bool useSimdBase)
        {
            if (useSimdBase && q3 + 4 <= srcBytes)
            {
                var px = BicubicCoreVnni(ref srcRef, base0, base1, base2, base3, q0, q1, q2, q3,
                    wH, wvy0, wvy1, wvy2, wvy3);
                Unsafe.Add(ref dstRef, d + 0) = px.GetElement(0);
                Unsafe.Add(ref dstRef, d + 1) = px.GetElement(1);
                Unsafe.Add(ref dstRef, d + 2) = px.GetElement(2);
                return;
            }

            for (int c = 0; c < 3; c++)
            {
                float r0 = fxw0 * Unsafe.Add(ref srcRef, base0 + q0 + c) + fxw1 * Unsafe.Add(ref srcRef, base0 + q1 + c) + fxw2 * Unsafe.Add(ref srcRef, base0 + q2 + c) + fxw3 * Unsafe.Add(ref srcRef, base0 + q3 + c);
                float r1 = fxw0 * Unsafe.Add(ref srcRef, base1 + q0 + c) + fxw1 * Unsafe.Add(ref srcRef, base1 + q1 + c) + fxw2 * Unsafe.Add(ref srcRef, base1 + q2 + c) + fxw3 * Unsafe.Add(ref srcRef, base1 + q3 + c);
                float r2 = fxw0 * Unsafe.Add(ref srcRef, base2 + q0 + c) + fxw1 * Unsafe.Add(ref srcRef, base2 + q1 + c) + fxw2 * Unsafe.Add(ref srcRef, base2 + q2 + c) + fxw3 * Unsafe.Add(ref srcRef, base2 + q3 + c);
                float r3 = fxw0 * Unsafe.Add(ref srcRef, base3 + q0 + c) + fxw1 * Unsafe.Add(ref srcRef, base3 + q1 + c) + fxw2 * Unsafe.Add(ref srcRef, base3 + q2 + c) + fxw3 * Unsafe.Add(ref srcRef, base3 + q3 + c);
                float val = fyw0 * r0 + fyw1 * r1 + fyw2 * r2 + fyw3 * r3;
                if (val < 0f) val = 0f; else if (val > 255f) val = 255f;
                Unsafe.Add(ref dstRef, d + c) = (byte)(val + 0.5f);
            }
        }

        private ImageProcessingContext ResizeBicubicVnni(int width, int height)
        {
            var src = _image.Buffer;
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
            var poolShort = ArrayPool<short>.Shared;
            int[] xIndex = poolInt.Rent(width * 4);
            short[] xWeight = poolShort.Rent(width * 4);
            int[] yIndex = poolInt.Rent(height * 4);
            short[] yWeight = poolShort.Rent(height * 4);
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
                        if (sx < 0) sx = 0; else if (sx >= sw) sx = sw - 1;
                        xIndex[idx] = sx * 3;
                        xWeight[idx] = (short)Math.Round(CubicF(t - k) * BicubicWScale);
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
                        if (sy < 0) sy = 0; else if (sy >= sh) sy = sh - 1;
                        yIndex[idx] = sy;
                        yWeight[idx] = (short)Math.Round(CubicF(t - k) * BicubicWScale);
                    }
                }

                // 预计算水平权重瓦片（256 位）：与 BicubicHorizRowVpdpwssd 的 int16 操作数布局一致
                // [w0,w1,w2,w3, w0,w1,w2,w3, w0,w1,w2,w3, 0,0,0,0]，
                // 对应去交错后的 [R0,R1,R2,R3, G0,G1,G2,G3, B0,B1,B2,B3, 0,0,0,0]。
                // 低 128 位 [w0,w1,w2,w3,w0,w1,w2,w3] 正好也是 vpmulld 回退核心所需的形状。
                var wHTile = new Vector256<short>[width];
                for (int x = 0; x < width; x++)
                {
                    int xo = x * 4;
                    short a0 = xWeight[xo + 0], a1 = xWeight[xo + 1], a2 = xWeight[xo + 2], a3 = xWeight[xo + 3];
                    wHTile[x] = Vector256.Create(
                        Vector128.Create(a0, a1, a2, a3, a0, a1, a2, a3),
                        Vector128.Create(a0, a1, a2, a3, (short)0, (short)0, (short)0, (short)0));
                }

                int srcBytes = sw * 3;
                bool simdBase = Avx2.IsSupported && Sse41.IsSupported && Ssse3.IsSupported;
                bool useVnni = simdBase && AvxVnni.IsSupported;

                // vpdpwssd 路径要求 4 个水平抽头在源里【严格连续】（q3 == q0 + 9，即未被边缘 clamp 打断），
                // 且从 q0 起可安全读 16 字节；两侧不满足的少量像素交给回退核心，故左右边界要分别求。
                int simdStart = 0, simdEnd = 0;
                // 无 AVX-VNNI 时回退到 vpmulld 核心的区间（只要求最后抽头 4 字节读取不越界）
                int simdEndBase = width - 1;
                if (useVnni)
                {
                    while (simdStart < width)
                    {
                        int o = simdStart * 4;
                        if (xIndex[o + 3] == xIndex[o] + 9 && xIndex[o] + 16 <= srcBytes) break;
                        simdStart++;
                    }
                    // 上界取 width-1：保证双像素批量写 8 字节时不越出目标行尾
                    simdEnd = width - 1;
                    while (simdEnd > simdStart)
                    {
                        int o = (simdEnd - 1) * 4;
                        if (xIndex[o + 3] == xIndex[o] + 9 && xIndex[o] + 16 <= srcBytes) break;
                        simdEnd--;
                    }
                    if (simdEnd <= simdStart) { simdStart = 0; simdEnd = 0; }
                }
                else if (simdBase)
                {
                    for (int x = 0; x < simdEndBase; x++)
                    {
                        if (xIndex[x * 4 + 3] > srcBytes - 4) { simdEndBase = x; break; }
                    }
                }
                else
                {
                    simdEndBase = 0;
                }

                Parallel.For(0, height, y =>
                {
                    ref byte srcRef = ref MemoryMarshal.GetReference(src.AsSpan());
                    ref byte dstRef = ref MemoryMarshal.GetReference(dst.AsSpan());
                    int yOff = y * 4;
                    int sy0 = yIndex[yOff + 0], sy1 = yIndex[yOff + 1], sy2 = yIndex[yOff + 2], sy3 = yIndex[yOff + 3];
                    short wy0 = yWeight[yOff + 0], wy1 = yWeight[yOff + 1], wy2 = yWeight[yOff + 2], wy3 = yWeight[yOff + 3];
                    int base0 = sy0 * sw * 3, base1 = sy1 * sw * 3, base2 = sy2 * sw * 3, base3 = sy3 * sw * 3;
                    int dBase = y * width * 3;
                    // 垂直权重广播向量（256 位，8 lane = 同 wy）：双像素批量与单像素回退共用，整行一次性建好复用
                    var wvy0 = Vector256.Create((int)wy0);
                    var wvy1 = Vector256.Create((int)wy1);
                    var wvy2 = Vector256.Create((int)wy2);
                    var wvy3 = Vector256.Create((int)wy3);

                    float inv = 1f / BicubicWScale;
                    float fyw0 = wy0 * inv, fyw1 = wy1 * inv, fyw2 = wy2 * inv, fyw3 = wy3 * inv;

                    int x = 0;
                    if (useVnni)
                    {
                        // 段 1：左边缘 —— 抽头被 clamp 打断，去交错掩码不适用
                        for (; x < simdStart; x++)
                        {
                            int o = x * 4;
                            BicubicPixelFallback(ref srcRef, ref dstRef, srcBytes,
                                base0, base1, base2, base3, dBase + x * 3,
                                xIndex[o], xIndex[o + 1], xIndex[o + 2], xIndex[o + 3],
                                xWeight[o] * inv, xWeight[o + 1] * inv, xWeight[o + 2] * inv, xWeight[o + 3] * inv,
                                fyw0, fyw1, fyw2, fyw3,
                                wHTile[x].GetLower(),
                                wvy0.GetLower(), wvy1.GetLower(), wvy2.GetLower(), wvy3.GetLower(),
                                simdBase);
                        }
                        // 段 2：主区间 —— vpdpwssd 一次算 2 个输出像素（N=2 批量），两条独立链喂满流水线
                        for (; x + 1 < simdEnd; x += 2)
                        {
                            BicubicCoreVnniV2(ref srcRef, base0, base1, base2, base3,
                                xIndex[x * 4], xIndex[(x + 1) * 4],
                                wHTile[x], wHTile[x + 1], wvy0, wvy1, wvy2, wvy3, ref dstRef, dBase, x);
                        }
                        for (; x < simdEnd; x++)
                        {
                            var px = BicubicCoreVnniV(ref srcRef, base0, base1, base2, base3, xIndex[x * 4],
                                wHTile[x], wvy0, wvy1, wvy2, wvy3);
                            int d0 = dBase + x * 3;
                            Unsafe.Add(ref dstRef, d0 + 0) = px.GetElement(0);
                            Unsafe.Add(ref dstRef, d0 + 1) = px.GetElement(1);
                            Unsafe.Add(ref dstRef, d0 + 2) = px.GetElement(2);
                        }
                    }
                    else
                    {
                        // 无 AVX-VNNI：退回 vpmulld 双像素批量（不要求抽头连续）
                        for (; x + 1 < simdEndBase; x += 2)
                        {
                            int xo = x * 4, xo1 = (x + 1) * 4;
                            BicubicCoreVnni2(ref srcRef, base0, base1, base2, base3,
                                xIndex[xo], xIndex[xo + 1], xIndex[xo + 2], xIndex[xo + 3],
                                xIndex[xo1], xIndex[xo1 + 1], xIndex[xo1 + 2], xIndex[xo1 + 3],
                                wHTile[x].GetLower(), wHTile[x + 1].GetLower(),
                                wvy0, wvy1, wvy2, wvy3, ref dstRef, dBase, x);
                        }
                        for (; x < simdEndBase; x++)
                        {
                            var px = BicubicCoreVnni(ref srcRef, base0, base1, base2, base3,
                                xIndex[x * 4], xIndex[x * 4 + 1], xIndex[x * 4 + 2], xIndex[x * 4 + 3],
                                wHTile[x].GetLower(),
                                wvy0.GetLower(), wvy1.GetLower(), wvy2.GetLower(), wvy3.GetLower());
                            int d0 = dBase + x * 3;
                            Unsafe.Add(ref dstRef, d0 + 0) = px.GetElement(0);
                            Unsafe.Add(ref dstRef, d0 + 1) = px.GetElement(1);
                            Unsafe.Add(ref dstRef, d0 + 2) = px.GetElement(2);
                        }
                    }
                    // 段 3：右边缘 / 读取不安全的区间 —— 统一回退
                    for (; x < width; x++)
                    {
                        int o = x * 4;
                        BicubicPixelFallback(ref srcRef, ref dstRef, srcBytes,
                            base0, base1, base2, base3, dBase + x * 3,
                            xIndex[o], xIndex[o + 1], xIndex[o + 2], xIndex[o + 3],
                            xWeight[o] * inv, xWeight[o + 1] * inv, xWeight[o + 2] * inv, xWeight[o + 3] * inv,
                            fyw0, fyw1, fyw2, fyw3,
                            wHTile[x].GetLower(),
                            wvy0.GetLower(), wvy1.GetLower(), wvy2.GetLower(), wvy3.GetLower(),
                            simdBase);
                    }
                });
            }
            finally
            {
                poolInt.Return(xIndex); poolInt.Return(yIndex);
                poolShort.Return(xWeight); poolShort.Return(yWeight);
            }
            _image.Update(width, height, dst);
            return this;
        }

        #endregion

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
