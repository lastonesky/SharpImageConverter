using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SharpImageConverter;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    [Collection(SharpImageConverter.Tests.JpegSimdStateCollection.Name)]
    public class OptimizeCrossCuttingTests
    {
        private static string NewTemp(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);

        private static byte[] GradientRgba(int w, int h, bool alpha = false)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    rgba[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                    rgba[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                    rgba[o + 2] = (byte)((x * 3 + y * 5) & 0xFF);
                    rgba[o + 3] = alpha ? (byte)(x * 255 / Math.Max(1, w - 1)) : (byte)255;
                }
            }
            return rgba;
        }

        [Fact]
        public void ForEachCandidate_RetriesSeriallyOnOutOfMemory()
        {
            var completed = new int[8];
            int attempts = 0;
            ImageOptimizer.ForEachCandidate(8, 4, i =>
            {
                if (Interlocked.Increment(ref attempts) <= 2) throw new OutOfMemoryException();
                completed[i] = 1;
            });

            Assert.All(completed, c => Assert.Equal(1, c));
        }

        [Fact]
        public void ConvertTransparentPngToJpeg_SuggestsAlphaAwareTarget()
        {
            int w = 64, h = 48;
            byte[] rgba = GradientRgba(w, h, alpha: true);
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(input)) PngWriter.WriteRgba(fs, w, h, rgba);
                var result = ImageOptimizer.Optimize(input, output);
                Assert.True(result.Converted);
                Assert.NotNull(result.Suggestion);
                Assert.Contains("WebP", result.Suggestion);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void ConvertPhotoLikeBmpToPng_SuggestsWebp()
        {
            // 照片类（>4096 色）转 PNG 应建议更有损效率的 WebP。
            int w = 128, h = 96;
            byte[] rgba = GradientRgba(w, h);
            byte[] rgb = new byte[w * h * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j] = rgba[i];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
            string input = NewTemp(".bmp");
            string output = NewTemp(".png");
            try
            {
                using (var fs = File.Create(input))
                {
                    SharpImageConverter.Formats.Bmp.BmpWriter.Write24(fs, w, h, rgb);
                }
                var result = ImageOptimizer.Optimize(input, output);
                Assert.True(result.Converted);
                Assert.NotNull(result.Suggestion);
                Assert.Contains("WebP", result.Suggestion);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void JpegTrellisQuantization_RoundTripsToValidImage()
        {
            int w = 96, h = 64;
            byte[] rgb = new byte[w * h * 3];
            byte[] rgba = GradientRgba(w, h);
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j] = rgba[i];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }

            using var ms = new MemoryStream();
            JpegEncoder.Write(ms, w, h, rgb,
                new JpegEncoderOptions(70, JpegChromaSubsampling.Yuv420, keepMetadata: false,
                    enableDiagnostics: false, optimizeHuffman: true, trellisQuantization: true));
            ms.Position = 0;
            var decoded = new JpegDecoderAdapter().DecodeRgb24(ms);
            Assert.Equal(w, decoded.Width);
            Assert.Equal(h, decoded.Height);

            var score = QualityMetrics.Compare(rgb, decoded.Buffer, w, h, 3);
            Assert.True(score.Psnr > 20, $"trellis 产物画质异常: {score}");
        }

        [Fact]
        public void WebpEncoderOptions_AppliesTuningFields()
        {
            int w = 128, h = 96;
            byte[] rgba = GradientRgba(w, h);

            byte[] Encode(WebpEncoderOptions options)
            {
                using var ms = new MemoryStream();
                new WebpEncoderAdapterRgba { Options = options }.EncodeRgba32(ms, new Image<Rgba32>(w, h, rgba));
                return ms.ToArray();
            }

            var defaults = new WebpEncoderOptions(60f, WebpConcurrencyStrategy.Parallel, method: 2);
            var tuned = defaults.WithTuning(snsStrength: 100, segments: 2, filterStrength: 0, preprocessing: 2);
            byte[] a = Encode(defaults);
            byte[] b = Encode(tuned);

            Assert.NotEqual(a.Length, b.Length);
            var decoded = new WebpDecoderAdapterRgba().DecodeRgba32(new MemoryStream(b));
            Assert.Equal(w, decoded.Width);
            Assert.Equal(h, decoded.Height);
        }
    }
}
