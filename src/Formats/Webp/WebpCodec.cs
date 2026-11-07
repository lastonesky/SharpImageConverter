using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>
    /// WebP 编解码的公共入口（纯 C# 实现）。
    ///
    /// 静态图解码见 <see cref="WebpDecoder"/>，静态图编码见 <see cref="WebpEncoder"/>，
    /// 动图编码见 <see cref="WebpAnimationWriter"/>，逐行解码见 <see cref="WebpRowSource"/>。
    /// 本类只做入口适配与参数校验，不再包含任何 native 调用。
    /// </summary>
    internal static class WebpCodec
    {
        public static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height)
            => WebpDecoder.DecodeRgba(data, out width, out height);

        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
            => WebpDecoder.DecodeRgba(data, out width, out height);

        public static byte[] DecodeRgbaFromStream(Stream stream, out int width, out int height)
            => WebpDecoder.DecodeRgba(stream, out width, out height);

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, float quality)
            => WebpEncoder.EncodeLossyRgba(rgba, width, height, quality);

        public static byte[] EncodeRgb(byte[] rgb, int width, int height, float quality)
            => WebpEncoder.EncodeLossyRgba(ExpandRgb(rgb, width, height), width, height, quality);

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, WebpEncoderOptions options,
            ImageMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(rgba);
            return options.Lossless
                ? WebpEncoder.EncodeRgba(rgba, width, height, options.Method, metadata)
                : WebpEncoder.EncodeLossyRgba(rgba, width, height, options.Quality, options.Method, metadata);
        }

        public static byte[] EncodeRgb(byte[] rgb, int width, int height, WebpEncoderOptions options,
            ImageMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(rgb);
            byte[] rgba = ExpandRgb(rgb, width, height);
            return options.Lossless
                ? WebpEncoder.EncodeRgba(rgba, width, height, options.Method, metadata)
                : WebpEncoder.EncodeLossyRgba(rgba, width, height, options.Quality, options.Method, metadata);
        }

        public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height, float quality)
            => WebpEncoder.EncodeLossyRgbaToStream(stream, rgba, width, height, quality);

        public static void EncodeRgbToStream(Stream stream, ReadOnlySpan<byte> rgb, int width, int height, float quality)
        {
            ArgumentNullException.ThrowIfNull(stream);
            byte[] rgba = ExpandRgb(rgb, width, height);
            WebpEncoder.EncodeLossyRgbaToStream(stream, rgba, width, height, quality);
        }

        public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height,
            WebpEncoderOptions options, ImageMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA 像素长度不匹配", nameof(rgba));
            if (options.Lossless)
            {
                WebpEncoder.EncodeRgbaToStream(stream, rgba, width, height, options.Method, metadata);
                return;
            }
            WebpEncoder.EncodeLossyRgbaToStream(stream, rgba, width, height, options.Quality,
                options.Method, metadata);
        }

        public static void EncodeRgbToStream(Stream stream, ReadOnlySpan<byte> rgb, int width, int height,
            WebpEncoderOptions options, ImageMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            byte[] rgba = ExpandRgb(rgb, width, height);
            if (options.Lossless)
            {
                WebpEncoder.EncodeRgbaToStream(stream, rgba, width, height, options.Method, metadata);
                return;
            }
            WebpEncoder.EncodeLossyRgbaToStream(stream, rgba, width, height, options.Quality,
                options.Method, metadata);
        }

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs,
            int loopCount, float quality)
            => EncodeAnimatedRgba(rgbaFrames, width, height, frameDurationsMs, loopCount,
                new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto));

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs,
            int loopCount, WebpEncoderOptions options, ImageMetadata? metadata = null)
        {
            ValidateAnimationInput(rgbaFrames, frameDurationsMs, width, height);

            // 单帧保持既有的“静态图”语义（不产出单帧 ANMF 动图）。
            if (rgbaFrames.Length == 1)
                return EncodeRgba(rgbaFrames[0], width, height, options, metadata);

            if (loopCount < 0) loopCount = 0;

            return WebpAnimationWriter.Encode(
                BuildAnimationFrames(rgbaFrames, frameDurationsMs),
                width, height, loopCount,
                new WebpAnimationEncodeOptions(options.Lossless, options.Quality), metadata);
        }

        public static void EncodeAnimatedRgbaToStream(Stream stream, byte[][] rgbaFrames, int width, int height,
            int[] frameDurationsMs, int loopCount, float quality)
            => EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, frameDurationsMs, loopCount,
                new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto));

        public static void EncodeAnimatedRgbaToStream(Stream stream, byte[][] rgbaFrames, int width, int height,
            int[] frameDurationsMs, int loopCount, WebpEncoderOptions options, ImageMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ValidateAnimationInput(rgbaFrames, frameDurationsMs, width, height);

            if (rgbaFrames.Length == 1)
            {
                EncodeRgbaToStream(stream, rgbaFrames[0], width, height, options, metadata);
                return;
            }

            if (loopCount < 0) loopCount = 0;

            WebpAnimationWriter.EncodeToStream(stream, BuildAnimationFrames(rgbaFrames, frameDurationsMs),
                width, height, loopCount,
                new WebpAnimationEncodeOptions(options.Lossless, options.Quality), metadata);
        }

        private static void ValidateAnimationInput(byte[][] rgbaFrames, int[] frameDurationsMs, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(rgbaFrames, nameof(rgbaFrames));
            ArgumentNullException.ThrowIfNull(frameDurationsMs, nameof(frameDurationsMs));
            if (rgbaFrames.Length == 0) throw new ArgumentOutOfRangeException(nameof(rgbaFrames));
            if (rgbaFrames.Length != frameDurationsMs.Length) throw new ArgumentException("帧与时长数量不一致");

            int frameBytes = checked(width * height * 4);
            for (int i = 0; i < rgbaFrames.Length; i++)
            {
                if (rgbaFrames[i] is null || rgbaFrames[i].Length != frameBytes)
                    throw new ArgumentException("RGBA 帧尺寸不一致");
            }
        }

        /// <summary>
        /// 把紧凑的帧输入转换为动图编码器输入。
        /// 与旧的 libwebp mux 行为对齐：所有帧不混合、帧结束后清除矩形；时长下限 10ms。
        /// </summary>
        private static List<WebpAnimationFrameInput> BuildAnimationFrames(byte[][] rgbaFrames, int[] frameDurationsMs)
        {
            var frames = new List<WebpAnimationFrameInput>(rgbaFrames.Length);
            for (int i = 0; i < rgbaFrames.Length; i++)
            {
                int duration = frameDurationsMs[i];
                if (duration < 10) duration = 10;
                frames.Add(new WebpAnimationFrameInput(rgbaFrames[i], duration,
                    Blend: false, Dispose: WebpAnimationDispose.Background));
            }
            return frames;
        }

        private static byte[] ExpandRgb(ReadOnlySpan<byte> rgb, int width, int height)
        {
            if (rgb.Length != checked(width * height * 3)) throw new ArgumentException("RGB 像素长度不匹配", nameof(rgb));
            var rgba = new byte[checked(width * height * 4)];
            SimdHelper.ExpandRgbToRgba(rgb, rgba);
            return rgba;
        }
    }
}
