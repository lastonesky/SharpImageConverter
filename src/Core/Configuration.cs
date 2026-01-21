using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Formats;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 框架配置：注册支持的格式、解码器与编码器。
    /// </summary>
    public sealed class Configuration
    {
        private readonly List<IImageFormat> _formats = new();
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
            var jpeg = new JpegFormat();
            var png = new PngFormat();
            var bmp = new BmpFormat();
            var webp = new WebpFormat();
            var gif = new GifFormat();
            cfg._formats.Add(jpeg);
            cfg._formats.Add(png);
            cfg._formats.Add(bmp);
            cfg._formats.Add(webp);
            cfg._formats.Add(gif);
            cfg._decoders[typeof(JpegFormat)] = new JpegDecoderAdapter();
            cfg._decoders[typeof(PngFormat)] = new PngDecoderAdapter();
            cfg._decoders[typeof(BmpFormat)] = new BmpDecoderAdapter();
            cfg._decoders[typeof(WebpFormat)] = new WebpDecoderAdapter();
            cfg._decoders[typeof(GifFormat)] = new GifDecoder();
            cfg._encoders[typeof(JpegFormat)] = new JpegEncoderAdapter();
            cfg._encoders[typeof(PngFormat)] = new PngEncoderAdapter();
            cfg._encoders[typeof(BmpFormat)] = new BmpEncoderAdapter();
            cfg._encoders[typeof(WebpFormat)] = new WebpEncoderAdapter();
            cfg._encoders[typeof(GifFormat)] = new GifEncoderAdapter();
            cfg._decodersRgba[typeof(PngFormat)] = new PngDecoderAdapterRgba();
            cfg._decodersRgba[typeof(WebpFormat)] = new WebpDecoderAdapterRgba();
            cfg._decodersRgba[typeof(GifFormat)] = new GifDecoderRgbaAdapter();
            cfg._encodersRgba[typeof(PngFormat)] = new PngEncoderAdapterRgba();
            cfg._encodersRgba[typeof(WebpFormat)] = new WebpEncoderAdapterRgba();
            cfg._encodersRgba[typeof(GifFormat)] = new GifEncoderAdapterRgba();
            return cfg;
        }

        /// <summary>
        /// 加载为 Rgb24 图像（自动识别格式）
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> LoadRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            foreach (var f in _formats)
            {
                fs.Position = 0;
                if (f.IsMatch(fs))
                {
                    var dec = _decoders[f.GetType()];
                    return dec.DecodeRgb24(path);
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

        /// <summary>
        /// 加载为 Rgba32 图像（自动识别格式，优先使用原生 RGBA 解码）
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgba32 图像</returns>
        public Image<Rgba32> LoadRgba32(string path)
        {
            using var fs = File.OpenRead(path);
            foreach (var f in _formats)
            {
                fs.Position = 0;
                if (f.IsMatch(fs))
                {
                    if (_decodersRgba.TryGetValue(f.GetType(), out var decRgba))
                    {
                        return decRgba.DecodeRgba32(path);
                    }
                    var rgb = _decoders[f.GetType()].DecodeRgb24(path);
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
    }
}
