using System;
using SharpImageConverter.Formats.Png;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// Adler-32 的 SIMD 累加（psadbw + pmaddwd）必须与最朴素的分块标量定义逐位一致。
    /// NMAX 分块是 Adler-32 正确性的硬约束，因此测试要跨过分块边界。
    /// </summary>
    public class Adler32Tests
    {
        private static uint Reference(byte[] data, int offset, int count)
        {
            uint s1 = 1, s2 = 0;
            const uint MOD = 65521;
            const int NMAX = 5552;
            int index = offset;
            int len = count;
            while (len > 0)
            {
                int k = len < NMAX ? len : NMAX;
                len -= k;
                while (k-- > 0)
                {
                    s1 += data[index++];
                    s2 += s1;
                }
                s1 %= MOD;
                s2 %= MOD;
            }
            return s2 << 16 | s1;
        }

        [Fact]
        public void KnownVector_Adler32OfWikipedia()
        {
            // RFC 1950 的经典用例："Wikipedia" -> 0x11E60398
            var data = System.Text.Encoding.ASCII.GetBytes("Wikipedia");
            Assert.Equal(0x11E60398u, Adler32.Compute(data, 0, data.Length));
        }

        [Fact]
        public void KnownVector_Adler32OfTheQuickBrownFox()
        {
            var data = System.Text.Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
            Assert.Equal(0x5BDC0FDAu, Adler32.Compute(data, 0, data.Length));
        }

        [Fact]
        public void MatchesReference_ForManyLengthsAndOffsets()
        {
            var rng = new Random(20261002);
            var data = new byte[20000];
            rng.NextBytes(data);

            // 关键长度：0、1、15、16、17、31、32、跨 NMAX(5552) 边界、远超 NMAX
            foreach (int len in new[] { 0, 1, 2, 15, 16, 17, 31, 32, 33, 100, 5551, 5552, 5553, 11000, 16657, 20000 })
            {
                foreach (int off in new[] { 0, 1, 3, 7 })
                {
                    if (off + len > data.Length) continue;
                    uint expected = Reference(data, off, len);
                    uint actual = Adler32.Compute(data, off, len);
                    Assert.True(expected == actual, $"len={len} off={off}: expected 0x{expected:X8}, actual 0x{actual:X8}");
                }
            }
        }

        [Fact]
        public void Update_ContinuesFromExistingChecksum()
        {
            var rng = new Random(7);
            var data = new byte[9000];
            rng.NextBytes(data);

            // 一次算完 vs 分两段 update
            uint whole = Adler32.Compute(data, 0, data.Length);
            uint part1 = Adler32.Compute(data, 0, 4000);
            uint part2 = Adler32.Update(part1, data, 4000, data.Length - 4000);
            Assert.Equal(whole, part2);
        }
    }
}
