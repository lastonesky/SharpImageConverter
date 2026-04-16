using System;
using System.IO;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Drawing;

/// <summary>
/// System.Drawing-like bitmap wrapper over ImageFrame.
/// </summary>
public sealed class Bitmap : Image
{
    public Bitmap(int width, int height) : base(CreateEmptyFrame(width, height))
    {
    }

    public Bitmap(string filename) : base(LoadFrameFromFile(filename))
    {
    }

    public Bitmap(Stream stream) : base(LoadFrameFromStream(stream))
    {
    }

    internal Bitmap(ImageFrame frame) : base(frame)
    {
    }

    public new static Bitmap FromFile(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return new Bitmap(filename);
    }

    public new static Bitmap FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new Bitmap(stream);
    }

    public new Size Size => new(Width, Height);

    public Rectangle Bounds => new(0, 0, Width, Height);

    public Color GetPixel(int x, int y)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposed();
            ValidateCoordinates(x, y);

            int index = (y * Width + x) * 3;
            byte[] pixels = Frame.Pixels;
            return Color.FromArgb(255, pixels[index], pixels[index + 1], pixels[index + 2]);
        }
    }

    public Color GetPixel(Point point)
    {
        return GetPixel(point.X, point.Y);
    }

    public void SetPixel(int x, int y, Color color)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposed();
            ValidateCoordinates(x, y);

            int index = (y * Width + x) * 3;
            byte[] pixels = Frame.Pixels;

            if (color.A == 255)
            {
                pixels[index] = color.R;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.B;
                return;
            }

            if (color.A == 0)
            {
                return;
            }

            // Alpha blend onto existing RGB pixel because ImageFrame stores RGB24 only.
            int alpha = color.A;
            int inverse = 255 - alpha;
            pixels[index] = (byte)((color.R * alpha + pixels[index] * inverse + 127) / 255);
            pixels[index + 1] = (byte)((color.G * alpha + pixels[index + 1] * inverse + 127) / 255);
            pixels[index + 2] = (byte)((color.B * alpha + pixels[index + 2] * inverse + 127) / 255);
        }
    }

    public void SetPixel(Point point, Color color)
    {
        SetPixel(point.X, point.Y, color);
    }

    public Bitmap Clone()
    {
        lock (SyncRoot)
        {
            ThrowIfDisposed();
            ImageFrame source = Frame;
            byte[] copy = new byte[source.Pixels.Length];
            Buffer.BlockCopy(source.Pixels, 0, copy, 0, copy.Length);
            var cloned = new ImageFrame(source.Width, source.Height, copy, CloneMetadata(source.Metadata));
            return new Bitmap(cloned);
        }
    }

    public Bitmap Clone(Rectangle rect)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposed();
            ValidateCloneRectangle(rect);

            if (rect.X == 0 && rect.Y == 0 && rect.Width == Width && rect.Height == Height)
            {
                return Clone();
            }

            ImageFrame source = Frame;
            byte[] dst = new byte[checked(rect.Width * rect.Height * 3)];
            int srcStride = source.Width * 3;
            int dstStride = rect.Width * 3;

            for (int row = 0; row < rect.Height; row++)
            {
                int srcOffset = ((rect.Y + row) * srcStride) + rect.X * 3;
                int dstOffset = row * dstStride;
                Buffer.BlockCopy(source.Pixels, srcOffset, dst, dstOffset, dstStride);
            }

            var cloned = new ImageFrame(rect.Width, rect.Height, dst, CloneMetadata(source.Metadata));
            return new Bitmap(cloned);
        }
    }

    public void RotateFlip(RotateFlipType rotateFlipType)
    {
        lock (SyncRoot)
        {
            ThrowIfDisposed();

            int operation = ((int)rotateFlipType) & 0x7;
            if (operation == 0)
            {
                return;
            }

            ImageFrame source = Frame;
            int width = source.Width;
            int height = source.Height;
            byte[] pixels = source.Pixels;

            int rotation = operation & 0x3;
            bool flipX = (operation & 0x4) != 0;

            if (rotation == 1)
            {
                pixels = Rotate90(pixels, width, height, out int newWidth, out int newHeight);
                width = newWidth;
                height = newHeight;
            }
            else if (rotation == 2)
            {
                Rotate180InPlace(pixels);
            }
            else if (rotation == 3)
            {
                pixels = Rotate270(pixels, width, height, out int newWidth, out int newHeight);
                width = newWidth;
                height = newHeight;
            }

            if (flipX)
            {
                FlipHorizontalInPlace(pixels, width, height);
            }

            ReplaceFrame(new ImageFrame(width, height, pixels, source.Metadata));
        }
    }

    private static ImageFrame LoadFrameFromFile(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return ImageFrame.Load(filename);
    }

    private static ImageFrame LoadFrameFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ImageFrame.Load(stream);
    }

    private static ImageFrame CreateEmptyFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width, nameof(width));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height, nameof(height));
        byte[] pixels = new byte[checked(width * height * 3)];
        return new ImageFrame(width, height, pixels);
    }

    private void ValidateCoordinates(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
    }

    private void ValidateCloneRectangle(Rectangle rect)
    {
        if (rect.Width <= 0) throw new ArgumentOutOfRangeException(nameof(rect));
        if (rect.Height <= 0) throw new ArgumentOutOfRangeException(nameof(rect));
        if (rect.X < 0 || rect.Y < 0) throw new ArgumentOutOfRangeException(nameof(rect));
        if (rect.Right > Width || rect.Bottom > Height) throw new ArgumentOutOfRangeException(nameof(rect));
    }

    private static ImageMetadata CloneMetadata(ImageMetadata source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ImageMetadata
        {
            Orientation = source.Orientation,
            ExifRaw = source.ExifRaw is null ? null : (byte[])source.ExifRaw.Clone(),
            IccProfile = source.IccProfile is null ? null : (byte[])source.IccProfile.Clone(),
            IccProfileKind = source.IccProfileKind,
            Exif = source.Exif is null
                ? null
                : new ExifMetadata
                {
                    Make = source.Exif.Make,
                    Model = source.Exif.Model,
                    DateTime = source.Exif.DateTime,
                    DateTimeOriginal = source.Exif.DateTimeOriginal,
                    GpsLatitude = source.Exif.GpsLatitude,
                    GpsLongitude = source.Exif.GpsLongitude
                }
        };
    }

    private static byte[] Rotate90(byte[] src, int srcWidth, int srcHeight, out int dstWidth, out int dstHeight)
    {
        dstWidth = srcHeight;
        dstHeight = srcWidth;
        byte[] dst = new byte[src.Length];

        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                int dx = srcHeight - 1 - y;
                int dy = x;
                int srcIndex = (y * srcWidth + x) * 3;
                int dstIndex = (dy * dstWidth + dx) * 3;
                dst[dstIndex] = src[srcIndex];
                dst[dstIndex + 1] = src[srcIndex + 1];
                dst[dstIndex + 2] = src[srcIndex + 2];
            }
        }

        return dst;
    }

    private static byte[] Rotate270(byte[] src, int srcWidth, int srcHeight, out int dstWidth, out int dstHeight)
    {
        dstWidth = srcHeight;
        dstHeight = srcWidth;
        byte[] dst = new byte[src.Length];

        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                int dx = y;
                int dy = srcWidth - 1 - x;
                int srcIndex = (y * srcWidth + x) * 3;
                int dstIndex = (dy * dstWidth + dx) * 3;
                dst[dstIndex] = src[srcIndex];
                dst[dstIndex + 1] = src[srcIndex + 1];
                dst[dstIndex + 2] = src[srcIndex + 2];
            }
        }

        return dst;
    }

    private static void Rotate180InPlace(byte[] pixels)
    {
        int left = 0;
        int right = pixels.Length - 3;
        while (left < right)
        {
            byte r = pixels[left];
            byte g = pixels[left + 1];
            byte b = pixels[left + 2];

            pixels[left] = pixels[right];
            pixels[left + 1] = pixels[right + 1];
            pixels[left + 2] = pixels[right + 2];

            pixels[right] = r;
            pixels[right + 1] = g;
            pixels[right + 2] = b;

            left += 3;
            right -= 3;
        }
    }

    private static void FlipHorizontalInPlace(byte[] pixels, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            int left = (y * width) * 3;
            int right = (y * width + (width - 1)) * 3;

            while (left < right)
            {
                byte r = pixels[left];
                byte g = pixels[left + 1];
                byte b = pixels[left + 2];

                pixels[left] = pixels[right];
                pixels[left + 1] = pixels[right + 1];
                pixels[left + 2] = pixels[right + 2];

                pixels[right] = r;
                pixels[right + 1] = g;
                pixels[right + 2] = b;

                left += 3;
                right -= 3;
            }
        }
    }
}
