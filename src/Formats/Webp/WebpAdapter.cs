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

    public readonly struct WebpEncoderOptions
    {
        public float Quality { get; }
        public WebpConcurrencyStrategy ConcurrencyStrategy { get; }

        /// <summary>
        /// 历史参数（原 libwebp <c>config.method</c>，0~6）。
        /// </summary>
        /// <remarks>
        /// 纯 C# 编码器没有 effort 档位，<b>本值不会影响产物</b>；保留只为让既有调用方与
        /// <c>--webp-method</c> 参数面继续可编译/可解析。编码器的努力程度由
        /// <see cref="Vp8LEncoderOptions.Auto"/> 的候选集与有损 VP8 的固定流程决定。
        /// </remarks>
        public int Method { get; }

        /// <summary>
        /// 是否使用无损 WebP（VP8L）。此时 <see cref="Quality"/> 不参与编码（无损没有画质档）。
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
            => options.WebpQuality is { } q
                ? new WebpEncoderOptions(q, Options.ConcurrencyStrategy, Options.Method, Options.Lossless)
                : Options;
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
            => options.WebpQuality is { } q
                ? new WebpEncoderOptions(q, Options.ConcurrencyStrategy, Options.Method, Options.Lossless)
                : Options;
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
