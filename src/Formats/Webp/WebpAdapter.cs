using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;
using System.Collections.Generic;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>
    /// WebP 编码器选项
    /// </summary>
    public readonly struct WebpEncoderOptions
    {
        public float Quality { get; }

        public WebpEncoderOptions(float quality)
        {
            if (quality < 0f || quality > 100f) throw new ArgumentOutOfRangeException(nameof(quality));
            Quality = quality;
        }

        public static WebpEncoderOptions Default => new WebpEncoderOptions(75f);
    }

    internal static class WebpStreamDecoder
    {
        internal static byte[] DecodeRgbaFromStream(Stream stream, out int width, out int height)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment))
            {
                ReadOnlySpan<byte> span = segment.AsSpan(0, (int)ms.Length);
                return WebpCodec.DecodeRgba(span, out width, out height);
            }

            using var tempMs = new MemoryStream();
            stream.CopyTo(tempMs);

            if (tempMs.TryGetBuffer(out ArraySegment<byte> segment2))
            {
                ReadOnlySpan<byte> span = segment2.AsSpan(0, (int)tempMs.Length);
                return WebpCodec.DecodeRgba(span, out width, out height);
            }

            var data = tempMs.ToArray();
            return WebpCodec.DecodeRgba(data, out width, out height);
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
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j + 0] = rgba[i + 0];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
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
        private static WebpEncoderOptions _defaultOptions = WebpEncoderOptions.Default;

        public static float Quality
        {
            get => _defaultOptions.Quality;
            set => _defaultOptions = new WebpEncoderOptions(value);
        }

        internal static WebpEncoderOptions DefaultOptions => _defaultOptions;

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
            var webp = WebpCodec.EncodeRgb(image.Buffer, image.Width, image.Height, DefaultOptions);
            stream.Write(webp, 0, webp.Length);
        }
    }

    /// <summary>
    /// WebP 编码器适配器（RGBA32）
    /// </summary>
    public sealed class WebpEncoderAdapterRgba : IImageEncoderRgba
    {
        private static WebpEncoderOptions _defaultOptions = WebpEncoderOptions.Default;

        public static float Quality
        {
            get => _defaultOptions.Quality;
            set => _defaultOptions = new WebpEncoderOptions(value);
        }

        public static float DefaultQuality
        {
            get => Quality;
            set => Quality = value;
        }

        internal static WebpEncoderOptions DefaultOptions => _defaultOptions;

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
            var webp = WebpCodec.EncodeRgba(image.Buffer, image.Width, image.Height, DefaultOptions);
            stream.Write(webp, 0, webp.Length);
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
                for (int i = 0, j = 0; j < frame.Buffer.Length; i += 4, j += 3)
                {
                    rgba[i + 0] = frame.Buffer[j + 0];
                    rgba[i + 1] = frame.Buffer[j + 1];
                    rgba[i + 2] = frame.Buffer[j + 2];
                    rgba[i + 3] = 255;
                }
                rgbaFrames[fi] = rgba;

                int d = frameDurationsMs[fi];
                durations[fi] = d < 10 ? 10 : d;
            }

            var webp = WebpCodec.EncodeAnimatedRgba(rgbaFrames, width, height, durations, loopCount, quality);
            stream.Write(webp, 0, webp.Length);
        }
    }
}
