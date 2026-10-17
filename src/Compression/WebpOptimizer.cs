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
        /// 搜索期的 libwebp <c>method</c>（便宜档，只用来给候选排序）。
        /// </summary>
        /// <remarks>
        /// method 2 相对 method 4 便宜一个数量级（4.19 MP / q80 单次编码 147.5 vs 490.9 ms，
        /// 体积比 1.045x），而搜索只需要「候选之间谁更小」的相对次序。
        /// 赢家确定后再按 <see cref="OutputMethod"/> 补编一次，产物不受影响。
        /// 实测（2048²，同一份二进制开关对照）：photo 源 1769 → 1191 ms、webp 源 1125 → 1076 ms、
        /// jpeg 源 1407 → 1208 ms，且三张图产物与「全程 method 4」**逐字节一致**（当时 OutputMethod 为 4；
        /// 换成 3 后同样的性质成立——补编用的是原始像素，与搜索期编码无关）。
        /// </remarks>
        private const int SearchMethod = 2;

        /// <summary>产出期的 libwebp <c>method</c>（正式档，决定最终体积/画质）。</summary>
        /// <remarks>
        /// 与 <c>WebpEncoderOptions</c> 的默认档保持一致（3）。libwebp 1.6.0 的 method 4 在 &gt;140 MP
        /// 有 4 倍悬崖且体积不比 m3 小（143 MP：m3 8.2 s / 1,597,784 B vs m4 32.3 s / 1,655,814 B），
        /// 超大图走 optimize 时 m4 会直接吃掉整个时间预算。
        /// 搜索期仍用 <see cref="SearchMethod"/>（m2）排序，赢家在此档补编一次。
        /// </remarks>
        private const int OutputMethod = 3;
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

            byte[] Encode(int q) => EncodeAt(q, SearchMethod);

            byte[] EncodeAt(int q, int method)
            {
                using var ms = new MemoryStream();
                // 搜索期多个候选会并发进入 libwebp。WebpCodec 默认走 EncodeLock（针对「线程安全未知」的
                // 保守闸门），那把锁会把候选并行**完全串行化**（实测 2405 → 2407 ms，×1.00，等于白并行），
                // 故这里显式声明 Parallel 绕过它。并发一致性由「逐质量串行基线 vs 高并发多轮 md5 比对」
                // 验证通过后才启用。
                var encoder = new WebpEncoderAdapterRgba
                {
                    Options = new WebpEncoderOptions(q, WebpConcurrencyStrategy.Parallel, method),
                };
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

            // 搜索下界取 JpegMinQuality（默认 40）。
            // 原先额外抬到 65，理由是「低质量区间省不了多少体积却会明显糊掉细节」；
            // 但 4.19 MP 实测 q=65 时感知 PSNR 已达标并被搜索选中，
            // 说明 40~64 从未被真正试过——不是搜索慢，是搜索够不到更小的解。
            // 达标判定（o.Accept）本身会拦住过低的 q，故放开下界是安全的。
            int minQuality = o.JpegMinQuality;
            // libwebp 编码是单线程的（thread_level=0），候选并行是这条路径唯一能吃到多核的杠杆。
            // 每次并发调用自身要拷贝一份输入（RGBA）并解码一次结果，故按像素数估峰值内存。
            int degree = ImageOptimizer.CandidateParallelism(
                96 - minQuality, (long)w * h * 8 + (4L << 20), memoryBudget: 384L << 20);
            var (quality, best, q) = ImageOptimizer.SearchQualityParallel(
                Encode, Decode, rgba, w, h, minQuality, 95, o.Accept, o.Log, degree);
            if (SearchMethod != OutputMethod)
            {
                // 搜索期用便宜档排序，赢家再补编一次到正式档
                best = EncodeAt(quality, OutputMethod);
            }
            return new OptimizeArtifact(best, $"WebP 质量 {quality}", q, o.Accept(q));
        }
    }
}
