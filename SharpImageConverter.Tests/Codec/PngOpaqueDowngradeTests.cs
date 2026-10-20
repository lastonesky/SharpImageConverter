using System;
using System.IO;
using SharpImageConverter.Formats.Png;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// 全不透明的 RGBA 写出应降级为 RGB（颜色类型 2）：像素必须完全等价，
    /// 只是不再写每像素一个恒为 255 的 alpha 字节。
    /// </summary>
    public class PngOpaqueDowngradeTests
    {
        /// <summary>IHDR 数据从第 16 字节开始，颜色类型位于其第 9 字节。</summary>
        private static int ColorType(byte[] png) => png[25];

        private static byte[] Encode(int width, int height, byte[] rgba)
        {
            using var ms = new MemoryStream();
            PngWriter.WriteRgba(ms, width, height, rgba);
            return ms.ToArray();
        }

        private static byte[] RandomOpaque(int width, int height, int seed)
        {
            var rgba = new byte[width * height * 4];
            var rng = new Random(seed);
            for (int i = 0; i < width * height; i++)
            {
                rgba[i * 4] = (byte)rng.Next(256);
                rgba[i * 4 + 1] = (byte)rng.Next(256);
                rgba[i * 4 + 2] = (byte)rng.Next(256);
                rgba[i * 4 + 3] = 255;
            }
            return rgba;
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 7)]
        [InlineData(37, 23)]
        [InlineData(64, 4)]
        public void OpaqueImage_IsWrittenAsRgb_AndPixelsSurvive(int width, int height)
        {
            byte[] rgba = RandomOpaque(width, height, 20261017 + width * 31 + height);

            byte[] png = Encode(width, height, rgba);
            Assert.Equal(2, ColorType(png));

            var decoder = new PngDecoder();
            byte[] rgb = decoder.DecodeToRGB(new MemoryStream(png));
            Assert.Equal(width * height * 3, rgb.Length);
            for (int i = 0, j = 0; i < width * height; i++, j += 3)
            {
                Assert.Equal(rgba[i * 4], rgb[j]);
                Assert.Equal(rgba[i * 4 + 1], rgb[j + 1]);
                Assert.Equal(rgba[i * 4 + 2], rgb[j + 2]);
            }

            byte[] decodedRgba = decoder.DecodeToRGBA(new MemoryStream(png));
            for (int i = 0; i < width * height; i++)
            {
                Assert.Equal(255, decodedRgba[i * 4 + 3]);
            }
        }

        [Fact]
        public void SingleTransparentPixel_KeepsRgbaColorType()
        {
            const int width = 37, height = 23;
            byte[] rgba = RandomOpaque(width, height, 7);
            // 只改最后一个像素：既验证不是「全 255 才降级」，也验证尾部（向量尾）不漏判。
            rgba[(width * height - 1) * 4 + 3] = 254;

            byte[] png = Encode(width, height, rgba);
            Assert.Equal(6, ColorType(png));

            var decoder = new PngDecoder();
            byte[] decoded = decoder.DecodeToRGBA(new MemoryStream(png));
            for (int i = 0; i < rgba.Length; i++)
            {
                Assert.Equal(rgba[i], decoded[i]);
            }
        }

        [Fact]
        public void FirstTransparentPixel_KeepsRgbaColorType()
        {
            const int width = 37, height = 23;
            byte[] rgba = RandomOpaque(width, height, 11);
            rgba[3] = 0;

            byte[] png = Encode(width, height, rgba);
            Assert.Equal(6, ColorType(png));
        }

        [Fact]
        public void ShortBuffer_StillEncodes()
        {
            // 小于一个向量的宽度（1 像素）走标量尾循环，确保边界不漏判。
            byte[] rgba = [10, 20, 30, 255];
            byte[] png = Encode(1, 1, rgba);
            Assert.Equal(2, ColorType(png));
        }
    }
}
