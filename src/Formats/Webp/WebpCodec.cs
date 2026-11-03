using System;
using System.IO;
using SharpImageConverter.Metadata;
using SharpImageConverter.Formats.Webp.Unmanaged;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>
    /// WebP 编解码的公共入口：默认走纯 C# 实现，只有显式选择
    /// <see cref="WebpEncodeBackend.LibWebp"/> / <see cref="WebpDecodeBackend.LibWebp"/>
    /// 或调用仍以 native 为实现的适配器（如本类的 <c>EncodeAnimatedRgba</c>）时才落到
    /// <see cref="LibWebpCodec"/>。纯 C# 侧的动图编码见 <see cref="WebpAnimationWriter"/>，
    /// 逐行解码见 <see cref="WebpRowSource"/>。
    /// </summary>
    internal static class WebpCodec
    {
        public static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height)
            => LibWebpCodec.DecodeRgba(data, out width, out height);

        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
            => LibWebpCodec.DecodeRgba(data, out width, out height);

        public static byte[] DecodeRgbaFromStream(Stream stream, out int width, out int height)
            => LibWebpCodec.DecodeRgbaFromStream(stream, out width, out height);

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, float quality)
            => LibWebpCodec.EncodeRgba(rgba, width, height, quality);

        public static byte[] EncodeRgb(byte[] rgb, int width, int height, float quality)
            => LibWebpCodec.EncodeRgb(rgb, width, height, quality);

        /// <summary>供编码策略分派使用：强制走 libwebp 的无损入口。</summary>
        internal static byte[] EncodeLosslessRgbaForDispatch(ReadOnlySpan<byte> rgba, int width, int height)
            => LibWebpCodec.EncodeLosslessRgbaForDispatch(rgba, width, height);

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, WebpEncoderOptions options,
            ImageMetadata? metadata = null)
        {
            try
            {
                byte[]? result = null;
                LibWebpCodec.ExecuteWithConcurrency(options.ConcurrencyStrategy, () =>
                {
                    // libwebp 的 config 路径在 lossless=1 时实测仍会做近无损预处理（像素差 1~5）。
                    // 无损统一走官方无损入口：默认由纯 C# VP8L 编码器完成，可用策略强制走 libwebp。
                    if (options.Lossless)
                    {
                        result = WebpEncodeDispatch.EncodeLosslessRgba(
                            rgba, width, height, WebpEncodeDispatch.ShouldEncodeLosslessCSharp(), metadata);
                        return;
                    }

                    if (WebpEncodeDispatch.ShouldEncodeLossyCSharp())
                    {
                        result = WebpEncoder.EncodeLossyRgba(rgba, width, height, options.Quality, metadata);
                        return;
                    }

                    byte[] encoded = LibWebpCodec.EncodeWithConfig(rgba, width, height, width * 4, true, options.Quality,
                        options.Method, false, false);
                    // 有损产物由 libwebp 生成；元数据由托管容器层原样附加（ICCP 前置、EXIF/XMP 后置）。
                    result = WebpMetadataWriter.Apply(encoded, width, height, metadata);
                });
                return result!;
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("未能加载 WebP 原生库，请检查 runtimes 目录或平台匹配。", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException("WebP 原生库版本不兼容，缺少所需入口点。", ex);
            }
        }

        public static byte[] EncodeRgb(byte[] rgb, int width, int height, WebpEncoderOptions options,
            ImageMetadata? metadata = null)
            => LibWebpCodec.EncodeRgb(rgb, width, height, options, metadata);

        public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height, float quality)
            => LibWebpCodec.EncodeRgbaToStream(stream, rgba, width, height, quality);

        public static void EncodeRgbToStream(Stream stream, ReadOnlySpan<byte> rgb, int width, int height, float quality)
            => LibWebpCodec.EncodeRgbToStream(stream, rgb, width, height, quality);

        public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height,
            WebpEncoderOptions options, ImageMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA 像素长度不匹配", nameof(rgba));
            try
            {
                if (options.Lossless)
                {
                    byte[] bytes = WebpEncodeDispatch.EncodeLosslessRgba(
                        rgba, width, height, WebpEncodeDispatch.ShouldEncodeLosslessCSharp(), metadata);
                    stream.Write(bytes, 0, bytes.Length);
                    return;
                }
                if (WebpEncodeDispatch.ShouldEncodeLossyCSharp())
                {
                    WebpEncoder.EncodeLossyRgbaToStream(stream, rgba, width, height, options.Quality,
                        metadata);
                    return;
                }
                if (WebpMetadataWriter.HasMetadata(metadata))
                {
                    // 需要重写容器以插入元数据，只能先拿到完整字节。
                    byte[] encoded = options.ConcurrencyStrategy == WebpConcurrencyStrategy.Parallel
                        ? LibWebpCodec.EncodeWithConfig(rgba, width, height, width * 4, true, options.Quality,
                            options.Method, false, false)
                        : LibWebpCodec.EncodeWithConfigLocked(rgba, width, height, width * 4, true, options.Quality,
                            options.Method, false, false);
                    byte[] wrapped = WebpMetadataWriter.Apply(encoded, width, height, metadata);
                    stream.Write(wrapped, 0, wrapped.Length);
                    return;
                }
                if (options.ConcurrencyStrategy == WebpConcurrencyStrategy.Parallel)
                {
                    LibWebpCodec.EncodeWithConfigToStream(stream, rgba, width, height, width * 4, true, options.Quality, options.Method, false, false);
                }
                else
                {
                    LibWebpCodec.EncodeWithConfigToStreamLocked(stream, rgba, width, height, width * 4, true, options.Quality, options.Method, false, false);
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("未能加载 WebP 原生库，请检查 runtimes 目录或平台匹配。", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException("WebP 原生库版本不兼容，缺少所需入口点。", ex);
            }
        }

        public static void EncodeRgbToStream(Stream stream, ReadOnlySpan<byte> rgb, int width, int height,
            WebpEncoderOptions options, ImageMetadata? metadata = null)
            => LibWebpCodec.EncodeRgbToStream(stream, rgb, width, height, options, metadata);

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, float quality)
            => LibWebpCodec.EncodeAnimatedRgba(rgbaFrames, width, height, frameDurationsMs, loopCount, quality);

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, WebpEncoderOptions options,
            ImageMetadata? metadata = null)
            => LibWebpCodec.EncodeAnimatedRgba(rgbaFrames, width, height, frameDurationsMs, loopCount, options, metadata);

        public static void EncodeAnimatedRgbaToStream(Stream stream, byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, float quality)
            => LibWebpCodec.EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, frameDurationsMs, loopCount, quality);

        public static void EncodeAnimatedRgbaToStream(Stream stream, byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, WebpEncoderOptions options,
            ImageMetadata? metadata = null)
            => LibWebpCodec.EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, frameDurationsMs, loopCount, options, metadata);
    }
}
