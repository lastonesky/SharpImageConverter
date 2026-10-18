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

namespace Jpeg2Bmp.Tests
{
    [Collection(SharpImageConverter.Tests.JpegSimdStateCollection.Name)]
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

        /// <summary>
        /// 关闭抖动时走的是「直方图桶 → 最近调色板色」的查表路径。
        /// 该路径曾用同一个下标 k 同时索引 _usedBins 与 _binArray，而 _binArray 已被
        /// 中位切分就地排序过一次，两者不再对应 —— 每个桶因此拿到别的桶的代表色，
        /// 整图错位（256 色下逐像素 PSNR 只有 ~2.6 dB、平均误差 ~158）。
        /// </summary>
        [Fact]
        public void Quantizer_WithoutDithering_StillMapsBinsToTheirOwnNearestColor()
        {
            int w = 96, h = 64;
            var rgba = new byte[w * h * 4];
            var rng = new Random(2026);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    rgba[o] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.05) + rng.Next(-6, 6), 0, 255);
                    rgba[o + 1] = (byte)Math.Clamp(120 + 80 * Math.Cos(y * 0.06) + rng.Next(-6, 6), 0, 255);
                    rgba[o + 2] = (byte)Math.Clamp(140 + 70 * Math.Sin((x + y) * 0.03) + rng.Next(-6, 6), 0, 255);
                    rgba[o + 3] = 255;
                }
            }

            var result = PaletteQuantizer.Quantize(rgba, w, h, new QuantizeOptions
            {
                MaxColors = 256,
                EnableDithering = false,
            });
            Assert.True(result.ColorCount > 2, $"颜色数 {result.ColorCount}");

            var restored = new byte[rgba.Length];
            for (int p = 0; p < w * h; p++)
            {
                int i = result.Indices[p] * 3;
                restored[p * 4] = result.PaletteRgb[i];
                restored[p * 4 + 1] = result.PaletteRgb[i + 1];
                restored[p * 4 + 2] = result.PaletteRgb[i + 2];
                restored[p * 4 + 3] = 255;
            }

            var q = QualityMetrics.Compare(rgba, restored, w, h, 4);
            Assert.True(q.MeanError < 12, $"平均误差 {q.MeanError:0.00}（疑似桶号错位）");
            Assert.True(q.Psnr > 25, $"逐像素 PSNR {q.Psnr:0.00} dB（疑似桶号错位）");
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

        // ───────────────────────── 跨格式「转换即最优」─────────────────────────

        /// <summary>
        /// 构造接近真实照片的 RGB 像素（平滑渐变 + 噪声），便于比较不同格式的压缩率。
        /// </summary>
        private static byte[] BuildPhotoLikeRgb(int w, int h, int seed)
        {
            var rgb = new byte[w * h * 3];
            var rng = new Random(seed);
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
            return rgb;
        }

        private static void Cleanup(params string[] paths)
        {
            foreach (var f in paths)
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }

        [Fact]
        public void Optimize_ConvertPngToWebp_ProducesSmallerValidWebp()
        {
            int w = 240, h = 160;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 5);
            string input = NewTemp(".png");
            string output = NewTemp(".webp");
            try
            {
                PngWriter.Write(input, w, h, rgb);
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.Converted);
                Assert.False(result.KeptOriginal);
                Assert.True(File.Exists(output));
                Assert.True(result.OptimizedSize < before, $"{result.OptimizedSize} 应小于 {before}");
                Assert.True(OptimizeOptions.Balanced.Accept(result.Quality), result.Quality.ToString());

                var decoded = new WebpDecoderAdapter().DecodeRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Theory]
        [InlineData(30)] // 锚点 40 已达标，答案在 (20,40]
        [InlineData(60)] // 锚点 40 不达标，答案在 (40,95]
        [InlineData(20)] // 下界本身达标
        [InlineData(95)] // 只有最高档达标
        public void SearchQualityAnchored_FindsLowestPassingQuality(int threshold)
        {
            int evaluations = 0;
            var (quality, score) = ImageOptimizer.SearchQualityAnchored(
                q =>
                {
                    evaluations++;
                    return new ImageQuality(99, q, 0);
                },
                20, 95, 40,
                s => s.PerceptualPsnr >= threshold,
                degree: 4);

            Assert.Equal(threshold, quality);
            Assert.Equal(threshold, score.PerceptualPsnr);
            Assert.True(evaluations <= 12, $"evaluations={evaluations}");
        }

        [Fact]
        public void WebpLossless_IsSmallerThanLossy_ForFlatGraphic()
        {
            int w = 256, h = 256;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = ((x / 8) + (y / 8)) % 8;
                    int o = (y * w + x) * 4;
                    rgba[o] = (byte)(c * 31);
                    rgba[o + 1] = (byte)(c * 17);
                    rgba[o + 2] = (byte)(c * 7);
                    rgba[o + 3] = 255;
                }
            }
            using var losslessStream = new MemoryStream();
            new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(75, WebpConcurrencyStrategy.Auto, 6, lossless: true) }
                .EncodeRgba32(losslessStream, new Image<Rgba32>(w, h, rgba));
            using var lossyStream = new MemoryStream();
            new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(95, WebpConcurrencyStrategy.Auto, 6) }
                .EncodeRgba32(lossyStream, new Image<Rgba32>(w, h, rgba));

            byte[] losslessBytes = losslessStream.ToArray();
            Assert.Contains("VP8L", Encoding.ASCII.GetString(losslessBytes));
            losslessStream.Position = 0;
            var decoded = new WebpDecoderAdapterRgba().DecodeRgba32(losslessStream).Buffer;
            Assert.Equal(rgba, decoded);
            Assert.True(losslessStream.Length < lossyStream.Length,
                $"lossless={losslessStream.Length}, lossy={lossyStream.Length}");
        }

        [Fact]
        public void Optimize_LowColorPngToWebp_CanChooseLosslessAndPreservePixels()
        {
            int w = 256, h = 256;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = ((x / 8) + (y / 8)) % 8;
                    int o = (y * w + x) * 3;
                    rgb[o] = (byte)(c * 31);
                    rgb[o + 1] = (byte)(c * 17);
                    rgb[o + 2] = (byte)(c * 7);
                }
            }
            string input = NewTemp(".png");
            string output = NewTemp(".webp");
            try
            {
                PngWriter.Write(input, w, h, rgb);
                var result = ImageOptimizer.Optimize(input, output);

                Assert.Contains("无损", result.Method);
                Assert.Equal(rgb, Configuration.Default.LoadRgb24(output).Buffer);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Theory]
        [InlineData(74.9f, 4, 4)]
        [InlineData(75f, 4, 3)]
        [InlineData(90f, 4, 3)]
        [InlineData(90f, 3, 3)]
        [InlineData(90f, 5, 5)]
        public void WebpEncoderOptions_Method4_IsDowngradedAtHighQuality(float quality, int requested, int expected)
        {
            var options = new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto, requested);
            Assert.Equal(expected, options.Method);
        }

        [Theory]
        [InlineData(JpegChromaSubsampling.Yuv444)]
        [InlineData(JpegChromaSubsampling.Yuv422)]
        [InlineData(JpegChromaSubsampling.Yuv420)]
        public void JpegEncoder_AllSubsamplings_RoundTrip(JpegChromaSubsampling subsampling)
        {
            int w = 96, h = 64;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 61);
            using var ms = new MemoryStream();
            JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), ms,
                new JpegEncoderOptions(85, subsampling, keepMetadata: false, enableDiagnostics: false));
            byte[] jpeg = ms.ToArray();

            var decoded = JpegDecoder.Decode(jpeg).ToRgb24();
            Assert.Equal(w * h * 3, decoded.Length);
            var q = QualityMetrics.Compare(rgb, decoded, w, h, 3);
            Assert.True(q.Psnr > 30, q.ToString());
        }

        [Fact]
        public void JpegEncoder_422_HandlesOddDimensions()
        {
            int w = 97, h = 65;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 63);
            using var ms = new MemoryStream();
            JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), ms,
                new JpegEncoderOptions(85, JpegChromaSubsampling.Yuv422, keepMetadata: false, enableDiagnostics: false));
            var decoded = JpegDecoder.Decode(ms.ToArray()).ToRgb24();
            var q = QualityMetrics.Compare(rgb, decoded, w, h, 3);
            Assert.True(q.Psnr > 25, q.ToString());
        }

        [Fact]
        public void Optimize_JpegSubsamplingComparison_IsNeverLargerThan420Only()
        {
            int w = 192, h = 192;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    bool stripe = (x / 2) % 2 == 0;
                    rgb[o] = stripe ? (byte)240 : (byte)20;
                    rgb[o + 1] = (byte)((x + y) % 64);
                    rgb[o + 2] = stripe ? (byte)20 : (byte)240;
                }
            }
            string input = NewTemp(".jpg");
            string compare = NewTemp(".jpg");
            string only420 = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(input))
                {
                    JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), fs,
                        new JpegEncoderOptions(95, subsample420: true, keepMetadata: false, enableDiagnostics: false));
                }
                var withCompare = ImageOptimizer.Optimize(input, compare, new OptimizeOptions { MinLossyGainRatio = 0 });
                var without = ImageOptimizer.Optimize(input, only420, new OptimizeOptions
                {
                    MinLossyGainRatio = 0,
                    JpegCompareSubsampling = false,
                });

                Assert.True(withCompare.OptimizedSize <= without.OptimizedSize,
                    $"{withCompare.OptimizedSize} vs {without.OptimizedSize}");
                Assert.Equal(w, Configuration.Default.LoadRgb24(compare).Width);
            }
            finally
            {
                Cleanup(input, compare, only420);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void JpegProgressiveTranscoder_RoundTripsPixels(bool subsample420)
        {
            int w = 320, h = 240;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 51);
            using var ms = new MemoryStream();
            JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), ms,
                new JpegEncoderOptions(80, subsample420, keepMetadata: false, enableDiagnostics: false));
            byte[] baseline = ms.ToArray();

            byte[]? progressive = JpegProgressiveTranscoder.TryTranscode(baseline);
            Assert.NotNull(progressive);
            // SOF2
            Assert.True(progressive!.AsSpan(2).IndexOf(new byte[] { 0xFF, 0xC2 }) >= 0);
            Assert.Equal(JpegDecoder.Decode(baseline).ToRgb24(),
                JpegDecoder.Decode(progressive).ToRgb24());
        }

        [Fact]
        public void Optimize_JpegBaseline_UsesLosslessHuffmanReorder()
        {
            int w = 320, h = 240;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 41);
            string input = NewTemp(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                WriteJpegWithMetadata(input, w, h, rgb, orientation: 1);
                var before = Configuration.Default.LoadRgb24(input).Buffer;
                long beforeSize = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { MinLossyGainRatio = 0.99 });

                Assert.Contains("Huffman", result.Method);
                Assert.True(result.OptimizedSize < beforeSize);
                Assert.Equal(before, Configuration.Default.LoadRgb24(output).Buffer);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_JpegWithExif_StripsMetadataLosslessly()
        {
            int w = 200, h = 150;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 31);
            string input = NewTemp(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                WriteJpegWithMetadata(input, w, h, rgb, orientation: 1);
                var before = Configuration.Default.LoadRgb24(input).Buffer;
                long beforeSize = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { MinLossyGainRatio = 0.99 });

                Assert.Contains("无损重封装", result.Method);
                Assert.True(result.OptimizedSize < beforeSize);
                Assert.False(ContainsMarker(File.ReadAllBytes(output), 0xE1));
                Assert.False(ContainsMarker(File.ReadAllBytes(output), 0xFE));
                Assert.Equal(before, Configuration.Default.LoadRgb24(output).Buffer);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_JpegWithNonDefaultOrientation_KeepsExif()
        {
            int w = 160, h = 120;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 33);
            string input = NewTemp(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                WriteJpegWithMetadata(input, w, h, rgb, orientation: 6);
                var before = Configuration.Default.LoadRgb24(input).Buffer;

                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { MinLossyGainRatio = 0.99 });

                Assert.Contains("无损重封装", result.Method);
                Assert.True(ContainsMarker(File.ReadAllBytes(output), 0xE1));
                Assert.Equal(before, Configuration.Default.LoadRgb24(output).Buffer);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        private static void WriteJpegWithMetadata(string path, int w, int h, byte[] rgb, int orientation)
        {
            using var ms = new MemoryStream();
            JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), ms,
                new JpegEncoderOptions(95, subsample420: true, keepMetadata: false, enableDiagnostics: false));
            byte[] jpeg = ms.ToArray();

            // "Exif  " + 最小 TIFF（1 个 Orientation 标签）
            var exif = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
            exif.AddRange([0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]); // II + 偏移 8
            exif.AddRange([0x01, 0x00]);                                     // 1 个 IFD 项
            exif.AddRange([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00]); // Orientation, SHORT, count=1
            exif.Add((byte)orientation); exif.Add(0); exif.Add(0); exif.Add(0);
            exif.AddRange([0, 0, 0, 0]);                                     // next IFD = 0

            using var fs = File.Create(path);
            fs.WriteByte(0xFF); fs.WriteByte(0xD8);

            byte[] app1 = BuildJpegSegment(0xE1, exif.ToArray());
            fs.Write(app1);
            byte[] com = BuildJpegSegment(0xFE, "hello"u8.ToArray());
            fs.Write(com);
            fs.Write(jpeg, 2, jpeg.Length - 2);
        }

        private static byte[] BuildJpegSegment(byte marker, byte[] payload)
        {
            int len = payload.Length + 2;
            var seg = new byte[4 + payload.Length];
            seg[0] = 0xFF; seg[1] = marker;
            seg[2] = (byte)(len >> 8); seg[3] = (byte)len;
            Buffer.BlockCopy(payload, 0, seg, 4, payload.Length);
            return seg;
        }

        private static bool ContainsMarker(byte[] jpeg, byte marker)
        {
            for (int i = 0; i + 1 < jpeg.Length; i++)
            {
                if (jpeg[i] == 0xFF && jpeg[i + 1] == marker) return true;
            }
            return false;
        }

        [Fact]
        public void Optimize_JpegHighLossyThreshold_PrefersLosslessAndPreservesPixels()
        {
            int w = 200, h = 150;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 13);
            string input = NewTemp(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(input))
                {
                    JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), fs,
                        new JpegEncoderOptions(95, subsample420: true, keepMetadata: false, enableDiagnostics: false));
                }
                var before = Configuration.Default.LoadRgb24(input).Buffer;
                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { MinLossyGainRatio = 0.99 });

                Assert.False(result.KeptOriginal);
                Assert.Contains("无损", result.Method);
                Assert.Equal(before, Configuration.Default.LoadRgb24(output).Buffer);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_16BitPng_KeepsOriginalInsteadOfSilentlyDowngrading()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                Write16BitPng(input);
                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.KeptOriginal);
                Assert.Contains("16-bit", result.Reason);
                Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_Apng_KeepsOriginalInsteadOfDroppingFrames()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                using (var fs = File.Create(input))
                {
                    PngWriter.Write(fs, 8, 8, new byte[8 * 8 * 3]);
                }
                InsertChunkAfterIhdr(input, "acTL", new byte[8]); // num_frames=0, num_plays=0，仅用于特征探测

                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.KeptOriginal);
                Assert.Contains("动画", result.Reason);
                Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_AnimatedWebp_KeepsOriginalInsteadOfFirstFrame()
        {
            string input = NewTemp(".webp");
            string output = NewTemp(".webp");
            try
            {
                WriteAnimatedWebpHeader(input);
                var traits = ImageOptimizer.DetectSourceTraits(input, ImageOptimizer.ImageKind.Webp);
                Assert.True(traits.IsAnimated);

                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.KeptOriginal);
                Assert.Contains("动画", result.Reason);
                Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        private static void Write16BitPng(string path)
        {
            using var fs = File.Create(path);
            fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 1);
            ihdr[8] = 16;   // bit depth
            ihdr[9] = 2;    // RGB
            WritePngChunk(fs, "IHDR", ihdr);

            // 1x1 RGB16：filter 0 + (0x1234, 0x5678, 0x9ABC)
            byte[] raw = [0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC];
            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw);
            }
            WritePngChunk(fs, "IDAT", compressed.ToArray());
            WritePngChunk(fs, "IEND", []);
        }

        private static void InsertChunkAfterIhdr(string path, string type, byte[] data)
        {
            byte[] png = File.ReadAllBytes(path);
            int ihdrLength = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4));
            int ihdrEnd = 8 + 4 + 4 + ihdrLength + 4;
            using var fs = File.Create(path);
            fs.Write(png, 0, ihdrEnd);
            WritePngChunk(fs, type, data);
            fs.Write(png, ihdrEnd, png.Length - ihdrEnd);
        }

        private static void WritePngChunk(Stream stream, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            stream.Write(len);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            var crcInput = new byte[typeBytes.Length + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
            Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(crcInput));
            stream.Write(crc);
        }

        private static void WriteAnimatedWebpHeader(string path)
        {
            using var body = new MemoryStream();
            body.Write("WEBP"u8);

            Span<byte> size = stackalloc byte[4];
            var vp8x = new byte[10];
            vp8x[0] = 0x02; // animation flag
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)vp8x.Length);
            body.Write("VP8X"u8);
            body.Write(size);
            body.Write(vp8x);

            var anim = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)anim.Length);
            body.Write("ANIM"u8);
            body.Write(size);
            body.Write(anim);

            using var fs = File.Create(path);
            fs.Write("RIFF"u8);
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            fs.Write(size);
            body.Position = 0;
            body.CopyTo(fs);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void PngWriter_WritePalette_ReducedBitDepth_RoundTrips(byte bitDepth)
        {
            int w = 37, h = 19;
            int colors = 1 << bitDepth;
            var palette = new byte[colors * 3];
            for (int i = 0; i < colors; i++)
            {
                palette[i * 3] = (byte)(i * 17);
                palette[i * 3 + 1] = (byte)(255 - i * 11);
                palette[i * 3 + 2] = (byte)(i * 29);
            }
            var indices = new byte[w * h];
            for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i % colors);

            string path = NewTemp(".png");
            try
            {
                using (var fs = File.Create(path))
                {
                    PngWriter.WritePalette(fs, w, h, indices, palette, null, null, PngFilterMode.Up, CompressionLevel.Optimal, bitDepth);
                }
                var decoded = Configuration.Default.LoadRgb24(path);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
                for (int i = 0; i < indices.Length; i++)
                {
                    int c = indices[i] * 3;
                    Assert.Equal(palette[c], decoded.Buffer[i * 3]);
                    Assert.Equal(palette[c + 1], decoded.Buffer[i * 3 + 1]);
                    Assert.Equal(palette[c + 2], decoded.Buffer[i * 3 + 2]);
                }
            }
            finally
            {
                Cleanup(path);
            }
        }

        [Fact]
        public void Optimize_LowColorPng_ReducesPaletteBitDepthWithoutChangingPixels()
        {
            int w = 64, h = 32;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = ((x / 8) + (y / 8)) % 16;
                    int o = (y * w + x) * 3;
                    rgb[o] = (byte)(c * 17);
                    rgb[o + 1] = (byte)(c * 7);
                    rgb[o + 2] = (byte)(c * 29);
                }
            }
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                PngWriter.Write(input, w, h, rgb);
                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { PngMaxDeflateOnWinner = false });

                byte[] png = File.ReadAllBytes(output);
                Assert.Equal(4, png[24]); // IHDR bit depth
                Assert.Equal(3, png[25]); // color type 3 (palette)
                Assert.Equal(rgb, Configuration.Default.LoadRgb24(output).Buffer);
                Assert.Contains("4-bit", result.Method);
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_PngDitherIsOptional_DualSearchNeverWorseThanNoDither()
        {
            int w = 128, h = 96;
            var rgb = BuildPhotoLikeRgb(w, h, seed: 21);
            string input = NewTemp(".png");
            string dual = NewTemp(".png");
            string plain = NewTemp(".png");
            try
            {
                PngWriter.Write(input, w, h, rgb);
                var common = new OptimizeOptions { PngMaxDeflateOnWinner = false };
                var dualResult = ImageOptimizer.Optimize(input, dual, new OptimizeOptions { PngMaxDeflateOnWinner = false, EnableDithering = true });
                var plainResult = ImageOptimizer.Optimize(input, plain, new OptimizeOptions { PngMaxDeflateOnWinner = false, EnableDithering = false });

                // 双候选包含「不抖动」这一支，因此选中体积不可能比强制不抖动更大
                Assert.True(dualResult.OptimizedSize <= plainResult.OptimizedSize,
                    $"dual={dualResult.OptimizedSize}, plain={plainResult.OptimizedSize}");
                Assert.True(OptimizeOptions.Balanced.Accept(dualResult.Quality), dualResult.Quality.ToString());
                Assert.Equal(w, Configuration.Default.LoadRgb24(dual).Width);
            }
            finally
            {
                Cleanup(input, dual, plain);
            }
        }

        [Fact]
        public void Optimize_PngWinnerRecompress_PreservesPixelsAndDoesNotGrow()
        {
            int w = 96, h = 64;
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    bool a = (x / 8 + y / 8) % 2 == 0;
                    rgb[o] = a ? (byte)200 : (byte)30;
                    rgb[o + 1] = a ? (byte)60 : (byte)180;
                    rgb[o + 2] = a ? (byte)20 : (byte)220;
                }
            }
            string input = NewTemp(".png");
            string fast = NewTemp(".png");
            string max = NewTemp(".png");
            try
            {
                PngWriter.Write(input, w, h, rgb);

                var fastResult = ImageOptimizer.Optimize(input, fast, new OptimizeOptions { PngMaxDeflateOnWinner = false });
                var maxResult = ImageOptimizer.Optimize(input, max, new OptimizeOptions { PngMaxDeflateOnWinner = true });

                Assert.True(maxResult.OptimizedSize <= fastResult.OptimizedSize);
                Assert.Equal(Configuration.Default.LoadRgb24(fast).Buffer, Configuration.Default.LoadRgb24(max).Buffer);
            }
            finally
            {
                Cleanup(input, fast, max);
            }
        }

        [Fact]
        public void PaletteQuantizer_KMeansRefinement_DoesNotIncreaseError()
        {
            int w = 96, h = 96;
            var rgba = new byte[w * h * 4];
            var rng = new Random(77);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    rgba[o] = (byte)Math.Clamp(120 + (x - w / 2) * 2 + rng.Next(-20, 20), 0, 255);
                    rgba[o + 1] = (byte)Math.Clamp(100 + (y - h / 2) * 2 + rng.Next(-20, 20), 0, 255);
                    rgba[o + 2] = (byte)Math.Clamp(140 + (x + y - w) + rng.Next(-20, 20), 0, 255);
                    rgba[o + 3] = 255;
                }
            }

            var coarse = PaletteQuantizer.Quantize(rgba, w, h,
                new QuantizeOptions { MaxColors = 16, EnableDithering = false, EnableKMeansRefinement = false });
            var refined = PaletteQuantizer.Quantize(rgba, w, h,
                new QuantizeOptions { MaxColors = 16, EnableDithering = false, EnableKMeansRefinement = true });

            var restoredCoarse = new byte[(long)w * h * 4];
            var restoredRefined = new byte[(long)w * h * 4];
            ImageOptimizer.RestoreRgba(coarse, restoredCoarse);
            ImageOptimizer.RestoreRgba(refined, restoredRefined);
            var q1 = QualityMetrics.Compare(rgba, restoredCoarse, w, h, 4);
            var q2 = QualityMetrics.Compare(rgba, restoredRefined, w, h, 4);

            Assert.True(q2.Psnr >= q1.Psnr - 0.05, $"{q2} vs {q1}");
            Assert.True(q2.Ssim >= q1.Ssim - 0.001, $"{q2} vs {q1}");
        }

        [Fact]
        public void QualityMetrics_Rgba_UsesPremultipliedAlpha()
        {
            var reference = new byte[] { 255, 0, 0, 0, 0, 255, 0, 255 };
            var sameVisible = new byte[] { 0, 0, 255, 0, 0, 255, 0, 255 };
            var transparentVsOpaque = new byte[] { 0, 0, 0, 255, 0, 255, 0, 255 };

            var same = QualityMetrics.Compare(reference, sameVisible, 2, 1, 4);
            Assert.True(same.IsLossless, same.ToString());

            var diff = QualityMetrics.Compare(reference, transparentVsOpaque, 2, 1, 4);
            Assert.True(diff.Psnr < 99, diff.ToString());
        }

        [Fact]
        public void QualityMetrics_ExternalBackend_IsUsed()
        {
            var previous = QualityMetrics.ExternalMetric;
            try
            {
                QualityMetrics.ExternalMetric = (_, _, _, _, _) =>
                    new ImageQuality(12, 34, 5, 0.6, 0.7);
                var q = QualityMetrics.Compare(new byte[12], new byte[12], 2, 2, 3);
                Assert.Equal(12, q.Psnr);
                Assert.Equal(34, q.PerceptualPsnr);
                Assert.Equal(5, q.MeanError);
                Assert.Equal(0.6, q.Ssim);
                Assert.Equal(0.7, q.WorstSsim);
            }
            finally
            {
                QualityMetrics.ExternalMetric = previous;
            }
        }

        [Fact]
        public void QualityMetrics_Ssim_CatchesCheckerboardThatBlockPsnrMisses()
        {
            int w = 64, h = 64;
            var reference = new byte[w * h * 3];
            var test = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    byte v = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                    reference[o] = reference[o + 1] = reference[o + 2] = v;
                    test[o] = test[o + 1] = test[o + 2] = 128;
                }
            }

            var q = QualityMetrics.Compare(reference, test, w, h, 3);

            // 4x4 块平均恰好把黑白棋盘抹成 128：旧指标几乎满分。
            Assert.True(q.PerceptualPsnr > 45, q.ToString());
            // SSIM 看到方差/结构被完全抹掉，应该明显低。
            Assert.True(q.Ssim < 0.9, q.ToString());
        }

        [Fact]
        public void OptimizeOptions_Accept_EnforcesSsimThresholds()
        {
            var options = new OptimizeOptions { MinSsim = 0.95, MinWorstSsim = 0.9 };
            Assert.False(options.Accept(new ImageQuality(99, 99, 0, 0.99, 0.5)));
            Assert.False(options.Accept(new ImageQuality(99, 99, 0, 0.5, 0.99)));
            Assert.True(options.Accept(new ImageQuality(99, 99, 0, 0.99, 0.95)));
        }

        [Fact]
        public void PaletteQuantizer_TransparentExactPalette_KeepsOpaqueColorsInOrder()
        {
            var sample = new byte[]
            {
                255, 0, 0, 255,
                255, 0, 0, 0,
                255, 0, 0, 255,
                0, 255, 0, 255,
            };
            var options = new QuantizeOptions { MaxColors = 16, TransparentAlphaThreshold = 128 };
            var quantizer = new PaletteQuantizer();
            quantizer.Prepare(sample, 4, 1, options);
            Assert.True(quantizer.IsExactPalette);
            var indices = quantizer.Map(sample, 4, 1, options);
            Assert.Equal(new byte[] { 1, 0, 1, 2 }, indices);
            var palette = quantizer.AssemblePalette(indices, reserveTransparent: true);
            Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0 }, palette.PaletteRgb.AsSpan(0, 9).ToArray());
        }

        [Fact]
        public void PaletteQuantizer_ExactPalette_MapsOutOfSampleColorToNearest()
        {
            // 采样只有红/绿两色，精确调色板只含这两色；映射时出现蓝色，不能抛 KeyNotFoundException。
            var sample = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 };
            var frame = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 };
            var options = new QuantizeOptions { MaxColors = 2 };

            var quantizer = new PaletteQuantizer();
            quantizer.Prepare(sample, 2, 1, options);
            Assert.True(quantizer.IsExactPalette);
            var indices = quantizer.Map(frame, 2, 1, options);

            Assert.Equal(2, indices.Length);
            Assert.Equal(0, indices[0]);
            Assert.InRange(indices[1], (byte)0, (byte)1);
        }

        [Fact]
        public void GifEncoder_LocalPalettes_RoundTripsFrames()
        {
            const int w = 4, h = 4;
            byte[] pal0 = [255, 0, 0];
            byte[] pal1 = [0, 255, 0];
            var f0 = new byte[w * h];
            var f1 = new byte[w * h];
            using var ms = new MemoryStream();
            new GifEncoder().EncodeIndexedFrames(
                w, h, globalPalette: null,
                frames: [f0, f1],
                rects: [(0, 0, w, h), (0, 0, w, h)],
                localPalettes: [pal0, pal1],
                frameDurationsMs: [100, 100],
                loopCount: 0,
                transparentIndex: -1,
                disposalMode: 0,
                stream: ms);
            ms.Position = 0;
            var animation = new GifDecoder().DecodeAnimationRgba32(ms);

            Assert.Equal(2, animation.RgbaFrames!.Count);
            Assert.Equal(255, animation.RgbaFrames[0].Buffer[0]);
            Assert.Equal(0, animation.RgbaFrames[0].Buffer[1]);
            Assert.Equal(255, animation.RgbaFrames[0].Buffer[3]);
            Assert.Equal(0, animation.RgbaFrames[1].Buffer[0]);
            Assert.Equal(255, animation.RgbaFrames[1].Buffer[1]);
        }

        [Fact]
        public void Optimize_AnimatedGif_DeduplicatesIdenticalFrames()
        {
            const int w = 64, h = 64;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
            var f0 = new byte[w * h];
            for (int i = 0; i < f0.Length; i++) f0[i] = (byte)(i % 4);
            var f1 = (byte[])f0.Clone();
            var f2 = (byte[])f0.Clone();
            for (int y = 8; y < 24; y++)
            {
                for (int x = 8; x < 24; x++) f2[y * w + x] = 2;
            }

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1, f2], [100, 200, 300], 0, fs);
                }
                var before = new GifDecoder().DecodeAnimationRgba32(input);
                var result = ImageOptimizer.Optimize(input, output,
                    new OptimizeOptions { KeepOriginalWhenNoGain = false, MaxColors = 5 });
                var after = new GifDecoder().DecodeAnimationRgba32(output);

                Assert.False(result.KeptOriginal, result.Reason);
                Assert.Equal(2, before.RgbaFrames!.Count - 1); // 输入 3 帧
                Assert.Equal(2, after.RgbaFrames!.Count);
                Assert.Equal(300, after.FrameDurationsMs[0]);
                for (int i = 0; i < after.RgbaFrames.Count; i++)
                {
                    Assert.True(after.RgbaFrames[i].Buffer.AsSpan().SequenceEqual(before.RgbaFrames[i == 0 ? 0 : 2].Buffer),
                        $"frame {i} / {result.Method}");
                }
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void GifEncoder_Disposal1_TransparentShowsPreviousFrame()
        {
            const int w = 4, h = 4;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 0]; // red, green, transparent slot
            var f0 = new byte[w * h]; // all red
            var f1 = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    f1[y * w + x] = y < 2 ? (byte)1 : (byte)2; // top green, bottom transparent
                }
            }

            using var ms = new MemoryStream();
            new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1], [100, 100], 0, ms,
                transparentIndex: 2, disposalMode: 1);
            ms.Position = 0;
            var animation = new GifDecoder().DecodeAnimationRgb24(ms);

            Assert.Equal(2, animation.Frames.Count);
            var second = animation.Frames[1].Buffer;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    if (y < 2)
                    {
                        Assert.Equal(0, second[o]);
                        Assert.Equal(255, second[o + 1]);
                    }
                    else
                    {
                        Assert.Equal(255, second[o]); // 透明像素保留上一帧的红色
                        Assert.Equal(0, second[o + 1]);
                    }
                }
            }
        }

        [Fact]
        public void Optimize_TransparentAnimatedGif_PreservesAlphaAndFrames()
        {
            const int w = 8, h = 8;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0]; // red, green, blue, transparent slot
            var f0 = new byte[w * h];
            var f1 = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    f0[y * w + x] = x < 4 ? (byte)0 : (byte)3;
                    f1[y * w + x] = x < 4 ? (byte)0 : (byte)1;
                }
            }

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1], [100, 100], 0, fs,
                        transparentIndex: 3, disposalMode: 2);
                }
                var before = new GifDecoder().DecodeAnimationRgba32(input);
                var result = ImageOptimizer.Optimize(input, output,
                    new OptimizeOptions { KeepOriginalWhenNoGain = false, MaxColors = 5 });
                var after = new GifDecoder().DecodeAnimationRgba32(output);

                Assert.False(result.KeptOriginal, $"{result.Reason} / {result.Method}");
                Assert.Equal(before.Frames.Count, after.Frames.Count);
                for (int i = 0; i < before.RgbaFrames!.Count; i++)
                {
                    var expected = before.RgbaFrames[i].Buffer;
                    var actual = after.RgbaFrames![i].Buffer;
                    for (int p = 0; p < expected.Length; p += 4)
                    {
                        Assert.Equal(expected[p + 3], actual[p + 3]);
                        if (expected[p + 3] == 0) continue; // 透明像素的 RGB 不显示
                        Assert.Equal(expected[p], actual[p]);
                        Assert.Equal(expected[p + 1], actual[p + 1]);
                        Assert.Equal(expected[p + 2], actual[p + 2]);
                    }
                }
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_AnimatedGif_UsesFrameDiffAndRoundTrips()
        {
            const int w = 32, h = 32;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
            var f0 = new byte[w * h];
            for (int i = 0; i < f0.Length; i++) f0[i] = (byte)(i % 4);
            var f1 = (byte[])f0.Clone();
            for (int y = 8; y < 16; y++)
            {
                for (int x = 8; x < 16; x++) f1[y * w + x] = 2;
            }
            var f2 = (byte[])f0.Clone();

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1, f2], [100, 100, 100], 0, fs);
                }
                long beforeSize = new FileInfo(input).Length;
                var inputAnimation = new GifDecoder().DecodeAnimationRgb24(input);

                var result = ImageOptimizer.Optimize(input, output);

                Assert.False(result.KeptOriginal);
                Assert.True(result.OptimizedSize < beforeSize, $"{result.OptimizedSize} vs {beforeSize}");
                var outputAnimation = new GifDecoder().DecodeAnimationRgb24(output);
                Assert.Equal(inputAnimation.Frames.Count, outputAnimation.Frames.Count);
                for (int i = 0; i < inputAnimation.Frames.Count; i++)
                {
                    Assert.Equal(inputAnimation.Frames[i].Buffer, outputAnimation.Frames[i].Buffer);
                }
            }
            finally
            {
                Cleanup(input, output);
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
