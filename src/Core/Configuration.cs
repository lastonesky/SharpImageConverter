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
            Type? matchedType = null;
            if (TryGetPreferredFormat(path, out var preferredFormat))
            {
                matchedType = preferredFormat.GetType();
                if (_decoders.TryGetValue(matchedType, out var preferredDecoder))
                {
                    fs.Position = 0;
                    if (preferredFormat.IsMatch(fs))
                    {
                        fs.Position = 0;
                        return preferredDecoder.DecodeRgb24(fs);
                    }
                }
            }
            foreach (var f in _formats)
            {
                if (matchedType != null && f.GetType() == matchedType) continue;
                fs.Position = 0;
                if (f.IsMatch(fs))
                {
                    var dec = _decoders[f.GetType()];
                    fs.Position = 0;
                    return dec.DecodeRgb24(fs);
                }
            }
            throw new NotSupportedException("未知图像格式");
        }

        /// <summary>
        /// 保存 Rgb24 图像到指定路径（按扩展名选择编码器）
        /// </summary>
        /// <param name="image">Rgb24 图像</param>
        /// <param name="path">输出文件路径</param>
        public void SaveRgb24(Image<Rgb24> image, string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            IImageEncoder? enc = null;
            if (ext == ".jpg" || ext == ".jpeg") enc = _encoders[typeof(JpegFormat)];
            else if (ext == ".png") enc = _encoders[typeof(PngFormat)];
            else if (ext == ".bmp") enc = _encoders[typeof(BmpFormat)];
            else if (ext == ".webp") enc = _encoders[typeof(WebpFormat)];
            else if (ext == ".gif") enc = _encoders[typeof(GifFormat)];
            if (enc == null) throw new NotSupportedException("不支持的输出格式");
            enc.EncodeRgb24(path, image);
        }

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
            Type? matchedType = null;
            if (TryGetPreferredFormat(path, out var preferredFormat))
            {
                matchedType = preferredFormat.GetType();
                fs.Position = 0;
                if (preferredFormat.IsMatch(fs))
                {
                    if (_decodersRgba.TryGetValue(matchedType, out var preferredRgbaDecoder))
                    {
                        fs.Position = 0;
                        return preferredRgbaDecoder.DecodeRgba32(fs);
                    }
                    if (_decoders.TryGetValue(matchedType, out var preferredRgbDecoder))
                    {
                        fs.Position = 0;
                        var rgbPreferred = preferredRgbDecoder.DecodeRgb24(fs);
                        return ConvertRgbToRgba(rgbPreferred);
                    }
                }
            }
            foreach (var f in _formats)
            {
                if (matchedType != null && f.GetType() == matchedType) continue;
                fs.Position = 0;
                if (f.IsMatch(fs))
                {
                    if (_decodersRgba.TryGetValue(f.GetType(), out var decRgba))
                    {
                        fs.Position = 0;
                        return decRgba.DecodeRgba32(fs);
                    }
                    fs.Position = 0;
                    var rgb = _decoders[f.GetType()].DecodeRgb24(fs);
                    return ConvertRgbToRgba(rgb);
                }
            }
            throw new NotSupportedException("未知图像格式");
        }

        /// <summary>
        /// 保存 Rgba32 图像到指定路径（按扩展名选择编码器；不支持则回退为 RGB 保存）
        /// </summary>
        /// <param name="image">Rgba32 图像</param>
        /// <param name="path">输出文件路径</param>
        public void SaveRgba32(Image<Rgba32> image, string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            IImageEncoderRgba? enc = null;
            if (ext == ".png") enc = _encodersRgba.TryGetValue(typeof(PngFormat), out var e1) ? e1 : null;
            else if (ext == ".webp") enc = _encodersRgba.TryGetValue(typeof(WebpFormat), out var e2) ? e2 : null;
            else if (ext == ".gif") enc = _encodersRgba.TryGetValue(typeof(GifFormat), out var e3) ? e3 : null;
            if (enc != null)
            {
                enc.EncodeRgba32(path, image);
                return;
            }
            var rgb = new Image<Rgb24>(image.Width, image.Height, RgbaToRgb(image.Buffer), image.Metadata);
            SaveRgb24(rgb, path);
        }

        private static byte[] RgbaToRgb(byte[] rgba)
        {
            var rgb = new byte[(rgba.Length / 4) * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j + 0] = rgba[i + 0];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
            return rgb;
        }

        private static Image<Rgba32> ConvertRgbToRgba(Image<Rgb24> rgb)
        {
            var rgbaBuf = new byte[rgb.Width * rgb.Height * 4];
            for (int i = 0, j = 0; j < rgb.Buffer.Length; i += 4, j += 3)
            {
                rgbaBuf[i + 0] = rgb.Buffer[j + 0];
                rgbaBuf[i + 1] = rgb.Buffer[j + 1];
                rgbaBuf[i + 2] = rgb.Buffer[j + 2];
                rgbaBuf[i + 3] = 255;
            }
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
        {
            format = null!;
            string? normalized = NormalizeExtension(Path.GetExtension(path));
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
            var rgb = new byte[gray.Length * 3];
            for (int i = 0, j = 0; i < gray.Length; i++, j += 3)
            {
                byte v = gray[i];
                rgb[j + 0] = v;
                rgb[j + 1] = v;
                rgb[j + 2] = v;
            }
            return rgb;
        }
    }
}
