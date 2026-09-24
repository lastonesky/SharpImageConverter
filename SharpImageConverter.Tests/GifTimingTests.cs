using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Formats.Gif;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// 校验 GIF 耗时统计：默认关闭时不产生任何统计（热路径零开销），
    /// 开启后编解码都要给出分阶段耗时，且各阶段之和不超过总耗时。
    /// </summary>
    public class GifTimingTests
    {
        private const int Width = 64;
        private const int Height = 48;

        /// <summary>
        /// 构造一幅渐变 RGB24 测试图，保证量化与 LZW 都走真实工作量。
        /// </summary>
        private static byte[] BuildRgb()
        {
            var rgb = new byte[Width * Height * 3];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int o = (y * Width + x) * 3;
                    rgb[o] = (byte)(x * 4);
                    rgb[o + 1] = (byte)(y * 5);
                    rgb[o + 2] = (byte)((x + y) * 2);
                }
            }
            return rgb;
        }

        private static byte[] EncodeToGif(byte[] rgb, bool diagnostics)
        {
            var encoder = new GifEncoder { EnableDiagnostics = diagnostics };
            using var ms = new MemoryStream();
            encoder.Encode(new SharpImageConverter.ImageFrame(Width, Height, rgb), ms);
            return ms.ToArray();
        }

        [Fact]
        public void Encode_DiagnosticsDisabled_KeepsTimingNull()
        {
            var encoder = new GifEncoder();
            using var ms = new MemoryStream();
            encoder.Encode(new SharpImageConverter.ImageFrame(Width, Height, BuildRgb()), ms);

            Assert.Null(encoder.LastTiming);
        }

        [Fact]
        public void Encode_DiagnosticsEnabled_ReportsPhases()
        {
            var reports = new List<string>();
            var encoder = new GifEncoder { EnableDiagnostics = true, DiagnosticsLog = reports.Add };
            using var ms = new MemoryStream();
            encoder.Encode(new SharpImageConverter.ImageFrame(Width, Height, BuildRgb()), ms);

            GifTiming? timing = encoder.LastTiming;
            Assert.NotNull(timing);
            Assert.Equal(GifTimingKind.Encode, timing.Kind);
            Assert.Equal(Width * Height, timing.PixelCount);
            Assert.True(timing!.TotalMs > 0);
            Assert.True(timing.QuantizeMs > 0, "量化阶段必须有耗时");
            Assert.True(timing.LzwMs > 0, "LZW 阶段必须有耗时");
            Assert.False(double.IsNaN(timing.TotalMs));

            // 各阶段之和不应超过总耗时
            double accounted = timing.QuantizeMs + timing.HeaderMs + timing.LzwMs + timing.PrepareMs;
            Assert.True(accounted <= timing.TotalMs + 0.5, "分阶段耗时之和不应明显超过总耗时");

            Assert.Single(reports);
            Assert.Contains("encode", reports[0]);
            Assert.Contains("quantize", reports[0]);
            Assert.Contains("lzw", reports[0]);
        }

        [Fact]
        public void Decode_DiagnosticsEnabled_ReportsPhases()
        {
            byte[] gif = EncodeToGif(BuildRgb(), diagnostics: false);

            var reports = new List<string>();
            var decoder = new GifDecoder { EnableDiagnostics = true, DiagnosticsLog = reports.Add };
            using var ms = new MemoryStream(gif);
            decoder.DecodeRgb24(ms);

            GifTiming? timing = decoder.LastTiming;
            Assert.NotNull(timing);
            Assert.Equal(GifTimingKind.Decode, timing.Kind);
            Assert.Equal(Width * Height, timing.PixelCount);
            Assert.True(timing!.TotalMs > 0);
            Assert.True(timing.LzwMs > 0, "LZW 解压阶段必须有耗时");

            Assert.Single(reports);
            Assert.Contains("decode", reports[0]);
            Assert.Contains("lzw", reports[0]);
        }

        [Fact]
        public void Animation_DiagnosticsEnabled_ReportsPhasesForBothDirections()
        {
            byte[] rgb = BuildRgb();
            var frames = new List<SharpImageConverter.ImageFrame>
            {
                new(Width, Height, rgb),
                new(Width, Height, rgb),
            };
            var durations = new List<int> { 100, 100 };

            var encodeReports = new List<string>();
            var encoder = new GifEncoder { EnableDiagnostics = true, DiagnosticsLog = encodeReports.Add };
            using var ms = new MemoryStream();
            encoder.EncodeAnimation(frames, durations, 0, ms);

            GifTiming? encodeTiming = encoder.LastTiming;
            Assert.NotNull(encodeTiming);
            Assert.Equal(2, encodeTiming!.FrameCount);
            Assert.Equal(Width * Height * 2, encodeTiming.PixelCount);
            Assert.True(encodeTiming.QuantizeMs > 0);
            Assert.True(encodeTiming.LzwMs > 0);
            Assert.Equal("animation", encodeTiming.Label);

            ms.Position = 0;
            var decodeReports = new List<string>();
            var decoder = new GifDecoder { EnableDiagnostics = true, DiagnosticsLog = decodeReports.Add };
            var animation = decoder.DecodeAnimationRgb24(ms);

            Assert.Equal(2, animation.Frames.Count);
            GifTiming? decodeTiming = decoder.LastTiming;
            Assert.NotNull(decodeTiming);
            Assert.Equal(2, decodeTiming!.FrameCount);
            Assert.True(decodeTiming.LzwMs > 0);
            Assert.True(decodeTiming.RenderMs > 0);
            Assert.Contains("animation", decodeTiming.Label);
        }

        [Fact]
        public void Timing_Format_IncludesThroughputAndPercentage()
        {
            var timing = new GifTiming(GifTimingKind.Encode, 1_000_000, 1)
            {
                TotalTicks = StopwatchTicksPerMs * 100,
                QuantizeTicks = StopwatchTicksPerMs * 60,
                LzwTicks = StopwatchTicksPerMs * 40,
            };

            string report = timing.Format();

            // 100 万像素 / 100ms => 10 Mpx/s
            Assert.Contains("10.000 Mpx/s", report);
            Assert.Contains("60.000%", report);
            Assert.Contains("40.000%", report);
            Assert.Contains("pixels=1000000", report);
        }

        private static long StopwatchTicksPerMs =>
            System.Diagnostics.Stopwatch.Frequency / 1000;
    }
}
