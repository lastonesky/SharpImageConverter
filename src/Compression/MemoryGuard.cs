using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 解码/编码前的峰值内存估算与准入判定。
    /// </summary>
    /// <remarks>
    /// 目标不是精确预测，而是在**分配发生之前**挡住明显会 OOM 的大图：
    /// 按文件头宽高 × 格式经验系数估算峰值，与「当前可用内存预算」比较。
    /// 对支持逐行处理的组合（BMP/PNG/GIF/WebP → BMP/PNG）会给出 UseStreaming，
    /// 由调用方走 <c>StreamingConversion</c>，而不是拒绝。
    /// 预算默认取 `GC 可用总量 − 当前进程工作集` 的 75%（含 native 内存），
    /// 可用 <c>SIC_MEMORY_LIMIT_MB</c> 或 CLI <c>--memory-limit</c> 覆盖。
    /// 估算失败（读不到宽高）时返回“放行”，避免因为探测失败误伤正常文件。
    /// </remarks>
    internal static class MemoryGuard
    {
        internal enum Action
        {
            /// <summary>预计峰值在预算内，正常处理。</summary>
            Ok,
            /// <summary>正常路径可能超预算，但改用流式解码后可以放下（仅 JPEG）。</summary>
            UseStreaming,
            /// <summary>预计峰值超过预算，且没有可用的流式降级路径。</summary>
            Refuse,
        }

        internal readonly record struct Verdict(Action Action, long EstimateBytes, long BudgetBytes, string Reason)
        {
            public bool Fits => Action == Action.Ok;
        }

        private static readonly long Megabyte = 1024L * 1024L;

        /// <summary>当前可用内存预算（字节）。</summary>
        /// <param name="explicitLimitBytes">显式上限；0 表示自动推导</param>
        /// <param name="fraction">自动推导时保留的安全余量比例（默认 0.75）</param>
        internal static long GetBudgetBytes(long explicitLimitBytes, double fraction = 0.75)
        {
            if (explicitLimitBytes > 0) return explicitLimitBytes;

            string? env = Environment.GetEnvironmentVariable("SIC_MEMORY_LIMIT_MB");
            if (!string.IsNullOrEmpty(env)
                && long.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out long envMb)
                && envMb > 0)
            {
                return envMb * Megabyte;
            }

            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (total <= 0) total = 2L << 30;

            long used;
            try
            {
                using var process = Process.GetCurrentProcess();
                used = process.WorkingSet64;
            }
            catch
            {
                used = GC.GetTotalMemory(forceFullCollection: false);
            }

            long free = total > used ? total - used : 0;
            long budget = (long)(free * fraction);
            return Math.Max(64L * Megabyte, budget);
        }

        /// <summary>纯转换（非 optimize）的准入判定。</summary>
        internal static Verdict CheckPlain(
            ImageFormatKind source,
            ImageFormatKind target,
            string inputPath,
            long explicitLimitBytes,
            bool allowStreaming = true)
        {
            if (!TryReadDimensions(inputPath, source, out int width, out int height, out int frameCount))
            {
                return new Verdict(Action.Ok, 0, GetBudgetBytes(explicitLimitBytes), "无法读取图像尺寸，跳过内存预检");
            }

            long pixels = (long)width * height;
            long budget = GetBudgetBytes(explicitLimitBytes);
            long fileBytes = TryGetFileLength(inputPath);
            long decode = EstimateDecodeBytes(source, pixels, frameCount) + fileBytes;
            long encode = EstimateEncodeWorkspaceBytes(target, pixels, frameCount);
            long estimate = decode + encode;

            if (estimate <= budget)
            {
                return new Verdict(Action.Ok, estimate, budget, string.Empty);
            }

            if (allowStreaming)
            {
                bool jpegFused = source == ImageFormatKind.Jpeg && TryGetJpegFusedRow(inputPath);
                long streamingEstimate = EstimateStreamingPlainBytes(source, target, width, height, frameCount, jpegFused);
                if (source == ImageFormatKind.Jpeg) streamingEstimate += fileBytes;
                if (streamingEstimate <= budget)
                {
                    return new Verdict(Action.UseStreaming, streamingEstimate, budget,
                        $"预计峰值 {Mb(estimate)} MB，超过预算 {Mb(budget)} MB；可改用流式处理（预计 {Mb(streamingEstimate)} MB）");
                }
            }

            return new Verdict(Action.Refuse, estimate, budget,
                $"预计峰值 {Mb(estimate)} MB（{width}×{height}，{FormatName(source)}→{FormatName(target)}），" +
                $"超过可用内存预算 {Mb(budget)} MB；为避免 OOM 已拒绝处理。流式处理后仍然不足，" +
                "或该源/目标组合不支持逐行处理。");
        }

        /// <summary>optimize 的准入判定。</summary>
        internal static Verdict CheckOptimize(
            ImageFormatKind source,
            string inputPath,
            OptimizeOptions options)
        {
            if (options.DisableMemoryGuard)
            {
                return new Verdict(Action.Ok, 0, GetBudgetBytes(options.MemoryLimitBytes), string.Empty);
            }

            if (!TryReadDimensions(inputPath, source, out int width, out int height, out int frameCount))
            {
                return new Verdict(Action.Ok, 0, GetBudgetBytes(options.MemoryLimitBytes), "无法读取图像尺寸，跳过内存预检");
            }

            long pixels = (long)width * height;
            long budget = GetBudgetBytes(options.MemoryLimitBytes);
            long estimate = EstimateOptimizeBytes(source, pixels, frameCount, options) + TryGetFileLength(inputPath);

            if (estimate <= budget)
            {
                return new Verdict(Action.Ok, estimate, budget, string.Empty);
            }

            return new Verdict(Action.Refuse, estimate, budget,
                $"预计 optimize 峰值 {Mb(estimate)} MB（{width}×{height}，{FormatName(source)}），" +
                $"超过可用内存预算 {Mb(budget)} MB；已拒绝 optimize。");
        }

        /// <summary>按格式估算解码阶段的峰值（含压缩输入缓冲）。</summary>
        internal static long EstimateDecodeBytes(ImageFormatKind kind, long pixels, int frameCount = 1)
        {
            long frames = Math.Max(1, frameCount);
            return kind switch
            {
                // RGB 3 B/px + 系数平面/native 3 B/px（4:2:0）
                ImageFormatKind.Jpeg => pixels * 6,
                // 解压后原始扫描线（约 4 B/px）+ 输出 RGB/RGBA 3~4 B/px
                ImageFormatKind.Png => pixels * 8,
                // 单帧：RGB 3 + 画布/索引约 5；动画按帧数放大
                ImageFormatKind.Gif => pixels * 8 * frames,
                // RGBA 输出 4 + libwebp 内部约 4
                ImageFormatKind.Webp => pixels * 8,
                ImageFormatKind.Bmp => pixels * 4,
                _ => pixels * 8,
            };
        }

        /// <summary>逐行流式路径的峰值估算（只保留少量行缓冲 / native 输出）。</summary>
        internal static long EstimateStreamingPlainBytes(
            ImageFormatKind source,
            ImageFormatKind target,
            int width,
            long height,
            int frameCount,
            bool jpegFusedRow = false)
        {
            long row = Math.Max(64, width) * 8L;
            long frames = Math.Max(1, frameCount);
            long pixels = (long)width * height;

            long decode = source switch
            {
                ImageFormatKind.Bmp => row * 2 + (256L << 10),
                ImageFormatKind.Png => row * 4 + (256L << 10),
                // GIF 首帧画布 / WebP 增量解码的 native 输出仍是整帧，但不再有托管副本。
                ImageFormatKind.Gif => pixels * 4 + row,
                ImageFormatKind.Webp => pixels * 4 + row,
                // 基线单扫描且 4:2:0/4:4:4 可走 MCU 行熵解码，不再保留整幅系数；
                // progressive/422 回退时仍持有约 3 B/px 的量化系数。
                ImageFormatKind.Jpeg => jpegFusedRow
                    ? Math.Max(64L << 20, row * 16 + (4L << 20))
                    : pixels * 3,
                _ => long.MaxValue / 4,
            };

            long encode = target switch
            {
                ImageFormatKind.Bmp => row * 2 + (64L << 10),
                ImageFormatKind.Png => row * 5 + (256L << 10),
                // GIF 必须整帧建调色板：单帧缓冲 + 编码工作集。
                ImageFormatKind.Gif => pixels * 4 + row + pixels * 4,
                // JPEG 逐行编码只需要一个 MCU 行高的 RGB 窄带 + Huffman 状态。
                ImageFormatKind.Jpeg => row * 2 + (256L << 10),
                _ => EstimateEncodeWorkspaceBytes(target, pixels, (int)Math.Min(frames, int.MaxValue)),
            };

            return decode + encode;
        }

        /// <summary>按格式估算编码阶段**额外**的工作集（输入像素已由解码阶段计入）。</summary>
        private static long EstimateEncodeWorkspaceBytes(ImageFormatKind target, long pixels, int frameCount = 1)
        {
            long frames = Math.Max(1, frameCount);
            return target switch
            {
                ImageFormatKind.Jpeg => pixels * 4,
                ImageFormatKind.Png => pixels * 6,
                ImageFormatKind.Webp => pixels * 8,
                ImageFormatKind.Gif => pixels * 4 * frames,
                ImageFormatKind.Bmp => pixels,
                _ => pixels * 6,
            };
        }

        /// <summary>optimize 搜索期的峰值估算（按最常用的搜索路径取保守值）。</summary>
        private static long EstimateOptimizeBytes(
            ImageFormatKind kind,
            long pixels,
            int frameCount,
            OptimizeOptions options)
        {
            long frames = Math.Max(1, frameCount);
            return kind switch
            {
                // 源 RGB + 样例缓存 + native 系数 + 重建 + 输出；444 比 420 多约 5 B/px。
                ImageFormatKind.Jpeg => pixels * (options.JpegCompareSubsampling ? 16 : 11),
                // 源 RGBA 4 + 候选并行（估算并行度按 8 B/px 单候选）+ 输出
                ImageFormatKind.Webp => EstimateWebpOptimize(pixels, options),
                // 源 RGBA 4 + 单候选还原 5 + 候选编码字节（按 2 B/px 留余量）
                ImageFormatKind.Png => pixels * 11,
                // 动画：每帧 RGB+RGBA 7 B/px + 编码候选
                ImageFormatKind.Gif => pixels * 8 * frames,
                ImageFormatKind.Bmp => pixels * 4,
                _ => pixels * 16,
            };
        }

        private static long EstimateWebpOptimize(long pixels, OptimizeOptions options)
        {
            // 与 WebpOptimizer.CandidateParallelism 同一口径：单候选约 8 B/px，
            // 并行度受核数与预算夹上限；这里用“最多 8 个候选”的保守上限。
            long perCandidate = pixels * 8 + (4 * Megabyte);
            // 与 WebpOptimizer 的 CandidateParallelism 同口径：候选预算取总预算的 1/4。
            long candidateBudget = GetBudgetBytes(options.MemoryLimitBytes, fraction: 0.25);
            long degree = Math.Max(1, candidateBudget / Math.Max(1, perCandidate));
            degree = Math.Min(degree, Environment.ProcessorCount);
            degree = Math.Min(degree, 8);
            long output = pixels * 2;
            return pixels * 4 + degree * perCandidate + output;
        }

        /// <summary>
        /// 读取文件头里的宽高（以及 GIF 帧数）。失败时返回 false 并让调用方放行。
        /// </summary>
        /// <remarks>
        /// 解析本身在 <see cref="ImageHeaderReader"/>（全库唯一实现）；这里只负责开文件与吞异常，
        /// 因为内存预检的口径是「读不到就放行」，不能把解析异常抛给调用方。
        /// </remarks>
        internal static bool TryReadDimensions(
            string path,
            ImageFormatKind kind,
            out int width,
            out int height)
            => TryReadDimensions(path, kind, out width, out height, out _);

        internal static bool TryReadDimensions(
            string path,
            ImageFormatKind kind,
            out int width,
            out int height,
            out int frameCount)
        {
            width = height = 0;
            frameCount = 1;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!ImageHeaderReader.TryReadInfo(fs, kind, out var info)) return false;
                width = info.Width;
                height = info.Height;
                frameCount = info.FrameCount;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 是否为「非渐进式 + 可走融合条带」的 JPEG（决定流式路径的峰值估算）。
        /// </summary>
        private static bool TryGetJpegFusedRow(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return ImageHeaderReader.TryReadJpeg(fs, out _, out _, out bool progressive, out bool fused)
                    && !progressive
                    && fused;
            }
            catch
            {
                return false;
            }
        }

        private static long TryGetFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static string Mb(long bytes) => (bytes / (double)Megabyte).ToString("F1", CultureInfo.InvariantCulture);

        private static string FormatName(ImageFormatKind kind) => kind switch
        {
            ImageFormatKind.Jpeg => "JPEG",
            ImageFormatKind.Png => "PNG",
            ImageFormatKind.Gif => "GIF",
            ImageFormatKind.Webp => "WebP",
            ImageFormatKind.Bmp => "BMP",
            _ => "未知",
        };
    }
}
