using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
            int bestColors = 0;
            bool bestDither = false;

            if (unique <= o.MaxColors)
            {
                quantizeOptions.MaxColors = o.MaxColors;
                var q = quantizer.QuantizeCore(rgba, width, height, quantizeOptions);
                bestBytes = Encode(q);
                ImageOptimizer.RestoreRgba(q, restored);
                bestQuality = QualityMetrics.Compare(rgba, restored, width, height, 4);
                bestMethod = $"PNG 调色板 {q.ColorCount} 色（无损）";
                bestColors = q.ColorCount;
                bestDither = false;
                o.Log?.Invoke($"  无损取色 {q.ColorCount} 色: {bestBytes.Length} 字节, {bestQuality}");
            }

            if (bestBytes == null)
            {
                int[] ladder = ImageOptimizer.BuildColorLadder(o.MaxColors);
                // 抖动是独立的搜索维度：同一色数下抖动会打散索引熵，LZW/deflate 命中率下降，
                // 体积可能差数倍（GIF 实测 92 KB vs 17 KB）。因此两个变体都参与画质判定与体积竞争。
                int variantCount = o.EnableDithering ? 2 : 1;
                bool[] variants = variantCount == 2 ? [true, false] : [false];
                var candidates = new (byte[] Bytes, int ColorCount, ImageQuality Quality, bool Dither)?[ladder.Length, variantCount];
                var pool = new ConcurrentQueue<byte[]>();
                long restoredBytes = (long)pixels * 4;
                int dop = ImageOptimizer.CandidateParallelism(ladder.Length * variantCount, (long)pixels * 5 + (1L << 20));

                // 候选按需编码并缓存：同一 (色数, 抖动) 只算一次
                (byte[] Bytes, int ColorCount, ImageQuality Quality, bool Dither) Get(int index, int variant) =>
                    candidates[index, variant] ??= EvaluateCandidate(
                        ladder[index], variants[variant], rgba, width, height, metadata, filter, level, o, pool, restoredBytes);

                bool AcceptAt(int index)
                {
                    for (int v = 0; v < variantCount; v++)
                    {
                        if (o.Accept(Get(index, v).Quality)) return true;
                    }
                    return false;
                }

                // 二分的第一步永远是最高档，先只算它。它不达标时（照片在 45 dB 感知门槛下
                // 几乎必然如此）整条阶梯都不会被访问 —— 原先「先并行铺满整条阶梯」在
                // 143 MP 照片上要白算 7 个候选（8 次编码里只用了 1 个），单次 optimize 因此多花 20 s。
                int topIndex = ladder.Length - 1;
                for (int v = 0; v < variantCount; v++) Get(topIndex, v);
                if (AcceptAt(topIndex))
                {
                    // 最高档达标：把整条固定网格全部评估一遍，再在所有达标候选里按体积取最小。
                    // 这里**不再复刻二分访问序列**：二分只是定位手段，既然候选已经/即将被编码，
                    // 多算出来的达标候选没有理由丢弃（体积与颜色数并不严格单调）。
                    // 固定网格（整条阶梯 × 抖动变体）保证结果与核数无关、可重复。
                    // 最高档不达标时仍然只算这一档：更低色数的画质只会更差。
                    for (int k = 0; k < dop; k++) pool.Enqueue(new byte[restoredBytes]);
                    var rest = new List<(int Index, int Variant)>(ladder.Length * variantCount);
                    for (int i = 0; i < ladder.Length; i++)
                    {
                        for (int v = 0; v < variantCount; v++)
                        {
                            if (candidates[i, v] != null) continue;
                            rest.Add((i, v));
                        }
                    }
                    if (rest.Count > 0)
                    {
                        EvaluateCandidates(rest, ladder, variants, candidates, rgba, width, height, metadata, filter, level, o, pool, restoredBytes, dop);
                    }
                    pool.Clear();

                    for (int i = 0; i < ladder.Length; i++)
                    {
                        for (int v = 0; v < variantCount; v++)
                        {
                            var c = Get(i, v);
                            o.Log?.Invoke($"  候选 {c.ColorCount} 色{(c.Dither ? " + 抖动" : "")}: {c.Bytes.Length} 字节, {c.Quality}");
                            if (!o.Accept(c.Quality)) continue;
                            // 体积并不严格随颜色数单调（中位切分的切法不同会有几个百分点的抖动），
                            // 因此在所有达标候选里按体积取最小；平局保留先出现的候选（固定顺序 ⇒ 确定）。
                            if (bestBytes != null && c.Bytes.Length >= bestBytes.Length) continue;
                            bestBytes = c.Bytes;
                            bestQuality = c.Quality;
                            bestMethod = $"PNG 调色板 {c.ColorCount} 色" + (c.Dither ? " + 抖动" : "");
                            bestColors = c.ColorCount;
                            bestDither = c.Dither;
                        }
                    }
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
                    bestColors = 0;
                }
            }

            // 赢家精修：按实际颜色数降到 1/2/4 位，并实测 None / Up / Adaptive 三种行滤波
            // （PNG 规范不推荐对索引图做滤波，但实测各有胜场），最后再试一次调色板重排。
            // 只对 ≤8 MP 或 ≤16 色的图做，避免大图多付几百毫秒~几秒的 deflate。
            if (bestBytes != null && bestColors > 0
                && ((long)width * height <= 8_000_000 || bestColors <= 16))
            {
                var winnerOptions = new QuantizeOptions { MaxColors = bestColors, EnableDithering = bestDither };
                var winner = new PaletteQuantizer().QuantizeCore(rgba, width, height, winnerOptions);
                byte minBits = winner.ColorCount <= 2 ? (byte)1 : winner.ColorCount <= 4 ? (byte)2 : winner.ColorCount <= 16 ? (byte)4 : (byte)8;
                Span<byte> bitChoices = stackalloc byte[minBits < 8 ? 2 : 1];
                bitChoices[0] = minBits;
                if (minBits < 8) bitChoices[1] = 8;
                PngFilterMode[] filters = o.AdaptiveFiltering
                    ? [PngFilterMode.None, PngFilterMode.Up, PngFilterMode.Adaptive]
                    : [PngFilterMode.None, PngFilterMode.Up];

                byte bestBits = 8;
                PngFilterMode bestFilter = filter;
                byte[]? bestPacked = null;
                foreach (byte bits in bitChoices)
                {
                    foreach (var f in filters)
                    {
                        using var ms = new MemoryStream();
                        PngWriter.WritePalette(ms, width, height, winner.Indices, winner.PaletteRgb, winner.PaletteAlpha,
                            metadata, f, level, bits);
                        byte[] packed = ms.ToArray();
                        if (bestPacked == null || packed.Length < bestPacked.Length)
                        {
                            bestPacked = packed;
                            bestBits = bits;
                            bestFilter = f;
                        }
                    }
                }

                if (bestPacked != null && bestPacked.Length < bestBytes.Length)
                {
                    o.Log?.Invoke($"  赢家精修: {bestBytes.Length} → {bestPacked.Length} 字节（{bestBits}-bit / {bestFilter}）");
                    bestBytes = bestPacked;
                    bestMethod += $"（{bestBits}-bit 索引, {bestFilter} 滤波）";
                }

                // 调色板重排：按使用频率降序排索引，常见色落到低位；对 deflate 通常有一点点收益。
                if (bestPacked != null)
                {
                    var reordered = ReorderPalette(winner);
                    using var ms = new MemoryStream();
                    PngWriter.WritePalette(ms, width, height, reordered.Indices, reordered.PaletteRgb, reordered.PaletteAlpha,
                        metadata, bestFilter, level, bestBits);
                    byte[] reorderedBytes = ms.ToArray();
                    if (reorderedBytes.Length < bestBytes.Length)
                    {
                        o.Log?.Invoke($"  调色板重排: {bestBytes.Length} → {reorderedBytes.Length} 字节");
                        bestBytes = reorderedBytes;
                        bestMethod += "（调色板重排）";
                    }
                }
            }

            // 搜索期的编码用的是 zlib 6（候选之间比体积够用），但赢家值得再压一遍：
            // zlib 9 的成本约为 6 的 7~10 倍，只对最终产物付一次。大图默认跳过（见选项注释）。
            if (bestBytes != null
                && o.PngMaxDeflateOnWinner
                && (long)width * height <= o.PngMaxDeflatePixelThreshold)
            {
                byte[] upgraded = PngWriter.RecompressIdat(bestBytes, CompressionLevel.SmallestSize);
                if (upgraded.Length < bestBytes.Length)
                {
                    o.Log?.Invoke($"  产物重压缩 zlib 9: {bestBytes.Length} → {upgraded.Length} 字节");
                    bestBytes = upgraded;
                }
            }

            // 灰度源：颜色类型 0 比调色板少一个 PLTE，且不经过索引映射；只对不透明图尝试。
            // 8-bit 灰度的唯一值最多 256 个，所以大多数情况下会先命中精确取色，这里再按体积竞争一次。
            if (bestBytes != null && IsOpaqueGray(rgba, pixels, out byte[] gray))
            {
                using var ms = new MemoryStream();
                PngWriter.WriteGray(ms, width, height, gray, metadata, PngFilterMode.Up, level);
                byte[] grayBytes = ms.ToArray();
                // 与调色板赢家同口径：都在 zlib 9 下比体积，否则灰色渐变会被 zlib 6 的差距误判。
                if (o.PngMaxDeflateOnWinner && (long)width * height <= o.PngMaxDeflatePixelThreshold)
                {
                    byte[] upgraded = PngWriter.RecompressIdat(grayBytes, CompressionLevel.SmallestSize);
                    if (upgraded.Length < grayBytes.Length) grayBytes = upgraded;
                }
                if (grayBytes.Length < bestBytes.Length)
                {
                    o.Log?.Invoke($"  灰度重写: {bestBytes.Length} → {grayBytes.Length} 字节");
                    bestBytes = grayBytes;
                    bestMethod = "PNG 灰度（无损）";
                    bestQuality = new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0);
                    bestColors = 0;
                }
            }

            // PNG 走到这里必然有产物（量化或无损重写），且无损重写一定达标
            bool meetsBar = bestQuality.IsLossless || o.Accept(bestQuality);
            return new OptimizeArtifact(bestBytes, bestMethod, bestQuality, meetsBar);
        }

        /// <summary>
        /// 评估一个颜色数候选：量化 → 编码 PNG → 还原像素算画质。
        /// </summary>
        private static (byte[] Bytes, int ColorCount, ImageQuality Quality, bool Dither) EvaluateCandidate(
            int colors,
            bool dithering,
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
            var quantizeOptions = new QuantizeOptions { EnableDithering = dithering, MaxColors = colors };
            var q = quantizer.QuantizeCore(rgba, width, height, quantizeOptions);

            using var ms = new MemoryStream();
            PngWriter.WritePalette(ms, width, height, q.Indices, q.PaletteRgb, q.PaletteAlpha, metadata, filter, level);
            byte[] bytes = ms.ToArray();

            if (!pool.TryDequeue(out var restored)) restored = new byte[restoredBytes];
            ImageOptimizer.RestoreRgba(q, restored);
            var quality = QualityMetrics.Compare(rgba, restored, width, height, 4);
            pool.Enqueue(restored);
            return (bytes, q.ColorCount, quality, dithering);
        }

        /// <summary>
        /// 并行评估指定的候选下标集合，结果写入 <paramref name="results"/> 的对应下标。
        /// </summary>
        /// <remarks>
        /// PNG 候选内的编码是 <c>DeflateStream</c>（单线程流式，且 PNG 规范把 IDAT 锁死在 deflate，
        /// 换不了算法），所以候选并行是这条路径上唯一能吃到多核的杠杆。
        /// </remarks>
        private static void EvaluateCandidates(
            List<(int Index, int Variant)> items,
            int[] ladder,
            bool[] variants,
            (byte[] Bytes, int ColorCount, ImageQuality Quality, bool Dither)?[,] results,
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
                // OOM 时自动退回串行重跑（见 ForEachCandidate 注释）。
                ImageOptimizer.ForEachCandidate(items.Count, dop, k =>
                {
                    var (i, v) = items[k];
                    results[i, v] = EvaluateCandidate(ladder[i], variants[v], rgba, width, height, metadata, filter, level, o, pool, restoredBytes);
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

        /// <summary>
        /// 按索引使用频率降序重排调色板，并同步重映射索引。
        /// </summary>
        private static QuantizeResult ReorderPalette(QuantizeResult q)
        {
            int colors = q.ColorCount;
            int[] freq = new int[colors];
            foreach (byte index in q.Indices) freq[index]++;

            int[] order = Enumerable.Range(0, colors)
                .OrderByDescending(i => freq[i])
                .ThenBy(i => i)
                .ToArray();

            bool identity = true;
            for (int i = 0; i < colors; i++)
            {
                if (order[i] != i) { identity = false; break; }
            }
            if (identity) return q;

            int[] map = new int[colors];
            for (int i = 0; i < colors; i++) map[order[i]] = i;

            byte[] indices = new byte[q.Indices.Length];
            for (int p = 0; p < indices.Length; p++) indices[p] = (byte)map[q.Indices[p]];

            byte[] palette = new byte[colors * 3];
            byte[]? alpha = q.PaletteAlpha != null ? new byte[colors] : null;
            for (int i = 0; i < colors; i++)
            {
                int src = order[i];
                palette[i * 3] = q.PaletteRgb[src * 3];
                palette[i * 3 + 1] = q.PaletteRgb[src * 3 + 1];
                palette[i * 3 + 2] = q.PaletteRgb[src * 3 + 2];
                if (alpha != null) alpha[i] = q.PaletteAlpha![src];
            }

            return new QuantizeResult
            {
                Indices = indices,
                PaletteRgb = palette,
                PaletteAlpha = alpha,
                ColorCount = colors,
            };
        }

        private static bool IsOpaqueGray(byte[] rgba, int pixels, out byte[] gray)
        {
            gray = [];
            for (int i = 0, p = 0; p < pixels; p++, i += 4)
            {
                if (rgba[i + 3] != 255 || rgba[i] != rgba[i + 1] || rgba[i] != rgba[i + 2])
                {
                    return false;
                }
            }
            // 先确认是灰度再分配：照片在第一个非灰像素就会退出，不该先付 pixels 字节的分配。
            gray = new byte[pixels];
            for (int i = 0, p = 0; p < pixels; p++, i += 4) gray[p] = rgba[i];
            return true;
        }

        private static byte[] PackRgb(byte[] rgba, int pixels)
        {
            var rgb = GC.AllocateUninitializedArray<byte>(pixels * 3);
            SimdHelper.PackRgbaToRgb(rgba.AsSpan(0, pixels * 4), rgb);
            return rgb;
        }
    }
}
