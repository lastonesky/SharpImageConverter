using System;
using PictureSharp.Core;
using PictureSharp;
using System.IO;

namespace PictureSharp.Formats
{
    public sealed class BmpDecoderAdapter : IImageDecoder
    {
        public Image<Rgb24> DecodeRgb24(string path)
        {
            var rgb = BmpReader.Read(path, out int width, out int height);
            return new Image<Rgb24>(width, height, rgb);
        }
    }

    public sealed class BmpEncoderAdapter : IImageEncoder
    {
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            BmpWriter.Write24(path, image.Width, image.Height, image.Buffer);
        }
    }
}
