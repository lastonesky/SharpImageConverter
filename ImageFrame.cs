using System;
using System.IO;

public enum ImagePixelFormat
{
    Rgb24
}

public sealed class ImageFrame
{
    public int Width { get; }
    public int Height { get; }
    public ImagePixelFormat PixelFormat { get; }
    public byte[] Pixels { get; }

    public ImageFrame(int width, int height, byte[] rgb24)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width, nameof(width));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height, nameof(height));
        ArgumentNullException.ThrowIfNull(rgb24, nameof(rgb24));        
        if (rgb24.Length != checked(width * height * 3)) throw new ArgumentException("RGB24 像素长度不匹配", nameof(rgb24));

        Width = width;
        Height = height;
        PixelFormat = ImagePixelFormat.Rgb24;
        Pixels = rgb24;
    }

    public static ImageFrame Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => LoadJpeg(path),
            ".png" => LoadPng(path),
            ".bmp" => LoadBmp(path),
            _ => throw new NotSupportedException($"不支持的输入文件格式: {ext}")
        };
    }

    public static ImageFrame LoadJpeg(string path)
    {
        var parser = new JpegParser();
        parser.Parse(path);
        var decoder = new JpegDecoder(parser);
        byte[] rgb = decoder.DecodeToRGB(path);

        if (parser.ExifOrientation != 1)
        {
            var t = ApplyExifOrientation(rgb, parser.Width, parser.Height, parser.ExifOrientation);
            return new ImageFrame(t.width, t.height, t.pixels);
        }

        return new ImageFrame(parser.Width, parser.Height, rgb);
    }

    public static ImageFrame LoadPng(string path)
    {
        var decoder = new PngDecoder();
        byte[] rgb = decoder.DecodeToRGB(path);
        return new ImageFrame(decoder.Width, decoder.Height, rgb);
    }

    public static ImageFrame LoadBmp(string path)
    {
        int width, height;
        byte[] rgb = BmpReader.Read(path, out width, out height);
        return new ImageFrame(width, height, rgb);
    }

    public void Save(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".bmp":
                SaveAsBmp(path);
                break;
            case ".png":
                SaveAsPng(path);
                break;
            case ".jpg":
            case ".jpeg":
                SaveAsJpeg(path);
                break;
            default:
                throw new NotSupportedException($"不支持的输出文件格式: {ext}");
        }
    }

    public void SaveAsBmp(string path)
    {
        BmpWriter.Write24(path, Width, Height, Pixels);
    }

    public void SaveAsPng(string path)
    {
        PngWriter.Write(path, Width, Height, Pixels);
    }

    public void SaveAsJpeg(string path, int quality = 75)
    {
        JpegEncoder.Write(path, Width, Height, Pixels, quality);
    }

    public ImageFrame ApplyExifOrientation(int orientation)
    {
        if (orientation == 1) return this;
        var t = ApplyExifOrientation(Pixels, Width, Height, orientation);
        return new ImageFrame(t.width, t.height, t.pixels);
    }

    private static (byte[] pixels, int width, int height) ApplyExifOrientation(byte[] src, int width, int height, int orientation)
    {
        int newW = width;
        int newH = height;
        switch (orientation)
        {
            case 1:
                return (src, width, height);
            case 2:
            case 3:
            case 4:
                newW = width; newH = height; break;
            case 5:
            case 6:
            case 7:
            case 8:
                newW = height; newH = width; break;
            default:
                return (src, width, height);
        }

        byte[] dst = new byte[newW * newH * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx, dy;
                switch (orientation)
                {
                    case 2:
                        dx = (width - 1 - x); dy = y; break;
                    case 3:
                        dx = (width - 1 - x); dy = (height - 1 - y); break;
                    case 4:
                        dx = x; dy = (height - 1 - y); break;
                    case 5:
                        dx = y; dy = x; break;
                    case 6:
                        dx = (height - 1 - y); dy = x; break;
                    case 7:
                        dx = (height - 1 - y); dy = (width - 1 - x); break;
                    case 8:
                        dx = y; dy = (width - 1 - x); break;
                    default:
                        dx = x; dy = y; break;
                }
                int srcIdx = (y * width + x) * 3;
                int dstIdx = (dy * newW + dx) * 3;
                dst[dstIdx + 0] = src[srcIdx + 0];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
            }
        }
        return (dst, newW, newH);
    }
}
