using System;
using PictureSharp.Core;
using PictureSharp;
using System.IO;

namespace PictureSharp.Formats.Gif
{
    public sealed class GifEncoderAdapter : IImageEncoder
    {
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            var encoder = new GifEncoder();
            using var fs = File.Create(path);
            var frame = new ImageFrame(image.Width, image.Height, image.Buffer);
            encoder.Encode(frame, fs);
        }
    }
}
