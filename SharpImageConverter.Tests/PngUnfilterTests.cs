using System;
using SharpImageConverter.Formats.Png;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// PNG 反滤波的 SIMD / 标量与参考实现对拍。
    /// Sub 滤波已改为"块内前缀和 + 跨块进位"的向量实现，
    /// 这里用最朴素的标量定义做 golden，覆盖 bpp 1..8 与各种长度（含小于 16 的短行、非 16 倍数长度）。
    /// </summary>
    public class PngUnfilterTests
    {
        /// <summary>纯粹的规范定义实现，不依赖库内任何代码。</summary>
        private static void ReferenceSub(ReadOnlySpan<byte> src, Span<byte> dst, int bpp)
        {
            for (int i = 0; i < src.Length; i++)
            {
                int left = i >= bpp ? dst[i - bpp] : 0;
                dst[i] = (byte)(src[i] + left); // mod 256 回绕
            }
        }

        [Fact]
        public void UnfilterSub_MatchesReference_ForAllBppAndLengths()
        {
            var rng = new Random(20261002);

            foreach (int bpp in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
            {
                // 覆盖：小于 16、等于 16、16 的边界附近、非 16 倍数、较大长度
                foreach (int len in new[] { 1, 2, 3, bpp, bpp + 1, 15, 16, 17, 31, 32, 33, 47, 48, 49, 100, 255, 256, 1000 })
                {
                    if (len <= 0) continue;

                    var src = new byte[len];
                    rng.NextBytes(src);

                    var actual = new byte[len];
                    var expected = new byte[len];

                    PngDecoder.UnfilterSub(src, actual, bpp);
                    ReferenceSub(src, expected, bpp);

                    for (int i = 0; i < len; i++)
                    {
                        Assert.True(actual[i] == expected[i],
                            $"bpp={bpp} len={len} i={i}: expected {expected[i]}, actual {actual[i]}");
                    }
                }
            }
        }

        [Fact]
        public void UnfilterSub_UsesModulo256_NotSaturate()
        {
            // 200 + 100 = 300 -> 44（mod 256）；若误用饱和加会得到 255
            var src = new byte[] { 200, 100, 0, 0 };
            var dst = new byte[4];
            PngDecoder.UnfilterSub(src, dst, 1);
            Assert.Equal(new byte[] { 200, 44, 44, 44 }, dst);
        }

        [Fact]
        public void UnfilterSub_ChainCarrySurvivesBlockBoundary()
        {
            // 32 字节 = 2 个 16 字节块；验证进位跨块正确传播（而非每块从头开始）
            var src = new byte[32];
            for (int i = 0; i < src.Length; i++) src[i] = 1;

            var dst = new byte[32];
            PngDecoder.UnfilterSub(src, dst, 4);

            // bpp=4：每个位置 = src[i](=1) + dst[i-4]，即每 4 字节一组累加
            var expected = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                expected[i] = (byte)(1 + (i >= 4 ? expected[i - 4] : 0));
            }
            Assert.Equal(expected, dst);
        }
    }
}
