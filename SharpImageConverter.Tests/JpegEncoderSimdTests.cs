using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// 验证编码方向 SIMD（FDCT / RGB-&gt;YCbCr）与标量实现逐位一致。
    /// </summary>
    public class JpegEncoderSimdTests
    {
        [Fact]
        public void ForwardDct8x8_MatchesScalar()
        {
            if (!SimdJpegEncodePipeline.FdctSupported) return;

            var rng = new Random(1234);
            var scalar = new int[64];
            var simd = new int[64];

            for (int iter = 0; iter < 500; iter++)
            {
                for (int i = 0; i < 64; i++)
                {
                    scalar[i] = rng.Next(-128, 128);
                }
                Array.Copy(scalar, simd, 64);

                JpegEncoder.FDCT8x8IntInPlace(scalar);
                SimdJpegEncodePipeline.ForwardDct8x8(simd);

                Assert.Equal(scalar, simd);
            }
        }

        [Fact]
        public void ForwardDct8x8_MatchesScalar_OnPatterns()
        {
            if (!SimdJpegEncodePipeline.FdctSupported) return;

            var patterns = new[]
            {
                new int[64],                       // all zero
                // 全 127
                CreateAll(127),
                // 全 -128
                CreateAll(-128),
                // 水平渐变
                Create((x, y) => x * 16 - 64),
                // 垂直渐变
                Create((x, y) => y * 16 - 64),
                // 棋盘
                Create((x, y) => ((x + y) & 1) == 0 ? 127 : -128),
                // 单像素
                Create((x, y) => (x == 3 && y == 5) ? 127 : 0),
            };

            foreach (int[] pattern in patterns)
            {
                var scalar = (int[])pattern.Clone();
                var simd = (int[])pattern.Clone();

                JpegEncoder.FDCT8x8IntInPlace(scalar);
                SimdJpegEncodePipeline.ForwardDct8x8(simd);

                Assert.Equal(scalar, simd);
            }
        }

        [Fact]
        public void RgbToYCbCr444_MatchesScalar()
        {
            if (!SimdJpegEncodePipeline.ColorSupported) return;

            const int width = 64;
            const int height = 64;
            var rng = new Random(5678);
            var rgb = new byte[width * height * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)rng.Next(256);

            var yScalar = new int[64];
            var cbScalar = new int[64];
            var crScalar = new int[64];
            var ySimd = new int[64];
            var cbSimd = new int[64];
            var crSimd = new int[64];

            for (int baseY = 0; baseY <= height - 8; baseY += 8)
            {
                for (int baseX = 0; baseX <= width - 8; baseX += 8)
                {
                    Array.Clear(yScalar);
                    Array.Clear(cbScalar);
                    Array.Clear(crScalar);
                    Array.Clear(ySimd);
                    Array.Clear(cbSimd);
                    Array.Clear(crSimd);

                    SimdJpegEncodePipeline.ColorSupported = false;
                    try
                    {
                        JpegEncoder.FillBlockRgbToYCbCr444(rgb, width, height, baseX, baseY, yScalar, cbScalar, crScalar, null);
                    }
                    finally
                    {
                        SimdJpegEncodePipeline.ColorSupported = System.Runtime.Intrinsics.X86.Ssse3.IsSupported;
                    }
                    SimdJpegEncodePipeline.RgbToYCbCr444(rgb, width, baseX, baseY, ySimd, cbSimd, crSimd);

                    Assert.Equal(yScalar, ySimd);
                    Assert.Equal(cbScalar, cbSimd);
                    Assert.Equal(crScalar, crSimd);
                }
            }
        }

        [Fact]
        public void RgbToYCbCr420_MatchesScalar()
        {
            if (!SimdJpegEncodePipeline.ColorSupported) return;

            const int width = 64;
            const int height = 64;
            var rng = new Random(9012);
            var rgb = new byte[width * height * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)rng.Next(256);

            var y00s = new int[64]; var y10s = new int[64]; var y01s = new int[64]; var y11s = new int[64];
            var cbs = new int[64]; var crs = new int[64];
            var y00v = new int[64]; var y10v = new int[64]; var y01v = new int[64]; var y11v = new int[64];
            var cbv = new int[64]; var crv = new int[64];

            for (int baseY = 0; baseY <= height - 16; baseY += 16)
            {
                for (int baseX = 0; baseX <= width - 16; baseX += 16)
                {
                    Array.Clear(y00s); Array.Clear(y10s); Array.Clear(y01s); Array.Clear(y11s);
                    Array.Clear(cbs); Array.Clear(crs);
                    Array.Clear(y00v); Array.Clear(y10v); Array.Clear(y01v); Array.Clear(y11v);
                    Array.Clear(cbv); Array.Clear(crv);

                    SimdJpegEncodePipeline.ColorSupported = false;
                    try
                    {
                        JpegEncoder.FillMcu420RgbToYCbCr(rgb, width, height, baseX, baseY,
                            y00s, y10s, y01s, y11s, cbs, crs, null);
                    }
                    finally
                    {
                        SimdJpegEncodePipeline.ColorSupported = System.Runtime.Intrinsics.X86.Ssse3.IsSupported;
                    }
                    SimdJpegEncodePipeline.RgbToYCbCr420(rgb, width, baseX, baseY,
                        y00v, y10v, y01v, y11v, cbv, crv);

                    Assert.Equal(y00s, y00v);
                    Assert.Equal(y10s, y10v);
                    Assert.Equal(y01s, y01v);
                    Assert.Equal(y11s, y11v);
                    Assert.Equal(cbs, cbv);
                    Assert.Equal(crs, crv);
                }
            }
        }

        [Fact]
        public void ForwardDctQuantize8x8_MatchesScalar()
        {
            if (!SimdJpegEncodePipeline.FdctSupported) return;

            var rng = new Random(4321);
            var recip = new int[64];
            for (int i = 0; i < 64; i++)
            {
                int quant = rng.Next(1, 40);
                recip[i] = (1 << 20) / (quant * 8);
            }

            var scalar = new int[64];
            var simd = new int[64];
            for (int iter = 0; iter < 500; iter++)
            {
                for (int i = 0; i < 64; i++) scalar[i] = rng.Next(-128, 128);
                Array.Copy(scalar, simd, 64);

                JpegEncoder.FDCT8x8IntInPlace(scalar);
                for (int i = 0; i < 64; i++) scalar[i] = QuantizeScalar(scalar[i], recip[i]);

                SimdJpegEncodePipeline.ForwardDctQuantize8x8(simd, recip);

                Assert.Equal(scalar, simd);
            }
        }

        private static int QuantizeScalar(int v, int recip)
        {
            if (v >= 0) return (int)(((long)v * recip + (1 << 19)) >> 20);
            int a = -v;
            return -(int)(((long)a * recip + (1 << 19)) >> 20);
        }

        [Fact]
        public void Encode444_Simd_MatchesScalar_Bytes_8x8Gradient()
        {
            const int w = 8, h = 8;
            var buf = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)((x * 255) / (w - 1));
                    int o = (y * w + x) * 3;
                    buf[o + 0] = v; buf[o + 1] = v; buf[o + 2] = v;
                }
            }

            var ys = new int[64]; var cbs = new int[64]; var crs = new int[64];
            var yv = new int[64]; var cbv = new int[64]; var crv = new int[64];
            SimdJpegEncodePipeline.ColorSupported = false;
            try
            {
                JpegEncoder.FillBlockRgbToYCbCr444(buf, w, h, 0, 0, ys, cbs, crs, null);
            }
            finally
            {
                SimdJpegEncodePipeline.ColorSupported = System.Runtime.Intrinsics.X86.Ssse3.IsSupported;
            }
            SimdJpegEncodePipeline.RgbToYCbCr444(buf, w, 0, 0, yv, cbv, crv);
            Assert.Equal(ys, yv);
            Assert.Equal(cbs, cbv);
            Assert.Equal(crs, crv);

            using var msA = new MemoryStream();
            JpegEncoder.Write(msA, w, h, buf, new JpegEncoderOptions(99, false, true, false));

            SimdJpegEncodePipeline.ColorSupported = false;
            SimdJpegEncodePipeline.FdctSupported = false;
            try
            {
                using var msB = new MemoryStream();
                JpegEncoder.Write(msB, w, h, buf, new JpegEncoderOptions(99, false, true, false));
                Assert.Equal(msB.ToArray(), msA.ToArray());
            }
            finally
            {
                SimdJpegEncodePipeline.ColorSupported = System.Runtime.Intrinsics.X86.Ssse3.IsSupported;
                SimdJpegEncodePipeline.FdctSupported = System.Runtime.Intrinsics.X86.Sse2.IsSupported;
            }
        }

        private static int[] CreateAll(int value)
        {
            var b = new int[64];
            Array.Fill(b, value);
            return b;
        }

        private static int[] Create(Func<int, int, int> f)
        {
            var b = new int[64];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    b[y * 8 + x] = f(x, y);
                }
            }
            return b;
        }
    }
}
