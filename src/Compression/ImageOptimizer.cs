using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

            // 目标扩展名能识别、且与源格式不同 → 一步完成「转换 + 智能压缩」
            if (targetKind != ImageKind.Unknown && targetKind != sourceKind)
            {
                return OptimizeConverted(inputPath, output, targetKind, originalSize, o);
            }

            switch (sourceKind)
            {
                case ImageKind.Png:
                    return PngOptimizer.Run(inputPath, output, originalSize, o);
                case ImageKind.Jpeg:
                    return JpegOptimizer.Run(inputPath, output, originalSize, o);
                case ImageKind.Gif:
                    return GifOptimizer.Run(inputPath, output, originalSize, o);
                case ImageKind.Webp:
                    return WebpOptimizer.Run(inputPath, output, originalSize, o);
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
        /// 按扩展名判断目标格式（转换的落点以扩展名为准，不嗅探文件头）。
        /// </summary>
        /// <param name="path">输出路径</param>
        internal static ImageKind DetectKindByExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageKind.Jpeg,
            ".png" => ImageKind.Png,
            ".gif" => ImageKind.Gif,
            ".webp" => ImageKind.Webp,
            ".bmp" => ImageKind.Bmp,
            _ => ImageKind.Unknown,
        };

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
        /// 跨格式优化：源格式与目标格式不同，直接按目标格式产出最小体积文件。
        /// 因为用户显式要求转换，无论目标体积是否小于源文件都会写出目标格式。
        /// </summary>
        private static OptimizationResult OptimizeConverted(string input, string output, ImageKind target, long originalSize, OptimizeOptions o)
        {
            switch (target)
            {
                case ImageKind.Jpeg:
                    return JpegOptimizer.RunFromImage(Configuration.Default.LoadRgb24(input), input, output, originalSize, o);
                case ImageKind.Png:
                    return PngOptimizer.RunFromImage(Configuration.Default.LoadRgba32(input), input, output, originalSize, o);
                case ImageKind.Webp:
                    return WebpOptimizer.RunFromImage(Configuration.Default.LoadRgba32(input), input, output, originalSize, o);
                case ImageKind.Gif:
                    return GifOptimizer.RunFromImage(Configuration.Default.LoadRgba32(input), input, output, originalSize, o);
                case ImageKind.Bmp:
                    return FinishConverted(input, output, originalSize, ProduceBmp(Configuration.Default.LoadRgba32(input)), o);
                default:
                    throw new NotSupportedException($"不支持转换到 {target} 格式");
            }
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
            byte[] bytes = artifact.Bytes ?? EncodePlain(inputPath, outputPath);
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
        private static OptimizeArtifact ProduceBmp(Image<Rgba32> image)
        {
            var rgb = new byte[image.Width * image.Height * 3];
            SimdHelper.PackRgbaToRgb(image.Buffer, rgb);
            using var ms = new MemoryStream();
            BmpWriter.Write24(ms, image.Width, image.Height, rgb);
            return new OptimizeArtifact(
                ms.ToArray(),
                "BMP 无损（该格式无压缩）",
                new ImageQuality(ImageQuality.MaxPsnr, ImageQuality.MaxPsnr, 0),
                true);
        }

        /// <summary>
        /// 兜底：按目标格式做一次普通编码（不做质量搜索），保证输出文件始终存在且可用。
        /// </summary>
        private static byte[] EncodePlain(string inputPath, string outputPath)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            using var ms = new MemoryStream();
            switch (ext)
            {
                case ".jpg" or ".jpeg":
                    JpegEncoder.Encode(Configuration.Default.LoadRgb24(inputPath), ms,
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
                    var img = Configuration.Default.LoadRgba32(inputPath);
                    var rgb = new byte[img.Width * img.Height * 3];
                    SimdHelper.PackRgbaToRgb(img.Buffer, rgb);
                    BmpWriter.Write24(ms, img.Width, img.Height, rgb);
                    break;
                }
                default:
                    throw new NotSupportedException($"不支持的目标格式: {ext}");
            }
            return ms.ToArray();
        }

        private static bool IsOpaque(byte[] rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255) return false;
            }
            return true;
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
