using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SharpImageConverter.Core;
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
        public static OptimizationResult Optimize(string inputPath, string? outputPath = null, OptimizeOptions? options = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputPath);
            var o = OptimizeOptions.Normalize(options);
            if (!File.Exists(inputPath)) throw new FileNotFoundException("输入文件不存在", inputPath);

            long originalSize = new FileInfo(inputPath).Length;
            string output = string.IsNullOrEmpty(outputPath) ? BuildOutputPath(inputPath) : outputPath;
            string? outDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            switch (DetectFormat(inputPath))
            {
                case ImageKind.Png:
                    return PngOptimizer.Run(inputPath, output, originalSize, o);
                case ImageKind.Jpeg:
                    return JpegOptimizer.Run(inputPath, output, originalSize, o);
                case ImageKind.Gif:
                    return GifOptimizer.Run(inputPath, output, originalSize, o);
                case ImageKind.Webp:
                    return OptimizeWebp(inputPath, output, originalSize, o);
                case ImageKind.Bmp:
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

        internal enum ImageKind
        {
            Unknown,
            Jpeg,
            Png,
            Gif,
            Webp,
            Bmp,
        }

        /// <summary>
        /// 先用文件头判断格式，失败再退回扩展名。
        /// </summary>
        /// <param name="path">文件路径</param>
        internal static ImageKind DetectFormat(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> head = stackalloc byte[16];
                int read = fs.Read(head);
                if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ImageKind.Jpeg;
                if (read >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ImageKind.Png;
                if (read >= 6 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F') return ImageKind.Gif;
                if (read >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                    && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return ImageKind.Webp;
                if (read >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M') return ImageKind.Bmp;
            }
            catch
            {
                // 读不到就走扩展名
            }

            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ImageKind.Jpeg,
                ".png" => ImageKind.Png,
                ".gif" => ImageKind.Gif,
                ".webp" => ImageKind.Webp,
                ".bmp" => ImageKind.Bmp,
                _ => ImageKind.Unknown,
            };
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
        internal static void RestoreRgba(QuantizeResult result, Span<byte> destination)
        {
            byte[] palette = result.PaletteRgb;
            byte[]? alpha = result.PaletteAlpha;
            byte[] indices = result.Indices;
            for (int p = 0; p < indices.Length; p++)
            {
                int i = indices[p] * 3;
                int o = p * 4;
                destination[o] = palette[i];
                destination[o + 1] = palette[i + 1];
                destination[o + 2] = palette[i + 2];
                destination[o + 3] = alpha != null ? alpha[indices[p]] : (byte)255;
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
        internal static (int Quality, byte[] Bytes, ImageQuality Score) SearchQuality(
            Func<int, byte[]> encode,
            Func<byte[], byte[]> decode,
            byte[] reference,
            int width,
            int height,
            int minQuality,
            int maxQuality,
            Func<ImageQuality, bool> accept,
            Action<string>? log)
        {
            var cache = new Dictionary<int, (byte[] Bytes, ImageQuality Quality)>(8);
            (byte[] Bytes, ImageQuality Quality) Eval(int q)
            {
                if (cache.TryGetValue(q, out var cached)) return cached;
                byte[] bytes = encode(q);
                byte[] decoded = decode(bytes);
                var quality = QualityMetrics.Compare(reference, decoded, width, height, 3);
                log?.Invoke($"  候选质量 {q}: {bytes.Length} 字节, {quality}");
                var result = (bytes, quality);
                cache[q] = result;
                return result;
            }

            int lo = Math.Max(1, minQuality);
            int hi = Math.Min(100, maxQuality);
            var top = Eval(hi);
            if (!accept(top.Quality)) return (hi, top.Bytes, top.Quality);

            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (accept(Eval(mid).Quality)) hi = mid;
                else lo = mid + 1;
            }
            var best = Eval(lo);
            return (lo, best.Bytes, best.Quality);
        }

        /// <summary>
        /// 大图用 Optimal、小图用 SmallestSize：级别 9 在大图上耗时增长明显而收益有限。
        /// </summary>
        /// <param name="pixelCount">像素总数</param>
        internal static CompressionLevel PickDeflateLevel(long pixelCount)
            => pixelCount > 8_000_000 ? CompressionLevel.Optimal : CompressionLevel.SmallestSize;

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
            string? reason = null)
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
            if (options.KeepOriginalWhenNoGain && ratio < options.MinSavingRatio)
            {
                if (!samePath) File.Copy(inputPath, outputPath, true);
                return new OptimizationResult
                {
                    InputPath = inputPath,
                    OutputPath = outputPath,
                    OriginalSize = originalSize,
                    OptimizedSize = originalSize,
                    KeptOriginal = true,
                    Reason = null,
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
        /// WebP：算法本身已很优秀，这里仍做一轮质量搜索，通常收益有限。
        /// </summary>
        private static OptimizationResult OptimizeWebp(string input, string output, long originalSize, OptimizeOptions o)
        {
            var image = Configuration.Default.LoadRgb24(input);
            int w = image.Width, h = image.Height;
            byte[] rgb = image.Buffer;
            var meta = PrepareMetadata(image.Metadata, o.StripMetadata);

            byte[] Encode(int q)
            {
                using var ms = new MemoryStream();
                var encoder = new WebpEncoderAdapter { Quality = q };
                encoder.EncodeRgb24(ms, new Image<Rgb24>(w, h, rgb, meta));
                return ms.ToArray();
            }

            byte[] Decode(byte[] bytes)
            {
                using var ms = new MemoryStream(bytes, false);
                return new WebpDecoderAdapter().DecodeRgb24(ms).Buffer;
            }

            if (o.JpegQuality.HasValue)
            {
                byte[] bytes = Encode(o.JpegQuality.Value);
                return Finish(input, output, originalSize, bytes, $"WebP 质量 {o.JpegQuality.Value}",
                    QualityMetrics.Compare(rgb, Decode(bytes), w, h, 3), o);
            }

            // WebP 本身已很高效：低质量区间省不了多少体积，却会明显糊掉高频细节，
            // 因此搜索下界比 JPEG 高一截（块状平均的感知度量对「变糊」不够敏感）。
            int minQuality = Math.Max(o.JpegMinQuality, 65);
            var (quality, best, q) = SearchQuality(Encode, Decode, rgb, w, h, minQuality, 95, o.Accept, o.Log);
            return Finish(input, output, originalSize, best, $"WebP 质量 {quality}", q, o);
        }

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
