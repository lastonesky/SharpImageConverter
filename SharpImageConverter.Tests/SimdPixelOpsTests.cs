using System;
using System.Runtime.Intrinsics.X86;
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// 针对 SimdHelper 中新增的 SIMD 像素运算做逐字节正确性校验：
    /// SIMD 路径（SSSE3）写出的结果必须与标量参考实现完全一致。
    /// </summary>
    public class SimdPixelOpsTests
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

        [Fact]
        public void AddBytesInPlace_WrapsModulo256_NotSaturate()
        {
            // 200 + 100 = 300 -> 44（mod 256），若误用饱和加会得到 255
            var dst = new byte[] { 200, 250, 10, 128 };
            var src = new byte[] { 100, 100, 10, 127 };
            SimdHelper.AddBytesInPlace(dst, src);
            Assert.Equal(new byte[] { 44, 94, 20, 255 }, dst);
        }

        [Fact]
        public void AddBytesInPlace_MatchesScalar_ForAllLengths()
        {
            for (int len = 0; len <= 70; len++)
            {
                var dst = MakeData(len, len + 1);
                var src = MakeData(len, len + 977);
                var expected = (byte[])dst.Clone();
                for (int i = 0; i < len; i++)
                {
                    expected[i] = (byte)(expected[i] + src[i]);
                }

                SimdHelper.AddBytesInPlace(dst, src);
                Assert.Equal(expected, dst);
            }
        }

        [Fact]
        public void AddBytesInPlace_StopsAtShorterSource()
        {
            var dst = new byte[] { 1, 2, 3, 4, 5 };
            var src = new byte[] { 10, 20 };
            SimdHelper.AddBytesInPlace(dst, src);
            Assert.Equal(new byte[] { 11, 22, 3, 4, 5 }, dst);
        }

        [Fact]
        public void GrayscaleRgb24InPlace_MatchesScalarFormula_ForAllLengths()
        {
            for (int pixels = 0; pixels <= 70; pixels++)
            {
                var buf = MakeData(pixels * 3, pixels + 31);
                var expected = (byte[])buf.Clone();
                for (int p = 0; p + 2 < expected.Length; p += 3)
                {
                    int y = (77 * expected[p] + 150 * expected[p + 1] + 29 * expected[p + 2]) >> 8;
                    expected[p] = (byte)y;
                    expected[p + 1] = (byte)y;
                    expected[p + 2] = (byte)y;
                }

                SimdHelper.GrayscaleRgb24InPlace(buf);
                Assert.Equal(expected, buf);
            }
        }

        [Fact]
        public void GrayscaleRgb24InPlace_HandlesExtremes()
        {
            // 全 0 与全 255 覆盖灰度公式的两端（255 -> 65280 >> 8 = 255）
            var white = new byte[48];
            for (int i = 0; i < white.Length; i++) white[i] = 255;
            SimdHelper.GrayscaleRgb24InPlace(white);
            Assert.All(white, b => Assert.Equal((byte)255, b));

            var black = new byte[48];
            SimdHelper.GrayscaleRgb24InPlace(black);
            Assert.All(black, b => Assert.Equal((byte)0, b));
        }

        [Fact]
        public void Grayscale_ProcessingContext_ParallelPathMatchesScalar()
        {
            // 500x400 = 200000 像素，触发 Grayscale 的 Parallel.For 分支
            const int width = 500, height = 400;
            var buf = MakeData(width * height * 3, 12345);
            var expected = (byte[])buf.Clone();
            for (int p = 0; p + 2 < expected.Length; p += 3)
            {
                int y = (77 * expected[p] + 150 * expected[p + 1] + 29 * expected[p + 2]) >> 8;
                expected[p] = (byte)y;
                expected[p + 1] = (byte)y;
                expected[p + 2] = (byte)y;
            }

            var img = new Image<Rgb24>(width, height, (byte[])buf.Clone());
            ImageExtensions.Mutate(img, ctx => ctx.Grayscale());
            Assert.Equal(expected, img.Buffer);
        }

        [Fact]
        public void ExpandGrayToRgb_MatchesScalar_ForAllLengths()
        {
            for (int n = 0; n <= 70; n++)
            {
                var gray = MakeData(n, n + 7);
                var actual = new byte[n * 3];
                SimdHelper.ExpandGrayToRgb(gray, actual);

                var expected = new byte[n * 3];
                for (int i = 0; i < n; i++)
                {
                    expected[i * 3 + 0] = gray[i];
                    expected[i * 3 + 1] = gray[i];
                    expected[i * 3 + 2] = gray[i];
                }

                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void PackRgbaToRgb_MatchesScalar_ForAllLengths()
        {
            for (int pixels = 0; pixels <= 40; pixels++)
            {
                var rgba = MakeData(pixels * 4, pixels + 101);
                var actual = new byte[pixels * 3];
                SimdHelper.PackRgbaToRgb(rgba, actual);

                var expected = new byte[pixels * 3];
                for (int p = 0; p < pixels; p++)
                {
                    expected[p * 3 + 0] = rgba[p * 4 + 0];
                    expected[p * 3 + 1] = rgba[p * 4 + 1];
                    expected[p * 3 + 2] = rgba[p * 4 + 2];
                }

                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void ExpandRgbToRgba_MatchesScalar_ForAllLengths()
        {
            for (int pixels = 0; pixels <= 40; pixels++)
            {
                var rgb = MakeData(pixels * 3, pixels + 211);
                var actual = new byte[pixels * 4];
                SimdHelper.ExpandRgbToRgba(rgb, actual);

                var expected = new byte[pixels * 4];
                for (int p = 0; p < pixels; p++)
                {
                    expected[p * 4 + 0] = rgb[p * 3 + 0];
                    expected[p * 4 + 1] = rgb[p * 3 + 1];
                    expected[p * 4 + 2] = rgb[p * 3 + 2];
                    expected[p * 4 + 3] = 255;
                }

                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void Ssse3_IsAvailable_OnThisMachine()
        {
            // 若本机不支持 SSSE3，上面的测试只会覆盖标量回退路径，这里显式提示，
            // 避免"SIMD 分支从未被执行"被误认为已验证。
            Assert.True(Ssse3.IsSupported, "当前 CPU 不支持 SSSE3，SIMD 分支未被覆盖");
        }
    }
}
