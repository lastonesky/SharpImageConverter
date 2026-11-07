﻿using System;
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

    /// <remarks>
    /// 声明为 <c>record</c> 只为拿到 <c>with</c> 表达式：适配器上的每个单值 setter 都要
    /// 「只改自己那一项、其余原样带过」，手写构造参数会在新增字段时静默丢值
    /// （历史上 <see cref="Lossless"/> 就这样在三个 setter 里被重置成 false）。
    /// </remarks>
    public readonly record struct WebpEncoderOptions
    {
        public float Quality { get; init; }
        public WebpConcurrencyStrategy ConcurrencyStrategy { get; init; }

        /// <summary>
        /// effort 档位（对齐 libwebp <c>config.method</c>，0~6）。
        /// </summary>
        /// <remarks>
        /// <c>0..4</c> 为快档：有损单遍 + 标量量化 + i4x4 单候选 + 滤波查表；
        /// 无损单候选 + 无精化轮 + 单组 Huffman 表。
        /// <c>5..6</c> 为完整档：有损两遍概率自适应 + 全 trellis + 双路 RD 决胜；
        /// 无损多候选择优 + 真实码长精化 + meta-Huffman。
        /// 两档输出都是合法可解码的 WebP，区别只在耗时与体积。
        /// </remarks>
        public int Method { get; init; }

        /// <summary>
        /// 是否使用无损 WebP（VP8L）。此时 <see cref="Quality"/> 不参与编码（无损没有画质档）。
        /// </summary>
        public bool Lossless { get; init; }

        public WebpEncoderOptions(
            float quality,
            WebpConcurrencyStrategy concurrencyStrategy = WebpConcurrencyStrategy.Auto,
            int method = 3,
            bool lossless = false)
        {
            if (quality < 0f || quality > 100f) throw new ArgumentOutOfRangeException(nameof(quality));
            if (method is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(method));
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
            => DecodeRgbaFromStream(stream, out width, out height, out _);

        internal static byte[] DecodeRgbaFromStream(Stream stream, out int width, out int height,
            out Metadata.ImageMetadata? metadata)
        {
            // 纯 C# 解码；适配器层把位流损坏统一折算为 InvalidDataException，
            // 与既有（native 时代）的调用方契约保持一致；不支持的动图语义保持 WebpUnsupportedException。
            try
            {
                return WebpDecodeDispatch.DecodeRgba(stream, out width, out height, out metadata);
            }
            catch (WebpUnsupportedException)
            {
                throw;
            }
            catch (WebpException ex)
            {
                throw new InvalidDataException("WebP 数据损坏或不完整", ex);
            }
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
            var rgba = WebpStreamDecoder.DecodeRgbaFromStream(stream, out int width, out int height, out var metadata);
            var rgb = new byte[width * height * 3];
            // 复用已有的 SSSE3 打包实现（原为逐像素标量循环）
            SimdHelper.PackRgbaToRgb(rgba, rgb);
            return new Image<Rgb24>(width, height, rgb, metadata);
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
            var rgba = WebpStreamDecoder.DecodeRgbaFromStream(stream, out int width, out int height, out var metadata);
            return new Image<Rgba32>(width, height, rgba, metadata);
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
            set => Options = Options with { Quality = value };
        }

        public WebpConcurrencyStrategy ConcurrencyStrategy
        {
            get => Options.ConcurrencyStrategy;
            set => Options = Options with { ConcurrencyStrategy = value };
        }

        public int Method
        {
            get => Options.Method;
            set => Options = Options with { Method = value };
        }

        public bool Lossless
        {
            get => Options.Lossless;
            set => Options = Options with { Lossless = value };
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
            WebpCodec.EncodeRgbToStream(stream, image.Buffer, image.Width, image.Height, Options, image.Metadata);
        }

        /// <summary>
        /// 按 <see cref="SaveOptions.WebpQuality"/> 编码；未提供时用本实例的质量。
        /// </summary>
        /// <remarks>
        /// 逐次保存改本实例的 <see cref="Quality"/> 会污染
        /// <see cref="Configuration.Default"/> 里的共享编码器，所以选项按调用传入。
        /// </remarks>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        /// <param name="options">保存选项</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image, SaveOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            WebpCodec.EncodeRgbToStream(stream, image.Buffer, image.Width, image.Height, Effective(options),
                image.Metadata);
        }

        /// <summary>把 <see cref="SaveOptions.WebpQuality"/> 套到本实例的其余参数上。</summary>
        private WebpEncoderOptions Effective(SaveOptions options)
            => Options with
            {
                Quality = options.WebpQuality ?? Options.Quality,
                Lossless = options.WebpLossless ?? Options.Lossless,
            };
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
            set => Options = Options with { Quality = value };
        }

        public WebpConcurrencyStrategy ConcurrencyStrategy
        {
            get => Options.ConcurrencyStrategy;
            set => Options = Options with { ConcurrencyStrategy = value };
        }

        public int Method
        {
            get => Options.Method;
            set => Options = Options with { Method = value };
        }

        public bool Lossless
        {
            get => Options.Lossless;
            set => Options = Options with { Lossless = value };
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
            WebpCodec.EncodeRgbaToStream(stream, image.Buffer, image.Width, image.Height, Options, image.Metadata);
        }

        /// <summary>
        /// 按 <see cref="SaveOptions.WebpQuality"/> 编码；未提供时用本实例的质量。
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        /// <param name="options">保存选项</param>
        public void EncodeRgba32(Stream stream, Image<Rgba32> image, SaveOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            WebpCodec.EncodeRgbaToStream(stream, image.Buffer, image.Width, image.Height, Effective(options),
                image.Metadata);
        }

        /// <summary>把 <see cref="SaveOptions.WebpQuality"/> 套到本实例的其余参数上。</summary>
        private WebpEncoderOptions Effective(SaveOptions options)
            => Options with
            {
                Quality = options.WebpQuality ?? Options.Quality,
                Lossless = options.WebpLossless ?? Options.Lossless,
            };
    }

    public static class WebpAnimationEncoder
    {
        public static void EncodeRgba32(string path, IReadOnlyList<Image<Rgba32>> frames,
            IReadOnlyList<int> frameDurationsMs, int loopCount, WebpEncoderOptions options)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            EncodeRgba32(fs, frames, frameDurationsMs, loopCount, options);
        }

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

            WebpCodec.EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, durations, loopCount, options,
                frames[0].Metadata);
        }

        /// <summary>
        /// 编码 RGBA 动图（保留 alpha 通道）。
        /// </summary>
        /// <remarks>
        /// 与 <see cref="EncodeRgb24(Stream, IReadOnlyList{Image{Rgb24}}, IReadOnlyList{int}, int, WebpEncoderOptions)"/>
        /// 的区别只在像素来源：这里直接使用每帧的 RGBA 数据，不做 RGB→RGBA 展开，
        /// 因此半透明与全透明像素都会原样进入 ANMF 的 ALPH 子块。
        /// <para>
        /// GIF 动图转 WebP 时必须走这个入口：GIF 的透明索引信息只存在于 alpha 通道，
        /// 用 Rgb24 路径会把透明背景变成不透明（实测整幅 4096 像素全部 alpha=255）。
        /// </para>
        /// </remarks>
        public static void EncodeRgba32(string path, IReadOnlyList<Image<Rgba32>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, float quality = 75f)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            EncodeRgba32(fs, frames, frameDurationsMs, loopCount, quality);
        }

        /// <summary>
        /// 编码 RGBA 动图（保留 alpha 通道）到流。
        /// </summary>
        public static void EncodeRgba32(Stream stream, IReadOnlyList<Image<Rgba32>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, float quality = 75f)
        {
            EncodeRgba32(stream, frames, frameDurationsMs, loopCount, new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto));
        }

        public static void EncodeRgba32(string path, byte[][] rgbaFrames, int width, int height,
            IReadOnlyList<int> frameDurationsMs, int loopCount, WebpEncoderOptions options)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            WebpCodec.EncodeAnimatedRgbaToStream(fs, rgbaFrames, width, height,
                frameDurationsMs is int[] durations ? durations : new List<int>(frameDurationsMs).ToArray(),
                loopCount, options);
        }

        /// <summary>
        /// 编码 RGBA 动图（保留 alpha 通道）到流。
        /// </summary>
        public static void EncodeRgba32(Stream stream, IReadOnlyList<Image<Rgba32>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, WebpEncoderOptions options)
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
                if (frame.Buffer.Length != checked(width * height * 4))
                    throw new ArgumentException($"第 {fi} 帧的 RGBA 数据长度与宽高不匹配", nameof(frames));

                // 帧数据按引用传递：WebpCodec 只读不改，不需要再复制一份。
                rgbaFrames[fi] = frame.Buffer;

                int d = frameDurationsMs[fi];
                durations[fi] = d < 10 ? 10 : d;
            }

            WebpCodec.EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, durations, loopCount, options,
                frames[0].Metadata);
        }
    }
}
