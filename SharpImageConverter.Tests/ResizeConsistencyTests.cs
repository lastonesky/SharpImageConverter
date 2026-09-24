using System;
using System.Runtime.Intrinsics.X86;
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// Resize 路径加了 SIMD / 预计算之后，数值结果必须与此前的标量实现逐位一致。
    /// 这里保留一份"参考实现"（改动前的原样算法）做对照。
    /// </summary>
    public class ResizeConsistencyTests
    {
        private static byte[] MakeData(int length, int seed)
        {
            var data = new byte[length];
            uint x = (uint)seed;
            for (int i = 0; i < length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] = (byte)(x >> 24);
            }
            return data;
        }

        // ---------------- 参考实现：改动前的 ResizeBilinear ----------------
        private static byte[] RefBilinear(byte[] src, int sw, int sh, int width, int height)
        {
            var dst = new byte[width * height * 3];
            float scaleX = sw <= 1 ? 0f : (float)(sw - 1) / Math.Max(1, width - 1);
            float scaleY = sh <= 1 ? 0f : (float)(sh - 1) / Math.Max(1, height - 1);

            const int Shift = 11;
            const int Scale = 1 << Shift;
            const int RoundingOffset = 1 << (2 * Shift - 1);

            var x0Index = new int[width];
            var x1Index = new int[width];
            var wx0Arr = new int[width];
            var wx1Arr = new int[width];
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

            for (int y = 0; y < height; y++)
            {
                float syf = y * scaleY;
                int y0 = (int)syf;
                int y1 = y0 + 1;
                if (y1 >= sh) y1 = sh - 1;
                float ty = syf - y0;
                int wy1 = (int)(ty * Scale + 0.5f);
                int wy0 = Scale - wy1;

                int row0 = y0 * sw * 3;
                int row1 = y1 * sw * 3;
                int dRow = y * width * 3;
                for (int x = 0; x < width; x++)
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
            return dst;
        }

        // ---------------- 参考实现：改动前的 ResizeArea ----------------
        private static byte[] RefArea(byte[] src, int sw, int sh, int width, int height)
        {
            var dst = new byte[width * height * 3];
            double scaleX = (double)sw / width;
            double scaleY = (double)sh / height;

            for (int dy = 0; dy < height; dy++)
            {
                double sy0 = dy * scaleY;
                double sy1 = (dy + 1) * scaleY;
                int syStart = (int)Math.Floor(sy0);
                int syEnd = (int)Math.Ceiling(sy1);
                if (syStart < 0) syStart = 0;
                if (syEnd > sh) syEnd = sh;

                for (int dx = 0; dx < width; dx++)
                {
                    double sx0 = dx * scaleX;
                    double sx1 = (dx + 1) * scaleX;
                    int sxStart = (int)Math.Floor(sx0);
                    int sxEnd = (int)Math.Ceiling(sx1);
                    if (sxStart < 0) sxStart = 0;
                    if (sxEnd > sw) sxEnd = sw;

                    double sumR = 0, sumG = 0, sumB = 0, totalArea = 0;
                    for (int sy = syStart; sy < syEnd; sy++)
                    {
                        double yTop = sy;
                        double yBottom = sy + 1;
                        double yOverlapTop = sy0 > yTop ? sy0 : yTop;
                        double yOverlapBottom = sy1 < yBottom ? sy1 : yBottom;
                        double yWeight = yOverlapBottom - yOverlapTop;
                        if (yWeight <= 0) continue;

                        for (int sx = sxStart; sx < sxEnd; sx++)
                        {
                            double xLeft = sx;
                            double xRight = sx + 1;
                            double xOverlapLeft = sx0 > xLeft ? sx0 : xLeft;
                            double xOverlapRight = sx1 < xRight ? sx1 : xRight;
                            double xWeight = xOverlapRight - xOverlapLeft;
                            if (xWeight <= 0) continue;

                            double area = xWeight * yWeight;
                            int s = (sy * sw + sx) * 3;
                            sumR += src[s + 0] * area;
                            sumG += src[s + 1] * area;
                            sumB += src[s + 2] * area;
                            totalArea += area;
                        }
                    }

                    int d = (dy * width + dx) * 3;
                    if (totalArea > 0)
                    {
                        double invArea = 1.0 / totalArea;
                        dst[d + 0] = (byte)(sumR * invArea + 0.5);
                        dst[d + 1] = (byte)(sumG * invArea + 0.5);
                        dst[d + 2] = (byte)(sumB * invArea + 0.5);
                    }
                }
            }
            return dst;
        }

        // ---------------- 参考实现：改动前的 ResizeBicubicOptimized ----------------
        private static float RefCubicF(float x)
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
        /// 原始标量版本。SIMD 路径必须与它逐位一致，因此这里的乘加结合顺序、
        /// 先夹取再 +0.5 截断的顺序都不能动。
        /// </summary>
        private static byte[] RefBicubic(byte[] src, int sw, int sh, int width, int height)
        {
            var dst = new byte[width * height * 3];
            float scaleX = (float)sw / width;
            float scaleY = (float)sh / height;

            var xIndex = new int[width * 4];
            var xWeight = new float[width * 4];
            var yIndex = new int[height * 4];
            var yWeight = new float[height * 4];

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
                    xWeight[idx] = RefCubicF(t - k);
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
                    yWeight[idx] = RefCubicF(t - k);
                }
            }

            for (int y = 0; y < height; y++)
            {
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
                int b0 = sy0 * sw * 3;
                int b1 = sy1 * sw * 3;
                int b2 = sy2 * sw * 3;
                int b3 = sy3 * sw * 3;

                for (int x = 0; x < width; x++)
                {
                    int xOff = x * 4;
                    float wx0 = xWeight[xOff + 0];
                    float wx1 = xWeight[xOff + 1];
                    float wx2 = xWeight[xOff + 2];
                    float wx3 = xWeight[xOff + 3];
                    int d = dBase + x * 3;

                    int q0 = xIndex[xOff + 0] * 3;
                    int q1 = xIndex[xOff + 1] * 3;
                    int q2 = xIndex[xOff + 2] * 3;
                    int q3 = xIndex[xOff + 3] * 3;
                    int p00 = b0 + q0, p01 = b0 + q1, p02 = b0 + q2, p03 = b0 + q3;
                    int p10 = b1 + q0, p11 = b1 + q1, p12 = b1 + q2, p13 = b1 + q3;
                    int p20 = b2 + q0, p21 = b2 + q1, p22 = b2 + q2, p23 = b2 + q3;
                    int p30 = b3 + q0, p31 = b3 + q1, p32 = b3 + q2, p33 = b3 + q3;

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
            }
            return dst;
        }

        [Fact]
        public void SimdPath_IsAvailable_OnThisMachine()
        {
            // 若平台不支持，上面的测试只会覆盖标量回退，这里显式提示避免"SIMD 分支从未执行"
            Assert.True(Ssse3.IsSupported && Sse41.IsSupported, "当前 CPU 不支持 SSSE3/SSE4.1，SIMD 分支未被覆盖");
        }

        public static TheoryData<int, int, int, int> SizeCases =>
            new()
            {
                { 2, 2, 3, 3 },
                { 2, 2, 1, 1 },
                { 7, 5, 13, 9 },
                { 16, 16, 5, 5 },
                { 33, 17, 8, 4 },
                { 64, 64, 200, 150 },
                { 100, 1, 7, 1 },
                { 1, 100, 1, 7 },
                { 10, 10, 3, 7 },
                { 17, 13, 4, 4 },
                { 5, 3, 40, 40 },
                { 8, 8, 1, 100 },
                { 13, 9, 13, 9 },
            };

        [Theory]
        [MemberData(nameof(SizeCases))]
        public void ResizeBilinear_MatchesReferenceExactly(int sw, int sh, int width, int height)
        {
            var src = MakeData(sw * sh * 3, sw * 31 + sh);
            var expected = RefBilinear(src, sw, sh, width, height);

            var img = new Image<Rgb24>(sw, sh, (byte[])src.Clone());
            ImageExtensions.Mutate(img, ctx => ctx.ResizeBilinear(width, height));

            Assert.Equal(width, img.Width);
            Assert.Equal(height, img.Height);
            Assert.Equal(expected, img.Buffer);
        }

        // Resize() 只在两个方向都不放大时才走 ResizeArea，因此这里只取纯缩小/等大的组合
        public static TheoryData<int, int, int, int> AreaCases =>
            new()
            {
                { 2, 2, 1, 1 },
                { 16, 16, 5, 5 },
                { 33, 17, 8, 4 },
                { 100, 1, 7, 1 },
                { 1, 100, 1, 7 },
                { 10, 10, 3, 7 },
                { 17, 13, 4, 4 },
                { 13, 9, 13, 9 },
                { 40, 30, 7, 11 },
            };

        /// <summary>
        /// 小尺寸穷举扫描。目的是逼出 SIMD 与标量的各种边界组合：
        /// 末尾不足 4 像素、右边缘 x1 被夹到 x0、8 字节载入贴着缓冲区末尾等。
        /// </summary>
        [Fact]
        public void ResizeBilinear_SweepSmallSizes_MatchesReference()
        {
            int[] dims = { 1, 2, 3, 4, 7, 8, 15, 16, 31, 32 };
            int[] outs = { 1, 2, 3, 4, 5, 8, 16 };

            foreach (int sw in dims)
            {
                foreach (int sh in dims)
                {
                    var src = MakeData(sw * sh * 3, sw * 101 + sh);
                    foreach (int dw in outs)
                    {
                        foreach (int dh in outs)
                        {
                            var expected = RefBilinear(src, sw, sh, dw, dh);
                            var img = new Image<Rgb24>(sw, sh, (byte[])src.Clone());
                            ImageExtensions.Mutate(img, ctx => ctx.ResizeBilinear(dw, dh));
                            Assert.Equal(expected, img.Buffer);
                        }
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(AreaCases))]
        public void ResizeArea_MatchesReferenceExactly(int sw, int sh, int width, int height)
        {
            var src = MakeData(sw * sh * 3, sw * 17 + sh * 3);
            var expected = RefArea(src, sw, sh, width, height);

            var img = new Image<Rgb24>(sw, sh, (byte[])src.Clone());
            ImageExtensions.Mutate(img, ctx => ctx.Resize(width, height));

            Assert.Equal(expected, img.Buffer);
        }

        [Theory]
        [MemberData(nameof(SizeCases))]
        public void ResizeBicubic_MatchesReferenceExactly(int sw, int sh, int width, int height)
        {
            var src = MakeData(sw * sh * 3, sw * 53 + sh);
            var expected = RefBicubic(src, sw, sh, width, height);

            var img = new Image<Rgb24>(sw, sh, (byte[])src.Clone());
            ImageExtensions.Mutate(img, ctx => ctx.ResizeBicubicOptimized(width, height));

            Assert.Equal(width, img.Width);
            Assert.Equal(height, img.Height);
            Assert.Equal(expected, img.Buffer);
        }

        /// <summary>
        /// 双三次的 SIMD 路径有两个必须在排查中被覆盖的边界：
        /// 抽头夹到了最后一个源像素（这类 x 会被排除出 SIMD），以及每行最后一个输出像素（只能按 3 字节写）。
        /// </summary>
        [Fact]
        public void ResizeBicubic_SweepSmallSizes_MatchesReference()
        {
            int[] dims = { 1, 2, 3, 4, 7, 8, 15, 16 };
            int[] outs = { 1, 2, 3, 4, 5, 8, 16 };

            foreach (int sw in dims)
            {
                foreach (int sh in dims)
                {
                    var src = MakeData(sw * sh * 3, sw * 7 + sh * 11);
                    foreach (int dw in outs)
                    {
                        foreach (int dh in outs)
                        {
                            var expected = RefBicubic(src, sw, sh, dw, dh);
                            var img = new Image<Rgb24>(sw, sh, (byte[])src.Clone());
                            ImageExtensions.Mutate(img, ctx => ctx.ResizeBicubicOptimized(dw, dh));
                            Assert.Equal(expected, img.Buffer);
                        }
                    }
                }
            }
        }
    }
}
