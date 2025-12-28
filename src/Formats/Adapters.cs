using System;
using PictureSharp.Core;
using PictureSharp;
using System.IO;

namespace PictureSharp.Formats
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

    public sealed class WebpDecoderAdapter : IImageDecoder
    {
        public Image<Rgb24> DecodeRgb24(string path)
        {
            var rgba = WebpCodec.DecodeRgba(File.ReadAllBytes(path), out int width, out int height);
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

    public sealed class WebpEncoderAdapter : IImageEncoder
    {
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            var rgba = new byte[image.Width * image.Height * 4];
            for (int i = 0, j = 0; j < image.Buffer.Length; i += 4, j += 3)
            {
                rgba[i + 0] = image.Buffer[j + 0];
                rgba[i + 1] = image.Buffer[j + 1];
                rgba[i + 2] = image.Buffer[j + 2];
                rgba[i + 3] = 255;
            }
            var webp = WebpCodec.EncodeRgba(rgba, image.Width, image.Height, 75f);
            File.WriteAllBytes(path, webp);
        }
    }
}
