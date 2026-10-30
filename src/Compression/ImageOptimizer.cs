using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 智能有损压缩（TinyPNG 式）的入口：输入任意受支持格式的图片，
    /// 在保证「人眼看不出差别」的前提下输出体积尽可能小的同格式文件。
    /// </summary>
    public static class ImageOptimizer
    {
        /// <summary>
        /// 优化单张图片。
        /// </summary>
        /// <param name="inputPath">输入文件路径</param>
        /// <param name="outputPath">输出文件路径；为 null 时自动生成（原名后加 .min）</param>
        /// <param name="options">优化选项，null 表示均衡档</param>
        /// <returns>优化结果（体积、画质、采用方案）</returns>
        /// <remarks>
        /// 输出路径的扩展名决定目标格式：
        /// 与源格式一致时按「同格式压缩」处理（无收益可保留原图）；
        /// 不一致时走「转换即最优」——直接按目标格式做质量 / 调色板搜索，
        /// 一步产出最小体积文件，无需再单独 optimize 一遍。
        /// </remarks>
        public static OptimizationResult Optimize(string inputPath, string? outputPath = null, OptimizeOptions? options = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputPath);
            var o = OptimizeOptions.Normalize(options);
            if (!File.Exists(inputPath)) throw new FileNotFoundException("输入文件不存在", inputPath);

            long originalSize = new FileInfo(inputPath).Length;
            string output = string.IsNullOrEmpty(outputPath) ? BuildOutputPath(inputPath) : outputPath;
            string? outDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            var sourceKind = DetectFormat(inputPath);
            var targetKind = DetectKindByExtension(output);
            var traits = DetectSourceTraits(inputPath, sourceKind);

            // 内存预检：在解码/搜索之前按文件头宽高估算峰值。optimize 超预算直接拒绝，
            // 避免进入分配阶段后 OOM。跨格式转换没有“保留原格式”的退路，因此抛出明确异常
            // （CLI 会按文件捕获并继续处理其余文件）。
            var memory = MemoryGuard.CheckOptimize(sourceKind, inputPath, o);

            // 目标扩展名能识别、且与源格式不同 → 一步完成「转换 + 智能压缩」
            if (targetKind != ImageFormatKind.Unknown && targetKind != sourceKind)
            {
                if (memory.Action == MemoryGuard.Action.Refuse)
                {
                    throw new InsufficientMemoryException(memory.Reason);
                }
                return OptimizeConverted(inputPath, output, targetKind, originalSize, o, traits.ConvertNote);
            }

            // 16-bit / 动画输入：同格式 optimize 会把位深截断或只保留首帧，
            // 这属于静默丢数据，宁可保留原图并把原因写清楚。
            if (traits.SameFormatReason is { } reason)
            {
                return Finish(inputPath, output, originalSize, null, null, default, o, reason);
            }

            if (memory.Action == MemoryGuard.Action.Refuse)
            {
                return Finish(inputPath, output, originalSize, null, null, default, o, memory.Reason);
            }

            switch (sourceKind)
            {
                case ImageFormatKind.Png:
                    return PngOptimizer.Run(inputPath, output, originalSize, o);
                case ImageFormatKind.Jpeg:
                    return JpegOptimizer.Run(inputPath, output, originalSize, o);
                case ImageFormatKind.Gif:
                    return GifOptimizer.Run(inputPath, output, originalSize, o);
                case ImageFormatKind.Webp:
                    return WebpOptimizer.Run(inputPath, output, originalSize, o);
                case ImageFormatKind.Bmp:
                    return OptimizeBmp(inputPath, output, originalSize, o);
                default:
                    return Finish(inputPath, output, originalSize, null, null, default, o, "不支持的图片格式");
            }
        }

        /// <summary>
        /// 生成默认输出路径：原名后追加 ".min"，扩展名保持不变。
        /// </summary>
        /// <param name="inputPath">输入路径</param>
        public static string BuildOutputPath(string inputPath)
        {
            string dir = Path.GetDirectoryName(inputPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(inputPath);
            string ext = Path.GetExtension(inputPath);
            return Path.Combine(dir, name + ".min" + ext);
        }

        /// <summary>
        /// 先用文件头判断格式，失败再退回扩展名。
        /// </summary>
        /// <param name="path">文件路径</param>
        internal static ImageFormatKind DetectFormat(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> head = stackalloc byte[ImageHeaderReader.SniffBytes];
                int read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                var kind = ImageHeaderReader.Sniff(head[..read]);
                if (kind != ImageFormatKind.Unknown) return kind;
            }
            catch
            {
                // 读不到就走扩展名
            }

            return DetectKindByExtension(path);
        }

        /// <summary>
        /// 按扩展名判断目标格式（转换的落点以扩展名为准，不嗅探文件头）。
        /// </summary>
        /// <param name="path">输出路径</param>
        internal static ImageFormatKind DetectKindByExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormatKind.Jpeg,
            ".png" => ImageFormatKind.Png,
            ".gif" => ImageFormatKind.Gif,
            ".webp" => ImageFormatKind.Webp,
            ".bmp" => ImageFormatKind.Bmp,
            _ => ImageFormatKind.Unknown,
        };

        /// <summary>
        /// 源文件在解码时会丢失、但 optimize 无法无损保留的特征（16-bit、动画）。
        /// </summary>
        internal readonly record struct SourceTraits(bool Is16Bit, bool IsAnimated)
        {
            /// <summary>同格式 optimize 是否应直接放弃（避免静默降级）。</summary>
            public bool BlocksSameFormat => Is16Bit || IsAnimated;

            /// <summary>同格式 optimize 放弃时给出的原因。</summary>
            public string? SameFormatReason => this switch
            {
                { IsAnimated: true } => "源为动画（APNG / 动画 WebP），重编码会只保留首帧，已保留原图",
                { Is16Bit: true } => "源为 16-bit PNG，重编码会降为 8-bit，已保留原图",
                _ => null,
            };

            /// <summary>跨格式转换时追加到方案描述里的提示。</summary>
            public string? ConvertNote => this switch
            {
                { IsAnimated: true } => "（源为动画，输出仅保留首帧）",
                { Is16Bit: true } => "（源为 16-bit，输出已降为 8-bit）",
                _ => null,
            };
        }

        /// <summary>
        /// 读取文件头/关键块，判断源文件是否有 16-bit 或动画特征。
        /// </summary>
        /// <remarks>
        /// 只读块头、按长度跳过块数据，不会把整个 PNG 读进内存；
        /// APNG 的 <c>acTL</c> 规范要求出现在第一个 <c>IDAT</c> 之前，扫到 IDAT 即可停。
        /// WebP 的动画标志在 <c>VP8X</c> 的 flags 字节 bit1，另有 <c>ANIM</c>/<c>ANMF</c> 块可兜底。
        /// </remarks>
        internal static SourceTraits DetectSourceTraits(string path, ImageFormatKind kind)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return kind switch
                {
                    ImageFormatKind.Png => DetectPngTraits(fs),
                    ImageFormatKind.Webp => DetectWebpTraits(fs),
                    _ => default,
                };
            }
            catch
            {
                // 读不到就当作普通文件，后续解码会给出更准确的错误
                return default;
            }
        }

        private static SourceTraits DetectPngTraits(Stream stream)
        {
            Span<byte> head = stackalloc byte[8];
            stream.ReadExactly(head);
            if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47)
            {
                return default;
            }

            bool is16Bit = false;
            Span<byte> chunkHeader = stackalloc byte[8];
            while (TryReadExactly(stream, chunkHeader))
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
                ReadOnlySpan<byte> type = chunkHeader.Slice(4, 4);

                if (type.SequenceEqual("IHDR"u8))
                {
                    Span<byte> ihdr = stackalloc byte[13];
                    stream.ReadExactly(ihdr);
                    is16Bit = ihdr[8] == 16;
                    // IHDR 数据后还有 4 字节 CRC
                    if (!Skip(stream, 4)) return new SourceTraits(is16Bit, false);
                }
                else if (type.SequenceEqual("acTL"u8))
                {
                    return new SourceTraits(is16Bit, true);
                }
                else if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                {
                    return new SourceTraits(is16Bit, false);
                }
                else if (!Skip(stream, (long)length + 4))
                {
                    return new SourceTraits(is16Bit, false);
                }
            }
            return new SourceTraits(is16Bit, false);
        }

        private static SourceTraits DetectWebpTraits(Stream stream)
        {
            Span<byte> head = stackalloc byte[12];
            stream.ReadExactly(head);
            if (head[0] != (byte)'R' || head[1] != (byte)'I' || head[2] != (byte)'F' || head[3] != (byte)'F'
                || head[8] != (byte)'W' || head[9] != (byte)'E' || head[10] != (byte)'B' || head[11] != (byte)'P')
            {
                return default;
            }

            Span<byte> chunk = stackalloc byte[8];
            while (TryReadExactly(stream, chunk))
            {
                ReadOnlySpan<byte> fourCc = chunk.Slice(0, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
                if (fourCc.SequenceEqual("VP8X"u8) && size >= 10)
                {
                    Span<byte> data = stackalloc byte[10];
                    stream.ReadExactly(data);
                    bool animated = (data[0] & 0x02) != 0;
                    return new SourceTraits(false, animated);
                }
                if (fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8))
                {
                    return new SourceTraits(false, true);
                }
                // 普通 VP8/VP8L 已确定不是动画；仍继续跳块只为稳妥地找到 VP8X
                if (!Skip(stream, (long)size + (size & 1) + 4))
                {
                    return default;
                }
            }
            return default;
        }

        private static bool TryReadExactly(Stream stream, Span<byte> buffer)
        {
            try
            {
                stream.ReadExactly(buffer);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        private static bool Skip(Stream stream, long bytes)
        {
            if (bytes < 0 || !stream.CanSeek) return false;
            long remaining = stream.Length - stream.Position;
            if (bytes > remaining) return false;
            stream.Seek(bytes, SeekOrigin.Current);
            return true;
        }

        /// <summary>
        /// 按选项处理元数据：默认只丢弃 EXIF，保留 ICC（影响色彩呈现）。
        /// </summary>
        /// <param name="metadata">原始元数据</param>
        /// <param name="strip">是否剥离</param>
        internal static ImageMetadata? PrepareMetadata(ImageMetadata? metadata, bool strip)
        {
            if (metadata == null || !strip) return metadata;
            if (metadata.IccProfile == null && metadata.IccProfileKind == IccProfileKind.Unknown) return null;
            return new ImageMetadata
            {
                IccProfile = metadata.IccProfile,
                IccProfileKind = metadata.IccProfileKind,
                Orientation = 1,
                HorizontalDpi = metadata.HorizontalDpi,
                VerticalDpi = metadata.VerticalDpi,
            };
        }

        /// <summary>
        /// 把「调色板 + 索引」还原成 RGBA，用于画质评估。
        /// </summary>
        /// <param name="result">量化结果</param>
        /// <param name="destination">输出缓冲区，长度 width*height*4</param>
        /// <remarks>
        /// 逐像素做「1 次 32 位查表 + 1 次 32 位写」，而不是 4 次字节查表 + 4 次字节写：
        /// 该函数在 optimize 搜索期要按候选数重复执行（GIF 是每个候选一次全图、PNG 同理），
        /// 143 MP 上省下的是两遍 4 通道随机访问的内存流量。
        /// </remarks>
        internal static void RestoreRgba(QuantizeResult result, Span<byte> destination)
        {
            byte[] palette = result.PaletteRgb;
            byte[]? alpha = result.PaletteAlpha;
            byte[] indices = result.Indices;
            int colors = Math.Min(palette.Length / 3, 256);

            // 调色板展开成「索引 → RGBA32（小端 R,G,B,A）」的打包表
            Span<uint> packed = stackalloc uint[256];
            for (int i = 0; i < colors; i++)
            {
                uint a = alpha != null ? alpha[i] : 255u;
                packed[i] = palette[i * 3]
                    | ((uint)palette[i * 3 + 1] << 8)
                    | ((uint)palette[i * 3 + 2] << 16)
                    | (a << 24);
            }

            if (BitConverter.IsLittleEndian)
            {
                var target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination);
                for (int p = 0; p < indices.Length; p++) target[p] = packed[indices[p]];
                return;
            }

            // 大端平台回退（当前支持的 x64 / arm64 都是小端，保留以防移植）
            for (int p = 0; p < indices.Length; p++)
            {
                uint v = packed[indices[p]];
                int o = p * 4;
                destination[o] = (byte)v;
                destination[o + 1] = (byte)(v >> 8);
                destination[o + 2] = (byte)(v >> 16);
                destination[o + 3] = (byte)(v >> 24);
            }
        }

        /// <summary>
        /// 调色板颜色数候选阶梯（升序）。
        /// </summary>
        /// <param name="maxColors">颜色上限</param>
        internal static int[] BuildColorLadder(int maxColors)
        {
            int[] full = [16, 32, 48, 64, 96, 128, 192, 256];
            var list = new List<int>(full.Length);
            foreach (int c in full)
            {
                if (c < maxColors) list.Add(c);
            }
            list.Add(maxColors);
            return list.ToArray();
        }

        /// <summary>
        /// 在升序候选阶梯上二分查找「满足画质下限的最小颜色数」。
        /// 颜色数越少体积越小、画质越差，故单调可二分。
        /// </summary>
        /// <param name="ladder">升序候选</param>
        /// <param name="evaluate">评估回调，返回候选画质</param>
        /// <param name="accept">达标判定</param>
        /// <returns>命中的候选下标；全部不满足时返回 -1</returns>
        internal static int SearchLadder(int[] ladder, Func<int, ImageQuality> evaluate, Func<ImageQuality, bool> accept)
        {
            var cache = new Dictionary<int, ImageQuality>(ladder.Length);
            ImageQuality Eval(int index)
            {
                if (cache.TryGetValue(index, out var cached)) return cached;
                var q = evaluate(ladder[index]);
                cache[index] = q;
                return q;
            }

            int lo = 0;
            int hi = ladder.Length - 1;
            if (!accept(Eval(hi))) return -1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (accept(Eval(mid))) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        /// <summary>
        /// optimize 搜索期「并行评估候选」的并行度上限。
        /// </summary>
        /// <remarks>
        /// 只在候选内编码是单线程的路径上用（PNG 的 deflate、WebP 的 libwebp）。
        /// JPEG 不适用：它的编码器内部已经是并行流水线，外层再并行只会超订。
        /// 按内存预算夹上限，避免目录批量时与外层并行度叠加爆内存。
        /// </remarks>
        /// <param name="candidateCount">候选个数</param>
        /// <param name="perCandidateBytes">单个候选在评估期间的峰值占用估计</param>
        /// <param name="memoryBudget">本次搜索允许的峰值内存预算；0 表示按运行时可用内存自动推导</param>
        internal static int CandidateParallelism(int candidateCount, long perCandidateBytes, long memoryBudget = 0)
        {
            if (candidateCount <= 1) return 1;
            long budget = memoryBudget > 0 ? memoryBudget : AutoCandidateMemoryBudget;
            long byMemory = Math.Max(1, budget / Math.Max(1, perCandidateBytes));
            return (int)Math.Max(1, Math.Min(candidateCount, Math.Min(Environment.ProcessorCount, byMemory)));
        }

        /// <summary>
        /// 候选并行的默认内存预算：取「当前可用内存」的 1/4，夹在 64 MB ~ 8 GB。
        /// </summary>
        /// <remarks>
        /// 原先固定 512 MB。大图上的单候选峰值是「索引 1 B/px + 还原缓冲 4 B/px」≈ 5 B/px，
        /// 143 MP 时约 716 MB ⇒ 512 MB / 716 MB = 0 被夹成 1，**并行度静默塌成 1、退化为串行**，
        /// 而调用方仍以为在并行（这就是 143 MP 照片 optimize 要 31 s 的另一半原因）。
        /// 之后改成「进程总量 / 4」，但 `TotalAvailableMemoryBytes` 是 GC 可用的**总量**
        /// （容器限额或物理内存），不是当前空闲量：目录批量同时处理多张图、或进程自己已占用
        /// 一两 GB 时，这个预算仍会高估可用内存并触发 OOM。
        /// 现在按「GC 可用总量 − 已提交给托管堆的部分」推导，且下界从 384 MB 降到 64 MB
        /// （小容器里 384 MB 的预算本身就可能超过真实空闲内存）。
        /// 仍然夹了 8 GB 上限，避免 64 GB 机器上 16 个候选同时铺开把 L3 打爆。
        /// </remarks>
        private static long AutoCandidateMemoryBudget
        {
            get
            {
                var info = GC.GetGCMemoryInfo();
                long total = info.TotalAvailableMemoryBytes;
                if (total <= 0) total = 2L << 30;
                long used = GC.GetTotalMemory(forceFullCollection: false);
                long free = total > used ? total - used : 0;
                return Math.Clamp(free / 4, 64L << 20, 8L << 30);
            }
        }

        /// <summary>
        /// 并行评估候选；遇到内存不足时自动退回串行重跑，避免整个 optimize 失败。
        /// </summary>
        /// <remarks>
        /// 候选并行是「按内存预算夹并行度」的第二道保险：预算基于瞬时可用内存，
        /// 而目录批量、外层并行度、以及原生缓冲（不进 GC 堆）都可能让真实峰值超出估算。
        /// 真 OOM 时把并行度降到 1 重跑同一批候选——每个候选的写区互不相交，重复计算是幂等的。
        /// </remarks>
        internal static void ForEachCandidate(int count, int degree, Action<int> body)
        {
            if (count <= 0) return;
            try
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) }, body);
            }
            catch (Exception ex) when (IsOutOfMemory(ex))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 1 }, body);
            }
        }

        private static bool IsOutOfMemory(Exception ex)
        {
            if (ex is OutOfMemoryException) return true;
            if (ex is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (inner is OutOfMemoryException) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 二分搜索「刚好达标」的最低质量，但只依赖画质分数 —— 不需要编码出字节流。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="SearchQuality"/> 同一套二分轨迹（含「最高档不达标就直接返回最高档」），
        /// 区别只是评估函数直接返回分数。JPEG 用它跳过熵编解码：
        /// 体积在搜索期从不参与决策，赢家最终还会完整重编一次。
        /// </remarks>
        internal static (int Quality, ImageQuality Score) SearchQualityByScore(
            Func<int, ImageQuality> evaluate,
            int minQuality,
            int maxQuality,
            Func<ImageQuality, bool> accept,
            Action<int, ImageQuality>? log)
        {
            var cache = new Dictionary<int, ImageQuality>(8);
            ImageQuality Eval(int q)
            {
                if (cache.TryGetValue(q, out var cached)) return cached;
                var score = evaluate(q);
                log?.Invoke(q, score);
                cache[q] = score;
                return score;
            }

            int lo = Math.Max(1, minQuality);
            int hi = Math.Min(100, maxQuality);
            if (!accept(Eval(hi))) return (hi, cache[hi]);

            // 边界多看 1~2 档：画质与体积都不是严格单调（量化表整数取整、Huffman 表切换），
            // 「最低达标 q」下面一档有时仍然达标且体积更小。评估成本很低（系数直通，不写比特流），
            // 命中就采用更低的 q。
            int probe = hi;
            for (int step = 1; step <= 2 && probe - 1 >= lo; step++)
            {
                probe--;
                if (accept(Eval(probe))) hi = probe;
            }

            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (accept(Eval(mid))) hi = mid;
                else lo = mid + 1;
            }
            return (lo, Eval(lo));
        }

        /// <summary>
        /// <see cref="SearchQuality"/> 的并行版：结论完全一致，只是把候选评估并行铺开。
        /// </summary>
        /// <remarks>
        /// 二分天生串行（下一个评估哪个取决于上一个是否达标），无法直接并行。
        /// 这里改成「粗网格并行扫一遍 → 在 (前一格, 命中格) 的开区间里全枚举并行扫一遍」：
        /// 因为画质随质量单调，两阶段找到的仍是**最低达标质量**，与二分同解。
        /// 网格点数取并行度，故两阶段的墙钟都只有一波编码。
        /// <para>
        /// <paramref name="degree"/> 为 1 时直接落回 <see cref="SearchQuality"/>（逐位等价）。
        /// </para>
        /// </remarks>
        /// <param name="encode">按质量编码，返回文件字节</param>
        /// <param name="decode">把文件字节解回像素</param>
        /// <param name="reference">参考像素</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="minQuality">质量下界</param>
        /// <param name="maxQuality">质量上界</param>
        /// <param name="accept">达标判定</param>
        /// <param name="log">日志委托</param>
        /// <param name="degree">并行度</param>
        /// <param name="bytesPerPixel">参考/解码像素的通道数：RGB 传 3，RGBA 传 4</param>
        internal static (int Quality, byte[] Bytes, ImageQuality Score) SearchQualityParallel(
            Func<int, byte[]> encode,
            Func<byte[], byte[]> decode,
            byte[] reference,
            int width,
            int height,
            int minQuality,
            int maxQuality,
            Func<ImageQuality, bool> accept,
            Action<string>? log,
            int degree,
            int bytesPerPixel = 3)
        {
            if (degree <= 1)
            {
                return SearchQuality(encode, decode, reference, width, height, minQuality, maxQuality, accept, log, bytesPerPixel);
            }

            int lo = Math.Max(1, minQuality);
            int hi = Math.Min(100, maxQuality);
            int span = hi - lo;
            var results = new Dictionary<int, (byte[] Bytes, ImageQuality Quality)>(span + 1);
            var sync = new object();

            void EvalBatch(List<int> qs, int dop)
            {
                var batch = new (int Q, byte[] Bytes, ImageQuality Quality)[qs.Count];
                try
                {
                    Parallel.For(0, qs.Count, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
                    {
                        int q = qs[i];
                        byte[] bytes = encode(q);
                        byte[] decoded = decode(bytes);
                        // 通道数必须与缓冲布局一致：WebP 解码出 RGBA，按 3 读会漏掉后 1/4、
                        // 且 RGB 权重落在错位的字节上（感知 PSNR 系统性虚高约 8 dB）。
                        var quality = QualityMetrics.Compare(reference, decoded, width, height, bytesPerPixel);
                        batch[i] = (q, bytes, quality);
                    });
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
                {
                    // 还原成单异常，保持调用方原有的异常类型与语义
                    ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                }

                lock (sync)
                {
                    foreach (var e in batch) results[e.Q] = (e.Bytes, e.Quality);
                }
            }

            // 阶段零：先探**下界**，再按需探上界；两者都只在需要时才编码。
            //   · 下界达标 → 直接收尾：它已是最低允许质量，任何二分轨迹都收敛到 lo，中间全是白算。
            //   · 下界不达标 → 必看最高档：最高档都达不到就得把最高档交出去，此时不该铺开网格
            //     （64 MP 噪声图实测：铺开会白编十几个候选，34 MB 输出，多花 30 s）。
            // 为什么不像从前那样把两端**并行**编：上界（q=95）比下界贵得多（143 MP m2 实测
            // 3.62 s vs 2.42 s），而下界达标时上界结果直接被丢弃 —— 墙钟被贵的那个占满，等于白付 1.2 s。
            // 串行的代价只落在「下界不达标」的图像上（多等一个下界编码），而照片类大图几乎都是下界达标。
            // 只在**大图**这么做：小图（<8 MP）一个候选只有几十 ms，多探一波反而净亏
            // （1.2 MP 实测 443 → 530 ms，+18%），且小图网格含两端、命中 lo 即返回，本来就是一波的事。
            bool probeBottomFirst = (long)width * height >= 8_000_000;
            if (probeBottomFirst && lo != hi)
            {
                EvalBatch(new List<int> { lo }, 1);
                if (accept(results[lo].Quality))
                {
                    EmitLog(log, results);
                    return (lo, results[lo].Bytes, results[lo].Quality);
                }
                EvalBatch(new List<int> { hi }, 1);
                if (!accept(results[hi].Quality))
                {
                    EmitLog(log, results);
                    return (hi, results[hi].Bytes, results[hi].Quality);
                }
            }

            // 阶段一：粗网格（两端必含）并行扫一遍，找第一个达标的网格点
            int gridCount = Math.Max(1, Math.Min(span + 1, degree));
            var grid = new List<int>(gridCount);
            if (gridCount == 1)
            {
                grid.Add(hi);
            }
            else
            {
                for (int k = 0; k < gridCount; k++)
                {
                    grid.Add(lo + (int)Math.Round((double)span * k / (gridCount - 1)));
                }
            }
            // 阶段零已算过的点（下界）不重复编码
            EvalBatch(grid.Where(q => !results.ContainsKey(q)).ToList(), degree);

            // 最高档不达标：按原语义直接把最高档交出去（hi 是网格最后一格，必定已评估过）
            if (!accept(results[hi].Quality))
            {
                EmitLog(log, results);
                return (hi, results[hi].Bytes, results[hi].Quality);
            }
            // 命中 lo（网格第一格）时，lo 就是答案，不必再枚举窗口。
            // 预探过下界的分支已在阶段零返回，这里只对「小图 / 下界不达标」两种情形生效。
            if (accept(results[grid[0]].Quality))
            {
                EmitLog(log, results);
                return (grid[0], results[grid[0]].Bytes, results[grid[0]].Quality);
            }

            // 第一个达标网格点：grid[0]（= lo）已知不达标，从 1 起找
            int foundIdx = 1;
            while (foundIdx < grid.Count && !accept(results[grid[foundIdx]].Quality)) foundIdx++;

            int qStar;
            if (foundIdx >= grid.Count)
            {
                // hi 必在网格里且已达标，理论上走不到这里，仅作兜底
                qStar = hi;
            }
            else
            {
                // 阶段二：在 (前一格, 命中格) 的开区间里全枚举
                int from = grid[foundIdx - 1] + 1;
                int to = grid[foundIdx] - 1;
                qStar = grid[foundIdx];
                if (from <= to)
                {
                    var window = new List<int>(to - from + 1);
                    for (int q = from; q <= to; q++) window.Add(q);
                    EvalBatch(window, degree);
                    for (int q = from; q <= to; q++)
                    {
                        if (accept(results[q].Quality)) { qStar = q; break; }
                    }
                }
            }

            EmitLog(log, results);
            return (qStar, results[qStar].Bytes, results[qStar].Quality);
        }

        /// <summary>
        /// 以某个“锚点质量”为起点搜索最低达标质量：先探锚点，再在需要的那一侧做小网格 + 二分。
        /// </summary>
        /// <remarks>
        /// WebP 的 q=40 是历史上的默认下界，绝大多数照片在 q40 已达标；从 q40 起步比从小下界
        /// （20）起步少走很多弯路，也不会因为 40 以下/以上的非单调点漏掉明显更优的档位。
        /// 锚点达标时只在 (min, anchor] 里找；锚点不达标时才看最高档，并在 (anchor, max] 里找。
        /// 小网格用于吃多核，二分用于把候选数压到 log2，避免 143 MP 上铺 19 个候选。
        /// </remarks>
        /// <param name="evaluate">按质量评估画质（内部会缓存，重复 q 不重复编码）</param>
        /// <param name="minQuality">质量下界</param>
        /// <param name="maxQuality">质量上界</param>
        /// <param name="anchorQuality">锚点质量（clamp 到 [min,max]）</param>
        /// <param name="accept">达标判定</param>
        /// <param name="degree">小网格的并行度</param>
        /// <param name="log">日志委托</param>
        internal static (int Quality, ImageQuality Score) SearchQualityAnchored(
            Func<int, ImageQuality> evaluate,
            int minQuality,
            int maxQuality,
            int anchorQuality,
            Func<ImageQuality, bool> accept,
            int degree,
            Action<int, ImageQuality>? log = null)
        {
            int lo = Math.Max(1, minQuality);
            int hi = Math.Min(100, maxQuality);
            if (lo >= hi) return (lo, evaluate(lo));

            var cache = new Dictionary<int, ImageQuality>(16);
            var sync = new object();
            ImageQuality Eval(int q)
            {
                lock (sync)
                {
                    if (cache.TryGetValue(q, out var cached)) return cached;
                }
                var score = evaluate(q);
                log?.Invoke(q, score);
                lock (sync) cache[q] = score;
                return score;
            }

            int anchor = Math.Clamp(anchorQuality, lo, hi);
            if (accept(Eval(anchor)))
            {
                // 锚点已达标：答案在 [lo, anchor]；先确认下界，不达标就得到左边界。
                if (anchor == lo || accept(Eval(lo))) return (lo, Eval(lo));
                hi = anchor;
            }
            else
            {
                // 锚点不达标：只有最高档可能达标。
                if (anchor == hi || !accept(Eval(hi))) return (hi, Eval(hi));
                lo = anchor;
            }

            // 小网格（不含两端）先并行扫一遍，把区间压到相邻两点之间。
            int interior = Math.Min(4, hi - lo - 1);
            if (interior > 0)
            {
                var probes = new int[interior];
                for (int i = 0; i < interior; i++)
                {
                    probes[i] = lo + (int)Math.Round((double)(hi - lo) * (i + 1) / (interior + 1));
                }
                try
                {
                    ForEachCandidate(probes.Length, degree, i => Eval(probes[i]));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
                }

                for (int i = 0; i < probes.Length; i++)
                {
                    int q = probes[i];
                    if (q <= lo || q >= hi) continue;
                    if (accept(Eval(q)))
                    {
                        hi = q;
                        break;
                    }
                    lo = q;
                }
            }

            // 二分收敛到最低达标 q（lo 已知不达标、hi 已知达标）。
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (accept(Eval(mid))) hi = mid;
                else lo = mid;
            }

            // 边界多看 1~2 档（与 SearchQualityByScore 同一理由）：低一档偶尔仍达标且体积更小。
            for (int step = 1; step <= 2 && hi - 1 > lo; step++)
            {
                if (accept(Eval(hi - 1))) hi--;
                else break;
            }
            return (hi, Eval(hi));
        }

        /// <summary>并行评估后补日志：按质量升序输出，保证多次运行的日志稳定可比。</summary>
        private static void EmitLog(Action<string>? log, Dictionary<int, (byte[] Bytes, ImageQuality Quality)> results)
        {
            if (log is null) return;
            foreach (int q in results.Keys.OrderBy(k => k))
            {
                log($"  候选质量 {q}: {results[q].Bytes.Length} 字节, {results[q].Quality}");
            }
        }

        /// <summary>
        /// 二分查找「满足画质下限的最低编码质量」（JPEG / WebP 用）。
        /// </summary>
        /// <param name="encode">按质量编码，返回文件字节</param>
        /// <param name="decode">把文件字节解回 RGB24</param>
        /// <param name="reference">参考 RGB24 像素</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="minQuality">质量下界</param>
        /// <param name="maxQuality">质量上界</param>
        /// <param name="accept">达标判定</param>
        /// <param name="log">日志委托</param>
        /// <param name="bytesPerPixel">参考/解码像素的通道数：RGB 传 3，RGBA 传 4</param>
        internal static (int Quality, byte[] Bytes, ImageQuality Score) SearchQuality(
            Func<int, byte[]> encode,
            Func<byte[], byte[]> decode,
            byte[] reference,
            int width,
            int height,
            int minQuality,
            int maxQuality,
            Func<ImageQuality, bool> accept,
            Action<string>? log,
            int bytesPerPixel = 3)
        {
            var cache = new Dictionary<int, (byte[] Bytes, ImageQuality Quality)>(8);
            (byte[] Bytes, ImageQuality Quality) Eval(int q)
            {
                if (cache.TryGetValue(q, out var cached)) return cached;
                byte[] bytes = encode(q);
                byte[] decoded = decode(bytes);
                var quality = QualityMetrics.Compare(reference, decoded, width, height, bytesPerPixel);
                log?.Invoke($"  候选质量 {q}: {bytes.Length} 字节, {quality}");
                var result = (bytes, quality);
                cache[q] = result;
                return result;
            }

            int lo = Math.Max(1, minQuality);
            int hi = Math.Min(100, maxQuality);

            // 先探下界：质量下界本身达标时，答案只可能是它（它已是允许范围里最小的质量），
            // 任何二分轨迹都会收敛到 lo，中间的档位全是白算。
            // 实测（143 MP 照片，q 下界 40 / 上界 95）：accept 在 q=40 就成立，
            // 旧轨迹评估 95→67→53→46→43→41→40 共 7 次，每次 = 编码 2.8 s + 解码 + 画质比较。
            var bottom = Eval(lo);
            if (accept(bottom.Quality) || lo == hi) return (lo, bottom.Bytes, bottom.Quality);

            var top = Eval(hi);
            if (!accept(top.Quality)) return (hi, top.Bytes, top.Quality);

            // 至此 lo 已知不达标、hi 已知达标，二分只需在开区间 (lo, hi) 里找最低达标质量。
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (accept(Eval(mid).Quality)) hi = mid;
                else lo = mid;
            }
            var best = Eval(hi);
            return (hi, best.Bytes, best.Quality);
        }

        /// <summary>
        /// optimize 路径写 PNG 时使用的 deflate 级别。
        /// </summary>
        /// <remarks>
        /// 原先按像素数在 <see cref="CompressionLevel.Optimal"/>（zlib 6）与
        /// <see cref="CompressionLevel.SmallestSize"/>（zlib 9）之间二选一（阈值 8 MP）。
        /// 4.19 MP 上实测：调色板索引数据 6→9 是 <b>7.4 倍时间换 2.61% 体积</b>
        /// （91.7 → 682.0 ms），真彩色无损重写是 10.4 倍换 6.64%（280.3 → 2905.3 ms）。
        /// 而 optimize 的搜索期要为**每个候选各付一次**：4 个候选累计 3.3 s，
        /// 占 PNG optimize 总耗时的 79%。
        /// 搜索期真正需要的只是「候选之间的体积排序」，级别 9 多换来的那 2%~7%
        /// 远小于候选之间的差距（相邻色档 10%~40%），故统一降到 Optimal。
        /// 该级别只影响 optimize，普通转换走 <c>PngWriter</c> 自己的默认值。
        /// </remarks>
        internal const CompressionLevel OptimizePngDeflateLevel = CompressionLevel.Optimal;

        /// <summary>
        /// 收尾：写出最优产物，或在无收益 / 不支持时复制原图。
        /// </summary>
        internal static OptimizationResult Finish(
            string inputPath,
            string outputPath,
            long originalSize,
            byte[]? bestBytes,
            string? method,
            ImageQuality quality,
            OptimizeOptions options,
            string? reason = null,
            double minGainRatio = 0)
        {
            bool samePath = string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase);

            if (bestBytes == null || reason != null)
            {
                if (!samePath) File.Copy(inputPath, outputPath, true);
                return new OptimizationResult
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    OriginalSize = originalSize,
                    OptimizedSize = originalSize,
                    KeptOriginal = true,
                    Reason = reason ?? "没有满足画质下限的方案",
                };
            }

            double ratio = (double)(originalSize - bestBytes.Length) / Math.Max(1, originalSize);
            double required = Math.Max(options.MinSavingRatio, minGainRatio);
            if (options.KeepOriginalWhenNoGain && ratio < required)
            {
                if (!samePath) File.Copy(inputPath, outputPath, true);
                string? keepReason = minGainRatio > options.MinSavingRatio && ratio >= options.MinSavingRatio
                    ? $"仅节省 {ratio:P1}，低于有损再压缩的最低收益 {minGainRatio:P0}，已保留原图"
                    : null;
                return new OptimizationResult
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    OriginalSize = originalSize,
                    OptimizedSize = originalSize,
                    KeptOriginal = true,
                    Reason = keepReason,
                };
            }

            File.WriteAllBytes(outputPath, bestBytes);
            return new OptimizationResult
            {
                InputPath = inputPath,
                OutputPath = outputPath,
                OriginalSize = originalSize,
                OptimizedSize = bestBytes.Length,
                Method = method ?? string.Empty,
                Quality = quality,
            };
        }

        /// <summary>
        /// 跨格式优化：源格式与目标格式不同，直接按目标格式产出最小体积文件。
        /// 因为用户显式要求转换，无论目标体积是否小于源文件都会写出目标格式。
        /// </summary>
        private static OptimizationResult OptimizeConverted(string input, string output, ImageFormatKind target, long originalSize, OptimizeOptions o, string? note = null)
        {
            // 解码一次，所有目标格式共用同一份像素；同时收集「更优目标格式」建议所需的统计。
            var rgba = Configuration.Default.LoadRgba32(input);
            bool hadAlpha = !SimdHelper.IsOpaqueRgba(rgba.Buffer);
            int uniqueColors = PaletteQuantizer.CountUniqueColors(rgba.Buffer, 4097);
            string? suggestion = SuggestTarget(target, rgba.Width, rgba.Height, hadAlpha, uniqueColors);
            string? alphaNote = hadAlpha && target is ImageFormatKind.Jpeg or ImageFormatKind.Bmp ? MatteNote(o) : null;

            OptimizationResult result = target switch
            {
                ImageFormatKind.Jpeg => JpegOptimizer.RunFromImage(ComposeRgb24(rgba, o, hadAlpha), input, output, originalSize, o),
                ImageFormatKind.Png => PngOptimizer.RunFromImage(rgba, input, output, originalSize, o),
                ImageFormatKind.Webp => WebpOptimizer.RunFromImage(rgba, input, output, originalSize, o),
                // GIF 支持单帧二值透明，交给 GifOptimizer 保留，不做背景合成。
                ImageFormatKind.Gif => GifOptimizer.RunFromImage(rgba, input, output, originalSize, o),
                ImageFormatKind.Bmp => FinishConverted(input, output, originalSize, ProduceBmp(ComposeRgb24(rgba, o, hadAlpha)), o),
                _ => throw new NotSupportedException($"不支持转换到 {target} 格式"),
            };
            return WithNote(result, note + alphaNote, suggestion);
        }

        /// <summary>
        /// 按 <see cref="OptimizeOptions.MatteColor"/> 把 alpha 合成到 RGB，
        /// 供 JPEG / BMP 这类不支持透明通道的目标格式使用。
        /// </summary>
        private static Image<Rgb24> LoadCompositedRgb24(string input, OptimizeOptions o, out bool hadAlpha)
        {
            var rgba = Configuration.Default.LoadRgba32(input);
            hadAlpha = !SimdHelper.IsOpaqueRgba(rgba.Buffer);
            return ComposeRgb24(rgba, o, hadAlpha);
        }

        private static Image<Rgb24> ComposeRgb24(Image<Rgba32> rgba, OptimizeOptions o, bool hadAlpha)
        {
            var rgb = new byte[(long)rgba.Width * rgba.Height * 3];
            if (hadAlpha) CompositeOnMatte(rgba.Buffer, rgb, o.MatteColor);
            else SimdHelper.PackRgbaToRgb(rgba.Buffer, rgb);
            return new Image<Rgb24>(rgba.Width, rgba.Height, rgb, rgba.Metadata);
        }

        /// <summary>
        /// 跨格式转换后的目标格式建议（启发式，不额外编码）。
        /// </summary>
        /// <remarks>
        /// 只提「方向明确」的建议，不确定时返回 null：
        /// 目标不支持 alpha 而源带透明；照片类内容转 PNG/GIF；颜色很少却转有损 JPEG；
        /// BMP 本身无压缩。建议文案里写清理由，避免变成“再猜一次格式”的噪声。
        /// 唯一色数用 4097 截断（照片很快命中上限，成本可忽略）。
        /// </remarks>
        private static string? SuggestTarget(ImageFormatKind target, int width, int height, bool hadAlpha, int uniqueColors)
        {
            bool photoLike = uniqueColors > 4096;
            return target switch
            {
                ImageFormatKind.Webp => null,
                ImageFormatKind.Jpeg when hadAlpha => "WebP 或 PNG（JPEG 不支持透明通道，当前已把 alpha 合成到背景）",
                ImageFormatKind.Bmp when hadAlpha => "PNG 或 WebP（BMP 无压缩且不支持透明，当前已合成背景）",
                ImageFormatKind.Bmp => "PNG（无损且体积通常只有 BMP 的 1/5~1/2）",
                ImageFormatKind.Gif when photoLike => "WebP（照片类图像 GIF 仅 256 色，有损 WebP 通常再小 30%~60%）",
                ImageFormatKind.Jpeg when !photoLike => "PNG 或 WebP（颜色数少、图形类内容，有损 JPEG 常常反而更大）",
                ImageFormatKind.Jpeg => "WebP（照片类图像通常比 JPEG 再小 25%~35%）",
                ImageFormatKind.Png when photoLike => "WebP（照片类图像有损 WebP 通常比 PNG 调色板小 30%~50%）",
                _ => null,
            };
        }

        private static void CompositeOnMatte(ReadOnlySpan<byte> rgba, Span<byte> rgb, (byte R, byte G, byte B) matte)
        {
            int pixels = rgba.Length / 4;
            for (int p = 0, i = 0, j = 0; p < pixels; p++, i += 4, j += 3)
            {
                int a = rgba[i + 3];
                if (a == 255)
                {
                    rgb[j] = rgba[i];
                    rgb[j + 1] = rgba[i + 1];
                    rgb[j + 2] = rgba[i + 2];
                    continue;
                }
                int ia = 255 - a;
                rgb[j] = (byte)((rgba[i] * a + matte.R * ia + 127) / 255);
                rgb[j + 1] = (byte)((rgba[i + 1] * a + matte.G * ia + 127) / 255);
                rgb[j + 2] = (byte)((rgba[i + 2] * a + matte.B * ia + 127) / 255);
            }
        }

        private static string MatteNote(OptimizeOptions o)
            => $"（透明通道已合成到背景 #{o.MatteColor.R:X2}{o.MatteColor.G:X2}{o.MatteColor.B:X2}）";

        /// <summary>
        /// 给跨格式结果追加“源特征已降级”的提示（16-bit / 动画）。
        /// </summary>
        private static OptimizationResult WithNote(OptimizationResult result, string? note, string? suggestion = null)
        {
            if (string.IsNullOrEmpty(note) && string.IsNullOrEmpty(suggestion)) return result;
            return new OptimizationResult
            {
                InputPath = result.InputPath,
                OutputPath = result.OutputPath,
                OriginalSize = result.OriginalSize,
                OptimizedSize = result.OptimizedSize,
                Method = string.IsNullOrEmpty(note) ? result.Method : result.Method + note,
                Quality = result.Quality,
                KeptOriginal = result.KeptOriginal,
                Converted = result.Converted,
                Reason = result.Reason,
                Suggestion = suggestion ?? result.Suggestion,
            };
        }

        /// <summary>
        /// 收尾（跨格式）：始终写出目标格式字节；没有可用产物时退回目标格式的普通编码。
        /// </summary>
        internal static OptimizationResult FinishConverted(
            string inputPath,
            string outputPath,
            long originalSize,
            OptimizeArtifact artifact,
            OptimizeOptions options)
        {
            byte[] bytes = artifact.Bytes ?? EncodePlain(inputPath, outputPath, options);
            File.WriteAllBytes(outputPath, bytes);

            // 跨格式时「无收益保留原图」没有意义（原格式 ≠ 目标格式），
            // 因此把无法优化的原因并入方案描述，而不是走 KeptOriginal。
            string method = artifact.Method
                ?? (artifact.Reason != null ? $"常规编码（{artifact.Reason}）" : "常规编码（未做有损搜索）");

            return new OptimizationResult
            {
                InputPath = inputPath,
                OutputPath = outputPath,
                OriginalSize = originalSize,
                OptimizedSize = bytes.Length,
                Method = method,
                Quality = artifact.Quality,
                Converted = true,
            };
        }

        /// <summary>
        /// BMP 无压缩：跨格式输出时按无损真彩色直写，没有进一步压缩空间。
        /// </summary>
        private static OptimizeArtifact ProduceBmp(Image<Rgb24> image)
        {
            using var ms = new MemoryStream();
            BmpWriter.Write24(ms, image.Width, image.Height, image.Buffer);
            return new OptimizeArtifact(
                ms.ToArray(),
                "BMP 无损（该格式无压缩）",
                new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0),
                true);
        }

        /// <summary>
        /// 兜底：按目标格式做一次普通编码（不做质量搜索），保证输出文件始终存在且可用。
        /// </summary>
        private static byte[] EncodePlain(string inputPath, string outputPath, OptimizeOptions options)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            using var ms = new MemoryStream();
            switch (ext)
            {
                case ".jpg" or ".jpeg":
                    JpegEncoder.Encode(LoadCompositedRgb24(inputPath, options, out _), ms,
                        new JpegEncoderOptions(90, subsample420: true, keepMetadata: false, enableDiagnostics: false));
                    break;
                case ".png":
                {
                    var img = Configuration.Default.LoadRgba32(inputPath);
                    if (IsOpaque(img.Buffer))
                    {
                        var rgb = new byte[img.Width * img.Height * 3];
                        SimdHelper.PackRgbaToRgb(img.Buffer, rgb);
                        PngWriter.Write(ms, img.Width, img.Height, rgb);
                    }
                    else
                    {
                        PngWriter.WriteRgba(ms, img.Width, img.Height, img.Buffer);
                    }
                    break;
                }
                case ".webp":
                    new WebpEncoderAdapterRgba { Quality = 90 }.EncodeRgba32(ms, Configuration.Default.LoadRgba32(inputPath));
                    break;
                case ".gif":
                    new GifEncoderAdapter().EncodeRgb24(ms, Configuration.Default.LoadRgb24(inputPath));
                    break;
                case ".bmp":
                {
                    var rgb = LoadCompositedRgb24(inputPath, options, out _);
                    BmpWriter.Write24(ms, rgb.Width, rgb.Height, rgb.Buffer);
                    break;
                }
                default:
                    throw new NotSupportedException($"不支持的目标格式: {ext}");
            }
            return ms.ToArray();
        }

        private static bool IsOpaque(byte[] rgba) => SimdHelper.IsOpaqueRgba(rgba);

        /// <summary>
        /// BMP 无法压缩，统一转成 PNG 走有损压缩（体积通常能降一个数量级）。
        /// </summary>
        private static OptimizationResult OptimizeBmp(string input, string output, long originalSize, OptimizeOptions o)
        {
            string pngOutput = Path.ChangeExtension(output, ".png");
            return PngOptimizer.Run(input, pngOutput, originalSize, o);
        }
    }
}
