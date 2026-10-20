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
    /// PNG 优化：滤波/调色板/位深缩减/抖动与赢家重压缩。
    /// </summary>
    public partial class OptimizeTests
    {
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
    }
}
