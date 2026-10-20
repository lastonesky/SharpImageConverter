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
    /// 调色板量化器：中位切分、K-means 精修、透明色保留。
    /// </summary>
    public partial class OptimizeTests
    {
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
    }
}
