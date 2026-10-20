using System;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests
{
    public class JpegTrellisQuantizerTests
    {
        [Fact]
        public void LambdaZero_ReproducesNearestQuantization()
        {
            var random = new Random(12345);
            Span<int> block = stackalloc int[64];
            Span<int> original = stackalloc int[64];
            Span<int> recip = stackalloc int[64];
            Span<byte> quant = stackalloc byte[64];
            Span<byte> acBits = stackalloc byte[256];

            for (int i = 0; i < 256; i++) acBits[i] = (byte)(i == 0 ? 4 : 8);
            for (int i = 0; i < 64; i++)
            {
                quant[i] = (byte)(1 + random.Next(20));
                recip[i] = (int)((1L << 20) / (quant[i] * 8L));
            }

            for (int trial = 0; trial < 10; trial++)
            {
                for (int i = 0; i < 64; i++)
                {
                    original[i] = random.Next(-4000, 4000);
                    block[i] = original[i];
                }

                JpegTrellisQuantizer.QuantizeInPlace(block, recip, quant, acBits, 0);

                for (int i = 0; i < 64; i++)
                {
                    int v = original[i];
                    int nearest = v >= 0
                        ? (int)(((long)v * recip[i] + (1L << 19)) >> 20)
                        : -(int)(((long)(-v) * recip[i] + (1L << 19)) >> 20);
                    Assert.Equal(nearest, block[i]);
                }
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(10.0)]
        [InlineData(50.0)]
        [InlineData(200.0)]
        [InlineData(500.0)]
        public void VectorizedDp_MatchesLegacyCost(double lambda)
        {
            var random = new Random(4242);
            Span<int> original = stackalloc int[64];
            Span<int> fast = stackalloc int[64];
            Span<int> legacy = stackalloc int[64];
            Span<int> recip = stackalloc int[64];
            Span<byte> quant = stackalloc byte[64];
            Span<byte> acBits = stackalloc byte[256];

            for (int i = 0; i < 256; i++)
            {
                // 模拟真实 Annex K 表：长度 2~16，ZRL/EOB 给短码。
                acBits[i] = (byte)(i == 0x00 ? 4 : i == 0xF0 ? 11 : 2 + (i % 15));
            }

            for (int trial = 0; trial < 40; trial++)
            {
                for (int i = 0; i < 64; i++)
                {
                    quant[i] = (byte)(1 + random.Next(255));
                    recip[i] = (int)((1L << 20) / (quant[i] * 8L));
                    original[i] = random.Next(-6000, 6000);
                }
                // DC 两条路径都按最近值处理，避免把 DC 差异混进比较。
                int dcNearest = Nearest(original[0], recip[0]);

                // QuantizeInPlace 会自己把 DC 转成最近值；legacy 入口不做 DC，需手动补上。
                original.CopyTo(fast);
                original.CopyTo(legacy);
                legacy[0] = dcNearest;

                JpegTrellisQuantizer.QuantizeInPlace(fast, recip, quant, acBits, lambda);
                JpegTrellisQuantizer.QuantizeInPlaceLegacy(legacy, recip, quant, acBits, lambda);

                long fastCost = Objective(original, fast, quant, acBits, lambda);
                long legacyCost = Objective(original, legacy, quant, acBits, lambda);
                Assert.Equal(legacyCost, fastCost);
                Assert.Equal(legacy.ToArray(), fast.ToArray());
            }
        }

        private static int Nearest(int v, int recip20)
            => v >= 0
                ? (int)(((long)v * recip20 + (1L << 19)) >> 20)
                : -(int)(((long)(-v) * recip20 + (1L << 19)) >> 20);

        private static long Objective(ReadOnlySpan<int> original, ReadOnlySpan<int> quantized,
            ReadOnlySpan<byte> quant, ReadOnlySpan<byte> acBits, double lambda)
        {
            long lambdaScaled = (long)Math.Round(lambda * 64.0);
            long distortion = 0;
            long bits = 0;
            int run = 0;
            for (int p = 1; p < 64; p++)
            {
                int index = JpegConstants.ZigZag[p];
                int k = quantized[index];
                long error = original[index] - (long)k * quant[index] * 8;
                distortion += error * error;
                if (k == 0)
                {
                    run++;
                    continue;
                }
                int category = Category(k);
                bits += RunBits(run, category, acBits) + category;
                run = 0;
            }
            if (run > 0) bits += Math.Max(1, (int)acBits[0x00]);
            return distortion + lambdaScaled * bits;
        }

        private static int Category(int v)
        {
            if (v < 0) v = -v;
            int c = 0;
            while (v > 0) { c++; v >>= 1; }
            return c;
        }

        private static int RunBits(int run, int category, ReadOnlySpan<byte> acBits)
        {
            int bits = 0;
            int zrl = Math.Max(1, (int)acBits[0xF0]);
            while (run >= 16) { bits += zrl; run -= 16; }
            int symbol = (run << 4) | category;
            int length = acBits[symbol];
            return bits + (length > 0 ? length : 16);
        }

    }
}
