using System;
using System.IO;
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
        /// A/B 开关：置 <c>SIC_JPEG_NOCACHE=1</c> 时搜索期退回「每候选重跑一遍 produce」，
        /// 用于同一份二进制内对照样本缓存的收益（跨二进制对比有 ±2% 系统偏差，见项目方法论）。
        /// </summary>
        private static readonly bool s_disableSampleCache =
            Environment.GetEnvironmentVariable("SIC_JPEG_NOCACHE") == "1";
        /// <summary>
        /// 同格式优化：读入源文件，按 JPEG 重编码为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgb24(input);
            var artifact = Produce(image, o);
            return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o, artifact.Reason);
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

            (bool Accepted, int Quality, byte[] Bytes, ImageQuality Score, bool Subsample420) TrySearch(bool subsample420)
            {
                byte[] Encode(int quality, bool optimizeHuffman)
                {
                    using var ms = new MemoryStream();
                    // 按图优化的 Huffman 表：多花约 2 倍编码时间（实测 1.9~2.4x）换 20%~40% 体积
                    // （第一遍只为统计符号分布，第二遍重跑整条管线再熵编码）；
                    // 对大片平坦区域的图（扫描件、截图、大留白照片）收益最大
                    JpegEncoder.Encode(image, ms, new JpegEncoderOptions(quality, subsample420, keepMetadata, false, optimizeHuffman));
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
                    if (s_disableSampleCache)
                    {
                        JpegEncoder.EncodeCoefficients(rgb, width, height, quality, subsample420, keepMetadata: false, metadata: null, sink);
                        return QualityMetrics.Compare(rgb, sink.Reconstruct(), width, height, 3);
                    }

                    if (!samples.Matches(width, height, subsample420))
                    {
                        samples.Capture(rgb, width, height, subsample420);
                    }
                    JpegEncoder.EncodeFromSamples(samples, width, height, subsample420, quality, sink);
                    byte[] decoded = sink.Reconstruct();
                    return QualityMetrics.Compare(rgb, decoded, width, height, 3);
                }

                if (o.JpegQuality.HasValue)
                {
                    int q = o.JpegQuality.Value;
                    var fixedScore = Score(q);
                    byte[] bytes = Encode(q, o.JpegOptimizeHuffman);
                    return (o.Accept(fixedScore), q, bytes, fixedScore, subsample420);
                }

                // 二分只关心「这一档够不够」，体积不参与任何决策（赢家最终一定会重新完整编码一次）。
                var (quality, s) = ImageOptimizer.SearchQualityByScore(
                    Score, o.JpegMinQuality, 95, o.Accept,
                    (q, score) => o.Log?.Invoke($"  候选质量 {q}: {score}"));

                byte[] best = Encode(quality, o.JpegOptimizeHuffman);
                return (o.Accept(s), quality, best, s, subsample420);
            }

            var result = TrySearch(true);
            if (!result.Accepted)
            {
                var fallback = TrySearch(false);
                if (fallback.Accepted || fallback.Score.PerceptualPsnr > result.Score.PerceptualPsnr)
                {
                    result = fallback;
                }
            }

            string chroma = result.Subsample420 ? "4:2:0" : "4:4:4";
            string method = result.Accepted
                ? $"JPEG 质量 {result.Quality}（{chroma}）"
                : $"JPEG 质量 {result.Quality}（{chroma}，未达画质下限，取最高档）";
            return new OptimizeArtifact(result.Bytes, method, result.Score, result.Accepted);
        }
    }
}
