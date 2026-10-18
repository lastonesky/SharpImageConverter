using System;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace Jpeg2Bmp.Tests
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
    }
}
