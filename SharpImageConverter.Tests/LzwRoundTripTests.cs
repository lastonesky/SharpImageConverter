using System;
using System.IO;
using SharpImageConverter.Formats.Gif;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// LZW 编解码往返测试。编码器用开放寻址哈希表（键打包为 fcode+1，槽位 0 表示空），
    /// 解码器用串长表把字符直写输出缓冲，两者都必须在不同数据分布下保持无损：
    /// 长游程会触发超长串与 KwKwK 分支，随机数据会频繁触发字典填满与清码。
    /// </summary>
    public class LzwRoundTripTests
    {
        /// <summary>编码 → 解码，返回解码结果。</summary>
        private static byte[] RoundTrip(byte[] indices, int colorDepth)
        {
            using var ms = new MemoryStream();
            using (var enc = new LzwEncoder(ms))
            {
                enc.Encode(indices, indices.Length, 1, colorDepth);
            }

            ms.Position = 0;
            int minCodeSize = ms.ReadByte();
            Assert.True(minCodeSize >= 2, "LZW 最小码长应由编码器写出");

            var decoded = new byte[indices.Length];
            using (var dec = new LzwDecoder(ms))
            {
                dec.Decode(decoded, indices.Length, 1, minCodeSize);
            }
            return decoded;
        }

        private static void AssertRoundTrip(byte[] indices, int colorDepth)
        {
            byte[] decoded = RoundTrip(indices, colorDepth);
            Assert.Equal(indices, decoded);
        }

        [Fact]
        public void RoundTrip_SinglePixel()
        {
            AssertRoundTrip(new byte[] { 7 }, 8);
        }

        [Fact]
        public void RoundTrip_TwoPixels()
        {
            AssertRoundTrip(new byte[] { 0, 255 }, 8);
        }

        /// <summary>全同像素：串长单调增长，最容易把字典填满并触发清码。</summary>
        [Fact]
        public void RoundTrip_SingleColor()
        {
            var data = new byte[60000];
            AssertRoundTrip(data, 8);
        }

        /// <summary>长游程：产生很长的字典串，覆盖解码端的长链展开。</summary>
        [Fact]
        public void RoundTrip_LongRuns()
        {
            var data = new byte[80000];
            int v = 0;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)v;
                if (i % 97 == 96) v = (v + 37) & 0xFF;
            }
            AssertRoundTrip(data, 8);
        }

        /// <summary>高熵数据：几乎每次查表都未命中，字典增长最快。</summary>
        [Fact]
        public void RoundTrip_RandomNoise()
        {
            var rng = new Random(12345);
            var data = new byte[100000];
            rng.NextBytes(data);
            AssertRoundTrip(data, 8);
        }

        /// <summary>小调色板：验证最小码长为 2 时的位宽增长与清码边界。</summary>
        [Fact]
        public void RoundTrip_TwoBitPalette()
        {
            var rng = new Random(999);
            var data = new byte[50000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(rng.Next(4));
            AssertRoundTrip(data, 2);
        }

        /// <summary>四色调色板 + 游程混合，覆盖 KwKwK（码尚未进字典）分支。</summary>
        [Fact]
        public void RoundTrip_FourColorRuns()
        {
            var data = new byte[60000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i / 13) % 4);
            AssertRoundTrip(data, 4);
        }

        /// <summary>同一输入编码两次必须字节一致（哈希表不应引入不确定性）。</summary>
        [Fact]
        public void Encode_IsDeterministic()
        {
            var rng = new Random(777);
            var data = new byte[40000];
            rng.NextBytes(data);

            byte[] first = RoundTrip(data, 8);
            byte[] second = RoundTrip(data, 8);
            Assert.Equal(first, second);
        }
    }
}
