using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// WebP 智能压缩：算法本身已很高效，这里仍做一轮质量搜索，通常收益有限。
    /// 按 RGBA 编解码，保留透明通道；低色数/图形类图像额外尝试无损档。
    /// </summary>
    internal static class WebpOptimizer
    {
        /// <summary>
        /// 搜索期的 libwebp <c>method</c>（便宜档，只用来给候选排序）。
        /// </summary>
        /// <remarks>
        /// method 2 相对 method 4 便宜一个数量级（4.19 MP / q80 单次编码 147.5 vs 490.9 ms，
        /// 体积比 1.045x），而搜索只需要「候选之间谁更小」的相对次序。
        /// 赢家确定后再按产出档补编一次，产物不受搜索档影响。
        /// </remarks>
        private const int SearchMethod = 2;

        /// <summary>小图产出档：method 6 在 ≤20 MP 上体积最小，耗时只多约 5%~14%。</summary>
        private const int SmallImageMethod = 6;

        /// <summary>method 6 的像素上限；超过后 143 MP 上 m6 反而比 m3 大，且更慢。</summary>
        private const long SmallImagePixelThreshold = 20_000_000;

        /// <summary>按像素数选择产出档：小图 m6，大图 m3。</summary>
        private static int OutputMethodFor(long pixels)
            => pixels <= SmallImagePixelThreshold ? SmallImageMethod : 3;

        /// <summary>
        /// 同格式优化：读入源文件，按 WebP 重编码为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgba32(input);
            var artifact = Produce(image, o);
            return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o, artifact.Reason,
                minGainRatio: o.MinLossyGainRatio);
        }

        /// <summary>
        /// 跨格式优化：源图已解码为 RGBA32，直接按 WebP 产出最小体积文件。
        /// </summary>
        internal static OptimizationResult RunFromImage(Image<Rgba32> image, string input, string output, long originalSize, OptimizeOptions o)
        {
            var artifact = Produce(image, o);
            return ImageOptimizer.FinishConverted(input, output, originalSize, artifact, o);
        }

        /// <summary>
        /// 计算最优 WebP 产物（不落盘）。
        /// </summary>
        internal static OptimizeArtifact Produce(Image<Rgba32> image, OptimizeOptions o)
        {
            int w = image.Width;
            int h = image.Height;
            long pixels = (long)w * h;
            byte[] rgba = image.Buffer;
            var meta = ImageOptimizer.PrepareMetadata(image.Metadata, o.StripMetadata);
            int outputMethod = OutputMethodFor(pixels);

            // 分阶段耗时探针（SIC_WEBP_OPT_TIMING=1）：搜索期的编码 / 解码 / 产出期编码各花了多少。
            // 与 SIC_JPEG_STAGE_TIMING 同一套约定；计数用 Interlocked，因为候选是并行的。
            long searchEncodeTicks = 0, searchDecodeTicks = 0, finalEncodeTicks = 0;
            int searchCandidates = 0;

            byte[] EncodeCore(byte[] source, int sw, int sh, int q, int method, bool isFinal, bool lossless)
            {
                using var ms = new MemoryStream();
                // 搜索期多个候选会并发进入 libwebp。WebpCodec 默认走 EncodeLock（针对「线程安全未知」的
                // 保守闸门），那把锁会把候选并行**完全串行化**（实测 2405 → 2407 ms，×1.00，等于白并行），
                // 故这里显式声明 Parallel 绕过它。并发一致性由「逐质量串行基线 vs 高并发多轮 md5 比对」
                // 验证通过后才启用。
                var encoder = new WebpEncoderAdapterRgba
                {
                    Options = new WebpEncoderOptions(q, WebpConcurrencyStrategy.Parallel, method, lossless),
                };
                long t0 = TimingEnabled ? Stopwatch.GetTimestamp() : 0;
                encoder.EncodeRgba32(ms, new Image<Rgba32>(sw, sh, source, meta));
                if (TimingEnabled)
                {
                    long ticks = Stopwatch.GetTimestamp() - t0;
                    if (isFinal) Interlocked.Add(ref finalEncodeTicks, ticks);
                    else
                    {
                        Interlocked.Add(ref searchEncodeTicks, ticks);
                        Interlocked.Increment(ref searchCandidates);
                    }
                }
                return ms.ToArray();
            }

            byte[] Decode(byte[] bytes)
            {
                long t0 = TimingEnabled ? Stopwatch.GetTimestamp() : 0;
                using var ms = new MemoryStream(bytes, false);
                byte[] result = new WebpDecoderAdapterRgba().DecodeRgba32(ms).Buffer;
                if (TimingEnabled) Interlocked.Add(ref searchDecodeTicks, Stopwatch.GetTimestamp() - t0);
                return result;
            }

            if (o.JpegQuality.HasValue)
            {
                // 固定质量也必须走产出档 method：曾经这里传 SearchMethod，
                // 导致最终文件实际是 m2 编码，与设定的产出档不符。
                byte[] bytes = EncodeCore(rgba, w, h, o.JpegQuality.Value, outputMethod, isFinal: true, lossless: false);
                var score = QualityMetrics.Compare(rgba, Decode(bytes), w, h, 4);
                return new OptimizeArtifact(bytes, $"WebP 质量 {o.JpegQuality.Value}", score, o.Accept(score));
            }

            // 搜索下界默认 20（独立于 JPEG 的 40）。旧口径按 3 通道读 RGBA 时 q=40 几乎必然“达标”，
            // 4 通道口径下大图 q≈30 仍可能达标，q40 会比必要值多约 8%~14% 体积。
            // 但大图每多一个候选要 ~2.5 s；实测 143 MP 从 q40 降到 q34 省 7.8% 却多花 ~15 s，
            // 故 >20 MP 默认仍从 40 起搜，需要极限体积时显式把两个下界都设成 20。
            int minQuality = pixels > SmallImagePixelThreshold
                ? Math.Max(o.WebpMinQuality, o.WebpLargeImageMinQuality)
                : o.WebpMinQuality;

            // 搜索在**全图**上做：候选之间的画质差异必须与最终产物一致，降采样会改变 q 的估计
            // （实测 143 MP 上曾把 q34 估成 q52）。为了压候选数，用「q40 锚点 + 小网格 + 二分」：
            // 锚点达标时只在 (min,40] 里找，不达标才进 (40,95]；小网格吃多核，二分把剩余候选压到 log2。
            // 单候选峰值按全图估（编码输入拷贝 + 解码输出），避免并行度被夹成 1。
            int degree = ImageOptimizer.CandidateParallelism(
                96 - minQuality, pixels * 8 + (4L << 20));
            long searchStart = TimingEnabled ? Stopwatch.GetTimestamp() : 0;
            // 记录每个已评估候选的 m2 体积，供「全部已评估达标候选取最小体积」使用。
            // 候选集合由 SearchQualityAnchored 的固定锚点/网格/二分决定，与并行度无关。
            var searchResults = new ConcurrentDictionary<int, (int Bytes, ImageQuality Quality)>();
            var (searchedQuality, _) = ImageOptimizer.SearchQualityAnchored(
                q =>
                {
                    byte[] bytes = EncodeCore(rgba, w, h, q, SearchMethod, isFinal: false, lossless: false);
                    var score = QualityMetrics.Compare(rgba, Decode(bytes), w, h, 4);
                    searchResults[q] = (bytes.Length, score);
                    return score;
                },
                minQuality, 95, 40, o.Accept, degree,
                (q, score) => o.Log?.Invoke($"  候选质量 {q}: {score}"));

            int quality = PickSmallestAccepted(searchResults, searchedQuality, o.Accept);

            // 边界多看 1~2 档：体积与画质都不是严格单调，且搜索期的 m2 体积也会有几个百分点的反转。
            // 大图上每个候选 2~3 s，不值得；小图加深 2 个候选。
            if (pixels <= 8_000_000 && searchResults.ContainsKey(quality))
            {
                for (int step = 1; step <= 2; step++)
                {
                    int q = quality - step;
                    if (q < minQuality || searchResults.ContainsKey(q)) continue;
                    byte[] bytes = EncodeCore(rgba, w, h, q, SearchMethod, isFinal: false, lossless: false);
                    var score = QualityMetrics.Compare(rgba, Decode(bytes), w, h, 4);
                    searchResults[q] = (bytes.Length, score);
                    o.Log?.Invoke($"  边界候选质量 {q}: {bytes.Length} 字节, {score}");
                }
                quality = PickSmallestAccepted(searchResults, quality, o.Accept);
            }

            long searchTicks = TimingEnabled ? Stopwatch.GetTimestamp() - searchStart : 0;

            // 产出档与搜索档不同（m2 → m3/m6），用最终产物重新解码复评；不达标时 +2 重试。
            long finalStart = TimingEnabled ? Stopwatch.GetTimestamp() : 0;
            int usedMethod = outputMethod;
            byte[] best = EncodeCore(rgba, w, h, quality, usedMethod, isFinal: true, lossless: false);
            var finalScore = QualityMetrics.Compare(rgba, Decode(best), w, h, 4);

            // m6 与 m2/m3 的画质不完全一致：边界上可能出现「搜索 q 达标、m6 产物不达标」。
            // 先在同一 q 退回 m3 试一次（通常能过，且比升 q 更省体积），再进入升 q 重试。
            if (!o.Accept(finalScore) && usedMethod != 3)
            {
                usedMethod = 3;
                byte[] alt = EncodeCore(rgba, w, h, quality, usedMethod, isFinal: true, lossless: false);
                var altScore = QualityMetrics.Compare(rgba, Decode(alt), w, h, 4);
                if (o.Accept(altScore) || alt.Length < best.Length)
                {
                    best = alt;
                    finalScore = altScore;
                }
            }

            int retries = 0;
            while (!o.Accept(finalScore) && quality < 95 && retries < 4)
            {
                quality = Math.Min(95, quality + 2);
                best = EncodeCore(rgba, w, h, quality, usedMethod, isFinal: true, lossless: false);
                finalScore = QualityMetrics.Compare(rgba, Decode(best), w, h, 4);
                retries++;
            }

            // 低色数/图形类：无损 WebP 常常比有损 q40 更小。只对 ≤20 MP、不透明且唯一色数不多的图尝试，
            // 避免 143 MP 照片白付一次无损编码；透明像素的 RGB 在 WebP 无损入口下可能不被保留，
            // 因此带 alpha 的图暂不参与无损候选（仍走有损 + 4 通道画质判定）。
            bool lossless = false;
            if (o.WebpTryLossless && pixels <= o.WebpLosslessMaxPixels && SimdHelper.IsOpaqueRgba(rgba))
            {
                int unique = PaletteQuantizer.CountUniqueColors(rgba, o.WebpLosslessMaxUniqueColors);
                if (unique <= o.WebpLosslessMaxUniqueColors)
                {
                    byte[] losslessBytes = EncodeCore(rgba, w, h, 75, outputMethod, isFinal: true, lossless: true);
                    if (losslessBytes.Length < best.Length)
                    {
                        best = losslessBytes;
                        finalScore = new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0);
                        lossless = true;
                    }
                }
            }

            if (TimingEnabled)
            {
                ReportTiming(w, h, degree, quality, searchCandidates,
                    ToMs(searchTicks), ToMs(searchEncodeTicks), ToMs(searchDecodeTicks),
                    ToMs(finalEncodeTicks), ToMs(Stopwatch.GetTimestamp() - finalStart),
                    lossless ? outputMethod : usedMethod, lossless, retries);
            }

            string method = lossless ? "WebP 无损" : $"WebP 质量 {quality}";
            return new OptimizeArtifact(best, method, finalScore, o.Accept(finalScore));
        }

        /// <summary>
        /// 在已评估的达标候选里按体积取最小（m2 口径）；平局取更低的 q，保证结果确定。
        /// </summary>
        private static int PickSmallestAccepted(
            ConcurrentDictionary<int, (int Bytes, ImageQuality Quality)> results,
            int fallback,
            Func<ImageQuality, bool> accept)
        {
            int bestQ = fallback;
            int bestSize = int.MaxValue;
            foreach (var pair in results.OrderBy(p => p.Key))
            {
                if (!accept(pair.Value.Quality)) continue;
                if (pair.Value.Bytes < bestSize)
                {
                    bestSize = pair.Value.Bytes;
                    bestQ = pair.Key;
                }
            }
            return bestQ;
        }

        /// <summary>分阶段耗时探针开关：<c>SIC_WEBP_OPT_TIMING=1</c>（与 <c>SIC_JPEG_STAGE_TIMING</c> 同一约定）。</summary>
        private static readonly bool TimingEnabled =
            Environment.GetEnvironmentVariable("SIC_WEBP_OPT_TIMING") == "1";

        private static long ToMs(long ticks) => ticks * 1000 / Stopwatch.Frequency;

        private static void ReportTiming(
            int w, int h, int degree, int quality, int candidates,
            long searchMs, long searchEncodeMs, long searchDecodeMs, long finalEncodeMs, long finalStageMs,
            int outputMethod, bool lossless, int retries)
        {
            // 搜索期的编码/解码是**并发累加**（CPU 时间），故只报「每候选平均」，不拿它去减墙钟。
            long perEncode = candidates > 0 ? searchEncodeMs / candidates : 0;
            long perDecode = candidates > 0 ? searchDecodeMs / candidates : 0;
            Console.WriteLine(
                $"[webp-opt-timing] {w}x{h} ({w * (double)h / 1e6:F1} MP) degree={degree} -> q={quality}{(lossless ? " / lossless" : "")}\n" +
                $"  search {searchMs,7} ms 墙钟 / {candidates} 个候选，每候选 CPU：编码 {perEncode} + 解码 {perDecode} ms\n" +
                $"  output {finalStageMs,7} ms 墙钟 / method {outputMethod} 编码 {finalEncodeMs} ms / 复评重试 {retries} 次");
        }
    }
}
