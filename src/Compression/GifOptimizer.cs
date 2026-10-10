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

        /// <summary>
        /// 同格式优化：读入源 GIF，重编码为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var decoder = new GifDecoder();
            var animation = decoder.DecodeAnimationRgb24(input);
            if (animation.Frames.Count == 0)
            {
                return ImageOptimizer.Finish(input, output, originalSize, null, null, default, o, "GIF 中没有可用帧");
            }

            byte[] rgba = decoder.DecodeRgba32(input).Buffer;
            bool hasTransparency = HasTransparency(rgba);
            var artifact = Produce(animation, hasTransparency, hasTransparency ? rgba : null, o);
            if (artifact.Bytes == null || !artifact.MeetsBar)
            {
                return ImageOptimizer.Finish(input, output, originalSize, null, null, default, o, artifact.Reason);
            }

            return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o);
        }

        /// <summary>
        /// 跨格式优化：源图为单帧 RGBA，直接按 GIF 产出最小体积文件。
        /// </summary>
        internal static OptimizationResult RunFromImage(Image<Rgba32> image, string input, string output, long originalSize, OptimizeOptions o)
        {
            var rgb = ToRgb24(image);
            var animation = new GifAnimation([rgb], [0], 0);
            bool hasTransparency = HasTransparency(image.Buffer);
            var artifact = Produce(animation, hasTransparency, hasTransparency ? image.Buffer : null, o);
            return ImageOptimizer.FinishConverted(input, output, originalSize, artifact, o);
        }

        /// <summary>
        /// 计算最优 GIF 产物（不落盘）。
        /// </summary>
        /// <param name="animation">源帧序列（单帧即静态图）</param>
        /// <param name="hasTransparency">源图是否含透明像素</param>
        /// <param name="singleFrameRgba">单帧且带透明时的 RGBA 像素；否则为 null</param>
        /// <param name="o">优化选项</param>
        internal static OptimizeArtifact Produce(GifAnimation animation, bool hasTransparency, byte[]? singleFrameRgba, OptimizeOptions o)
        {
            int width = animation.Frames[0].Width;
            int height = animation.Frames[0].Height;
            int framePixels = width * height;
            bool animated = animation.Frames.Count > 1;

            if (animated && hasTransparency)
            {
                return OptimizeArtifact.Unsupported("带透明通道的动画 GIF 暂不支持有损优化");
            }

            // 单帧走 RGBA（保留透明），多帧用 RGB24 采样帧拼成一张大图建调色板
            bool reserveTransparent = hasTransparency;
            int sampleFrames = animated
                ? Math.Min(animation.Frames.Count, Math.Max(1, (int)(MaxSamplePixels / Math.Max(1, framePixels))))
                : 1;
            byte[] sample = BuildSample(animation, singleFrameRgba, sampleFrames, width, height);
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
                    if (reserveTransparent && singleFrameRgba != null)
                    {
                        // 透明像素必须带真实 alpha 参与映射，否则 0 号透明槽永远不会被命中
                        frames.Add(quantizer.Map(singleFrameRgba, width, height, quantizeOptions));
                    }
                    else
                    {
                        ToRgba(animation.Frames[i].Buffer, frameRgbaBuffer);
                        frames.Add(quantizer.Map(frameRgbaBuffer, width, height, quantizeOptions));
                    }
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

            byte[]? bestBytes = null;
            ImageQuality bestQuality = default;
            int bestColors = 0;
            foreach (var pair in produced)
            {
                if (hit >= 0)
                {
                    // 体积并不严格随颜色数单调（体积与画质都会有几个百分点的抖动），
                    // 因此在所有达标候选里按体积取最小。
                    if (!o.Accept(pair.Value.Quality)) continue;
                    if (bestBytes != null && pair.Value.Bytes.Length >= bestBytes.Length) continue;
                }
                else
                {
                    // 全部不达标时退回「画质最高」的候选，跨格式输出至少保证文件可用
                    if (bestBytes != null && pair.Value.Quality.PerceptualPsnr <= bestQuality.PerceptualPsnr) continue;
                }

                bestBytes = pair.Value.Bytes;
                bestQuality = pair.Value.Quality;
                bestColors = pair.Key;
            }

            if (bestBytes == null) return OptimizeArtifact.Unsupported("GIF 量化未产出可用候选");

            string method = $"GIF 调色板 {bestColors} 色" + (o.EnableDithering ? " + 抖动" : "") + (animated ? $"（{animation.Frames.Count} 帧）" : "");
            if (hit < 0) method += "（未达画质下限，取最高画质档）";
            return new OptimizeArtifact(bestBytes, method, bestQuality, hit >= 0);
        }

        private static bool HasTransparency(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255) return true;
            }
            return false;
        }

        private static Image<Rgb24> ToRgb24(Image<Rgba32> image)
        {
            var rgb = new byte[image.Width * image.Height * 3];
            SimdHelper.PackRgbaToRgb(image.Buffer, rgb);
            return new Image<Rgb24>(image.Width, image.Height, rgb, image.Metadata);
        }

        private static byte[] BuildSample(
            GifAnimation animation,
            byte[]? providedRgba,
            int sampleFrames,
            int width,
            int height)
        {
            int framePixels = width * height;
            byte[] sample = new byte[(long)framePixels * sampleFrames * 4];

            if (providedRgba == null)
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

            // 单帧且带透明：直接用 RGBA 源
            Buffer.BlockCopy(providedRgba, 0, sample, 0, sample.Length);
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
