using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;

namespace SharpImageConverter.Formats
{
    public sealed class PngDecoderAdapter : IImageDecoder
    {
        public Image<Rgb24> DecodeRgb24(string path)
        {
            var dec = new PngDecoder();
            var rgb = dec.DecodeToRGB(path);
            return new Image<Rgb24>(dec.Width, dec.Height, rgb);
        }
    }

    public sealed class PngEncoderAdapter : IImageEncoder
    {
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            PngWriter.Write(path, image.Width, image.Height, image.Buffer);
        }
    }
}
