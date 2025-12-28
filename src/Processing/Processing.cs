using System;
using PictureSharp.Core;

namespace PictureSharp.Processing
{
    public interface IImageProcessor
    {
        void Execute(Image<Rgb24> image);
    }

    public sealed class ImageProcessingContext
    {
        private readonly Image<Rgb24> _image;
        public ImageProcessingContext(Image<Rgb24> image) { _image = image; }
        public ImageProcessingContext Resize(int width, int height)
        {
            var src = _image.Buffer;
            var dst = new byte[width * height * 3];
            int sw = _image.Width, sh = _image.Height;
            for (int y = 0; y < height; y++)
            {
                int sy = (int)((long)y * sh / height);
                for (int x = 0; x < width; x++)
                {
                    int sx = (int)((long)x * sw / width);
                    int s = (sy * sw + sx) * 3;
                    int d = (y * width + x) * 3;
                    dst[d + 0] = src[s + 0];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                }
            }
            _image.Update(width, height, dst);
            return this;
        }

        public ImageProcessingContext ResizeToFit(int maxWidth, int maxHeight)
        {
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0) return this;
            if (maxWidth <= 0 || maxHeight <= 0) return this;

            double scaleW = (double)maxWidth / sw;
            double scaleH = (double)maxHeight / sh;
            double scale = Math.Min(scaleW, scaleH);

            int w = Math.Max(1, (int)Math.Round(sw * scale));
            int h = Math.Max(1, (int)Math.Round(sh * scale));

            return Resize(w, h);
        }
        public ImageProcessingContext Grayscale()
        {
            var buf = _image.Buffer;
            int n = buf.Length / 3;
            for (int i = 0; i < n; i++)
            {
                int o = i * 3;
                int r = buf[o + 0], g = buf[o + 1], b = buf[o + 2];
                int y = (77 * r + 150 * g + 29 * b) >> 8;
                byte yy = (byte)y;
                buf[o + 0] = yy;
                buf[o + 1] = yy;
                buf[o + 2] = yy;
            }
            return this;
        }
    }

    public static class ImageExtensions
    {
        public static void Mutate(this Image<Rgb24> image, Action<ImageProcessingContext> action)
        {
            var ctx = new ImageProcessingContext(image);
            action(ctx);
        }
    }
}
