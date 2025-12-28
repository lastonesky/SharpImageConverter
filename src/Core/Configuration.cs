using System;
using System.Collections.Generic;
using System.IO;
using PictureSharp.Formats;

namespace PictureSharp.Core
{
    public sealed class Configuration
    {
        private readonly List<IImageFormat> _formats = new();
        private readonly Dictionary<Type, IImageDecoder> _decoders = new();
        private readonly Dictionary<Type, IImageEncoder> _encoders = new();

        public static Configuration Default { get; } = CreateDefault();

        private static Configuration CreateDefault()
        {
            var cfg = new Configuration();
            var jpeg = new JpegFormat();
            var png = new PngFormat();
            var bmp = new BmpFormat();
            var webp = new WebpFormat();
            cfg._formats.Add(jpeg);
            cfg._formats.Add(png);
            cfg._formats.Add(bmp);
            cfg._formats.Add(webp);
            cfg._decoders[typeof(JpegFormat)] = new JpegDecoderAdapter();
            cfg._decoders[typeof(PngFormat)] = new PngDecoderAdapter();
            cfg._decoders[typeof(BmpFormat)] = new BmpDecoderAdapter();
            cfg._decoders[typeof(WebpFormat)] = new WebpDecoderAdapter();
            cfg._encoders[typeof(JpegFormat)] = new JpegEncoderAdapter();
            cfg._encoders[typeof(PngFormat)] = new PngEncoderAdapter();
            cfg._encoders[typeof(BmpFormat)] = new BmpEncoderAdapter();
            cfg._encoders[typeof(WebpFormat)] = new WebpEncoderAdapter();
            return cfg;
        }

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

        public void SaveRgb24(Image<Rgb24> image, string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            IImageEncoder? enc = null;
            if (ext == ".jpg" || ext == ".jpeg") enc = _encoders[typeof(JpegFormat)];
            else if (ext == ".png") enc = _encoders[typeof(PngFormat)];
            else if (ext == ".bmp") enc = _encoders[typeof(BmpFormat)];
            else if (ext == ".webp") enc = _encoders[typeof(WebpFormat)];
            if (enc == null) throw new NotSupportedException("不支持的输出格式");
            enc.EncodeRgb24(path, image);
        }
    }
}
