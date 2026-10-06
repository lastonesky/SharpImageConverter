using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// GIF 智能压缩：跨帧共享一张全局调色板，用二分搜索找到满足画质下限的最少颜色数。
    /// 颜色越少，LZW 的字典命中率越高，体积下降明显。
    /// </summary>
    /// <remarks>
    /// 动画 GIF 目前只支持不透明素材：解码器只暴露 RGB24 帧序列，
    /// 带透明通道的动画若重编码会丢失透明度，因此直接保留原图。
    /// </remarks>
    internal static class GifOptimizer
    {
        private const long MaxSamplePixels = 8_000_000;

        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var decoder = new GifDecoder();
            var animation = decoder.DecodeAnimationRgb24(input);
            if (animation.Frames.Count == 0)
            {
                return ImageOptimizer.Finish(input, output, originalSize, null, null, default, o, "GIF 中没有可用帧");
            }

            int width = animation.Frames[0].Width;
            int height = animation.Frames[0].Height;
            int framePixels = width * height;
            bool animated = animation.Frames.Count > 1;

            bool hasTransparency = HasTransparency(decoder.DecodeRgba32(input).Buffer);
            if (animated && hasTransparency)
            {
                return ImageOptimizer.Finish(input, output, originalSize, null, null, default, o, "带透明通道的动画 GIF 暂不支持有损优化");
            }

            // 单帧走 RGBA（保留透明），多帧用 RGB24 采样帧拼成一张大图建调色板
            bool reserveTransparent = hasTransparency;
            int sampleFrames = animated
                ? Math.Min(animation.Frames.Count, Math.Max(1, (int)(MaxSamplePixels / Math.Max(1, framePixels))))
                : 1;
            byte[] sample = BuildSample(animation, hasTransparency, sampleFrames, width, height, decoder, input);
            int sampleRows = height * sampleFrames;

            var quantizer = new PaletteQuantizer();
            var quantizeOptions = new QuantizeOptions
            {
                EnableDithering = o.EnableDithering,
                TransparentAlphaThreshold = reserveTransparent ? 128 : 0,
            };
            var produced = new Dictionary<int, (byte[] Bytes, ImageQuality Quality)>();
            byte[] restored = new byte[(long)framePixels * sampleFrames * 4];
            byte[] frameRgbaBuffer = new byte[(long)framePixels * 4];

            (byte[] Bytes, ImageQuality Quality) Evaluate(int colors)
            {
                if (produced.TryGetValue(colors, out var cached)) return cached;
                quantizeOptions.MaxColors = colors;
                quantizer.Prepare(sample, width, sampleRows, quantizeOptions);

                var frames = new List<byte[]>(animation.Frames.Count);
                for (int i = 0; i < animation.Frames.Count; i++)
                {
                    ToRgba(animation.Frames[i].Buffer, frameRgbaBuffer);
                    frames.Add(quantizer.Map(frameRgbaBuffer, width, height, quantizeOptions));
                }

                var palette = quantizer.AssemblePalette(frames[0], reserveTransparent);
                using var ms = new MemoryStream();
                new GifEncoder().EncodeIndexed(
                    width, height, palette.PaletteRgb, frames,
                    animated ? animation.FrameDurationsMs : null,
                    animation.LoopCount, ms,
                    reserveTransparent ? 0 : -1);

                byte[] bytes = ms.ToArray();
                var sampled = new QuantizeResult
                {
                    PaletteRgb = palette.PaletteRgb,
                    PaletteAlpha = palette.PaletteAlpha,
                    Indices = BuildSampleIndices(frames, sampleFrames, framePixels),
                    ColorCount = palette.ColorCount,
                };
                ImageOptimizer.RestoreRgba(sampled, restored);
                var quality = QualityMetrics.Compare(sample, restored, width, sampleRows, 4);
                o.Log?.Invoke($"  候选 {colors} 色: {bytes.Length} 字节, {quality}");
                var entry = (bytes, quality);
                produced[colors] = entry;
                return entry;
            }

            // GIF 本身就是调色板格式：颜色数等于上限时重写没有收益，
            // 因此不做「颜色够少就只走无损」的短路，统一进入阶梯搜索继续降色。
            int[] ladder = ImageOptimizer.BuildColorLadder(o.MaxColors);
            int hit = ImageOptimizer.SearchLadder(ladder, c => Evaluate(c).Quality, o.Accept);
            if (hit < 0)
            {
                return ImageOptimizer.Finish(input, output, originalSize, null, null, default, o);
            }

            // 同样按体积取最小（体积并不严格随颜色数单调）
            byte[] bestBytes = null!;
            ImageQuality bestQuality = default;
            int bestColors = 0;
            foreach (var pair in produced)
            {
                if (!o.Accept(pair.Value.Quality)) continue;
                if (bestBytes != null && pair.Value.Bytes.Length >= bestBytes.Length) continue;
                bestBytes = pair.Value.Bytes;
                bestQuality = pair.Value.Quality;
                bestColors = pair.Key;
            }

            string method = $"GIF 调色板 {bestColors} 色" + (o.EnableDithering ? " + 抖动" : "") + (animated ? $"（{animation.Frames.Count} 帧）" : "");
            return ImageOptimizer.Finish(input, output, originalSize, bestBytes, method, bestQuality, o);
        }

        private static bool HasTransparency(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255) return true;
            }
            return false;
        }

        private static byte[] BuildSample(
            GifAnimation animation,
            bool hasTransparency,
            int sampleFrames,
            int width,
            int height,
            GifDecoder decoder,
            string input)
        {
            int framePixels = width * height;
            byte[] sample = new byte[(long)framePixels * sampleFrames * 4];

            if (!hasTransparency)
            {
                // 不透明：直接把采样帧的 RGB24 展开成 RGBA
                for (int s = 0; s < sampleFrames; s++)
                {
                    int frameIndex = sampleFrames == 1 ? 0 : (int)((long)s * animation.Frames.Count / sampleFrames);
                    byte[] src = animation.Frames[frameIndex].Buffer;
                    int dst = s * framePixels * 4;
                    for (int i = 0, j = dst; i < src.Length; i += 3, j += 4)
                    {
                        sample[j] = src[i];
                        sample[j + 1] = src[i + 1];
                        sample[j + 2] = src[i + 2];
                        sample[j + 3] = 255;
                    }
                }
                return sample;
            }

            // 单帧且带透明：直接用 RGBA 解码结果
            byte[] rgba = decoder.DecodeRgba32(input).Buffer;
            Buffer.BlockCopy(rgba, 0, sample, 0, sample.Length);
            return sample;
        }

        private static void ToRgba(byte[] rgb, byte[] rgba)
        {
            for (int i = 0, j = 0; j < rgba.Length; i += 3, j += 4)
            {
                rgba[j] = rgb[i];
                rgba[j + 1] = rgb[i + 1];
                rgba[j + 2] = rgb[i + 2];
                rgba[j + 3] = 255;
            }
        }

        /// <summary>
        /// 把各帧索引按采样顺序拼回一条连续缓冲，便于一次性做画质评估。
        /// </summary>
        private static byte[] BuildSampleIndices(List<byte[]> frames, int sampleFrames, int framePixels)
        {
            if (frames.Count == sampleFrames)
            {
                var joined = new byte[(long)framePixels * sampleFrames];
                for (int i = 0; i < frames.Count; i++)
                {
                    Buffer.BlockCopy(frames[i], 0, joined, i * framePixels, framePixels);
                }
                return joined;
            }

            var sampled = new byte[(long)framePixels * sampleFrames];
            for (int s = 0; s < sampleFrames; s++)
            {
                int frameIndex = sampleFrames == 1 ? 0 : (int)((long)s * frames.Count / sampleFrames);
                Buffer.BlockCopy(frames[frameIndex], 0, sampled, s * framePixels, framePixels);
            }
            return sampled;
        }
    }
}
