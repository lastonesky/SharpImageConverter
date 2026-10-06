using System;
using System.IO;
using System.IO.Compression;
using SharpImageConverter.Compression;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    public class CompressionTests
    {
        private static string NewTemp(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);

        /// <summary>
        /// 自适应滤波是逐行选型的，必须保证解码端能还原出完全一致的像素。
        /// </summary>
        [Theory]
        [InlineData(PngFilterMode.Adaptive)]
        [InlineData(PngFilterMode.None)]
        [InlineData(PngFilterMode.Up)]
        public void PngAdaptiveFilter_RoundTrip_PixelsPreserved(PngFilterMode mode)
        {
            // 混合四类行，确保 None/Sub/Up/Average/Paeth 都会在某些行胜出：
            // 平坦行 → Sub/Up/Paeth 全零；水平渐变 → Paeth；垂直渐变 → Average/Up；随机行 → Sub
            int w = 37, h = 24;
            var rgb = new byte[w * h * 3];
            var rng = new Random(7);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    switch (y % 4)
                    {
                        case 0:
                            rgb[o] = rgb[o + 1] = rgb[o + 2] = 100;
                            break;
                        case 1:
                            rgb[o] = (byte)(x * 6);
                            rgb[o + 1] = (byte)(255 - x * 6);
                            rgb[o + 2] = (byte)((x * 3 + y * 2) & 0xFF);
                            break;
                        case 2:
                            rgb[o] = rgb[o + 1] = rgb[o + 2] = (byte)(y * 9);
                            break;
                        default:
                            rgb[o] = (byte)rng.Next(256);
                            rgb[o + 1] = (byte)rng.Next(256);
                            rgb[o + 2] = (byte)rng.Next(256);
                            break;
                    }
                }
            }

            string path = NewTemp(".png");
            try
            {
                using (var fs = File.Create(path))
                {
                    PngWriter.Write(fs, w, h, rgb, null, mode, CompressionLevel.Optimal);
                }
                var decoded = Configuration.Default.LoadRgb24(path);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
                Assert.Equal(rgb, decoded.Buffer);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void PngPaletteWriter_RoundTrip_PixelsPreserved()
        {
            int w = 16, h = 9;
            var indices = new byte[w * h];
            for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i % 4);
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
            byte[] alpha = [255, 255, 128, 0];

            string path = NewTemp(".png");
            try
            {
                PngWriter.WritePalette(path, w, h, indices, palette, alpha);
                var decoded = Configuration.Default.LoadRgba32(path);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
                for (int p = 0; p < indices.Length; p++)
                {
                    int idx = indices[p];
                    Assert.Equal(palette[idx * 3], decoded.Buffer[p * 4]);
                    Assert.Equal(palette[idx * 3 + 1], decoded.Buffer[p * 4 + 1]);
                    Assert.Equal(palette[idx * 3 + 2], decoded.Buffer[p * 4 + 2]);
                    Assert.Equal(alpha[idx], decoded.Buffer[p * 4 + 3]);
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Quantizer_ExactPalette_IsLossless()
        {
            int w = 8, h = 8;
            var rgba = new byte[w * h * 4];
            for (int p = 0; p < w * h; p++)
            {
                int c = p % 5;
                rgba[p * 4] = (byte)(c * 50);
                rgba[p * 4 + 1] = (byte)(255 - c * 40);
                rgba[p * 4 + 2] = (byte)(c * 17);
                rgba[p * 4 + 3] = 255;
            }

            var result = PaletteQuantizer.Quantize(rgba, w, h, new QuantizeOptions { MaxColors = 16 });
            Assert.Equal(5, result.ColorCount);
            Assert.Null(result.PaletteAlpha);

            var restored = new byte[rgba.Length];
            for (int p = 0; p < w * h; p++)
            {
                int i = result.Indices[p] * 3;
                restored[p * 4] = result.PaletteRgb[i];
                restored[p * 4 + 1] = result.PaletteRgb[i + 1];
                restored[p * 4 + 2] = result.PaletteRgb[i + 2];
                restored[p * 4 + 3] = 255;
            }
            Assert.Equal(rgba, restored);
        }

        [Fact]
        public void Quantizer_MedianCut_HonorsColorBudget()
        {
            int w = 32, h = 32;
            var rgba = new byte[w * h * 4];
            var rng = new Random(11);
            for (int p = 0; p < w * h; p++)
            {
                rgba[p * 4] = (byte)rng.Next(256);
                rgba[p * 4 + 1] = (byte)rng.Next(256);
                rgba[p * 4 + 2] = (byte)rng.Next(256);
                rgba[p * 4 + 3] = 255;
            }

            var result = PaletteQuantizer.Quantize(rgba, w, h, new QuantizeOptions { MaxColors = 64 });
            Assert.True(result.ColorCount <= 64, $"实际颜色数 {result.ColorCount}");
            for (int p = 0; p < w * h; p++)
            {
                Assert.True(result.Indices[p] < result.ColorCount);
            }
        }

        [Fact]
        public void Quantizer_TransparentReservation_MapsToIndexZero()
        {
            int w = 4, h = 4;
            var rgba = new byte[w * h * 4];
            for (int p = 0; p < w * h; p++)
            {
                rgba[p * 4] = (byte)(p * 7);
                rgba[p * 4 + 1] = 30;
                rgba[p * 4 + 2] = 60;
                // 前半不透明，后半透明
                rgba[p * 4 + 3] = p < 8 ? (byte)255 : (byte)0;
            }

            var result = PaletteQuantizer.Quantize(rgba, w, h, new QuantizeOptions
            {
                MaxColors = 32,
                TransparentAlphaThreshold = 128,
            });

            Assert.NotNull(result.PaletteAlpha);
            Assert.Equal(0, result.PaletteAlpha![0]);
            for (int p = 0; p < 8; p++) Assert.NotEqual(0, result.Indices[p]);
            for (int p = 8; p < w * h; p++) Assert.Equal(0, result.Indices[p]);
        }

        [Fact]
        public void QualityMetrics_IdenticalImages_AreLossless()
        {
            var a = new byte[64];
            for (int i = 0; i < a.Length; i++) a[i] = (byte)i;
            var q = QualityMetrics.Compare(a, a, 4, 4, 4);
            Assert.True(q.IsLossless);
            Assert.Equal(ImageQuality.MaxPsnr, q.PerceptualPsnr);
        }

        [Fact]
        public void QualityMetrics_DitherNoise_HasHigherPerceptualThanRawPsnr()
        {
            int w = 16, h = 16;
            var reference = new byte[w * h * 3];
            var test = new byte[w * h * 3];
            for (int p = 0; p < w * h; p++)
            {
                byte v = 128;
                reference[p * 3] = v;
                reference[p * 3 + 1] = v;
                reference[p * 3 + 2] = v;
                // 棋盘式 ±40 抖动：块平均后完全抵消
                byte d = (p % 2 == 0) ? (byte)168 : (byte)88;
                test[p * 3] = d;
                test[p * 3 + 1] = d;
                test[p * 3 + 2] = d;
            }

            var q = QualityMetrics.Compare(reference, test, w, h, 3);
            Assert.True(q.PerceptualPsnr > q.Psnr + 10, $"感知 {q.PerceptualPsnr} vs 原始 {q.Psnr}");
        }

        [Fact]
        public void Optimize_FewColorPng_IsLosslessAndSmaller()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                // 先用真彩色 PNG 保存一块 6 色图像，再交给优化器
                int w = 120, h = 90;
                var rgb = new byte[w * h * 3];
                byte[][] palette =
                [
                    [255, 255, 255], [33, 33, 33], [0, 122, 204],
                    [244, 67, 54], [76, 175, 80], [255, 193, 7],
                ];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var c = palette[((x / 30) + (y / 30)) % palette.Length];
                        int o = (y * w + x) * 3;
                        rgb[o] = c[0]; rgb[o + 1] = c[1]; rgb[o + 2] = c[2];
                    }
                }
                PngWriter.Write(input, w, h, rgb);
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);
                long after = new FileInfo(output).Length;

                Assert.True(after < before, $"{after} 应小于 {before}");
                Assert.True(result.Quality.IsLossless, result.Quality.ToString());

                var decoded = Configuration.Default.LoadRgb24(output);
                Assert.Equal(rgb, decoded.Buffer);
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
        public void Optimize_PhotoLikePng_StaysWithinQualityBar()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                int w = 200, h = 150;
                var rgb = new byte[w * h * 3];
                var rng = new Random(2024);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 3;
                        rgb[o] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.05) + rng.Next(-12, 12), 0, 255);
                        rgb[o + 1] = (byte)Math.Clamp(120 + 80 * Math.Cos(y * 0.06) + rng.Next(-12, 12), 0, 255);
                        rgb[o + 2] = (byte)Math.Clamp(140 + 70 * Math.Sin((x + y) * 0.03) + rng.Next(-12, 12), 0, 255);
                    }
                }
                PngWriter.Write(input, w, h, rgb);
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);

                if (!result.KeptOriginal)
                {
                    Assert.True(result.OptimizedSize < before, "优化产物应当更小");
                    Assert.True(OptimizeOptions.Balanced.Accept(result.Quality), $"画质未达标: {result.Quality}");
                }
                Assert.True(File.Exists(output));
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
        public void Optimize_StaticGif_KeepsImageValid()
        {
            int w = 64, h = 48;
            var rgb = new byte[w * h * 3];
            var rng = new Random(31);
            rng.NextBytes(rgb);

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input)) new GifEncoder().Encode(new ImageFrame(w, h, rgb), fs);
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);
                Assert.True(File.Exists(output));
                if (!result.KeptOriginal)
                {
                    Assert.True(result.OptimizedSize <= before);
                }
                var decoded = new GifDecoder().DecodeRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
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
        public void Optimize_AnimatedGif_KeepsFrameCount()
        {
            int w = 64, h = 48;
            var frames = new List<ImageFrame>();
            for (int f = 0; f < 3; f++)
            {
                var buf = new byte[w * h * 3];
                for (int p = 0; p < w * h; p++)
                {
                    buf[p * 3] = (byte)((p * 3 + f * 40) % 256);
                    buf[p * 3 + 1] = (byte)((p * 5 + f * 20) % 256);
                    buf[p * 3 + 2] = (byte)((p * 7) % 256);
                }
                frames.Add(new ImageFrame(w, h, buf));
            }

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeAnimation(frames, [100, 100, 100], 0, fs);
                }
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);

                var animation = new GifDecoder().DecodeAnimationRgb24(output);
                Assert.Equal(3, animation.Frames.Count);
                Assert.Equal(w, animation.Frames[0].Width);
                if (!result.KeptOriginal)
                {
                    Assert.True(result.OptimizedSize <= before, $"{result.OptimizedSize} vs {before}");
                }
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
        public void Optimize_Jpeg_ReducesSizeAndKeepsQuality()
        {
            int w = 200, h = 150;
            var rgb = new byte[w * h * 3];
            var rng = new Random(77);
            // 通道相关的平滑图像（更接近真实照片的色度分布）
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    int noise = rng.Next(-8, 8);
                    rgb[o] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.05) + noise, 0, 255);
                    rgb[o + 1] = (byte)Math.Clamp(120 + 80 * Math.Cos(y * 0.06) + noise, 0, 255);
                    rgb[o + 2] = (byte)Math.Clamp(140 + 70 * Math.Sin((x + y) * 0.03) + noise, 0, 255);
                }
            }

            string input = NewTemp(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                var image = new Image<Rgb24>(w, h, rgb);
                JpegEncoder.Encode(image, input, new JpegEncoderOptions(100, false, false, false));
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);
                if (!result.KeptOriginal)
                {
                    Assert.True(result.OptimizedSize < before, $"{result.OptimizedSize} vs {before}");
                    Assert.True(OptimizeOptions.Balanced.Accept(result.Quality), result.Quality.ToString());
                }
                var decoded = Configuration.Default.LoadRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
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
        public void OptimizeOptions_QualityMapping_IsMonotonic()
        {
            double conservative = OptimizeOptions.Conservative.MinPerceptualPsnr;
            double balanced = OptimizeOptions.Balanced.MinPerceptualPsnr;
            double aggressive = OptimizeOptions.Aggressive.MinPerceptualPsnr;
            Assert.True(conservative > balanced);
            Assert.True(balanced > aggressive);
        }
    }
}
