using System;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// WebP 智能压缩：算法本身已很高效，这里仍做一轮质量搜索，通常收益有限。
    /// 按 RGBA 编解码，保留透明通道。
    /// </summary>
    internal static class WebpOptimizer
    {
        /// <summary>
        /// 同格式优化：读入源文件，按 WebP 重编码为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgba32(input);
            var artifact = Produce(image, o);
            return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o, artifact.Reason);
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
            byte[] rgba = image.Buffer;
            var meta = ImageOptimizer.PrepareMetadata(image.Metadata, o.StripMetadata);

            byte[] Encode(int q)
            {
                using var ms = new MemoryStream();
                var encoder = new WebpEncoderAdapterRgba { Quality = q };
                encoder.EncodeRgba32(ms, new Image<Rgba32>(w, h, rgba, meta));
                return ms.ToArray();
            }

            byte[] Decode(byte[] bytes)
            {
                using var ms = new MemoryStream(bytes, false);
                return new WebpDecoderAdapterRgba().DecodeRgba32(ms).Buffer;
            }

            if (o.JpegQuality.HasValue)
            {
                byte[] bytes = Encode(o.JpegQuality.Value);
                var score = QualityMetrics.Compare(rgba, Decode(bytes), w, h, 4);
                return new OptimizeArtifact(bytes, $"WebP 质量 {o.JpegQuality.Value}", score, o.Accept(score));
            }

            // WebP 本身已很高效：低质量区间省不了多少体积，却会明显糊掉高频细节，
            // 因此搜索下界比 JPEG 高一截（块状平均的感知度量对「变糊」不够敏感）。
            int minQuality = Math.Max(o.JpegMinQuality, 65);
            var (quality, best, q) = ImageOptimizer.SearchQuality(Encode, Decode, rgba, w, h, minQuality, 95, o.Accept, o.Log);
            return new OptimizeArtifact(best, $"WebP 质量 {quality}", q, o.Accept(q));
        }
    }
}
