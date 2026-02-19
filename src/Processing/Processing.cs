using System;
using System.Numerics;
using System.Threading.Tasks;
using SharpImageConverter.Core;

namespace SharpImageConverter.Processing
{
    /// <summary>
    /// 图像处理器接口，用于对图像执行处理操作。
    /// </summary>
    public interface IImageProcessor
    {
        /// <summary>
        /// 执行处理操作
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        void Execute(Image<Rgb24> image);
    }

    /// <summary>
    /// 图像处理上下文，提供 resize、灰度等处理方法。
    /// </summary>
    public sealed class ImageProcessingContext
    {
        private readonly Image<Rgb24> _image;
        /// <summary>
        /// 使用指定图像创建处理上下文
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        public ImageProcessingContext(Image<Rgb24> image) { _image = image; }
        /// <summary>
        /// 最近邻缩放到指定尺寸
        /// </summary>
        /// <param name="width">目标宽度</param>
        /// <param name="height">目标高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext Resize(int width, int height)
        {
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0) return this;
            if (sw == width && sh == height) return this;

            bool isUpscale = width > sw || height > sh;
            if (isUpscale)
            {
                return ResizeBicubicOptimized(width, height);
            }

            return ResizeArea(width, height);
        }

        /// <summary>
        /// 双线性插值缩放到指定尺寸
        /// </summary>
        /// <param name="width">目标宽度</param>
        /// <param name="height">目标高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext ResizeBilinear(int width, int height)
        {
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0) return this;
            if (sw == width && sh == height) return this;
            var src = _image.Buffer;
            var dst = new byte[width * height * 3];
            float scaleX = sw <= 1 ? 0f : (float)(sw - 1) / Math.Max(1, width - 1);
            float scaleY = sh <= 1 ? 0f : (float)(sh - 1) / Math.Max(1, height - 1);
            int[] x0Index = new int[width];
            int[] x1Index = new int[width];
            float[] wx0Arr = new float[width];
            float[] wx1Arr = new float[width];
            for (int x = 0; x < width; x++)
            {
                float sxf = x * scaleX;
                int x0 = (int)sxf;
                int x1 = x0 + 1;
                if (x1 >= sw) x1 = sw - 1;
                float tx = sxf - x0;
                x0Index[x] = x0 * 3;
                x1Index[x] = x1 * 3;
                wx1Arr[x] = tx;
                wx0Arr[x] = 1f - tx;
            }
            for (int y = 0; y < height; y++)
            {
                float syf = y * scaleY;
                int y0 = (int)syf;
                int y1 = y0 + 1; if (y1 >= sh) y1 = sh - 1;
                float ty = syf - y0;
                float wy1 = ty;
                float wy0 = 1f - ty;
                int row0 = y0 * sw * 3;
                int row1 = y1 * sw * 3;
                int dRow = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    int s00 = row0 + x0Index[x];
                    int s10 = row0 + x1Index[x];
                    int s01 = row1 + x0Index[x];
                    int s11 = row1 + x1Index[x];
                    int d = dRow + x * 3;
                    float wx0 = wx0Arr[x];
                    float wx1 = wx1Arr[x];
                    float r0 = src[s00 + 0] * wx0 + src[s10 + 0] * wx1;
                    float r1 = src[s01 + 0] * wx0 + src[s11 + 0] * wx1;
                    dst[d + 0] = (byte)(r0 * wy0 + r1 * wy1 + 0.5f);
                    float g0 = src[s00 + 1] * wx0 + src[s10 + 1] * wx1;
                    float g1 = src[s01 + 1] * wx0 + src[s11 + 1] * wx1;
                    dst[d + 1] = (byte)(g0 * wy0 + g1 * wy1 + 0.5f);
                    float b0 = src[s00 + 2] * wx0 + src[s10 + 2] * wx1;
                    float b1 = src[s01 + 2] * wx0 + src[s11 + 2] * wx1;
                    dst[d + 2] = (byte)(b0 * wy0 + b1 * wy1 + 0.5f);
                }
            }
            _image.Update(width, height, dst);
            return this;
        }

        private ImageProcessingContext ResizeArea(int width, int height)
        {
            var src = _image.Buffer;
            int sw = _image.Width;
            int sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0) return this;

            var dst = new byte[width * height * 3];

            double scaleX = (double)sw / width;
            double scaleY = (double)sh / height;

            for (int dy = 0; dy < height; dy++)
            {
                double sy0 = dy * scaleY;
                double sy1 = (dy + 1) * scaleY;
                int syStart = (int)Math.Floor(sy0);
                int syEnd = (int)Math.Ceiling(sy1);
                if (syStart < 0) syStart = 0;
                if (syEnd > sh) syEnd = sh;

                for (int dx = 0; dx < width; dx++)
                {
                    double sx0 = dx * scaleX;
                    double sx1 = (dx + 1) * scaleX;
                    int sxStart = (int)Math.Floor(sx0);
                    int sxEnd = (int)Math.Ceiling(sx1);
                    if (sxStart < 0) sxStart = 0;
                    if (sxEnd > sw) sxEnd = sw;

                    double sumR = 0;
                    double sumG = 0;
                    double sumB = 0;
                    double totalArea = 0;

                    for (int sy = syStart; sy < syEnd; sy++)
                    {
                        double yTop = sy;
                        double yBottom = sy + 1;
                        double yOverlapTop = sy0 > yTop ? sy0 : yTop;
                        double yOverlapBottom = sy1 < yBottom ? sy1 : yBottom;
                        double yWeight = yOverlapBottom - yOverlapTop;
                        if (yWeight <= 0) continue;

                        for (int sx = sxStart; sx < sxEnd; sx++)
                        {
                            double xLeft = sx;
                            double xRight = sx + 1;
                            double xOverlapLeft = sx0 > xLeft ? sx0 : xLeft;
                            double xOverlapRight = sx1 < xRight ? sx1 : xRight;
                            double xWeight = xOverlapRight - xOverlapLeft;
                            if (xWeight <= 0) continue;

                            double area = xWeight * yWeight;
                            int s = (sy * sw + sx) * 3;
                            sumR += src[s + 0] * area;
                            sumG += src[s + 1] * area;
                            sumB += src[s + 2] * area;
                            totalArea += area;
                        }
                    }

                    int d = (dy * width + dx) * 3;
                    if (totalArea > 0)
                    {
                        double invArea = 1.0 / totalArea;
                        dst[d + 0] = (byte)(sumR * invArea + 0.5);
                        dst[d + 1] = (byte)(sumG * invArea + 0.5);
                        dst[d + 2] = (byte)(sumB * invArea + 0.5);
                    }
                    else
                    {
                        dst[d + 0] = 0;
                        dst[d + 1] = 0;
                        dst[d + 2] = 0;
                    }
                }
            }

            _image.Update(width, height, dst);
            return this;
        }

        /// <summary>
        /// 使用优化双三次插值将图像缩放到指定尺寸
        /// </summary>
        /// <param name="width">目标宽度</param>
        /// <param name="height">目标高度</param>
        /// <returns>上下文自身</returns>
        public ImageProcessingContext ResizeBicubicOptimized(int width, int height)
        {
            var src = _image.Buffer;
            var dst = new byte[width * height * 3];
            int sw = _image.Width, sh = _image.Height;
            if (sw <= 0 || sh <= 0 || width <= 0 || height <= 0)
            {
                _image.Update(width, height, dst);
                return this;
            }

            float scaleX = (float)sw / width;
            float scaleY = (float)sh / height;

            int[] xIndex = new int[width * 4];
            float[] xWeight = new float[width * 4];
            for (int x = 0; x < width; x++)
            {
                float gx = (x + 0.5f) * scaleX - 0.5f;
                int ix = (int)MathF.Floor(gx);
                float t = gx - ix;
                for (int k = -1; k <= 2; k++)
                {
                    int idx = x * 4 + (k + 1);
                    int sx = ix + k;
                    if (sx < 0) sx = 0;
                    else if (sx >= sw) sx = sw - 1;
                    xIndex[idx] = sx;
                    xWeight[idx] = CubicF(t - k);
                }
            }

            int[] yIndex = new int[height * 4];
            float[] yWeight = new float[height * 4];
            for (int y = 0; y < height; y++)
            {
                float gy = (y + 0.5f) * scaleY - 0.5f;
                int iy = (int)MathF.Floor(gy);
                float t = gy - iy;
                for (int k = -1; k <= 2; k++)
                {
                    int idx = y * 4 + (k + 1);
                    int sy = iy + k;
                    if (sy < 0) sy = 0;
                    else if (sy >= sh) sy = sh - 1;
                    yIndex[idx] = sy;
                    yWeight[idx] = CubicF(t - k);
                }
            }

            int vecSize = Vector<float>.Count;

            Parallel.For(0, height, y =>
            {
                int yOff = y * 4;
                int sy0 = yIndex[yOff + 0];
                int sy1 = yIndex[yOff + 1];
                int sy2 = yIndex[yOff + 2];
                int sy3 = yIndex[yOff + 3];
                float wy0 = yWeight[yOff + 0];
                float wy1 = yWeight[yOff + 1];
                float wy2 = yWeight[yOff + 2];
                float wy3 = yWeight[yOff + 3];

                int dBase = y * width * 3;
                Span<int> sxBuf0 = stackalloc int[4 * Vector<float>.Count];
                Span<float> wxBuf0 = stackalloc float[4 * Vector<float>.Count];

                int x = 0;
                int vecLimit = width - (width % vecSize);
                for (; x < vecLimit; x += vecSize)
                {
                    int xOff0 = x * 4;

                    for (int i = 0; i < vecSize; i++)
                    {
                        int xo = x + i;
                        int xOff = xo * 4;
                        sxBuf0[i * 4 + 0] = xIndex[xOff + 0];
                        sxBuf0[i * 4 + 1] = xIndex[xOff + 1];
                        sxBuf0[i * 4 + 2] = xIndex[xOff + 2];
                        sxBuf0[i * 4 + 3] = xIndex[xOff + 3];
                        wxBuf0[i * 4 + 0] = xWeight[xOff + 0];
                        wxBuf0[i * 4 + 1] = xWeight[xOff + 1];
                        wxBuf0[i * 4 + 2] = xWeight[xOff + 2];
                        wxBuf0[i * 4 + 3] = xWeight[xOff + 3];
                    }

                    for (int c = 0; c < 3; c++)
                    {
                        for (int i = 0; i < vecSize; i++)
                        {
                            int sx0 = sxBuf0[i * 4 + 0];
                            int sx1 = sxBuf0[i * 4 + 1];
                            int sx2 = sxBuf0[i * 4 + 2];
                            int sx3 = sxBuf0[i * 4 + 3];
                            float wx0 = wxBuf0[i * 4 + 0];
                            float wx1 = wxBuf0[i * 4 + 1];
                            float wx2 = wxBuf0[i * 4 + 2];
                            float wx3 = wxBuf0[i * 4 + 3];

                            float row0 =
                                wx0 * src[(sy0 * sw + sx0) * 3 + c] +
                                wx1 * src[(sy0 * sw + sx1) * 3 + c] +
                                wx2 * src[(sy0 * sw + sx2) * 3 + c] +
                                wx3 * src[(sy0 * sw + sx3) * 3 + c];
                            float row1 =
                                wx0 * src[(sy1 * sw + sx0) * 3 + c] +
                                wx1 * src[(sy1 * sw + sx1) * 3 + c] +
                                wx2 * src[(sy1 * sw + sx2) * 3 + c] +
                                wx3 * src[(sy1 * sw + sx3) * 3 + c];
                            float row2 =
                                wx0 * src[(sy2 * sw + sx0) * 3 + c] +
                                wx1 * src[(sy2 * sw + sx1) * 3 + c] +
                                wx2 * src[(sy2 * sw + sx2) * 3 + c] +
                                wx3 * src[(sy2 * sw + sx3) * 3 + c];
                            float row3 =
                                wx0 * src[(sy3 * sw + sx0) * 3 + c] +
                                wx1 * src[(sy3 * sw + sx1) * 3 + c] +
                                wx2 * src[(sy3 * sw + sx2) * 3 + c] +
                                wx3 * src[(sy3 * sw + sx3) * 3 + c];

                            float val =
                                wy0 * row0 +
                                wy1 * row1 +
                                wy2 * row2 +
                                wy3 * row3;

                            if (val < 0f) val = 0f;
                            else if (val > 255f) val = 255f;

                            int d = dBase + (x + i) * 3 + c;
                            dst[d] = (byte)(val + 0.5f);
                        }
                    }
                }

                for (; x < width; x++)
                {
                    int xOff = x * 4;
                    int sx0 = xIndex[xOff + 0];
                    int sx1 = xIndex[xOff + 1];
                    int sx2 = xIndex[xOff + 2];
                    int sx3 = xIndex[xOff + 3];
                    float wx0 = xWeight[xOff + 0];
                    float wx1 = xWeight[xOff + 1];
                    float wx2 = xWeight[xOff + 2];
                    float wx3 = xWeight[xOff + 3];
                    int d = dBase + x * 3;

                    for (int c = 0; c < 3; c++)
                    {
                        float row0 =
                            wx0 * src[(sy0 * sw + sx0) * 3 + c] +
                            wx1 * src[(sy0 * sw + sx1) * 3 + c] +
                            wx2 * src[(sy0 * sw + sx2) * 3 + c] +
                            wx3 * src[(sy0 * sw + sx3) * 3 + c];
                        float row1 =
                            wx0 * src[(sy1 * sw + sx0) * 3 + c] +
                            wx1 * src[(sy1 * sw + sx1) * 3 + c] +
                            wx2 * src[(sy1 * sw + sx2) * 3 + c] +
                            wx3 * src[(sy1 * sw + sx3) * 3 + c];
                        float row2 =
                            wx0 * src[(sy2 * sw + sx0) * 3 + c] +
                            wx1 * src[(sy2 * sw + sx1) * 3 + c] +
                            wx2 * src[(sy2 * sw + sx2) * 3 + c] +
                            wx3 * src[(sy2 * sw + sx3) * 3 + c];
                        float row3 =
                            wx0 * src[(sy3 * sw + sx0) * 3 + c] +
                            wx1 * src[(sy3 * sw + sx1) * 3 + c] +
                            wx2 * src[(sy3 * sw + sx2) * 3 + c] +
                            wx3 * src[(sy3 * sw + sx3) * 3 + c];

                        float val =
                            wy0 * row0 +
                            wy1 * row1 +
                            wy2 * row2 +
                            wy3 * row3;

                        if (val < 0f) val = 0f;
                        else if (val > 255f) val = 255f;
                        dst[d + c] = (byte)(val + 0.5f);
                    }
                }
            });

            _image.Update(width, height, dst);
            return this;

            static float CubicF(float x)
            {
                const float a = -0.5f;
                x = MathF.Abs(x);
                if (x <= 1f)
                {
                    return (a + 2f) * x * x * x - (a + 3f) * x * x + 1f;
                }
                if (x < 2f)
                {
                    return a * x * x * x - 5f * a * x * x + 8f * a * x - 4f * a;
                }
                return 0f;
            }
        }

        /// <summary>
        /// 将图像缩放到不超过指定最大宽高（保持宽高比）
        /// </summary>
        /// <param name="maxWidth">最大宽度</param>
        /// <param name="maxHeight">最大高度</param>
        /// <returns>上下文自身</returns>
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
        /// <summary>
        /// 转换为灰度图（简单加权平均）
        /// </summary>
        /// <returns>上下文自身</returns>
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

    /// <summary>
    /// 图像扩展方法
    /// </summary>
    public static class ImageExtensions
    {
        /// <summary>
        /// 克隆图像，应用处理上下文并返回新图像
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        /// <param name="action">处理操作</param>
        /// <returns>处理后的新图像（Rgb24）</returns>
        public static Image<Rgb24> Clone(this Image<Rgb24> image, Action<ImageProcessingContext> action)
        {
            var srcBuffer = image.Buffer;
            var clonedBuffer = new byte[srcBuffer.Length];
            Buffer.BlockCopy(srcBuffer, 0, clonedBuffer, 0, srcBuffer.Length);
            var clonedImage = new Image<Rgb24>(image.Width, image.Height, clonedBuffer);
            var ctx = new ImageProcessingContext(clonedImage);
            action(ctx);
            return clonedImage;
        }

        /// <summary>
        /// 对图像应用处理上下文并执行指定操作
        /// </summary>
        /// <param name="image">输入图像（Rgb24）</param>
        /// <param name="action">处理操作</param>
        public static void Mutate(this Image<Rgb24> image, Action<ImageProcessingContext> action)
        {
            var ctx = new ImageProcessingContext(image);
            action(ctx);
        }
    }
}
