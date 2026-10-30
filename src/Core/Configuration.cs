using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Formats;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 框架配置：注册支持的格式、解码器与编码器。
    /// </summary>
    public sealed class Configuration
    {
        private readonly List<IImageFormat> _formats = new();
        private readonly Dictionary<Type, IImageFormat> _formatsByType = new();
        private readonly Dictionary<string, Type> _formatTypeByExtension = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Type, IImageDecoder> _decoders = new();
        private readonly Dictionary<Type, IImageEncoder> _encoders = new();
        private readonly Dictionary<Type, IImageDecoderRgba> _decodersRgba = new();
        private readonly Dictionary<Type, IImageEncoderRgba> _encodersRgba = new();

        /// <summary>
        /// 默认配置实例
        /// </summary>
        public static Configuration Default { get; } = CreateDefault();

        private static Configuration CreateDefault()
        {
            var cfg = new Configuration();
            cfg.Register(
                new JpegFormat(),
                new JpegDecoderAdapter(),
                new JpegEncoderAdapter());
            cfg.Register(
                new PngFormat(),
                new PngDecoderAdapter(),
                new PngEncoderAdapter(),
                new PngDecoderAdapterRgba(),
                new PngEncoderAdapterRgba());
            cfg.Register(
                new BmpFormat(),
                new BmpDecoderAdapter(),
                new BmpEncoderAdapter());
            cfg.Register(
                new WebpFormat(),
                new WebpDecoderAdapter(),
                new WebpEncoderAdapter(),
                new WebpDecoderAdapterRgba(),
                new WebpEncoderAdapterRgba());
            cfg.Register(
                new GifFormat(),
                new GifDecoder(),
                new GifEncoderAdapter(),
                new GifDecoderRgbaAdapter(),
                new GifEncoderAdapterRgba());
            return cfg;
        }

        public void Register(
            IImageFormat format,
            IImageDecoder decoder,
            IImageEncoder encoder,
            IImageDecoderRgba? decoderRgba = null,
            IImageEncoderRgba? encoderRgba = null,
            bool prepend = false)
        {
            ArgumentNullException.ThrowIfNull(format);
            ArgumentNullException.ThrowIfNull(decoder);
            ArgumentNullException.ThrowIfNull(encoder);
            UpsertFormat(format, prepend);
            Type formatType = format.GetType();
            _decoders[formatType] = decoder;
            _encoders[formatType] = encoder;
            if (decoderRgba != null)
            {
                _decodersRgba[formatType] = decoderRgba;
            }
            if (encoderRgba != null)
            {
                _encodersRgba[formatType] = encoderRgba;
            }
        }

        public void RegisterFormat(IImageFormat format, bool prepend = false)
        {
            ArgumentNullException.ThrowIfNull(format);
            UpsertFormat(format, prepend);
        }

        public void RegisterDecoder<TFormat>(IImageDecoder decoder) where TFormat : IImageFormat
        {
            ArgumentNullException.ThrowIfNull(decoder);
            _decoders[typeof(TFormat)] = decoder;
        }

        public void RegisterEncoder<TFormat>(IImageEncoder encoder) where TFormat : IImageFormat
        {
            ArgumentNullException.ThrowIfNull(encoder);
            _encoders[typeof(TFormat)] = encoder;
        }

        public void RegisterDecoderRgba<TFormat>(IImageDecoderRgba decoder) where TFormat : IImageFormat
        {
            ArgumentNullException.ThrowIfNull(decoder);
            _decodersRgba[typeof(TFormat)] = decoder;
        }

        public void RegisterEncoderRgba<TFormat>(IImageEncoderRgba encoder) where TFormat : IImageFormat
        {
            ArgumentNullException.ThrowIfNull(encoder);
            _encodersRgba[typeof(TFormat)] = encoder;
        }

        /// <summary>
        /// 加载为 Rgb24 图像（自动识别格式）
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> LoadRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            return LoadRgb24(fs, Path.GetExtension(path));
        }

        /// <summary>
        /// 从流加载为 Rgb24 图像（按文件头自动识别格式）。
        /// </summary>
        /// <remarks>
        /// 流必须可 Seek：嗅探需要反复回到起点。不可 Seek 的输入请用
        /// <see cref="ImageFrame.Load(Stream, bool)"/>（它会把已读部分缓存起来）。
        /// 加载完成后流位置不确定，调用方不要依赖。
        /// </remarks>
        /// <param name="stream">输入数据流</param>
        /// <param name="extensionHint">可选的扩展名提示（如 ".png"）；只决定先试哪个解码器，不影响最终判定</param>
        /// <returns>Rgb24 图像</returns>
        /// <exception cref="NotSupportedException">没有可用的解码器能识别该数据</exception>
        public Image<Rgb24> LoadRgb24(Stream stream, string? extensionHint = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanSeek)
            {
                throw new ArgumentException(
                    "格式嗅探需要可 Seek 的流；不可 Seek 的输入请改用 ImageFrame.Load(Stream)。",
                    nameof(stream));
            }

            long start = stream.Position;
            Type? matchedType = null;
            if (TryGetPreferredFormatByExtension(extensionHint, out var preferredFormat))
            {
                matchedType = preferredFormat.GetType();
                if (TryDecodeRgb24(preferredFormat, stream, start) is { } preferred)
                {
                    return preferred;
                }
            }
            foreach (var f in _formats)
            {
                if (matchedType != null && f.GetType() == matchedType) continue;
                if (TryDecodeRgb24(f, stream, start) is { } decoded)
                {
                    return decoded;
                }
            }
            throw BuildUnknownFormatException(stream, start);
        }

        /// <summary>
        /// 没有解码器能识别该数据时抛出的异常。先嗅一次文件头：
        /// TIFF / ICO 这类「能识别但不提供读写」的格式要报出格式名，
        /// 否则调用方只能看到「未知图像格式」，无法判断是不是自己传错了文件。
        /// </summary>
        private static NotSupportedException BuildUnknownFormatException(Stream stream, long start)
        {
            long saved = stream.Position;
            try
            {
                Span<byte> head = stackalloc byte[ImageHeaderReader.SniffBytes];
                stream.Position = start;
                int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                var kind = ImageHeaderReader.Sniff(head[..read]);
                if (ImageInfo.IsRecognizedButUnsupported(kind))
                {
                    return new NotSupportedException(ImageInfo.UnsupportedMessage(kind));
                }
            }
            finally
            {
                stream.Position = saved;
            }

            return new NotSupportedException($"未知图像格式（当前支持 {ImageInfo.SupportedFormatList}）");
        }

        /// <summary>
        /// 用指定格式尝试解码为 Rgb24；格式不匹配或该格式没有可用解码器时返回 null。
        /// 原生 Rgb24 解码器缺失但提供 Rgba32 解码器时，解码后降通道复用（与
        /// <see cref="LoadRgba32(string)"/> 的反向回退对称）。
        /// </summary>
        private Image<Rgb24>? TryDecodeRgb24(IImageFormat format, Stream stream, long basePosition = 0)
        {
            Type formatType = format.GetType();
            stream.Position = basePosition;
            if (!format.IsMatch(stream)) return null;

            if (_decoders.TryGetValue(formatType, out var decoder))
            {
                stream.Position = basePosition;
                return decoder.DecodeRgb24(stream);
            }
            if (_decodersRgba.TryGetValue(formatType, out var decoderRgba))
            {
                stream.Position = basePosition;
                var rgba = decoderRgba.DecodeRgba32(stream);
                return new Image<Rgb24>(rgba.Width, rgba.Height, RgbaToRgb(rgba.Buffer), rgba.Metadata);
            }
            // 只登记了格式（RegisterFormat）而没有解码器：跳过，让后面的格式继续尝试，
            // 不要用 KeyNotFoundException 把注册期的疏漏暴露成运行时崩溃。
            return null;
        }

        /// <summary>
        /// 保存 Rgb24 图像到指定路径（按扩展名选择编码器）
        /// </summary>
        /// <param name="image">Rgb24 图像</param>
        /// <param name="path">输出文件路径</param>
        public void SaveRgb24(Image<Rgb24> image, string path)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentException.ThrowIfNullOrEmpty(path);
            Save(image.ToFrame(), path);
        }

        /// <summary>
        /// 保存 Rgb24 图像到流（按 <paramref name="extension"/> 选择编码器）。
        /// </summary>
        /// <param name="image">Rgb24 图像</param>
        /// <param name="stream">输出流（不负责关闭）</param>
        /// <param name="extension">目标格式的扩展名（".png" 或 "png"）</param>
        public void SaveRgb24(Image<Rgb24> image, Stream stream, string extension)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(stream);
            Save(image.ToFrame(), stream, extension);
        }

        /// <summary>
        /// 保存图像帧到指定路径（按扩展名经注册表选择编码器）。
        /// </summary>
        /// <remarks>
        /// 这是**唯一**的保存实现：<see cref="ImageFrame.Save(string)"/> 与
        /// <see cref="SaveRgb24(Image{Rgb24}, string)"/> 都委托到它，
        /// 因此两种图像模型不会各自维护一套「扩展名 → 编码器」派发。
        /// 带 alpha 的帧优先走该格式的 RGBA 编码器，格式表达不了 alpha（JPEG/BMP）时降通道。
        /// </remarks>
        /// <param name="frame">图像帧</param>
        /// <param name="path">输出文件路径</param>
        /// <param name="options">可选保存选项；null 表示用已注册编码器的默认设置</param>
        /// <exception cref="NotSupportedException">扩展名未注册，或该格式没有对应编码器</exception>
        public void Save(ImageFrame frame, string path, SaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentException.ThrowIfNullOrEmpty(path);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            Save(frame, fs, Path.GetExtension(path), options);
        }

        /// <summary>
        /// 保存图像帧到指定路径，**格式由 <paramref name="extension"/> 显式指定**
        /// （不看 <paramref name="path"/> 的扩展名）。
        /// </summary>
        /// <param name="frame">图像帧</param>
        /// <param name="path">输出文件路径</param>
        /// <param name="extension">目标格式的扩展名（".png" 或 "png"）</param>
        /// <param name="options">可选保存选项；null 表示用已注册编码器的默认设置</param>
        /// <exception cref="NotSupportedException">扩展名未注册，或该格式没有对应编码器</exception>
        public void SaveAs(ImageFrame frame, string path, string extension, SaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentException.ThrowIfNullOrEmpty(path);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            Save(frame, fs, extension, options);
        }

        /// <summary>
        /// 保存图像帧到流（按 <paramref name="extension"/> 经注册表选择编码器）。
        /// </summary>
        /// <param name="frame">图像帧</param>
        /// <param name="stream">输出流（不负责关闭）</param>
        /// <param name="extension">目标格式的扩展名（".png" 或 "png"）</param>
        /// <param name="options">可选保存选项；null 表示用已注册编码器的默认设置</param>
        /// <exception cref="NotSupportedException">扩展名未注册，或该格式没有对应编码器</exception>
        public void Save(ImageFrame frame, Stream stream, string extension, SaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(stream);
            options ??= EmptySaveOptions;

            Type formatType = ResolveFormatTypeOrThrow(extension);
            if (HasAlpha(frame.PixelFormat) && _encodersRgba.TryGetValue(formatType, out var encoderRgba))
            {
                encoderRgba.EncodeRgba32(stream, AsRgba32Image(frame.ToRgba32()), options);
                return;
            }
            if (_encoders.TryGetValue(formatType, out var encoder))
            {
                encoder.EncodeRgb24(stream, AsRgb24Image(frame.ToRgb24()), options);
                return;
            }
            throw new NotSupportedException($"格式 {formatType.Name} 未注册 Rgb24 编码器");
        }

        private static readonly SaveOptions EmptySaveOptions = new();

        private static bool HasAlpha(ImagePixelFormat format)
            => format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;

        /// <summary>把帧的缓冲区（不复制）包成类型化图像，供已注册编码器使用。</summary>
        private static Image<Rgb24> AsRgb24Image(ImageFrame frame)
            => new(frame.Width, frame.Height, frame.Pixels, frame.Metadata);

        /// <summary>把帧的缓冲区（不复制）包成类型化图像，供已注册编码器使用。</summary>
        private static Image<Rgba32> AsRgba32Image(ImageFrame frame)
            => new(frame.Width, frame.Height, frame.Pixels, frame.Metadata);

        public void SaveGray8(Image<Gray8> image, string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".bmp")
            {
                BmpWriter.Write8(path, image.Width, image.Height, image.Buffer);
                return;
            }
            if (ext == ".png")
            {
                PngWriter.WriteGray(path, image.Width, image.Height, image.Buffer);
                return;
            }
            if (ext == ".jpg" || ext == ".jpeg")
            {
                JpegEncoder.WriteGray8(path, image.Width, image.Height, image.Buffer, 75);
                return;
            }
            var rgb = new Image<Rgb24>(image.Width, image.Height, GrayToRgb(image.Buffer), image.Metadata);
            SaveRgb24(rgb, path);
        }

        /// <summary>
        /// 加载为 Rgba32 图像（自动识别格式，优先使用原生 RGBA 解码）
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgba32 图像</returns>
        public Image<Rgba32> LoadRgba32(string path)
        {
            using var fs = File.OpenRead(path);
            return LoadRgba32(fs, Path.GetExtension(path));
        }

        /// <summary>
        /// 从流加载为 Rgba32 图像（按文件头自动识别格式）。
        /// </summary>
        /// <remarks>
        /// 流必须可 Seek，理由同 <see cref="LoadRgb24(Stream, string?)"/>。
        /// </remarks>
        /// <param name="stream">输入数据流</param>
        /// <param name="extensionHint">可选的扩展名提示（如 ".png"）</param>
        /// <returns>Rgba32 图像</returns>
        public Image<Rgba32> LoadRgba32(Stream stream, string? extensionHint = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanSeek)
            {
                throw new ArgumentException(
                    "格式嗅探需要可 Seek 的流；不可 Seek 的输入请改用 ImageFrame.Load(Stream)。",
                    nameof(stream));
            }

            long start = stream.Position;
            Type? matchedType = null;
            if (TryGetPreferredFormatByExtension(extensionHint, out var preferredFormat))
            {
                matchedType = preferredFormat.GetType();
                if (TryDecodeRgba32(preferredFormat, stream, start) is { } preferred)
                {
                    return preferred;
                }
            }
            foreach (var f in _formats)
            {
                if (matchedType != null && f.GetType() == matchedType) continue;
                if (TryDecodeRgba32(f, stream, start) is { } decoded)
                {
                    return decoded;
                }
            }
            throw BuildUnknownFormatException(stream, start);
        }

        /// <summary>
        /// 用指定格式尝试解码为 Rgba32；优先原生 RGBA 解码，缺失时用 RGB 解码后补 alpha=255。
        /// 格式不匹配或该格式没有注册任何解码器时返回 null。
        /// </summary>
        private Image<Rgba32>? TryDecodeRgba32(IImageFormat format, Stream stream, long basePosition = 0)
        {
            Type formatType = format.GetType();
            stream.Position = basePosition;
            if (!format.IsMatch(stream)) return null;

            if (_decodersRgba.TryGetValue(formatType, out var decoderRgba))
            {
                stream.Position = basePosition;
                return decoderRgba.DecodeRgba32(stream);
            }
            if (_decoders.TryGetValue(formatType, out var decoder))
            {
                stream.Position = basePosition;
                return ConvertRgbToRgba(decoder.DecodeRgb24(stream));
            }
            return null;
        }

        /// <summary>
        /// 保存 Rgba32 图像到指定路径（按扩展名选择编码器；不支持则回退为 RGB 保存）
        /// </summary>
        /// <param name="image">Rgba32 图像</param>
        /// <param name="path">输出文件路径</param>
        public void SaveRgba32(Image<Rgba32> image, string path)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentException.ThrowIfNullOrEmpty(path);
            Save(image.ToFrame(), path);
        }

        /// <summary>
        /// 保存 Rgba32 图像到流（按 <paramref name="extension"/> 选择编码器；该格式不支持 alpha 时降通道）。
        /// </summary>
        /// <param name="image">Rgba32 图像</param>
        /// <param name="stream">输出流（不负责关闭）</param>
        /// <param name="extension">目标格式的扩展名（".png" 或 "png"）</param>
        public void SaveRgba32(Image<Rgba32> image, Stream stream, string extension)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(stream);
            Save(image.ToFrame(), stream, extension);
        }

        /// <summary>
        /// 按扩展名解析已注册的格式类型；未注册时抛 <see cref="NotSupportedException"/>
        /// （而不是字典访问异常）。
        /// </summary>
        /// <param name="extension">扩展名（".png" 或 "png"）</param>
        private Type ResolveFormatTypeOrThrow(string extension)
        {
            Type? formatType = ResolveFormatTypeByExtensionName(extension);
            if (formatType is null)
            {
                throw new NotSupportedException(
                    $"不支持的输出格式：\"{extension}\"。已注册的扩展名：{string.Join("、", _formatTypeByExtension.Keys)}");
            }
            return formatType;
        }

        /// <summary>
        /// 扩展名 → 已注册格式类型；扩展名未注册时返回 null。
        /// </summary>
        /// <param name="extension">扩展名（".png" 或 "png"）</param>
        private Type? ResolveFormatTypeByExtensionName(string? extension)
        {
            string? normalized = NormalizeExtension(extension);
            if (normalized == null) return null;
            return _formatTypeByExtension.TryGetValue(normalized, out var formatType) ? formatType : null;
        }

        private static byte[] RgbaToRgb(byte[] rgba)
        {
            // 目标缓冲区每个字节都会被写入，跳过 new byte[] 的无谓清零
            var rgb = GC.AllocateUninitializedArray<byte>((rgba.Length / 4) * 3);
            SimdHelper.PackRgbaToRgb(rgba, rgb);
            return rgb;
        }

        private static Image<Rgba32> ConvertRgbToRgba(Image<Rgb24> rgb)
        {
            int pixels = rgb.Buffer.Length / 3;
            var rgbaBuf = GC.AllocateUninitializedArray<byte>(pixels * 4);
            SimdHelper.ExpandRgbToRgba(rgb.Buffer, rgbaBuf);
            return new Image<Rgba32>(rgb.Width, rgb.Height, rgbaBuf, rgb.Metadata);
        }

        private void UpsertFormat(IImageFormat format, bool prepend)
        {
            Type formatType = format.GetType();
            if (_formatsByType.TryGetValue(formatType, out var existing))
            {
                _formats.Remove(existing);
            }
            if (prepend)
            {
                _formats.Insert(0, format);
            }
            else
            {
                _formats.Add(format);
            }
            _formatsByType[formatType] = format;

            var keysToRemove = new List<string>();
            foreach (var pair in _formatTypeByExtension)
            {
                if (pair.Value == formatType)
                {
                    keysToRemove.Add(pair.Key);
                }
            }
            for (int i = 0; i < keysToRemove.Count; i++)
            {
                _formatTypeByExtension.Remove(keysToRemove[i]);
            }

            var extensions = format.Extensions;
            if (extensions == null) return;
            for (int i = 0; i < extensions.Length; i++)
            {
                string? normalized = NormalizeExtension(extensions[i]);
                if (normalized != null)
                {
                    _formatTypeByExtension[normalized] = formatType;
                }
            }
        }

        private bool TryGetPreferredFormat(string path, out IImageFormat format)
            => TryGetPreferredFormatByExtension(Path.GetExtension(path), out format);

        private bool TryGetPreferredFormatByExtension(string? extension, out IImageFormat format)
        {
            format = null!;
            string? normalized = NormalizeExtension(extension);
            if (normalized == null) return false;
            if (!_formatTypeByExtension.TryGetValue(normalized, out var formatType)) return false;
            if (_formatsByType.TryGetValue(formatType, out var matched))
            {
                format = matched;
                return true;
            }
            return false;
        }

        private static string? NormalizeExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return null;
            return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
        }

        private static byte[] GrayToRgb(byte[] gray)
        {
            var rgb = GC.AllocateUninitializedArray<byte>(gray.Length * 3);
            SimdHelper.ExpandGrayToRgb(gray, rgb);
            return rgb;
        }
    }
}
