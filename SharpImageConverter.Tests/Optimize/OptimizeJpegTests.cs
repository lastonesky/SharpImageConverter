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
    /// JPEG 优化：质量搜索、子采样对比、渐进式、Huffman 重排与元数据。
    /// </summary>
    public partial class OptimizeTests
    {
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
    }
}
