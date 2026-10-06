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
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgb24(input);
            int width = image.Width;
            int height = image.Height;
            byte[] rgb = image.Buffer;
            bool keepMetadata = !o.StripMetadata;

            (bool Accepted, int Quality, byte[] Bytes, ImageQuality Score, bool Subsample420) TrySearch(bool subsample420)
            {
                byte[] Encode(int quality)
                {
                    using var ms = new MemoryStream();
                    // 按图优化的 Huffman 表：多花一倍编码时间换 20%~40% 体积，
                    // 对大片平坦区域的图（扫描件、截图、大留白照片）收益最大
                    JpegEncoder.Encode(image, ms, new JpegEncoderOptions(quality, subsample420, keepMetadata, false, o.JpegOptimizeHuffman));
                    return ms.ToArray();
                }

                byte[] Decode(byte[] bytes)
                {
                    using var ms = new MemoryStream(bytes, false);
                    return new JpegDecoderAdapter().DecodeRgb24(ms).Buffer;
                }

                if (o.JpegQuality.HasValue)
                {
                    int q = o.JpegQuality.Value;
                    byte[] bytes = Encode(q);
                    var score = QualityMetrics.Compare(rgb, Decode(bytes), width, height, 3);
                    return (o.Accept(score), q, bytes, score, subsample420);
                }

                var (quality, best, s) = ImageOptimizer.SearchQuality(
                    Encode, Decode, rgb, width, height, o.JpegMinQuality, 95, o.Accept, o.Log);
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
            return ImageOptimizer.Finish(input, output, originalSize, result.Bytes, method, result.Score, o);
        }
    }
}
