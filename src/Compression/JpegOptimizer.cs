using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// JPEG 智能压缩：解码后重编码，用二分搜索找到「刚好满足画质下限」的最低质量，
    /// 顺带剥离 EXIF。默认 4:2:0 色度下采样；若下采样导致画质不达标（彩色文字、色度高频丰富的图），
    /// 自动退回 4:4:4 再搜一轮。
    /// </summary>
    internal static class JpegOptimizer
    {
        /// <summary>
        /// 同格式优化：读入源文件，按 JPEG 重编码为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgb24(input);
            var artifact = Produce(image, o);

            // 无损先算：Huffman 重排 + 去元数据，都不碰系数，像素逐字节不变。
            // 有损结果必须比「原图 / 无损产物中较小者」再小 MinLossyGainRatio 才值得损失一代画质。
            byte[] fileBytes = File.ReadAllBytes(input);
            byte[]? huffman = JpegHuffmanOptimizer.TryReencodeBaseline(fileBytes);
            byte[]? stripped = JpegLosslessOptimizer.StripMetadata(fileBytes);
            byte[]? lossless = huffman;
            string losslessMethod = "JPEG 无损重封装（Huffman 重排）";
            if (stripped != null && (lossless == null || stripped.Length < lossless.Length))
            {
                lossless = stripped;
                losslessMethod = "JPEG 无损重封装（去除 EXIF/注释）";
            }
            long baseline = Math.Min(originalSize, lossless?.Length ?? originalSize);
            bool lossyWins = artifact.MeetsBar
                && artifact.Bytes != null
                && baseline > 0
                && (double)(baseline - artifact.Bytes.Length) / baseline >= o.MinLossyGainRatio;

            if (lossyWins)
            {
                return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o);
            }
            if (lossless != null)
            {
                return ImageOptimizer.Finish(input, output, originalSize, lossless, losslessMethod,
                    new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0), o);
            }
            return ImageOptimizer.Finish(input, output, originalSize, null, null, default, o,
                $"有损重编码收益不足 {o.MinLossyGainRatio:P0} 或未达画质下限，已保留原图");
        }

        /// <summary>
        /// 跨格式优化：源图已解码为 RGB24，直接按 JPEG 产出最小体积文件。
        /// </summary>
        internal static OptimizationResult RunFromImage(Image<Rgb24> image, string input, string output, long originalSize, OptimizeOptions o)
        {
            var artifact = Produce(image, o);
            return ImageOptimizer.FinishConverted(input, output, originalSize, artifact, o);
        }

        /// <summary>
        /// 计算最优 JPEG 产物（不落盘）。
        /// </summary>
        internal static OptimizeArtifact Produce(Image<Rgb24> image, OptimizeOptions o)
        {
            int width = image.Width;
            int height = image.Height;
            byte[] rgb = image.Buffer;
            bool keepMetadata = !o.StripMetadata;

            // 搜索期的系数缓冲：4:2:0 与 4:4:4 的几何不同，Begin 会自动识别并切换。
            using var sink = new JpegCoefficientSink();
            // 采样后的 YCbCr 样本只跟源图和几何有关，与质量档无关：整幅采一次，各档共用。
            using var samples = new JpegSampleCache();

            (bool Accepted, int Quality, byte[] Bytes, ImageQuality Score, JpegChromaSubsampling Subsampling) TrySearch(JpegChromaSubsampling subsampling)
            {
                byte[] Encode(int quality, bool optimizeHuffman)
                {
                    using var ms = new MemoryStream();
                    // 按图优化的 Huffman 表：多花约 2 倍编码时间（实测 1.9~2.4x）换 20%~40% 体积
                    // （第一遍只为统计符号分布，第二遍重跑整条管线再熵编码）；
                    // 对大片平坦区域的图（扫描件、截图、大留白照片）收益最大
                    JpegEncoder.Encode(image, ms, new JpegEncoderOptions(quality, subsampling, keepMetadata, false, optimizeHuffman, o.JpegTrellisQuantization));
                    return ms.ToArray();
                }

                // 搜索期只跑到量化：系数直接交给重建，既不写也不读比特流。
                // 依据是 Huffman 只改变系数的编码形式、不改变系数本身（熵编解码严格无损），
                // 所以这里得到的像素与「编成字节流再熵解码」逐字节相同，
                // 但每个候选省掉了一趟「写位流 + 逐位读回」（143 MP 实测熵解码 192 ms）。
                // 再往上一层：produce（RGB→YCbCr + 下采样）与质量无关，143 MP 单次 186 ms
                // 占单候选 56%，采一次缓存后各档只做「量化 + 重建 + 评估」。
                ImageQuality Score(int quality)
                {
                    if (!samples.Matches(width, height, subsampling))
                    {
                        samples.Capture(rgb, width, height, subsampling);
                    }
                    JpegEncoder.EncodeFromSamples(samples, width, height, subsampling, quality, sink, o.JpegTrellisQuantization);
                    byte[] decoded = sink.Reconstruct();
                    return QualityMetrics.Compare(rgb, decoded, width, height, 3);
                }

                if (o.JpegQuality.HasValue)
                {
                    int q = o.JpegQuality.Value;
                    var fixedScore = Score(q);
                    byte[] bytes = Encode(q, o.JpegOptimizeHuffman);
                    return (o.Accept(fixedScore), q, bytes, fixedScore, subsampling);
                }

                // 二分只关心「这一档够不够」，体积不参与任何决策（赢家最终一定会重新完整编码一次）。
                var (quality, s) = ImageOptimizer.SearchQualityByScore(
                    Score, o.JpegMinQuality, 95, o.Accept,
                    (q, score) => o.Log?.Invoke($"  候选质量 {q}: {score}"));

                byte[] best = Encode(quality, o.JpegOptimizeHuffman);
                return (o.Accept(s), quality, best, s, subsampling);
            }

            var candidates = new List<(bool Accepted, int Quality, byte[] Bytes, ImageQuality Score, JpegChromaSubsampling Subsampling)>
            {
                TrySearch(JpegChromaSubsampling.Yuv420)
            };
            if (!candidates[0].Accepted || o.JpegCompareSubsampling)
            {
                candidates.Add(TrySearch(JpegChromaSubsampling.Yuv422));
                candidates.Add(TrySearch(JpegChromaSubsampling.Yuv444));
            }

            var accepted = candidates.Where(c => c.Accepted).OrderBy(c => c.Bytes.Length).ToList();
            var result = accepted.Count > 0
                ? accepted[0]
                : candidates.OrderByDescending(c => c.Score.PerceptualPsnr).First();

            byte[] best = result.Bytes;
            bool progressive = false;
            if (o.JpegProgressive && best.Length > 0)
            {
                byte[]? p = JpegProgressiveTranscoder.TryTranscode(best);
                if (p != null && p.Length < best.Length)
                {
                    best = p;
                    progressive = true;
                }
            }

            string chroma = result.Subsampling switch
            {
                JpegChromaSubsampling.Yuv420 => "4:2:0",
                JpegChromaSubsampling.Yuv422 => "4:2:2",
                _ => "4:4:4",
            };
            string method = result.Accepted
                ? $"JPEG 质量 {result.Quality}（{chroma}{(progressive ? " / progressive" : "")}）"
                : $"JPEG 质量 {result.Quality}（{chroma}，未达画质下限，取最高档{(progressive ? " / progressive" : "")}）";
            return new OptimizeArtifact(best, method, result.Score, result.Accepted);
        }
    }
}
