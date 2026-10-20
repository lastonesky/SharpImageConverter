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
    /// 画质度量（PSNR/感知 PSNR/SSIM）与 OptimizeOptions 门槛。
    /// </summary>
    public partial class OptimizeTests
    {
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
