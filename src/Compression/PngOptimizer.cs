using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Png;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// PNG 智能压缩：把图像量化到调色板（每像素 1 字节）后重写 PNG，
    /// 颜色数由画质下限二分搜索决定；量化不达标时回退为无损真彩色重写。
    /// </summary>
    internal static class PngOptimizer
    {
        /// <summary>
        /// 同格式优化：读入源文件，按 PNG 重写为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgba32(input);
            var artifact = Produce(image, originalSize, o);
            return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o, artifact.Reason);
        }

        /// <summary>
        /// 跨格式优化：源图已解码为 RGBA32，直接按 PNG 产出最小体积文件。
        /// </summary>
        internal static OptimizationResult RunFromImage(Image<Rgba32> image, string input, string output, long originalSize, OptimizeOptions o)
        {
            var artifact = Produce(image, originalSize, o);
            return ImageOptimizer.FinishConverted(input, output, originalSize, artifact, o);
        }

        /// <summary>
        /// 计算最优 PNG 产物（不落盘）。
        /// </summary>
        /// <param name="image">源图（RGBA32）</param>
        /// <param name="originalSize">源文件字节数；用于判断量化收益是否值得</param>
        /// <param name="o">优化选项</param>
        internal static OptimizeArtifact Produce(Image<Rgba32> image, long originalSize, OptimizeOptions o)
        {
            int width = image.Width;
            int height = image.Height;
            int pixels = width * height;
            byte[] rgba = image.Buffer;
            var metadata = ImageOptimizer.PrepareMetadata(image.Metadata, o.StripMetadata);

            var filter = o.AdaptiveFiltering ? PngFilterMode.Adaptive : PngFilterMode.Up;
            var level = ImageOptimizer.PickDeflateLevel(pixels);

            var quantizer = new PaletteQuantizer();
            var quantizeOptions = new QuantizeOptions { EnableDithering = o.EnableDithering };
            byte[] restored = new byte[(long)pixels * 4];

            var produced = new Dictionary<int, (byte[] Bytes, QuantizeResult Quantized, ImageQuality Quality)>();

            byte[] Encode(QuantizeResult q)
            {
                using var ms = new MemoryStream();
                PngWriter.WritePalette(ms, width, height, q.Indices, q.PaletteRgb, q.PaletteAlpha, metadata, filter, level);
                return ms.ToArray();
            }

            ImageQuality Evaluate(int colors)
            {
                if (produced.TryGetValue(colors, out var cached)) return cached.Quality;
                quantizeOptions.MaxColors = colors;
                var q = quantizer.QuantizeCore(rgba, width, height, quantizeOptions);
                byte[] bytes = Encode(q);
                ImageOptimizer.RestoreRgba(q, restored);
                var quality = QualityMetrics.Compare(rgba, restored, width, height, 4);
                o.Log?.Invoke($"  候选 {q.ColorCount} 色: {bytes.Length} 字节, {quality}");
                produced[colors] = (bytes, q, quality);
                return quality;
            }

            // 颜色本来就不多：优先无损取色，不再做有损尝试
            int unique = PaletteQuantizer.CountUniqueColors(rgba, o.MaxColors);
            byte[]? bestBytes = null;
            string? bestMethod = null;
            ImageQuality bestQuality = default;

            if (unique <= o.MaxColors)
            {
                quantizeOptions.MaxColors = o.MaxColors;
                var q = quantizer.QuantizeCore(rgba, width, height, quantizeOptions);
                bestBytes = Encode(q);
                ImageOptimizer.RestoreRgba(q, restored);
                bestQuality = QualityMetrics.Compare(rgba, restored, width, height, 4);
                bestMethod = $"PNG 调色板 {q.ColorCount} 色（无损）";
                o.Log?.Invoke($"  无损取色 {q.ColorCount} 色: {bestBytes.Length} 字节, {bestQuality}");
            }

            if (bestBytes == null)
            {
                int[] ladder = ImageOptimizer.BuildColorLadder(o.MaxColors);
                ImageOptimizer.SearchLadder(ladder, Evaluate, o.Accept);
                // 体积并不严格随颜色数单调（中位切分的切法不同会有几个百分点的抖动），
                // 因此在所有达标候选里按体积取最小，而不是直接取颜色最少的那个。
                foreach (var pair in produced)
                {
                    if (!o.Accept(pair.Value.Quality)) continue;
                    if (bestBytes != null && pair.Value.Bytes.Length >= bestBytes.Length) continue;
                    bestBytes = pair.Value.Bytes;
                    bestQuality = pair.Value.Quality;
                    bestMethod = $"PNG 调色板 {pair.Value.Quantized.ColorCount} 色" + (o.EnableDithering ? " + 抖动" : "");
                }
            }
            // 量化收益不明显（或压根没达标）时，再试一次无损真彩色重写：
            // 渐变类图像抖动后索引近乎随机，调色板反而会比真彩色大很多。
            bool needLossless = bestBytes == null || (originalSize - bestBytes.Length) < originalSize * 0.10;
            if (needLossless)
            {
                using var ms = new MemoryStream();
                if (IsOpaque(rgba))
                {
                    PngWriter.Write(ms, width, height, PackRgb(rgba, pixels), metadata, filter, level);
                }
                else
                {
                    PngWriter.WriteRgba(ms, width, height, rgba, metadata, filter, level);
                }
                byte[] lossless = ms.ToArray();
                if (bestBytes == null || lossless.Length < bestBytes.Length)
                {
                    bestBytes = lossless;
                    bestMethod = "PNG 无损重写（自适应滤波）";
                    bestQuality = new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0);
                }
            }

            // PNG 走到这里必然有产物（量化或无损重写），且无损重写一定达标
            bool meetsBar = bestQuality.IsLossless || o.Accept(bestQuality);
            return new OptimizeArtifact(bestBytes, bestMethod, bestQuality, meetsBar);
        }

        private static bool IsOpaque(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255) return false;
            }
            return true;
        }

        private static byte[] PackRgb(byte[] rgba, int pixels)
        {
            var rgb = GC.AllocateUninitializedArray<byte>(pixels * 3);
            for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
            {
                rgb[j] = rgba[i];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
            return rgb;
        }
    }
}
