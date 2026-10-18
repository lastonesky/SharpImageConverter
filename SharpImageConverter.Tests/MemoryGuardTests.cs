using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Processing;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    [Collection(SharpImageConverter.Tests.JpegSimdStateCollection.Name)]
    public class MemoryGuardTests
    {
        private static string NewTemp(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);

        private static byte[] GradientRgba(int w, int h)
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
                    rgba[o + 3] = 255;
                }
            }
            return rgba;
        }

        private static byte[] GradientRgb(int w, int h)
        {
            byte[] rgba = GradientRgba(w, h);
            var rgb = new byte[w * h * 3];
            SimdHelper.PackRgbaToRgb(rgba, rgb);
            return rgb;
        }


        [Fact]
        public void StreamingConversion_PngToBmp_RoundTripsDimensions()
        {
            int w = 37, h = 23;
            string input = NewTemp(".png");
            string output = NewTemp(".bmp");
            try
            {
                using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, GradientRgb(w, h));
                StreamingConversion.Convert(input, output, ImageOptimizer.ImageKind.Bmp, gray: false);
                Assert.True(MemoryGuard.TryReadDimensions(output, ImageOptimizer.ImageKind.Bmp, out int ow, out int oh));
                Assert.Equal((w, h), (ow, oh));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void StreamingConversion_PngToJpeg_ProducesDecodableImage()
        {
            int w = 64, h = 48;
            byte[] rgb = GradientRgb(w, h);
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, rgb);
                StreamingConversion.Convert(input, output, ImageOptimizer.ImageKind.Jpeg, gray: false, jpegQuality: 80);
                var decoded = Configuration.Default.LoadRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
                var score = QualityMetrics.Compare(rgb, decoded.Buffer, w, h, 3);
                Assert.True(score.Psnr > 25, $"streaming JPEG 画质异常: {score}");
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void StreamingConversion_PngToProgressiveJpeg_EmitsSof2()
        {
            int w = 48, h = 32;
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, GradientRgb(w, h));
                StreamingConversion.Convert(input, output, ImageOptimizer.ImageKind.Jpeg, gray: false,
                    jpegQuality: 75, jpegSubsampling: JpegChromaSubsampling.Yuv420, jpegProgressive: true);
                byte[] bytes = File.ReadAllBytes(output);
                Assert.True(bytes.AsSpan(0, Math.Min(bytes.Length, 2048)).IndexOf(new byte[] { 0xFF, 0xC2 }) >= 0);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Theory]
        [InlineData(JpegChromaSubsampling.Yuv420)]
        [InlineData(JpegChromaSubsampling.Yuv422)]
        [InlineData(JpegChromaSubsampling.Yuv444)]
        public void JpegRowSource_MatchesFullDecode(JpegChromaSubsampling subsampling)
        {
            int w = 800, h = 600;
            byte[] rgb = GradientRgb(w, h);
            string input = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(input))
                {
                    JpegEncoder.Write(fs, w, h, rgb, new JpegEncoderOptions(90, subsampling, keepMetadata: false));
                }
                var full = Configuration.Default.LoadRgb24(input);
                using var rows = JpegRowSource.Open(input);
                var row = new byte[rows.Width * 3];
                var actual = new byte[rows.Width * rows.Height * 3];
                for (int y = 0; y < rows.Height; y++)
                {
                    try
                    {
                        Assert.True(rows.ReadRow(row));
                    }
                    catch (Exception ex)
                    {
                        throw new Xunit.Sdk.XunitException($"读取第 {y} 行失败: {ex}");
                    }
                    row.CopyTo(actual, y * rows.Width * 3);
                }
                Assert.False(rows.ReadRow(row));
                var score = QualityMetrics.Compare(full.Buffer, actual, rows.Width, rows.Height, 3);
                // 行级路径用标量 IDCT + 线性色度上采样，与整幅 SIMD 融合路径存在舍入/边缘差异。
                Assert.True(score.Psnr > 40, $"行级 JPEG 解码与全量解码差异过大: {score}");
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
            }
        }

        [Fact]
        public void StreamingConversion_PreservesPngAlpha()
        {
            int w = 41, h = 27;
            byte[] rgba = GradientRgba(w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    rgba[(y * w + x) * 4 + 3] = (byte)(x * 255 / (w - 1));
                }
            }
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                using (var fs = File.Create(input)) PngWriter.WriteRgba(fs, w, h, rgba);
                StreamingConversion.Convert(input, output, ImageOptimizer.ImageKind.Png, gray: false);
                var expected = Configuration.Default.LoadRgba32(input);
                var actual = Configuration.Default.LoadRgba32(output);
                Assert.Equal(expected.Buffer, actual.Buffer);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void StreamingConversion_PngToGif_ProducesValidGif()
        {
            int w = 41, h = 27;
            string input = NewTemp(".png");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, GradientRgb(w, h));
                StreamingConversion.Convert(input, output, ImageOptimizer.ImageKind.Gif, gray: false);
                Assert.True(MemoryGuard.TryReadDimensions(output, ImageOptimizer.ImageKind.Gif, out int ow, out int oh));
                Assert.Equal((w, h), (ow, oh));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void WebpRowSource_MatchesFullDecode()
        {
            int w = 41, h = 27;
            byte[] rgba = GradientRgba(w, h);
            string input = NewTemp(".webp");
            try
            {
                using (var fs = File.Create(input))
                {
                    new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(90f, WebpConcurrencyStrategy.Parallel) }
                        .EncodeRgba32(fs, new Image<Rgba32>(w, h, rgba));
                }
                var full = Configuration.Default.LoadRgba32(input);
                using var rows = new WebpRowSource(input);
                Assert.Equal(full.Width, rows.Width);
                Assert.Equal(full.Height, rows.Height);
                var row = new byte[rows.Width * 4];
                for (int y = 0; y < rows.Height; y++)
                {
                    Assert.True(rows.ReadRow(row));
                    for (int x = 0; x < rows.Width; x++)
                    {
                        int e = (y * rows.Width + x) * 4;
                        int a = x * rows.Channels;
                        if (full.Buffer[e] != row[a] || full.Buffer[e + 1] != row[a + 1] || full.Buffer[e + 2] != row[a + 2])
                        {
                            throw new Xunit.Sdk.XunitException(
                                $"y={y} x={x} channels={rows.Channels} full=({full.Buffer[e]},{full.Buffer[e + 1]},{full.Buffer[e + 2]}) " +
                                $"row=({row[a]},{row[a + 1]},{row[a + 2]}) firstRowBytes={BitConverter.ToString(row, 0, Math.Min(16, row.Length))}");
                        }
                    }
                }
                Assert.False(rows.ReadRow(row));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
            }
        }

        [Theory]
        [InlineData(".png", ".bmp")]
        [InlineData(".bmp", ".png")]
        [InlineData(".png", ".png")]
        [InlineData(".webp", ".png")]
        [InlineData(".gif", ".png")]
        public void StreamingConversion_MatchesSourcePixels(string sourceExt, string targetExt)
        {
            int w = 41, h = 27;
            byte[] rgb = GradientRgb(w, h);
            byte[] rgba = GradientRgba(w, h);
            string input = NewTemp(sourceExt);
            string output = NewTemp(targetExt);
            try
            {
                switch (sourceExt)
                {
                    case ".png":
                        using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, rgb);
                        break;
                    case ".bmp":
                        using (var fs = File.Create(input)) BmpWriter.Write24(fs, w, h, rgb);
                        break;
                    case ".webp":
                        using (var fs = File.Create(input))
                        {
                            new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(90f, WebpConcurrencyStrategy.Parallel) }
                                .EncodeRgba32(fs, new Image<Rgba32>(w, h, rgba));
                        }
                        break;
                    case ".gif":
                    {
                        using var fs = File.Create(input);
                        new GifEncoderAdapter().EncodeRgb24(fs, new Image<Rgb24>(w, h, rgb));
                        break;
                    }
                    case ".jpg":
                        using (var fs = File.Create(input))
                        {
                            JpegEncoder.Write(fs, w, h, rgb, new JpegEncoderOptions(90, JpegChromaSubsampling.Yuv420, keepMetadata: false));
                        }
                        break;
                }

                StreamingConversion.Convert(input, output, ImageOptimizer.DetectKindByExtension(targetExt), gray: false);

                var expected = Configuration.Default.LoadRgba32(input);
                var actual = Configuration.Default.LoadRgba32(output);
                Assert.Equal(expected.Width, actual.Width);
                Assert.Equal(expected.Height, actual.Height);
                for (int i = 0; i < expected.Buffer.Length; i += 4)
                {
                    if (expected.Buffer[i] != actual.Buffer[i]
                        || expected.Buffer[i + 1] != actual.Buffer[i + 1]
                        || expected.Buffer[i + 2] != actual.Buffer[i + 2]
                        || (targetExt == ".png" && expected.Buffer[i + 3] != actual.Buffer[i + 3]))
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"first mismatch at pixel {i / 4} (x={i / 4 % w}, y={i / 4 / w}): " +
                            $"exp=({expected.Buffer[i]},{expected.Buffer[i + 1]},{expected.Buffer[i + 2]},{expected.Buffer[i + 3]}) " +
                            $"act=({actual.Buffer[i]},{actual.Buffer[i + 1]},{actual.Buffer[i + 2]},{actual.Buffer[i + 3]})");
                    }
                }
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void TryReadDimensions_ReadsAllSupportedHeaders()
        {
            int w = 37, h = 23;
            byte[] rgb = GradientRgb(w, h);
            byte[] rgba = GradientRgba(w, h);

            string png = NewTemp(".png");
            string jpg = NewTemp(".jpg");
            string bmp = NewTemp(".bmp");
            string webp = NewTemp(".webp");
            string gif = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(png)) PngWriter.Write(fs, w, h, rgb);
                using (var fs = File.Create(jpg))
                {
                    JpegEncoder.Write(fs, w, h, rgb, new JpegEncoderOptions(70, JpegChromaSubsampling.Yuv420, keepMetadata: false));
                }
                using (var fs = File.Create(bmp)) BmpWriter.Write24(fs, w, h, rgb);
                using (var fs = File.Create(webp))
                {
                    new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(60f, WebpConcurrencyStrategy.Parallel) }
                        .EncodeRgba32(fs, new Image<Rgba32>(w, h, rgba));
                }
                File.WriteAllBytes(gif, MinimalGif(w, h));

                Assert.True(MemoryGuard.TryReadDimensions(png, ImageOptimizer.ImageKind.Png, out int pw, out int ph));
                Assert.Equal((w, h), (pw, ph));
                Assert.True(MemoryGuard.TryReadDimensions(jpg, ImageOptimizer.ImageKind.Jpeg, out int jw, out int jh));
                Assert.Equal((w, h), (jw, jh));
                Assert.True(MemoryGuard.TryReadDimensions(bmp, ImageOptimizer.ImageKind.Bmp, out int bw, out int bh));
                Assert.Equal((w, h), (bw, bh));
                Assert.True(MemoryGuard.TryReadDimensions(webp, ImageOptimizer.ImageKind.Webp, out int ww, out int wh));
                Assert.Equal((w, h), (ww, wh));
                Assert.True(MemoryGuard.TryReadDimensions(gif, ImageOptimizer.ImageKind.Gif, out int gw, out int gh, out int gf));
                Assert.Equal((w, h), (gw, gh));
                Assert.Equal(1, gf);
            }
            finally
            {
                foreach (string p in new[] { png, jpg, bmp, webp, gif })
                {
                    if (File.Exists(p)) File.Delete(p);
                }
            }
        }

        [Fact]
        public void Optimize_RefusesWhenBudgetTooSmall()
        {
            int w = 64, h = 48;
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, GradientRgb(w, h));

                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { MemoryLimitBytes = 1 });
                Assert.True(result.KeptOriginal);
                Assert.Contains("内存", result.Reason);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void Optimize_DisableMemoryGuard_BypassesRefusal()
        {
            int w = 64, h = 48;
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                using (var fs = File.Create(input)) PngWriter.Write(fs, w, h, GradientRgb(w, h));

                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions
                {
                    MemoryLimitBytes = 1,
                    DisableMemoryGuard = true,
                });
                Assert.False(result.KeptOriginal);
                Assert.True(File.Exists(output));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        [Fact]
        public void CheckPlain_Jpeg_EstimatesFusedStreamingLowerThanFullRows()
        {
            int w = 256, h = 256;
            string jpg = NewTemp(".jpg");
            try
            {
                using (var fs = File.Create(jpg))
                {
                    JpegEncoder.Write(fs, w, h, GradientRgb(w, h), new JpegEncoderOptions(95, JpegChromaSubsampling.Yuv422, keepMetadata: false));
                }

                long fullRows = MemoryGuard.EstimateStreamingPlainBytes(
                    ImageOptimizer.ImageKind.Jpeg, ImageOptimizer.ImageKind.Png, 10000, 10000, 1);
                long fused = MemoryGuard.EstimateStreamingPlainBytes(
                    ImageOptimizer.ImageKind.Jpeg, ImageOptimizer.ImageKind.Png, 10000, 10000, 1, jpegFusedRow: true);
                Assert.True(fused < fullRows);

                var ok = MemoryGuard.CheckPlain(ImageOptimizer.ImageKind.Jpeg, ImageOptimizer.ImageKind.Png, jpg, long.MaxValue / 2);
                Assert.Equal(MemoryGuard.Action.Ok, ok.Action);
                var refused = MemoryGuard.CheckPlain(ImageOptimizer.ImageKind.Jpeg, ImageOptimizer.ImageKind.Png, jpg, 1);
                Assert.Equal(MemoryGuard.Action.Refuse, refused.Action);
            }
            finally
            {
                if (File.Exists(jpg)) File.Delete(jpg);
            }
        }

        private static byte[] MinimalGif(int w, int h)
        {
            using var ms = new MemoryStream();
            ms.Write("GIF89a"u8);
            ms.WriteByte((byte)(w & 0xFF));
            ms.WriteByte((byte)(w >> 8));
            ms.WriteByte((byte)(h & 0xFF));
            ms.WriteByte((byte)(h >> 8));
            ms.WriteByte(0x80); // global color table, 2 entries
            ms.WriteByte(0);
            ms.WriteByte(0);
            ms.Write([0, 0, 0, 255, 255, 255]);
            ms.WriteByte(0x2C); // image descriptor
            ms.Write([0, 0, 0, 0]);
            ms.WriteByte((byte)(w & 0xFF));
            ms.WriteByte((byte)(w >> 8));
            ms.WriteByte((byte)(h & 0xFF));
            ms.WriteByte((byte)(h >> 8));
            ms.WriteByte(0);
            ms.WriteByte(2);
            ms.WriteByte(2);
            ms.Write([0x44, 0x01]);
            ms.WriteByte(0);
            ms.WriteByte(0x3B);
            return ms.ToArray();
        }
    }
}
