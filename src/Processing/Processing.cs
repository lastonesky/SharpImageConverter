using System;
using SharpImageConverter.Core;

namespace SharpImageConverter.Processing
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

        public ImageProcessingContext ResizeBilinear(int width, int height)
        {
            var src = _image.Buffer;
            var dst = new byte[width * height * 3];
            int sw = _image.Width, sh = _image.Height;
            double scaleX = sw <= 1 ? 0 : (double)(sw - 1) / Math.Max(1, width - 1);
            double scaleY = sh <= 1 ? 0 : (double)(sh - 1) / Math.Max(1, height - 1);
            for (int y = 0; y < height; y++)
            {
                double syf = y * scaleY;
                int y0 = (int)syf;
                int y1 = y0 + 1; if (y1 >= sh) y1 = sh - 1;
                double ty = syf - y0;
                for (int x = 0; x < width; x++)
                {
                    double sxf = x * scaleX;
                    int x0 = (int)sxf;
                    int x1 = x0 + 1; if (x1 >= sw) x1 = sw - 1;
                    double tx = sxf - x0;
                    int s00 = (y0 * sw + x0) * 3;
                    int s10 = (y0 * sw + x1) * 3;
                    int s01 = (y1 * sw + x0) * 3;
                    int s11 = (y1 * sw + x1) * 3;
                    int d = (y * width + x) * 3;
                    double r0 = src[s00 + 0] * (1 - tx) + src[s10 + 0] * tx;
                    double r1 = src[s01 + 0] * (1 - tx) + src[s11 + 0] * tx;
                    dst[d + 0] = (byte)(r0 * (1 - ty) + r1 * ty + 0.5);
                    double g0 = src[s00 + 1] * (1 - tx) + src[s10 + 1] * tx;
                    double g1 = src[s01 + 1] * (1 - tx) + src[s11 + 1] * tx;
                    dst[d + 1] = (byte)(g0 * (1 - ty) + g1 * ty + 0.5);
                    double b0 = src[s00 + 2] * (1 - tx) + src[s10 + 2] * tx;
                    double b1 = src[s01 + 2] * (1 - tx) + src[s11 + 2] * tx;
                    dst[d + 2] = (byte)(b0 * (1 - ty) + b1 * ty + 0.5);
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
