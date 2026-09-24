using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Formats.Gif;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// 校验 GIF 解码的调色板展开路径：整幅不透明覆盖的专用展开路径（含 AVX2 gather）
    /// 必须与通用路径（逐像素带边界判断）以及被跳过的“画布背景填充”等价。
    /// 用例覆盖像素数 1/2/3/5/8/13/40（跨越向量批次与标量尾部）。
    /// </summary>
    public class GifDecodePathTests
    {
        /// <summary>
        /// 手工构造 GIF：每像素用 clear + index 两个码字表达（码长恒为 3 位），
        /// 索引在 0..3 之间变化，配合 4 色调色板。
        /// </summary>
        private static byte[] BuildGif(int canvasW, int canvasH, int frameW, int frameH, bool interlace, int transparentIndex)
        {
            var ms = new MemoryStream();
            void A(string s) { foreach (char c in s) ms.WriteByte((byte)c); }
            void B(params byte[] v) => ms.Write(v);

            A("GIF89a");
            B((byte)canvasW, (byte)(canvasW >> 8), (byte)canvasH, (byte)(canvasH >> 8));
            B(0x80 | (0x07 << 4) | 0x01); // 有全局色表，色表 4 色
            B(3, 0);                       // 背景索引 3（黄色），与帧内容区分
            B(255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0);

            if (transparentIndex >= 0)
            {
                B(0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, (byte)transparentIndex, 0x00);
            }

            B(0x2C, 0, 0, 0, 0, (byte)frameW, (byte)(frameW >> 8), (byte)frameH, (byte)(frameH >> 8));
            B((byte)(interlace ? 0x40 : 0x00));
            B(2); // LZW 最小码长

            int accum = 0, bits = 0;
            var payload = new List<byte>();
            void Code(int c)
            {
                accum |= c << bits;
                bits += 3;
                while (bits >= 8) { payload.Add((byte)(accum & 0xFF)); accum >>= 8; bits -= 8; }
            }
            // 索引只取 0..2，透明索引 3 不出现在数据流里；按“逻辑像素”给出内容，
            // 交错时按 pass 顺序写入，保证两种排布解出的画布相同。
            void Emit(int x, int y) { Code(4); Code((x + y) % 3); }

            if (interlace)
            {
                int[] start = { 0, 4, 2, 1 };
                int[] inc = { 8, 8, 4, 2 };
                for (int pass = 0; pass < 4; pass++)
                {
                    for (int y = start[pass]; y < frameH; y += inc[pass])
                    {
                        for (int x = 0; x < frameW; x++) Emit(x, y);
                    }
                }
            }
            else
            {
                for (int y = 0; y < frameH; y++)
                {
                    for (int x = 0; x < frameW; x++) Emit(x, y);
                }
            }

            Code(5); // end
            while (bits > 0) { payload.Add((byte)(accum & 0xFF)); accum >>= 8; bits -= 8; }

            for (int i = 0; i < payload.Count; i += 255)
            {
                int len = Math.Min(255, payload.Count - i);
                ms.WriteByte((byte)len);
                ms.Write(payload.GetRange(i, len).ToArray(), 0, len);
            }
            ms.WriteByte(0);
            ms.WriteByte(0x3B);
            return ms.ToArray();
        }

        public static IEnumerable<object[]> Sizes()
        {
            foreach (int size in new[] { 1, 2, 3, 5, 8, 13, 40 })
            {
                yield return new object[] { size };
            }
        }

        [Theory]
        [MemberData(nameof(Sizes))]
        public void FastExpandPath_MatchesGeneralPath(int size)
        {
            int transparentIndex = 3; // 流里只出现 0..2，透明索引不会被命中
            byte[] fast = BuildGif(size, size, size, size, interlace: false, transparentIndex: -1);
            byte[] general = BuildGif(size, size, size, size, interlace: false, transparentIndex);

            byte[] fastRgb = new GifDecoder().DecodeRgb24(new MemoryStream(fast)).Buffer;
            byte[] generalRgb = new GifDecoder().DecodeRgb24(new MemoryStream(general)).Buffer;
            Assert.Equal(generalRgb, fastRgb);

            byte[] fastRgba = new GifDecoder().DecodeRgba32(new MemoryStream(fast)).Buffer;
            byte[] generalRgba = new GifDecoder().DecodeRgba32(new MemoryStream(general)).Buffer;
            Assert.Equal(generalRgba, fastRgba);
        }

        [Theory]
        [MemberData(nameof(Sizes))]
        public void FastExpandPath_MatchesInterlacedPath(int size)
        {
            byte[] fast = BuildGif(size, size, size, size, interlace: false, transparentIndex: -1);
            byte[] interlaced = BuildGif(size, size, size, size, interlace: true, transparentIndex: -1);

            byte[] fastRgb = new GifDecoder().DecodeRgb24(new MemoryStream(fast)).Buffer;
            byte[] interlacedRgb = new GifDecoder().DecodeRgb24(new MemoryStream(interlaced)).Buffer;
            Assert.Equal(interlacedRgb, fastRgb);
        }

        [Fact]
        public void PartialFrame_KeepsBackgroundColor()
        {
            // 帧只覆盖左上角，其余像素必须是背景色（黄色），
            // 用于守住“整幅覆盖时才可跳过背景填充”这一前提。
            byte[] gif = BuildGif(9, 7, 4, 3, interlace: false, transparentIndex: -1);
            byte[] rgb = new GifDecoder().DecodeRgb24(new MemoryStream(gif)).Buffer;

            int stride = 9 * 3;
            for (int y = 0; y < 7; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    bool insideFrame = x < 4 && y < 3;
                    if (insideFrame) continue;
                    int o = y * stride + x * 3;
                    Assert.Equal(255, rgb[o]);
                    Assert.Equal(255, rgb[o + 1]);
                    Assert.Equal(0, rgb[o + 2]);
                }
            }
        }
    }
}
