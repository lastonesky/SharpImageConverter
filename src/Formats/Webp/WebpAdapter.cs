using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;
using System.Collections.Generic;

namespace SharpImageConverter.Formats.Webp
{
    public enum WebpConcurrencyStrategy
    {
        Auto = 0,
        Serial = 1,
        Parallel = 2
    }

    public readonly struct WebpEncoderOptions
    {
        private static readonly bool AllowM4HighQuality =
            Environment.GetEnvironmentVariable("SIC_WEBP_ALLOW_M4_HIGHQ") == "1";

        public float Quality { get; }
        public WebpConcurrencyStrategy ConcurrencyStrategy { get; }

        /// <summary>
        /// libwebp 的 <c>config.method</c>（0~6）：越大越慢、体积越小，默认 3。
        /// </summary>
        /// <remarks>
        /// 2048²（4.19 MP）/ q80 实测单次编码：method 2 = 147.5 ms / 70,838 B、
        /// method 3 = 501.7 ms / 67,784 B、method 4 = 490.9 ms / 67,818 B、
        /// method 6 = 579.4 ms / 64,872 B。悬崖在 2→3 之间：≥3 才走完整 RD 优化。
        /// 默认取 3（不取 4）的原因：libwebp 1.6.0 的 method 4 在 &gt;140 MP 且有质量相关悬崖
        /// （143 MP 实测 q75：m3 7.8 s / 1,597,784 B vs m4 26.3 s / 1,655,814 B，m4 更慢还更大；
        /// q40/q65 不触发），且在 7 个样本（1~143 MP，含真实照片）上 m3 体积均 ≤ m4。
        /// 因此构造时强制：<b>quality ≥ 75 且 method == 4 时降为 3</b>，避免调用方误用。
        /// 详见 docs 与 .perf/results。
        /// </remarks>
        public int Method { get; }

        /// <summary>
        /// 是否使用无损 WebP。无损路径直通 libwebp 的 <c>WebPEncodeLosslessRGBA</c>，
        /// 此时 <see cref="Quality"/> / <see cref="Method"/> 不参与编码参数（仅保留给调用方读取）。
        /// </summary>
        public bool Lossless { get; }

        public WebpEncoderOptions(
            float quality,
            WebpConcurrencyStrategy concurrencyStrategy = WebpConcurrencyStrategy.Auto,
            int method = 3,
            bool lossless = false)
        {
            if (quality < 0f || quality > 100f) throw new ArgumentOutOfRangeException(nameof(quality));
            if (method is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(method));
            // 实测 m4 在 143 MP / q75 有 3.4 倍悬崖且体积更大；高质量档直接降回 m3。
            // 无损模式下 quality 是“压缩努力”而非画质档，但 m4 的悬崖是 libwebp 内部实现问题，
            // 同样避开更稳妥。SIC_WEBP_ALLOW_M4_HIGHQ=1 仅用于复现/诊断。
            if (method == 4 && quality >= 75f && !AllowM4HighQuality) method = 3;
            Quality = quality;
            ConcurrencyStrategy = concurrencyStrategy;
            Method = method;
            Lossless = lossless;
        }

        public static WebpEncoderOptions Default => new(75f, WebpConcurrencyStrategy.Auto);
    }

    internal static class WebpStreamDecoder
    {
        internal static byte[] DecodeRgbaFromStream(Stream stream, out int width, out int height)
        {
            return WebpCodec.DecodeRgbaFromStream(stream, out width, out height);
        }
    }

    /// <summary>
    /// WebP 解码器适配器（RGB24）
    /// </summary>
    public sealed class WebpDecoderAdapter : IImageDecoder
    {

        /// <summary>
        /// 解码 WebP 文件为 RGB24 图像
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>RGB24 图像</returns>
        public Image<Rgb24> DecodeRgb24(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return DecodeRgb24(fs);
        }

        /// <summary>
        /// 解码 WebP 流为 RGB24 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>RGB24 图像</returns>
        public Image<Rgb24> DecodeRgb24(Stream stream)
        {
            var rgba = WebpStreamDecoder.DecodeRgbaFromStream(stream, out int width, out int height);
            var rgb = new byte[width * height * 3];
            // 复用已有的 SSSE3 打包实现（原为逐像素标量循环）
            SimdHelper.PackRgbaToRgb(rgba, rgb);
            return new Image<Rgb24>(width, height, rgb);
        }
    }

    /// <summary>
    /// WebP 解码器适配器（RGBA32）
    /// </summary>
    public sealed class WebpDecoderAdapterRgba : IImageDecoderRgba
    {
        /// <summary>
        /// 解码 WebP 文件为 RGBA32 图像
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return DecodeRgba32(fs);
        }

        /// <summary>
        /// 解码 WebP 流为 RGBA32 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(Stream stream)
        {
            var rgba = WebpStreamDecoder.DecodeRgbaFromStream(stream, out int width, out int height);
            return new Image<Rgba32>(width, height, rgba);
        }
    }

    /// <summary>
    /// WebP 编码器适配器（RGB24）
    /// </summary>
    public sealed class WebpEncoderAdapter : IImageEncoder
    {
        public WebpEncoderOptions Options { get; set; } = WebpEncoderOptions.Default;

        public float Quality
        {
            get => Options.Quality;
            set => Options = new WebpEncoderOptions(value, Options.ConcurrencyStrategy, Options.Method);
        }

        public WebpConcurrencyStrategy ConcurrencyStrategy
        {
            get => Options.ConcurrencyStrategy;
            set => Options = new WebpEncoderOptions(Options.Quality, value, Options.Method);
        }

        public int Method
        {
            get => Options.Method;
            set => Options = new WebpEncoderOptions(Options.Quality, Options.ConcurrencyStrategy, value);
        }

        /// <summary>
        /// 将 RGB24 图像编码为 WebP 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            EncodeRgb24(fs, image);
        }

        /// <summary>
        /// 将 RGB24 图像编码为 WebP 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image)
        {
            WebpCodec.EncodeRgbToStream(stream, image.Buffer, image.Width, image.Height, Options);
        }
    }

    /// <summary>
    /// WebP 编码器适配器（RGBA32）
    /// </summary>
    public sealed class WebpEncoderAdapterRgba : IImageEncoderRgba
    {
        public WebpEncoderOptions Options { get; set; } = WebpEncoderOptions.Default;

        public float Quality
        {
            get => Options.Quality;
            set => Options = new WebpEncoderOptions(value, Options.ConcurrencyStrategy, Options.Method);
        }

        public WebpConcurrencyStrategy ConcurrencyStrategy
        {
            get => Options.ConcurrencyStrategy;
            set => Options = new WebpEncoderOptions(Options.Quality, value, Options.Method);
        }

        public int Method
        {
            get => Options.Method;
            set => Options = new WebpEncoderOptions(Options.Quality, Options.ConcurrencyStrategy, value);
        }

        /// <summary>
        /// 将 RGBA32 图像编码为 WebP 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(string path, Image<Rgba32> image)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            EncodeRgba32(fs, image);
        }

        /// <summary>
        /// 将 RGBA32 图像编码为 WebP 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(Stream stream, Image<Rgba32> image)
        {
            WebpCodec.EncodeRgbaToStream(stream, image.Buffer, image.Width, image.Height, Options);
        }
    }

    public static class WebpAnimationEncoder
    {
        public static void EncodeRgb24(string path, IReadOnlyList<Image<Rgb24>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, float quality = 75f)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            EncodeRgb24(fs, frames, frameDurationsMs, loopCount, quality);
        }

        public static void EncodeRgb24(Stream stream, IReadOnlyList<Image<Rgb24>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, float quality = 75f)
        {
            EncodeRgb24(stream, frames, frameDurationsMs, loopCount, new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto));
        }

        public static void EncodeRgb24(Stream stream, IReadOnlyList<Image<Rgb24>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, WebpEncoderOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream, nameof(stream));
            ArgumentNullException.ThrowIfNull(frames, nameof(frames));
            ArgumentNullException.ThrowIfNull(frameDurationsMs, nameof(frameDurationsMs));

            if (frames.Count == 0) throw new ArgumentOutOfRangeException(nameof(frames));
            if (frames.Count != frameDurationsMs.Count) throw new ArgumentException("帧与时长数量不一致");

            int width = frames[0].Width;
            int height = frames[0].Height;

            var rgbaFrames = new byte[frames.Count][];
            var durations = new int[frames.Count];

            for (int fi = 0; fi < frames.Count; fi++)
            {
                var frame = frames[fi];
                if (frame.Width != width || frame.Height != height) throw new ArgumentException("所有帧必须具有相同的宽高");

                var rgba = new byte[width * height * 4];
                // 复用已有的 SSSE3 展开实现（原为逐像素标量循环）
                SimdHelper.ExpandRgbToRgba(frame.Buffer, rgba);
                rgbaFrames[fi] = rgba;

                int d = frameDurationsMs[fi];
                durations[fi] = d < 10 ? 10 : d;
            }

            WebpCodec.EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, durations, loopCount, options);
        }
    }
}
