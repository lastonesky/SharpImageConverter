using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpImageConverter.Compression;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// 跨格式优化（--optimize --to X）与不支持的输入。
    /// </summary>
    public partial class OptimizeTests
    {
        [Fact]
        public void Optimize_UnsupportedFile_KeepsOriginal()
        {
            string input = NewTemp(".bin");
            string output = NewTemp(".bin");
            try
            {
                File.WriteAllBytes(input, [1, 2, 3, 4, 5]);
                var result = ImageOptimizer.Optimize(input, output);
                Assert.True(result.KeptOriginal);
                Assert.NotNull(result.Reason);
                Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
            }
            finally
            {
                foreach (var f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        [Fact]
        public void Optimize_TransparentPngToJpeg_CompositesOnMatteInsteadOfBlack()
        {
            int w = 64, h = 64;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool center = x >= 24 && x < 40 && y >= 24 && y < 40;
                    rgba[o] = 220;
                    rgba[o + 1] = center ? (byte)30 : (byte)0;
                    rgba[o + 2] = center ? (byte)30 : (byte)0;
                    rgba[o + 3] = center ? (byte)255 : (byte)0;
                }
            }
            string input = NewTemp(".png");
            string jpeg = NewTemp(".jpg");
            string bmp = NewTemp(".bmp");
            try
            {
                PngWriter.WriteRgba(input, w, h, rgba);

                var jpegResult = ImageOptimizer.Optimize(input, jpeg);
                Assert.Contains("合成", jpegResult.Method);
                var decoded = Configuration.Default.LoadRgb24(jpeg);
                int corner = 0; // 左上角原本 alpha=0，应接近白色背景
                Assert.True(decoded.Buffer[corner] > 230, $"R={decoded.Buffer[corner]}");
                Assert.True(decoded.Buffer[corner + 1] > 230, $"G={decoded.Buffer[corner + 1]}");
                Assert.True(decoded.Buffer[corner + 2] > 230, $"B={decoded.Buffer[corner + 2]}");

                var bmpResult = ImageOptimizer.Optimize(input, bmp);
                Assert.Contains("合成", bmpResult.Method);
                var bmpDecoded = Configuration.Default.LoadRgb24(bmp);
                Assert.Equal(255, bmpDecoded.Buffer[corner]);
                Assert.Equal(255, bmpDecoded.Buffer[corner + 1]);
                Assert.Equal(255, bmpDecoded.Buffer[corner + 2]);
            }
            finally
            {
                Cleanup(input, jpeg, bmp);
            }
        }

        [Fact]
        public void Optimize_ConvertJpegToPng_WritesRealPngNotOriginalCopy()
        {
            // 源 JPEG 已经很小，转成 PNG 后更大；关键是要「如实写出 PNG」，
            // 而不是走同格式路径的「无收益就复制原图」把 JPEG 字节塞进 .png 文件。
            int w = 200, h = 150;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 9);
            string input = NewTemp(".jpg");
            string output = NewTemp(".png");
            try
            {
                JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), input, new JpegEncoderOptions(90, true, false, false));
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.Converted);
                Assert.False(result.KeptOriginal);
                Assert.True(result.OptimizedSize > before, "PNG 无损容器通常比高质量 JPEG 更大");

                // PNG 魔数：89 50 4E 47
                var head = new byte[8];
                using (var fs = File.OpenRead(output)) fs.ReadExactly(head);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);

                var decoded = Configuration.Default.LoadRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_ConvertPngToBmp_IsLossless()
        {
            int w = 64, h = 48;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 12);
            string input = NewTemp(".png");
            string output = NewTemp(".bmp");
            try
            {
                PngWriter.Write(input, w, h, rgb);

                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.Converted);
                Assert.True(result.Quality.IsLossless, result.Quality.ToString());
                var decoded = Configuration.Default.LoadRgb24(output);
                Assert.Equal(rgb, decoded.Buffer);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_ConvertPngToGif_ProducesValidGif()
        {
            int w = 120, h = 90;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 21);
            string input = NewTemp(".png");
            string output = NewTemp(".gif");
            try
            {
                PngWriter.Write(input, w, h, rgb);

                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.Converted);
                Assert.True(File.Exists(output));
                var decoded = new GifDecoder().DecodeRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_SameFormatTarget_IsNotMarkedConverted()
        {
            // 输出扩展名与源格式一致时必须继续走同格式压缩，不能被误判成跨格式转换
            int w = 120, h = 90;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 33);
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                PngWriter.Write(input, w, h, rgb);

                var result = ImageOptimizer.Optimize(input, output);

                Assert.False(result.Converted);
                Assert.Equal(output, result.OutputPath);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_ConvertPngToGif_NotLargerThanPlainConversion()
        {
            // 回归（2026-10-17）：--optimize 走 Floyd–Steinberg 抖动时索引流熵高、LZW 命中率暴跌，
            // 全范围渐变图上曾出现 optimize 产物比不加 optimize 的普通转换大 1~4 倍。
            // 修复后抖动候选受「同色数无抖动体积 × 2」预算约束，且单帧有常规编码基线兜底。
            int w = 600, h = 450;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 3;
                    rgb[i] = (byte)(x * 255 / (w - 1));
                    rgb[i + 1] = (byte)(y * 255 / (h - 1));
                    rgb[i + 2] = 128;
                }
            }

            string input = NewTemp(".png");
            string output = NewTemp(".gif");
            string plain = NewTemp(".gif");
            try
            {
                PngWriter.Write(input, w, h, rgb);

                var result = ImageOptimizer.Optimize(input, output);

                // 基线：与不加 --optimize 的普通转换完全同参（八叉树 + Bayer8、256 色）
                new GifEncoderAdapter().EncodeRgb24(plain, Configuration.Default.LoadRgb24(input));
                long plainSize = new FileInfo(plain).Length;

                Assert.True(File.Exists(output));
                Assert.True(result.OptimizedSize <= plainSize, $"{result.OptimizedSize} > 普通转换 {plainSize}");
                var decoded = new GifDecoder().DecodeRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
            }
            finally
            {
                Cleanup(input, output, plain);
            }
        }
    }
}
