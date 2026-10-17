using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 压缩前后的画质评估结果。
    /// </summary>
    /// <remarks>
    /// 调色板量化 + 抖动会把误差打散成高频颗粒：逐像素 PSNR 会掉得很厉害，
    /// 但人眼看到的（低频、面积平均后的）画面几乎不变。因此判定「看不看得出来」
    /// 统一使用 <see cref="PerceptualPsnr"/>（先做块状平均再比较），
    /// <see cref="Psnr"/> 只作为参考值输出。
    /// </remarks>
    public readonly struct ImageQuality
    {
        /// <summary>
        /// 构造画质评估结果。
        /// </summary>
        /// <param name="psnr">逐像素 PSNR（dB）</param>
        /// <param name="perceptualPsnr">块状平均后的 PSNR（dB）</param>
        /// <param name="meanError">平均绝对误差（0-255）</param>
        public ImageQuality(double psnr, double perceptualPsnr, double meanError)
        {
            Psnr = psnr;
            PerceptualPsnr = perceptualPsnr;
            MeanError = meanError;
        }

        /// <summary>
        /// 逐像素 PSNR（dB）。对抖动颗粒敏感，仅作参考。
        /// </summary>
        public double Psnr { get; }

        /// <summary>
        /// 4x4 块平均后的 PSNR（dB）。抹平抖动噪声，用于「人眼是否看得出」的判定。
        /// </summary>
        public double PerceptualPsnr { get; }

        /// <summary>
        /// 平均绝对误差（0-255），越小越好。
        /// </summary>
        public double MeanError { get; }

        /// <summary>
        /// 是否与原始像素完全一致。
        /// </summary>
        public bool IsLossless => Psnr >= MaxPsnr;

        /// <summary>
        /// PSNR 的上限（完全一致时的表示值）。
        /// </summary>
        public const double MaxPsnr = 99.0;

        /// <summary>
        /// 是否满足给定的感知质量下限。
        /// </summary>
        /// <param name="minPerceptualPsnr">感知 PSNR 下限（dB）</param>
        /// <param name="minRawPsnr">逐像素 PSNR 下限（dB），0 表示不限制</param>
        public bool Meets(double minPerceptualPsnr, double minRawPsnr = 0)
            => PerceptualPsnr >= minPerceptualPsnr && Psnr >= minRawPsnr;

        /// <inheritdoc />
        public override string ToString()
            => $"PSNR {Psnr:0.00}dB / 感知 {PerceptualPsnr:0.00}dB / 平均误差 {MeanError:0.00}";
    }

    /// <summary>
    /// 图像画质度量：逐像素 PSNR 与抗抖动的感知 PSNR。
    /// </summary>
    public static class QualityMetrics
    {
        /// <summary>
        /// 感知度量默认使用的块边长。
        /// </summary>
        public const int DefaultBlockSize = 4;

        private static readonly double[] s_weightsRgb = [0.299, 0.587, 0.114];
        private static readonly double[] s_weightsRgba = [0.2392, 0.4696, 0.0912, 0.2];

        /// <summary>
        /// 启用按带分片的像素下限。低于此值串行更快（并行调度与分片合并的固定开销摊不平）：
        /// 按 ~1.9 ns/px 算，4 MP 串行约 8 ms，24 核并行约 1 ms。
        /// </summary>
        private const long ParallelPixelThreshold = 4_000_000;

        /// <summary>
        /// 比较两幅同尺寸图像的差异。
        /// </summary>
        /// <param name="reference">参考图像像素（RGB24 或 RGBA32）</param>
        /// <param name="test">待评估图像像素，通道数与参考一致</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="bytesPerPixel">每像素字节数，取值 3（RGB）或 4（RGBA）</param>
        /// <param name="blockSize">感知度量的块边长，默认 4</param>
        /// <returns>画质评估结果</returns>
        public static ImageQuality Compare(
            ReadOnlySpan<byte> reference,
            ReadOnlySpan<byte> test,
            int width,
            int height,
            int bytesPerPixel,
            int blockSize = DefaultBlockSize)
            => CompareCore(reference, test, width, height, bytesPerPixel, blockSize, null, null, true);

        /// <summary>
        /// 数组重载：允许按带分片并行。
        /// </summary>
        /// <remarks>
        /// <see cref="ReadOnlySpan{T}"/> 不能跨线程捕获，所以并行路径只能从数组入口进来；
        /// 传 Span 的调用仍走串行实现，二者数值口径完全一致。
        /// </remarks>
        internal static ImageQuality Compare(
            byte[] reference,
            byte[] test,
            int width,
            int height,
            int bytesPerPixel,
            int blockSize = DefaultBlockSize)
            => CompareCore(reference, test, width, height, bytesPerPixel, blockSize, reference, test, true);

        /// <summary>
        /// 数组重载（可关闭内层并行）。
        /// </summary>
        /// <param name="allowInnerParallel">
        /// false 时强制串行累加。调用方本身已经在「按候选并行」时用（GIF / PNG / WebP 的 optimize
        /// 搜索期会有 8~16 个候选同时跑），内层再按核数展开只会超订：核数² 个线程互相抢占，
        /// 每条带的 cache 局部性也被打散。
        /// </param>
        internal static ImageQuality Compare(
            byte[] reference,
            byte[] test,
            int width,
            int height,
            int bytesPerPixel,
            bool allowInnerParallel,
            int blockSize = DefaultBlockSize)
            => CompareCore(reference, test, width, height, bytesPerPixel, blockSize, reference, test, allowInnerParallel);

        private static ImageQuality CompareCore(
            ReadOnlySpan<byte> reference,
            ReadOnlySpan<byte> test,
            int width,
            int height,
            int bytesPerPixel,
            int blockSize,
            byte[]? refArray,
            byte[]? testArray,
            bool allowInnerParallel)
        {
            if (bytesPerPixel is not (3 or 4))
            {
                throw new ArgumentOutOfRangeException(nameof(bytesPerPixel), "仅支持 3（RGB）或 4（RGBA）通道");
            }
            int expected = checked(width * height * bytesPerPixel);
            if (reference.Length < expected || test.Length < expected)
            {
                throw new ArgumentException("像素缓冲区长度与宽高不匹配");
            }
            if (blockSize < 1) blockSize = DefaultBlockSize;

            int channels = bytesPerPixel;
            double[] weights = channels == 4 ? s_weightsRgba : s_weightsRgb;

            reference = reference.Slice(0, expected);
            test = test.Slice(0, expected);

            int rowBytes = width * channels;
            int bandCount = (height + blockSize - 1) / blockSize;

            // 按「带」累加：一带 = blockSize 行，4×4 块不会跨带。
            // 分片因此只是把若干整带分给不同线程，分片内部的累加顺序与串行版逐项相同。
            void Accumulate(ReadOnlySpan<byte> refSpan, ReadOnlySpan<byte> testSpan, int bandStart, int bandEnd, ref double rawSquared, ref double absSum, ref double blockSquared, ref long blockCount)
            {
                Span<double> refSum = stackalloc double[4];
                Span<double> testSum = stackalloc double[4];

                for (int b = bandStart; b < bandEnd; b++)
                {
                    int by = b * blockSize;
                    int yEnd = Math.Min(by + blockSize, height);
                    for (int bx = 0; bx < width; bx += blockSize)
                    {
                        int xEnd = Math.Min(bx + blockSize, width);
                        for (int c = 0; c < channels; c++)
                        {
                            refSum[c] = 0;
                            testSum[c] = 0;
                        }

                        int n = 0;
                        for (int y = by; y < yEnd; y++)
                        {
                            int rowStart = y * rowBytes;
                            for (int x = bx; x < xEnd; x++)
                            {
                                int i = rowStart + x * channels;
                                n++;
                                for (int c = 0; c < channels; c++)
                                {
                                    int rv = refSpan[i + c];
                                    int tv = testSpan[i + c];
                                    refSum[c] += rv;
                                    testSum[c] += tv;
                                    int d = rv - tv;
                                    rawSquared += weights[c] * d * d;
                                    absSum += weights[c] * (d < 0 ? -d : d);
                                }
                            }
                        }

                        if (n == 0) continue;
                        double inv = 1.0 / n;
                        for (int c = 0; c < channels; c++)
                        {
                            double d = (refSum[c] - testSum[c]) * inv;
                            blockSquared += weights[c] * d * d;
                        }
                        blockCount++;
                    }
                }
            }

            double rawSquared = 0;
            double absSum = 0;
            double blockSquared = 0;
            long blockCount = 0;

            // 大图按带分片：143 MP 上这是 optimize 搜索期的头号开销（标量单线程 ≈ 1.9 ns/px）。
            // 分片数固定为 min(核数, 带数)，合并顺序按分片下标 —— 同一台机器上每次结果完全一致，
            // 不会因为线程调度而让「刚好压线」的候选随机翻转。
            // Span 不能跨线程捕获，所以分片路径要求两个输入都坐在数组上（调用方传的都是 byte[]）。
            if (bandCount >= 2
                && allowInnerParallel
                && (long)width * height >= ParallelPixelThreshold
                && Environment.ProcessorCount > 1
                && refArray is not null
                && testArray is not null)
            {
                byte[] refBuffer = refArray;
                byte[] testBuffer = testArray;
                int dop = Math.Min(Environment.ProcessorCount, bandCount);
                int bandsPerChunk = (bandCount + dop - 1) / dop;
                var partial = new double[dop * 3];
                var partialBlocks = new long[dop];

                Parallel.For(0, dop, d =>
                {
                    int from = d * bandsPerChunk;
                    int to = Math.Min(from + bandsPerChunk, bandCount);
                    ReadOnlySpan<byte> localReference = refBuffer.AsSpan(0, expected);
                    ReadOnlySpan<byte> localTest = testBuffer.AsSpan(0, expected);
                    double r = 0, a = 0, s = 0;
                    long c = 0;
                    Accumulate(localReference, localTest, from, to, ref r, ref a, ref s, ref c);
                    partial[d * 3] = r;
                    partial[d * 3 + 1] = a;
                    partial[d * 3 + 2] = s;
                    partialBlocks[d] = c;
                });

                for (int d = 0; d < dop; d++)
                {
                    rawSquared += partial[d * 3];
                    absSum += partial[d * 3 + 1];
                    blockSquared += partial[d * 3 + 2];
                    blockCount += partialBlocks[d];
                }
            }
            else
            {
                Accumulate(reference, test, 0, bandCount, ref rawSquared, ref absSum, ref blockSquared, ref blockCount);
            }

            long pixels = (long)width * height;
            double rawMse = pixels > 0 ? rawSquared / pixels : 0;
            double blockMse = blockCount > 0 ? blockSquared / blockCount : 0;
            double meanError = pixels > 0 ? absSum / pixels : 0;

            return new ImageQuality(ToPsnr(rawMse), ToPsnr(blockMse), meanError);
        }

        /// <summary>
        /// 比较两幅图像在指定采样帧上的差异（用于动画，取最差的一帧作为代表）。
        /// </summary>
        /// <param name="referenceFrames">参考帧列表</param>
        /// <param name="testFrames">待评估帧列表</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="bytesPerPixel">每像素字节数</param>
        /// <param name="blockSize">感知度量的块边长</param>
        /// <returns>最差帧的画质评估结果</returns>
        public static ImageQuality CompareWorst(
            IReadOnlyList<byte[]> referenceFrames,
            IReadOnlyList<byte[]> testFrames,
            int width,
            int height,
            int bytesPerPixel,
            int blockSize = DefaultBlockSize)
        {
            ArgumentNullException.ThrowIfNull(referenceFrames);
            ArgumentNullException.ThrowIfNull(testFrames);
            if (referenceFrames.Count == 0) throw new ArgumentException("参考帧为空", nameof(referenceFrames));
            if (referenceFrames.Count != testFrames.Count) throw new ArgumentException("帧数不一致", nameof(testFrames));

            ImageQuality worst = new(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0);
            for (int i = 0; i < referenceFrames.Count; i++)
            {
                var q = Compare(referenceFrames[i], testFrames[i], width, height, bytesPerPixel, blockSize);
                if (q.PerceptualPsnr < worst.PerceptualPsnr) worst = q;
            }
            return worst;
        }

        /// <summary>
        /// 由 MSE 换算 PSNR（dB），完全一致时返回 <see cref="ImageQuality.MaxPsnr"/>。
        /// </summary>
        /// <param name="mse">均方误差</param>
        public static double ToPsnr(double mse)
        {
            if (mse <= 1e-12) return ImageQuality.MaxPsnr;
            double psnr = 10.0 * Math.Log10(65025.0 / mse);
            return psnr > ImageQuality.MaxPsnr ? ImageQuality.MaxPsnr : psnr;
        }
    }
}
