using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;

namespace SharpImageConverter.Formats
{
    public sealed class JpegDecoderAdapter : IImageDecoder
    {
        public Image<Rgb24> DecodeRgb24(string path)
        {
            var parser = new JpegParser();
            parser.Parse(path);
            var dec = new JpegDecoder(parser);
            var rgb = dec.DecodeToRGB(path);
            var img = new Image<Rgb24>(parser.Width, parser.Height, rgb);
            if (parser.ExifOrientation != 1)
            {
                var frame = new ImageFrame(parser.Width, parser.Height, rgb);
                frame = frame.ApplyExifOrientation(parser.ExifOrientation);
                img.Update(frame.Width, frame.Height, frame.Pixels);
            }
            return img;
        }
    }

    public sealed class JpegEncoderAdapter : IImageEncoder
    {
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            JpegEncoder.Write(path, image.Width, image.Height, image.Buffer, 75);
        }
    }
}
