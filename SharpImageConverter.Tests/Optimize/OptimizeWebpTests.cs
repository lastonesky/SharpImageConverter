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
    /// WebP 优化：有损/无损选择、质量锚点搜索与 method 分档。
    /// </summary>
    public partial class OptimizeTests
    {
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

        [Fact]
        public void Optimize_AnimatedWebp_KeepsOriginalInsteadOfFirstFrame()
        {
            string input = NewTemp(".webp");
            string output = NewTemp(".webp");
            try
            {
                WriteAnimatedWebpHeader(input);
                var traits = ImageOptimizer.DetectSourceTraits(input, ImageFormatKind.Webp);
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
    }
}
