using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        /// 分阶段耗时探针开关（`SIC_GIF_OPT_TIMING=1`）。关闭时不进入任何 Stopwatch 调用。
        /// </summary>
        private static readonly bool StageTiming = Environment.GetEnvironmentVariable("SIC_GIF_OPT_TIMING") == "1";

        /// <summary>
        /// 强制串行评估候选（`SIC_GIF_NO_PARALLEL=1`）。
        /// 同二进制交错 A/B 用：跨二进制对比带 ±2% 系统性偏差，判不了这类改动。
        /// </summary>
        private static readonly bool ForceSerial = Environment.GetEnvironmentVariable("SIC_GIF_NO_PARALLEL") == "1";

        // 探针累计（仅 StageTiming 时写入；单次 Process 内多文件会累加）
        private static long _ticksPrepare, _ticksMap, _ticksEncode, _ticksRestore, _ticksCompare, _ticksBaseline;
        private static int _evaluations, _framesMapped;

        internal static void ReportStageTiming()
        {
            if (!StageTiming || _evaluations == 0) return;
            static double Ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.Error.WriteLine(
                $"[gif-opt] 候选 {_evaluations} 个（映射 {_framesMapped} 帧）: " +
                $"prepare {Ms(_ticksPrepare):F1} ms | map {Ms(_ticksMap):F1} ms | " +
                $"encode {Ms(_ticksEncode):F1} ms | quality {Ms(_ticksRestore + _ticksCompare):F1} ms" +
                $"（restore {Ms(_ticksRestore):F1} + compare {Ms(_ticksCompare):F1}） | " +
                $"baseline {Ms(_ticksBaseline):F1} ms");
        }

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

            byte[]? rgba = null;
            bool hasTransparency = false;
            if (!animation.CompositeFullyOpaque)
            {
                // 首帧合成不可能全不透明时才需要真的解一次 RGBA 来探测：
                // 该解码只解首帧就返回，实测 2048² 约 20 ms（动/静帧同价）。
                rgba = decoder.DecodeRgba32(input).Buffer;
                hasTransparency = HasTransparency(rgba);
            }

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
            if (!StageTiming) return ProduceCore(animation, hasTransparency, singleFrameRgba, o);
            _ticksPrepare = _ticksMap = _ticksEncode = _ticksRestore = _ticksCompare = _ticksBaseline = 0;
            _evaluations = _framesMapped = 0;
            try
            {
                return ProduceCore(animation, hasTransparency, singleFrameRgba, o);
            }
            finally
            {
                ReportStageTiming();
            }
        }

        private static OptimizeArtifact ProduceCore(GifAnimation animation, bool hasTransparency, byte[]? singleFrameRgba, OptimizeOptions o)
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

            // key = (颜色数, 是否抖动)。抖动与否是独立的搜索维度：
            // Floyd–Steinberg 的误差扩散把索引流打成高频噪声，LZW 命中率暴跌，
            // 实测同一张图同一色数下体积可差 5 倍以上（car.png 256 色：抖动 92 KB / 不抖 17 KB），
            // 因此两个变体都必须参与体积竞争，不能预设「抖动画质更好就该选它」。
            var produced = new Dictionary<(int Colors, bool Dither), (byte[] Bytes, ImageQuality Quality)>();
            var logSync = new object();
            int transparentThreshold = reserveTransparent ? 128 : 0;

            // GIF 本身就是调色板格式：颜色数等于上限时重写没有收益，
            // 因此不做「颜色够少就只走无损」的短路，统一进入阶梯搜索继续降色。
            int[] ladder = ImageOptimizer.BuildColorLadder(o.MaxColors);

            // 候选并行：GIF 的量化 / 映射 / LZW 编码全是单线程，与 PNG 的 deflate、WebP 的 libwebp 同构，
            // 候选级并行因此是唯一的多核杠杆（24 核单文件时串行只用 1 个核）。
            // 并行度按内存预算夹（单候选峰值 ≈ 索引 1 B/px + 还原缓冲 4 B/px）。
            int variantCount = o.EnableDithering ? 2 : 1;
            long perCandidateBytes = (long)framePixels * sampleFrames * 5 + (2L << 20);
            int degree = ForceSerial
                ? 1
                : ImageOptimizer.CandidateParallelism(ladder.Length * variantCount, perCandidateBytes);
            // 画质比较自身也会按带并行。候选数不足以喂满核数时让它并行（填满空闲核），
            // 已经能占满核数时关掉（16 候选 × 核数 只会互相抢占）。
            bool compareInnerParallel = degree < Environment.ProcessorCount;

            // 单候选评估。量化器 / 查找缓存 / 还原缓冲 / 帧缓冲都按候选独立传入，
            // 因而同一段代码既能串行复用（共享一次 Prepare），也能在候选并行的每个任务里独立跑。
            (byte[] Bytes, ImageQuality Quality) Evaluate(
                PaletteQuantizer quantizer,
                ref int preparedColors,
                byte[] restored,
                byte[] frameRgbaBuffer,
                int colors,
                bool dithering)
            {
                if (StageTiming) _evaluations++;
                long mark = StageTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                var quantizeOptions = new QuantizeOptions
                {
                    MaxColors = colors,
                    EnableDithering = dithering,
                    TransparentAlphaThreshold = transparentThreshold,
                };

                if (preparedColors != colors)
                {
                    quantizer.Prepare(sample, width, sampleRows, quantizeOptions);
                    preparedColors = colors;
                }
                else
                {
                    // 同色数的第二个变体：查找缓存被上一个变体污染过（桶级记忆化依赖首个像素），
                    // 必须清空，保证每个变体的映射与「独立评估」逐字节一致。
                    quantizer.ResetLookupCache();
                }
                if (StageTiming)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ticksPrepare += now - mark;
                    mark = now;
                    _framesMapped += animation.Frames.Count;
                }

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
                if (StageTiming)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ticksMap += now - mark;
                    mark = now;
                }

                var palette = quantizer.AssemblePalette(frames[0], reserveTransparent);
                using var ms = new MemoryStream();
                new GifEncoder().EncodeIndexed(
                    width, height, palette.PaletteRgb, frames,
                    animated ? animation.FrameDurationsMs : null,
                    animation.LoopCount, ms,
                    reserveTransparent ? 0 : -1);

                byte[] bytes = ms.ToArray();
                if (StageTiming)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ticksEncode += now - mark;
                    mark = now;
                }

                var sampled = new QuantizeResult
                {
                    PaletteRgb = palette.PaletteRgb,
                    PaletteAlpha = palette.PaletteAlpha,
                    Indices = BuildSampleIndices(frames, sampleFrames, framePixels),
                    ColorCount = palette.ColorCount,
                };
                ImageOptimizer.RestoreRgba(sampled, restored);
                if (StageTiming)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ticksRestore += now - mark;
                    mark = now;
                }
                var quality = QualityMetrics.Compare(sample, restored, width, sampleRows, 4, compareInnerParallel);
                if (StageTiming) _ticksCompare += System.Diagnostics.Stopwatch.GetTimestamp() - mark;
                if (o.Log is { } log)
                {
                    string line = $"  候选 {colors} 色{(dithering ? " + 抖动" : "")}: {bytes.Length} 字节, {quality}";
                    lock (logSync) log(line);
                }
                return (bytes, quality);
            }

            // GIF 本身就是调色板格式：颜色数等于上限时重写没有收益，
            // 因此不做「颜色够少就只走无损」的短路，统一进入阶梯搜索继续降色。
            int hit;

            if (degree <= 1)
            {
                var quantizer = new PaletteQuantizer();
                int preparedColors = -1;
                byte[] restored = new byte[(long)framePixels * sampleFrames * 4];
                byte[] frameRgbaBuffer = new byte[(long)framePixels * 4];

                (byte[] Bytes, ImageQuality Quality) EvaluateCached(int colors, bool dithering)
                {
                    var key = (colors, dithering);
                    if (produced.TryGetValue(key, out var cached)) return cached;
                    var result = Evaluate(quantizer, ref preparedColors, restored, frameRgbaBuffer, colors, dithering);
                    produced[key] = result;
                    return result;
                }

                // 二分定位用：该色数下「最能达标」的变体画质（默认抖动版感知 PSNR 更高）。
                // 两个变体在这里都完成评估，后续挑选直接查表；精确取色调色板不走抖动，两变体同产物，只评一次。
                ImageQuality EvaluateBest(int colors)
                {
                    if (!o.EnableDithering) return EvaluateCached(colors, false).Quality;
                    var dithered = EvaluateCached(colors, true);
                    if (quantizer.IsExactPalette) return dithered.Quality;
                    EvaluateCached(colors, false);
                    return dithered.Quality;
                }

                hit = ImageOptimizer.SearchLadder(ladder, EvaluateBest, o.Accept);
            }
            else
            {
                // 二分天生串行（下一个评估哪档取决于上一个是否达标），无法直接并行。
                // 做法与 PNG 一致：并行评估全部候选，再用 ReplayLadderVisits 复刻串行二分**实际会访问**
                // 的候选集合，只把这一子集交给挑选逻辑 —— 产物与串行路径逐字节相同，多算的候选直接丢弃。
                var keys = new List<(int Colors, bool Dither)>(ladder.Length * variantCount);
                foreach (int c in ladder)
                {
                    if (o.EnableDithering)
                    {
                        keys.Add((c, true));
                        keys.Add((c, false));
                    }
                    else
                    {
                        keys.Add((c, false));
                    }
                }

                var evaluated = new Dictionary<(int Colors, bool Dither), (byte[] Bytes, ImageQuality Quality)>(keys.Count);
                var exactKeys = new HashSet<(int Colors, bool Dither)>();
                Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = degree }, i =>
                {
                    var key = keys[i];
                    var quantizer = new PaletteQuantizer();
                    int preparedColors = -1;
                    byte[] restored = new byte[(long)framePixels * sampleFrames * 4];
                    byte[] frameRgbaBuffer = new byte[(long)framePixels * 4];
                    var result = Evaluate(quantizer, ref preparedColors, restored, frameRgbaBuffer, key.Colors, key.Dither);
                    bool exact = quantizer.IsExactPalette;
                    lock (evaluated)
                    {
                        evaluated[key] = result;
                        if (exact) exactKeys.Add(key);
                    }
                });

                // 串行语义回放：二分用的画质固定取「抖动变体」（!EnableDithering 时就是唯一变体）
                bool AcceptAt(int colors) => o.Accept(evaluated[(colors, o.EnableDithering)].Quality);
                var visits = ImageOptimizer.ReplayLadderVisits(ladder.Length, i => AcceptAt(ladder[i]));
                hit = ImageOptimizer.SearchLadder(ladder, c => evaluated[(c, o.EnableDithering)].Quality, o.Accept);

                // 只保留串行路径会产出的那些 key（含 exact 时「两变体同产物、只记抖动那一份」的规则）
                var keep = new HashSet<(int Colors, bool Dither)>();
                foreach (int index in visits)
                {
                    int colors = ladder[index];
                    keep.Add((colors, o.EnableDithering));
                    if (o.EnableDithering && !exactKeys.Contains((colors, true))) keep.Add((colors, false));
                }
                foreach (var pair in evaluated)
                {
                    if (keep.Contains(pair.Key)) produced[pair.Key] = pair.Value;
                }
            }


            // 抖动的体积代价预算：抖动变体不得超过同色数无抖动变体的该倍数，
            // 否则视为「抖动收益抵不过 LZW 损失」，直接放弃抖动候选。
            const double MaxDitherCostRatio = 2.0;

            byte[]? bestBytes = null;
            ImageQuality bestQuality = default;
            int bestColors = 0;
            bool bestDither = false;

            // 按 (颜色数, 抖动) 排序保证平局时的选择确定（取色数更小、无抖动优先）
            foreach (var pair in produced.OrderBy(p => p.Key.Colors).ThenBy(p => p.Key.Dither))
            {
                var (colors, dither) = pair.Key;
                var (bytes, quality) = pair.Value;
                if (dither
                    && produced.TryGetValue((colors, false), out var noDither)
                    && bytes.Length > noDither.Bytes.Length * MaxDitherCostRatio)
                {
                    o.Log?.Invoke($"  跳过 {colors} 色 + 抖动：体积 {bytes.Length} 超过无抖动 {noDither.Bytes.Length} 的 {MaxDitherCostRatio:0.#} 倍预算");
                    continue;
                }

                if (hit >= 0)
                {
                    // 体积并不严格随颜色数单调（体积与画质都会有几个百分点的抖动），
                    // 因此在所有达标候选里按体积取最小。
                    if (!o.Accept(quality)) continue;
                    if (bestBytes != null && bytes.Length >= bestBytes.Length) continue;
                }
                else
                {
                    // 全部不达标时退回「画质最高」的候选，跨格式输出至少保证文件可用
                    if (bestBytes != null && quality.PerceptualPsnr <= bestQuality.PerceptualPsnr) continue;
                }

                bestBytes = bytes;
                bestQuality = quality;
                bestColors = colors;
                bestDither = dither;
            }

            if (hit >= 0 && bestBytes == null)
            {
                // 达标候选可能全被抖动预算拒绝（抖动达标但代价过高、无抖动不达标）：
                // 此时降级为「未达标」语义，在剩余候选里挑画质最高的，交由基线兜底。
                hit = -1;
                foreach (var pair in produced.OrderBy(p => p.Key.Colors).ThenBy(p => p.Key.Dither))
                {
                    var (colors, dither) = pair.Key;
                    var (bytes, quality) = pair.Value;
                    if (dither
                        && produced.TryGetValue((colors, false), out var noDither)
                        && bytes.Length > noDither.Bytes.Length * MaxDitherCostRatio)
                    {
                        continue;
                    }
                    if (bestBytes != null && quality.PerceptualPsnr <= bestQuality.PerceptualPsnr) continue;
                    bestBytes = bytes;
                    bestQuality = quality;
                    bestColors = colors;
                    bestDither = dither;
                }
            }

            // 单帧时补一条「常规编码」基线（Octree + Bayer8、256 色，与不加 --optimize 的转换完全同参）：
            // 任何候选若不比它小就是负收益 —— 用户加 --optimize 却得到更大的文件是不可接受的，直接退回基线。
            // 多帧动画不需要：同格式路径有 KeepOriginalWhenNoGain 兜底，不会写出更大的文件。
            if (!animated && bestBytes != null)
            {
                long mark = StageTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                byte[] baseline = EncodeBaseline(animation.Frames[0], reserveTransparent ? singleFrameRgba : null, width, height);
                if (StageTiming) _ticksBaseline += System.Diagnostics.Stopwatch.GetTimestamp() - mark;
                if (baseline.Length <= bestBytes.Length)
                {
                    o.Log?.Invoke($"  常规编码基线 {baseline.Length} 字节 ≤ 最优候选 {bestBytes.Length} 字节，退回基线");
                    // 基线几乎不会被采用（候选通常更小），因此它的画质评估推迟到真正采用的这条冷路径上，
                    // 省掉每次 optimize 都要付的一次解码 + 全图比较。
                    return MeasureBaseline(baseline, sample, width, height);
                }
            }

            if (bestBytes == null) return OptimizeArtifact.Unsupported("GIF 量化未产出可用候选");

            string method = $"GIF 调色板 {bestColors} 色" + (bestDither ? " + 抖动" : "") + (animated ? $"（{animation.Frames.Count} 帧）" : "");
            if (hit < 0) method += "（未达画质下限，取最高画质档）";
            return new OptimizeArtifact(bestBytes, method, bestQuality, hit >= 0);
        }

        /// <summary>
        /// 单帧「常规编码」基线：与不加 --optimize 的普通转换同参（八叉树量化 + Bayer 有序抖动 8、256 色）。
        /// 用于保证 optimize 的产物体积不会劣于普通转换。
        /// </summary>
        /// <returns>编码后的 GIF 字节；画质不在这一步评估（见 <see cref="MeasureBaseline"/>）</returns>
        private static byte[] EncodeBaseline(Image<Rgb24> frame, byte[]? rgba, int width, int height)
        {
            var encoder = new GifEncoder(); // 默认八叉树 + Bayer8、256 色，与普通转换同参
            using var ms = new MemoryStream();
            if (rgba != null) encoder.EncodeRgba(width, height, rgba, ms);
            else encoder.Encode(new ImageFrame(width, height, frame.Buffer), ms);
            return ms.ToArray();
        }

        /// <summary>
        /// 退回基线时才评估其画质（冷路径）。
        /// </summary>
        private static OptimizeArtifact MeasureBaseline(byte[] bytes, byte[] sample, int width, int height)
        {
            using var decodeStream = new MemoryStream(bytes);
            byte[] decoded = new GifDecoder().DecodeRgba32(decodeStream).Buffer;
            var quality = QualityMetrics.Compare(sample, decoded, width, height, 4);
            return new OptimizeArtifact(
                bytes,
                "GIF 常规 256 色（优化无额外收益，退回普通编码）",
                quality,
                true);
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
            // 单帧（跨格式转换的常态）：索引缓冲本身就是完整的「采样序列」，无需再复制一遍
            if (frames.Count == 1 && sampleFrames == 1) return frames[0];

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
