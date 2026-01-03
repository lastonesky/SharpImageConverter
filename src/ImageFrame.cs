using System;
using System.IO;

namespace SharpImageConverter;

/// <summary>
/// 像素格式类型
/// </summary>
public enum ImagePixelFormat
{
    /// <summary>
    /// 每像素 24 位的 RGB 格式（8 位 R、G、B）
    /// </summary>
    Rgb24
}

/// <summary>
/// 表示一帧 24 位 RGB 图像数据，包含宽高与像素缓冲区。
/// </summary>
public sealed class ImageFrame
{
    /// <summary>
    /// 图像宽度（像素）
    /// </summary>
    public int Width { get; }
    /// <summary>
    /// 图像高度（像素）
    /// </summary>
    public int Height { get; }
    /// <summary>
    /// 像素格式（当前固定为 Rgb24）
    /// </summary>
    public ImagePixelFormat PixelFormat { get; }
    /// <summary>
    /// 像素数据缓冲区，长度为 Width * Height * 3，按 RGB 顺序排列
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// 创建一个新的图像帧
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb24">RGB24 像素缓冲区</param>
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

    /// <summary>
    /// 从指定路径加载图像（自动根据扩展名识别格式）
    /// </summary>
    /// <param name="path">输入文件路径</param>
    /// <returns>加载后的图像帧</returns>
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

    /// <summary>
    /// 从 JPEG 文件加载图像帧，并根据 EXIF 方向进行必要的旋转/翻转
    /// </summary>
    /// <param name="path">JPEG 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadJpeg(string path)
    {
        using var fs = File.OpenRead(path);
        var parser = new JpegParser();
        parser.Parse(fs);
        var decoder = new JpegDecoder(parser);
        byte[] rgb = decoder.DecodeToRGB(fs);

        if (parser.ExifOrientation != 1)
        {
            var t = ApplyExifOrientation(rgb, parser.Width, parser.Height, parser.ExifOrientation);
            return new ImageFrame(t.width, t.height, t.pixels);
        }

        return new ImageFrame(parser.Width, parser.Height, rgb);
    }

    /// <summary>
    /// 从 PNG 文件加载图像帧
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadPng(string path)
    {
        var decoder = new PngDecoder();
        byte[] rgb = decoder.DecodeToRGB(path);
        return new ImageFrame(decoder.Width, decoder.Height, rgb);
    }

    /// <summary>
    /// 从 BMP 文件加载图像帧
    /// </summary>
    /// <param name="path">BMP 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadBmp(string path)
    {
        int width, height;
        byte[] rgb = BmpReader.Read(path, out width, out height);
        return new ImageFrame(width, height, rgb);
    }

    /// <summary>
    /// 从 GIF 文件加载首帧为图像帧（RGB24）
    /// </summary>
    /// <param name="path">GIF 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadGif(string path)
    {
        var dec = new SharpImageConverter.Formats.Gif.GifDecoder();
        var img = dec.DecodeRgb24(path);
        return new ImageFrame(img.Width, img.Height, img.Buffer);
    }

    /// <summary>
    /// 保存图像到指定路径（根据扩展名选择格式）
    /// </summary>
    /// <param name="path">输出文件路径</param>
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
            case ".gif":
                SaveAsGif(path);
                break;
            default:
                throw new NotSupportedException($"不支持的输出文件格式: {ext}");
        }
    }

    /// <summary>
    /// 以 BMP 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsBmp(string path)
    {
        BmpWriter.Write24(path, Width, Height, Pixels);
    }

    /// <summary>
    /// 以 PNG 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsPng(string path)
    {
        PngWriter.Write(path, Width, Height, Pixels);
    }

    /// <summary>
    /// 以 JPEG 格式保存图像（默认质量 75）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    public void SaveAsJpeg(string path, int quality = 75)
    {
        JpegEncoder.Write(path, Width, Height, Pixels, quality);
    }

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量与采样方式）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    public void SaveAsJpeg(string path, int quality, bool subsample420)
    {
        JpegEncoder.Write(path, Width, Height, Pixels, quality, subsample420);
    }

    /// <summary>
    /// 以 GIF 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsGif(string path)
    {
        var encoder = new SharpImageConverter.Formats.Gif.GifEncoder();
        using var fs = File.Create(path);
        encoder.Encode(this, fs);
    }

    /// <summary>
    /// 按 EXIF 方向对图像进行旋转/翻转并返回新图像
    /// </summary>
    /// <param name="orientation">EXIF 方向值（1-8）</param>
    /// <returns>应用方向后的新图像帧</returns>
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
