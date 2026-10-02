using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// 校验 <see cref="SimdJpegPipeline.InterleaveRgb24"/> 的字节置换：
    /// pshufb 向量化实现必须与"UnpackLow + 3 次 GetElement + 12 次标量写入"的旧实现逐字节一致。
    /// </summary>
    public class JpegRgbInterleaveTests
    {
        /// <summary>
        /// 旧实现的等价标量参考：完全照搬改造前的写法（rgLow = UnpackLow(r,g)，
        /// 取 3 个 ulong 后手工拼 24 字节）。用于锁定"只改实现、不改语义"。
        /// </summary>
        private static void ReferenceInterleave(ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, Span<byte> dest)
        {
            // UnpackLow(r, g) 的 16 字节 = [R0 G0 R1 G1 ... R7 G7]；
            // 低 64 位（rg0）装像素 0..3，高 64 位（rg1）装像素 4..7。
            ulong rg0 = 0, rg1 = 0, bData = 0;
            for (int i = 0; i < 4; i++)
            {
                rg0 |= (ulong)r[i] << (8 * (2 * i + 0));
                rg0 |= (ulong)g[i] << (8 * (2 * i + 1));
            }
            for (int i = 4; i < 8; i++)
            {
                rg1 |= (ulong)r[i] << (8 * (2 * (i - 4) + 0));
                rg1 |= (ulong)g[i] << (8 * (2 * (i - 4) + 1));
            }
            for (int i = 0; i < 8; i++)
            {
                bData |= (ulong)b[i] << (8 * i);
            }

            dest[0] = (byte)rg0;
            dest[1] = (byte)(rg0 >> 8);
            dest[2] = (byte)bData;
            dest[3] = (byte)(rg0 >> 16);
            dest[4] = (byte)(rg0 >> 24);
            dest[5] = (byte)(bData >> 8);
            dest[6] = (byte)(rg0 >> 32);
            dest[7] = (byte)(rg0 >> 40);
            dest[8] = (byte)(bData >> 16);
            dest[9] = (byte)(rg0 >> 48);
            dest[10] = (byte)(rg0 >> 56);
            dest[11] = (byte)(bData >> 24);
            dest[12] = (byte)rg1;
            dest[13] = (byte)(rg1 >> 8);
            dest[14] = (byte)(bData >> 32);
            dest[15] = (byte)(rg1 >> 16);
            dest[16] = (byte)(rg1 >> 24);
            dest[17] = (byte)(bData >> 40);
            dest[18] = (byte)(rg1 >> 32);
            dest[19] = (byte)(rg1 >> 40);
            dest[20] = (byte)(bData >> 48);
            dest[21] = (byte)(rg1 >> 48);
            dest[22] = (byte)(rg1 >> 56);
            dest[23] = (byte)(bData >> 56);
        }

        [Fact]
        public void InterleaveRgb24_MatchesScalarReference()
        {
            if (!Sse2.IsSupported || !Ssse3.IsSupported) return;

            var rng = new Random(20261002);
            var r = new byte[16];
            var g = new byte[16];
            var b = new byte[16];

            for (int iter = 0; iter < 500; iter++)
            {
                rng.NextBytes(r);
                rng.NextBytes(g);
                rng.NextBytes(b);

                var actual = new byte[32];
                var expected = new byte[24];

                SimdJpegPipeline.InterleaveRgb24(
                    Vector128.Create(r.AsSpan()).AsByte(),
                    Vector128.Create(g.AsSpan()).AsByte(),
                    Vector128.Create(b.AsSpan()).AsByte(),
                    actual.AsSpan(0, 32));

                ReferenceInterleave(r, g, b, expected);

                for (int i = 0; i < 24; i++)
                {
                    Assert.Equal(expected[i], actual[i]);
                }
                // 第 24 字节之后不应被写入
                for (int i = 24; i < actual.Length; i++)
                {
                    Assert.Equal(0, actual[i]);
                }
            }
        }

        [Fact]
        public void InterleaveRgb24_ProducesRgbOrder_OnKnownInput()
        {
            if (!Sse2.IsSupported || !Ssse3.IsSupported) return;

            // R = 0..7, G = 100..107, B = 200..207
            var r = new byte[16];
            var g = new byte[16];
            var b = new byte[16];
            for (int i = 0; i < 8; i++)
            {
                r[i] = (byte)i;
                g[i] = (byte)(100 + i);
                b[i] = (byte)(200 + i);
            }

            var actual = new byte[24];
            SimdJpegPipeline.InterleaveRgb24(
                Vector128.Create(r.AsSpan()).AsByte(),
                Vector128.Create(g.AsSpan()).AsByte(),
                Vector128.Create(b.AsSpan()).AsByte(),
                actual);

            for (int i = 0; i < 8; i++)
            {
                Assert.Equal((byte)i, actual[i * 3 + 0]);
                Assert.Equal((byte)(100 + i), actual[i * 3 + 1]);
                Assert.Equal((byte)(200 + i), actual[i * 3 + 2]);
            }
        }
    }
}
