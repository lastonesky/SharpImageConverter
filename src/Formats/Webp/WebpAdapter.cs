using System;
using PictureSharp.Core;
using PictureSharp;
using System.IO;

namespace PictureSharp.Formats
{
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
