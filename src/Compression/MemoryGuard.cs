using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;

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
            ImageOptimizer.ImageKind source,
            ImageOptimizer.ImageKind target,
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
                bool jpegFused = source == ImageOptimizer.ImageKind.Jpeg
                    && TryReadJpegHeaderInfo(inputPath, out JpegHeaderInfo jpegInfo)
                    && !jpegInfo.Progressive
                    && jpegInfo.FusedRow;
                long streamingEstimate = EstimateStreamingPlainBytes(source, target, width, height, frameCount, jpegFused);
                if (source == ImageOptimizer.ImageKind.Jpeg) streamingEstimate += fileBytes;
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
            ImageOptimizer.ImageKind source,
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
        internal static long EstimateDecodeBytes(ImageOptimizer.ImageKind kind, long pixels, int frameCount = 1)
        {
            long frames = Math.Max(1, frameCount);
            return kind switch
            {
                // RGB 3 B/px + 系数平面/native 3 B/px（4:2:0）
                ImageOptimizer.ImageKind.Jpeg => pixels * 6,
                // 解压后原始扫描线（约 4 B/px）+ 输出 RGB/RGBA 3~4 B/px
                ImageOptimizer.ImageKind.Png => pixels * 8,
                // 单帧：RGB 3 + 画布/索引约 5；动画按帧数放大
                ImageOptimizer.ImageKind.Gif => pixels * 8 * frames,
                // RGBA 输出 4 + libwebp 内部约 4
                ImageOptimizer.ImageKind.Webp => pixels * 8,
                ImageOptimizer.ImageKind.Bmp => pixels * 4,
                _ => pixels * 8,
            };
        }

        /// <summary>逐行流式路径的峰值估算（只保留少量行缓冲 / native 输出）。</summary>
        internal static long EstimateStreamingPlainBytes(
            ImageOptimizer.ImageKind source,
            ImageOptimizer.ImageKind target,
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
                ImageOptimizer.ImageKind.Bmp => row * 2 + (256L << 10),
                ImageOptimizer.ImageKind.Png => row * 4 + (256L << 10),
                // GIF 首帧画布 / WebP 增量解码的 native 输出仍是整帧，但不再有托管副本。
                ImageOptimizer.ImageKind.Gif => pixels * 4 + row,
                ImageOptimizer.ImageKind.Webp => pixels * 4 + row,
                // 基线单扫描且 4:2:0/4:4:4 可走 MCU 行熵解码，不再保留整幅系数；
                // progressive/422 回退时仍持有约 3 B/px 的量化系数。
                ImageOptimizer.ImageKind.Jpeg => jpegFusedRow
                    ? Math.Max(64L << 20, row * 16 + (4L << 20))
                    : pixels * 3,
                _ => long.MaxValue / 4,
            };

            long encode = target switch
            {
                ImageOptimizer.ImageKind.Bmp => row * 2 + (64L << 10),
                ImageOptimizer.ImageKind.Png => row * 5 + (256L << 10),
                // GIF 必须整帧建调色板：单帧缓冲 + 编码工作集。
                ImageOptimizer.ImageKind.Gif => pixels * 4 + row + pixels * 4,
                // JPEG 逐行编码只需要一个 MCU 行高的 RGB 窄带 + Huffman 状态。
                ImageOptimizer.ImageKind.Jpeg => row * 2 + (256L << 10),
                _ => EstimateEncodeWorkspaceBytes(target, pixels, (int)Math.Min(frames, int.MaxValue)),
            };

            return decode + encode;
        }

        /// <summary>按格式估算编码阶段**额外**的工作集（输入像素已由解码阶段计入）。</summary>
        private static long EstimateEncodeWorkspaceBytes(ImageOptimizer.ImageKind target, long pixels, int frameCount = 1)
        {
            long frames = Math.Max(1, frameCount);
            return target switch
            {
                ImageOptimizer.ImageKind.Jpeg => pixels * 4,
                ImageOptimizer.ImageKind.Png => pixels * 6,
                ImageOptimizer.ImageKind.Webp => pixels * 8,
                ImageOptimizer.ImageKind.Gif => pixels * 4 * frames,
                ImageOptimizer.ImageKind.Bmp => pixels,
                _ => pixels * 6,
            };
        }

        /// <summary>optimize 搜索期的峰值估算（按最常用的搜索路径取保守值）。</summary>
        private static long EstimateOptimizeBytes(
            ImageOptimizer.ImageKind kind,
            long pixels,
            int frameCount,
            OptimizeOptions options)
        {
            long frames = Math.Max(1, frameCount);
            return kind switch
            {
                // 源 RGB + 样例缓存 + native 系数 + 重建 + 输出；444 比 420 多约 5 B/px。
                ImageOptimizer.ImageKind.Jpeg => pixels * (options.JpegCompareSubsampling ? 16 : 11),
                // 源 RGBA 4 + 候选并行（估算并行度按 8 B/px 单候选）+ 输出
                ImageOptimizer.ImageKind.Webp => EstimateWebpOptimize(pixels, options),
                // 源 RGBA 4 + 单候选还原 5 + 候选编码字节（按 2 B/px 留余量）
                ImageOptimizer.ImageKind.Png => pixels * 11,
                // 动画：每帧 RGB+RGBA 7 B/px + 编码候选
                ImageOptimizer.ImageKind.Gif => pixels * 8 * frames,
                ImageOptimizer.ImageKind.Bmp => pixels * 4,
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

        /// <summary>读取文件头里的宽高（以及 GIF 帧数）。失败时返回 false 并让调用方放行。</summary>
        internal static bool TryReadDimensions(
            string path,
            ImageOptimizer.ImageKind kind,
            out int width,
            out int height)
            => TryReadDimensions(path, kind, out width, out height, out _);

        internal static bool TryReadDimensions(
            string path,
            ImageOptimizer.ImageKind kind,
            out int width,
            out int height,
            out int frameCount)
        {
            width = height = 0;
            frameCount = 1;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return kind switch
                {
                    ImageOptimizer.ImageKind.Png => TryReadPng(fs, out width, out height),
                    ImageOptimizer.ImageKind.Jpeg => TryReadJpeg(fs, out width, out height),
                    ImageOptimizer.ImageKind.Gif => TryReadGif(fs, out width, out height, out frameCount),
                    ImageOptimizer.ImageKind.Webp => TryReadWebp(fs, out width, out height),
                    ImageOptimizer.ImageKind.Bmp => TryReadBmp(fs, out width, out height),
                    _ => false,
                };
            }
            catch
            {
                return false;
            }
        }

        private readonly record struct JpegHeaderInfo(int Width, int Height, bool Progressive, bool FusedRow);

        private static bool TryReadJpegHeaderInfo(string path, out JpegHeaderInfo info)
        {
            info = default;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return false;

                Span<byte> buffer = stackalloc byte[4];
                while (true)
                {
                    int b = stream.ReadByte();
                    if (b < 0) return false;
                    if (b != 0xFF) continue;
                    int marker;
                    do { marker = stream.ReadByte(); } while (marker == 0xFF);
                    if (marker < 0) return false;
                    if (marker == 0xD8 || marker == 0xD9 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
                    {
                        if (marker == 0xD9) return false;
                        continue;
                    }
                    if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2) return false;
                    int length = (buffer[0] << 8) | buffer[1];
                    if (length < 2) return false;

                    bool isSof = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
                        or 0xC5 or 0xC6 or 0xC7
                        or 0xC9 or 0xCA or 0xCB
                        or 0xCD or 0xCE or 0xCF;
                    if (!isSof)
                    {
                        stream.Seek(length - 2, SeekOrigin.Current);
                        continue;
                    }

                    int payload = length - 2;
                    if (payload < 6) return false;
                    Span<byte> sof = stackalloc byte[6 + 12];
                    if (stream.ReadAtLeast(sof[..Math.Min(payload, sof.Length)], Math.Min(payload, sof.Length), throwOnEndOfStream: false) < Math.Min(payload, sof.Length)) return false;
                    int h = (sof[1] << 8) | sof[2];
                    int w = (sof[3] << 8) | sof[4];
                    int count = sof[5];
                    bool progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
                    bool fused = false;
                    if (count == 3 && payload >= 6 + 9)
                    {
                        int hv0 = sof[7];
                        int hv1 = sof[10];
                        int hv2 = sof[13];
                        fused = (hv0 == 0x22 && hv1 == 0x11 && hv2 == 0x11)
                            || (hv0 == 0x11 && hv1 == 0x11 && hv2 == 0x11);
                    }
                    if (w <= 0 || h <= 0) return false;
                    info = new JpegHeaderInfo(w, h, progressive, fused);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadPng(Stream stream, out int width, out int height)
        {
            width = height = 0;
            Span<byte> head = stackalloc byte[24];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47) return false;
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(head[8..12]);
            if (chunkLength < 8 || head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R') return false;
            uint w = BinaryPrimitives.ReadUInt32BigEndian(head[16..20]);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(head[20..24]);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
            width = (int)w;
            height = (int)h;
            return true;
        }

        private static bool TryReadJpeg(Stream stream, out int width, out int height)
        {
            width = height = 0;
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return false;

            Span<byte> buffer = stackalloc byte[4];
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return false;
                if (b != 0xFF) continue;
                int marker;
                do
                {
                    marker = stream.ReadByte();
                } while (marker == 0xFF);
                if (marker < 0) return false;

                // 无长度的独立标记
                if (marker == 0xD8 || marker == 0xD9 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
                {
                    if (marker == 0xD9) return false;
                    continue;
                }

                if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2) return false;
                int length = (buffer[0] << 8) | buffer[1];
                if (length < 2) return false;

                bool isSof = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
                    or 0xC5 or 0xC6 or 0xC7
                    or 0xC9 or 0xCA or 0xCB
                    or 0xCD or 0xCE or 0xCF;
                if (isSof)
                {
                    if (length < 7) return false;
                    if (stream.ReadAtLeast(buffer[..3], 3, throwOnEndOfStream: false) < 3) return false;
                    int h = (buffer[1] << 8) | buffer[2];
                    if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2) return false;
                    int w = (buffer[0] << 8) | buffer[1];
                    if (w <= 0 || h <= 0) return false;
                    width = w;
                    height = h;
                    return true;
                }

                stream.Seek(length - 2, SeekOrigin.Current);
            }
        }

        private static bool TryReadGif(Stream stream, out int width, out int height, out int frameCount)
        {
            width = height = 0;
            frameCount = 1;
            Span<byte> head = stackalloc byte[13];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (head[0] != 'G' || head[1] != 'I' || head[2] != 'F') return false;

            width = head[6] | (head[7] << 8);
            height = head[8] | (head[9] << 8);
            if (width <= 0 || height <= 0) return false;

            int packed = head[10];
            if ((packed & 0x80) != 0)
            {
                int gctSize = 3 * (1 << ((packed & 0x07) + 1));
                stream.Seek(gctSize, SeekOrigin.Current);
            }

            int frames = 0;
            Span<byte> block = stackalloc byte[9];
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                if (b == 0x3B) break; // trailer
                if (b == 0x21) // extension
                {
                    if (stream.ReadByte() < 0) break;
                    if (!SkipGifSubBlocks(stream)) break;
                    continue;
                }
                if (b != 0x2C) break; // unknown block

                frames++;
                if (stream.ReadAtLeast(block[..9], 9, throwOnEndOfStream: false) < 9) break;
                int imgPacked = block[8];
                if ((imgPacked & 0x80) != 0)
                {
                    int lctSize = 3 * (1 << ((imgPacked & 0x07) + 1));
                    stream.Seek(lctSize, SeekOrigin.Current);
                }
                if (stream.ReadByte() < 0) break; // LZW minimum code size
                if (!SkipGifSubBlocks(stream)) break;
            }

            if (frames > 0) frameCount = frames;
            return true;
        }

        private static bool SkipGifSubBlocks(Stream stream)
        {
            while (true)
            {
                int len = stream.ReadByte();
                if (len < 0) return false;
                if (len == 0) return true;
                stream.Seek(len, SeekOrigin.Current);
            }
        }

        private static bool TryReadWebp(Stream stream, out int width, out int height)
        {
            width = height = 0;
            Span<byte> head = stackalloc byte[12];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (head[0] != 'R' || head[1] != 'I' || head[2] != 'F' || head[3] != 'F'
                || head[8] != 'W' || head[9] != 'E' || head[10] != 'B' || head[11] != 'P') return false;

            Span<byte> chunk = stackalloc byte[8];
            Span<byte> data = stackalloc byte[10];
            while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                if (chunk[0] == 'V' && chunk[1] == 'P' && chunk[2] == '8' && chunk[3] == 'X')
                {
                    if (size < 10 || stream.ReadAtLeast(data, 10, throwOnEndOfStream: false) < 10) return false;
                    uint w = (uint)(data[4] | (data[5] << 8) | (data[6] << 16)) + 1;
                    uint h = (uint)(data[7] | (data[8] << 8) | (data[9] << 16)) + 1;
                    width = (int)Math.Min(w, int.MaxValue);
                    height = (int)Math.Min(h, int.MaxValue);
                    return width > 0 && height > 0;
                }
                if (chunk[0] == 'V' && chunk[1] == 'P' && chunk[2] == '8' && chunk[3] == ' ')
                {
                    if (size < 10 || stream.ReadAtLeast(data, 10, throwOnEndOfStream: false) < 10) return false;
                    if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A) return false;
                    width = (data[6] | (data[7] << 8)) & 0x3FFF;
                    height = (data[8] | (data[9] << 8)) & 0x3FFF;
                    return width > 0 && height > 0;
                }
                if (chunk[0] == 'V' && chunk[1] == 'P' && chunk[2] == '8' && chunk[3] == 'L')
                {
                    if (size < 5 || stream.ReadAtLeast(data[..5], 5, throwOnEndOfStream: false) < 5) return false;
                    if (data[0] != 0x2F) return false;
                    uint bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                    return true;
                }

                long skip = (long)size + (size & 1);
                stream.Seek(skip, SeekOrigin.Current);
            }
            return false;
        }

        private static bool TryReadBmp(Stream stream, out int width, out int height)
        {
            width = height = 0;
            Span<byte> head = stackalloc byte[26];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (head[0] != 'B' || head[1] != 'M') return false;
            int w = BinaryPrimitives.ReadInt32LittleEndian(head[18..22]);
            int h = BinaryPrimitives.ReadInt32LittleEndian(head[22..26]);
            if (w <= 0 || h == 0) return false;
            width = w;
            height = Math.Abs(h);
            return true;
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

        private static string FormatName(ImageOptimizer.ImageKind kind) => kind switch
        {
            ImageOptimizer.ImageKind.Jpeg => "JPEG",
            ImageOptimizer.ImageKind.Png => "PNG",
            ImageOptimizer.ImageKind.Gif => "GIF",
            ImageOptimizer.ImageKind.Webp => "WebP",
            ImageOptimizer.ImageKind.Bmp => "BMP",
            _ => "未知",
        };
    }
}
