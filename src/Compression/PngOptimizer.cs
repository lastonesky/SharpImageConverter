using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// PNG 智能压缩：把图像量化到调色板（每像素 1 字节）后重写 PNG，
    /// 颜色数由画质下限二分搜索决定；量化不达标时回退为无损真彩色重写。
    /// </summary>
    internal static class PngOptimizer
    {
        /// <summary>
        /// 同格式优化：读入源文件，按 PNG 重写为最小体积。
        /// </summary>
        internal static OptimizationResult Run(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgba32(input);
            var artifact = Produce(image, originalSize, o);
            return ImageOptimizer.Finish(input, output, originalSize, artifact.Bytes, artifact.Method, artifact.Quality, o, artifact.Reason);
        }

        /// <summary>
        /// 跨格式优化：源图已解码为 RGBA32，直接按 PNG 产出最小体积文件。
        /// </summary>
        internal static OptimizationResult RunFromImage(Image<Rgba32> image, string input, string output, long originalSize, OptimizeOptions o)
        {
            var artifact = Produce(image, originalSize, o);
            return ImageOptimizer.FinishConverted(input, output, originalSize, artifact, o);
        }

        /// <summary>
        /// 计算最优 PNG 产物（不落盘）。
        /// </summary>
        /// <param name="image">源图（RGBA32）</param>
        /// <param name="originalSize">源文件字节数；用于判断量化收益是否值得</param>
        /// <param name="o">优化选项</param>
        internal static OptimizeArtifact Produce(Image<Rgba32> image, long originalSize, OptimizeOptions o)
        {
            int width = image.Width;
            int height = image.Height;
            int pixels = width * height;
            byte[] rgba = image.Buffer;
            var metadata = ImageOptimizer.PrepareMetadata(image.Metadata, o.StripMetadata);

            var filter = o.AdaptiveFiltering ? PngFilterMode.Adaptive : PngFilterMode.Up;
            // 搜索期每个候选都要完整编码一次，级别 9 在这里是 7~10 倍的时间换 2.6%~6.6% 体积，
            // 而候选之间的体积差距本身就有 10%~40%，故统一用 Optimal（详见该常量的注释）。
            var level = ImageOptimizer.OptimizePngDeflateLevel;

            // 无损取色短路用到的临时状态（只在那一支里用）
            var quantizer = new PaletteQuantizer();
            var quantizeOptions = new QuantizeOptions { EnableDithering = o.EnableDithering };
            byte[] restored = new byte[(long)pixels * 4];

            byte[] Encode(QuantizeResult q)
            {
                using var ms = new MemoryStream();
                PngWriter.WritePalette(ms, width, height, q.Indices, q.PaletteRgb, q.PaletteAlpha, metadata, filter, level);
                return ms.ToArray();
            }

            // 颜色本来就不多：优先无损取色，不再做有损尝试
            int unique = PaletteQuantizer.CountUniqueColors(rgba, o.MaxColors);
            byte[]? bestBytes = null;
            string? bestMethod = null;
            ImageQuality bestQuality = default;

            if (unique <= o.MaxColors)
            {
                quantizeOptions.MaxColors = o.MaxColors;
                var q = quantizer.QuantizeCore(rgba, width, height, quantizeOptions);
                bestBytes = Encode(q);
                ImageOptimizer.RestoreRgba(q, restored);
                bestQuality = QualityMetrics.Compare(rgba, restored, width, height, 4);
                bestMethod = $"PNG 调色板 {q.ColorCount} 色（无损）";
                o.Log?.Invoke($"  无损取色 {q.ColorCount} 色: {bestBytes.Length} 字节, {bestQuality}");
            }

            if (bestBytes == null)
            {
                int[] ladder = ImageOptimizer.BuildColorLadder(o.MaxColors);
                var candidates = new (byte[] Bytes, int ColorCount, ImageQuality Quality)?[ladder.Length];
                var pool = new ConcurrentQueue<byte[]>();
                long restoredBytes = (long)pixels * 4;
                int dop = ImageOptimizer.CandidateParallelism(ladder.Length, (long)pixels * 5 + (1L << 20));

                // 候选按需编码并缓存：同一下标只算一次
                (byte[] Bytes, int ColorCount, ImageQuality Quality) Get(int index) =>
                    candidates[index] ??= EvaluateCandidate(ladder[index], rgba, width, height, metadata, filter, level, o, pool, restoredBytes);

                // 二分的第一步永远是最高档，先只算它。它不达标时（照片在 45 dB 感知门槛下
                // 几乎必然如此）整条阶梯都不会被访问 —— 原先「先并行铺满整条阶梯」在
                // 143 MP 照片上要白算 7 个候选（8 次编码里只用了 1 个），单次 optimize 因此多花 20 s。
                var top = Get(ladder.Length - 1);
                if (o.Accept(top.Quality) && dop >= 2)
                {
                    // 达标才需要继续二分：把其余候选一次波并行铺满，墙钟约等于「最慢一个候选」。
                    // dop == 1 时留空，由 Get 在二分访问时惰性编码（与串行二分完全同序）。
                    for (int k = 0; k < dop; k++) pool.Enqueue(new byte[restoredBytes]);
                    var rest = new int[ladder.Length - 1];
                    for (int k = 0; k < rest.Length; k++) rest[k] = k;
                    EvaluateCandidates(rest, ladder, candidates, rgba, width, height, metadata, filter, level, o, pool, restoredBytes, dop);
                    pool.Clear();
                }

                // 仍按二分**实际会访问的序列**来挑结果，产物与串行二分逐个相同。
                foreach (int i in ImageOptimizer.ReplayLadderVisits(ladder.Length, i => o.Accept(Get(i).Quality)))
                {
                    var c = Get(i);
                    o.Log?.Invoke($"  候选 {c.ColorCount} 色: {c.Bytes.Length} 字节, {c.Quality}");
                    if (!o.Accept(c.Quality)) continue;
                    // 体积并不严格随颜色数单调（中位切分的切法不同会有几个百分点的抖动），
                    // 因此在所有达标候选里按体积取最小，而不是直接取颜色最少的那个。
                    if (bestBytes != null && c.Bytes.Length >= bestBytes.Length) continue;
                    bestBytes = c.Bytes;
                    bestQuality = c.Quality;
                    bestMethod = $"PNG 调色板 {c.ColorCount} 色" + (o.EnableDithering ? " + 抖动" : "");
                }
            }
            // 量化收益不明显（或压根没达标）时，再试一次无损真彩色重写：
            // 渐变类图像抖动后索引近乎随机，调色板反而会比真彩色大很多。
            bool needLossless = bestBytes == null || (originalSize - bestBytes.Length) < originalSize * 0.10;
            if (needLossless)
            {
                var lossless = EncodeLosslessSmallest(rgba, width, height, pixels, metadata, o.AdaptiveFiltering, level);
                if (bestBytes == null || lossless.Bytes.Length < bestBytes.Length)
                {
                    bestBytes = lossless.Bytes;
                    bestMethod = lossless.Filter == PngFilterMode.Adaptive
                        ? "PNG 无损重写（自适应滤波）"
                        : "PNG 无损重写（Up 滤波）";
                    bestQuality = new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0);
                }
            }

            // PNG 走到这里必然有产物（量化或无损重写），且无损重写一定达标
            bool meetsBar = bestQuality.IsLossless || o.Accept(bestQuality);
            return new OptimizeArtifact(bestBytes, bestMethod, bestQuality, meetsBar);
        }

        /// <summary>
        /// 评估一个颜色数候选：量化 → 编码 PNG → 还原像素算画质。
        /// </summary>
        private static (byte[] Bytes, int ColorCount, ImageQuality Quality) EvaluateCandidate(
            int colors,
            byte[] rgba,
            int width,
            int height,
            ImageMetadata? metadata,
            PngFilterMode filter,
            CompressionLevel level,
            OptimizeOptions o,
            ConcurrentQueue<byte[]> pool,
            long restoredBytes)
        {
            // 每个候选实例化自己的 PaletteQuantizer——它内部持有可变的装箱与 LUT 状态，不能跨候选共享。
            var quantizer = new PaletteQuantizer();
            var quantizeOptions = new QuantizeOptions { EnableDithering = o.EnableDithering, MaxColors = colors };
            var q = quantizer.QuantizeCore(rgba, width, height, quantizeOptions);

            using var ms = new MemoryStream();
            PngWriter.WritePalette(ms, width, height, q.Indices, q.PaletteRgb, q.PaletteAlpha, metadata, filter, level);
            byte[] bytes = ms.ToArray();

            if (!pool.TryDequeue(out var restored)) restored = new byte[restoredBytes];
            ImageOptimizer.RestoreRgba(q, restored);
            var quality = QualityMetrics.Compare(rgba, restored, width, height, 4);
            pool.Enqueue(restored);
            return (bytes, q.ColorCount, quality);
        }

        /// <summary>
        /// 并行评估指定的候选下标集合，结果写入 <paramref name="results"/> 的对应下标。
        /// </summary>
        /// <remarks>
        /// PNG 候选内的编码是 <c>DeflateStream</c>（单线程流式，且 PNG 规范把 IDAT 锁死在 deflate，
        /// 换不了算法），所以候选并行是这条路径上唯一能吃到多核的杠杆。
        /// </remarks>
        private static void EvaluateCandidates(
            int[] indices,
            int[] ladder,
            (byte[] Bytes, int ColorCount, ImageQuality Quality)?[] results,
            byte[] rgba,
            int width,
            int height,
            ImageMetadata? metadata,
            PngFilterMode filter,
            CompressionLevel level,
            OptimizeOptions o,
            ConcurrentQueue<byte[]> pool,
            long restoredBytes,
            int dop)
        {
            try
            {
                Parallel.For(0, indices.Length, new ParallelOptions { MaxDegreeOfParallelism = dop }, k =>
                {
                    int i = indices[k];
                    results[i] = EvaluateCandidate(ladder[i], rgba, width, height, metadata, filter, level, o, pool, restoredBytes);
                });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
        }

        /// <summary>
        /// 无损真彩色重写：固定 Up 与自适应滤波各编一遍，按**实际体积**取小者。
        /// </summary>
        /// <remarks>
        /// 自适应滤波是启发式（逐行挑「有符号绝对值和」最小的滤波器），它优化的是滤波后
        /// 字节的 L1 范数，而 deflate 真正吃的是**跨行重复**：整列统一 Up 时相邻行的差分
        /// 高度相似，LZ77 能跨行匹配整行；逐行换滤波器会把这条匹配链打断。
        /// 143 MP 照片实测（同一份像素、level 6）：Up 29,469,583 B / 自动选择 35,142,122 B，
        /// 启发式反而大 <b>19.2%</b>，且慢 2.4 倍（2040 ms → 4959 ms）——它 2972/3000 行都选了 Paeth。
        /// 而普通转换（<c>PngAdapter</c>）用的是 Up，所以「只试自适应」会让 <c>--optimize</c>
        /// 产出比不加该参数还大。这里改为两者都实测，只保留更小的那个。
        /// </remarks>
        private static (byte[] Bytes, PngFilterMode Filter) EncodeLosslessSmallest(
            byte[] rgba,
            int width,
            int height,
            int pixels,
            ImageMetadata? metadata,
            bool tryAdaptive,
            CompressionLevel level)
        {
            bool opaque = IsOpaque(rgba);
            byte[] pixels24 = opaque ? PackRgb(rgba, pixels) : rgba;

            byte[] Encode(PngFilterMode mode)
            {
                using var ms = new MemoryStream();
                if (opaque) PngWriter.Write(ms, width, height, pixels24, metadata, mode, level);
                else PngWriter.WriteRgba(ms, width, height, pixels24, metadata, mode, level);
                return ms.ToArray();
            }

            if (!tryAdaptive) return (Encode(PngFilterMode.Up), PngFilterMode.Up);

            // 两遍互不依赖，并行跑：墙钟等于较慢的那一遍（自适应约 5 s / 143 MP），
            // 而不是两者相加。内存只多一份几十 MB 的输出缓冲，源像素是共享只读的。
            byte[]? up = null, adaptive = null;
            Parallel.Invoke(
                () => up = Encode(PngFilterMode.Up),
                () => adaptive = Encode(PngFilterMode.Adaptive));
            return adaptive!.Length < up!.Length
                ? (adaptive, PngFilterMode.Adaptive)
                : (up, PngFilterMode.Up);
        }

        /// <summary>
        /// alpha 全 255 时可以直接写 RGB（颜色类型 2），省掉每像素重复 4 次的 alpha 字节。
        /// </summary>
        private static bool IsOpaque(byte[] rgba) => SimdHelper.IsOpaqueRgba(rgba);

        private static byte[] PackRgb(byte[] rgba, int pixels)
        {
            var rgb = GC.AllocateUninitializedArray<byte>(pixels * 3);
            SimdHelper.PackRgbaToRgb(rgba.AsSpan(0, pixels * 4), rgb);
            return rgb;
        }
    }
}
