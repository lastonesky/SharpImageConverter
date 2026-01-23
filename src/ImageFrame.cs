using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Metadata;

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

    public ImageMetadata Metadata { get; }

    /// <summary>
    /// 创建一个新的图像帧
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="rgb24">RGB24 像素缓冲区</param>
    public ImageFrame(int width, int height, byte[] rgb24, ImageMetadata? metadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width, nameof(width));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height, nameof(height));
        ArgumentNullException.ThrowIfNull(rgb24, nameof(rgb24));        
        if (rgb24.Length != checked(width * height * 3)) throw new ArgumentException("RGB24 像素长度不匹配", nameof(rgb24));

        Width = width;
        Height = height;
        PixelFormat = ImagePixelFormat.Rgb24;
        Pixels = rgb24;
        Metadata = metadata ?? new ImageMetadata();
    }

    /// <summary>
    /// 从指定路径加载图像（自动根据扩展名识别格式）
    /// </summary>
    /// <param name="path">输入文件路径</param>
    /// <returns>加载后的图像帧</returns>
    public static ImageFrame Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs);
    }

    /// <summary>
    /// 从字节数组加载图像（自动根据文件头识别格式）
    /// </summary>
    /// <param name="data">输入数据</param>
    /// <returns>加载后的图像帧</returns>
    public static ImageFrame Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data, writable: false);
        return Load(ms);
    }

    /// <summary>
    /// 从内存段加载图像（自动根据文件头识别格式）
    /// </summary>
    /// <param name="data">输入数据</param>
    /// <returns>加载后的图像帧</returns>
    public static ImageFrame Load(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) && segment.Array != null)
        {
            using var ms = new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
            return Load(ms);
        }
        return Load(data.ToArray());
    }

    /// <summary>
    /// 从流加载图像（自动根据文件头识别格式，支持不可 Seek 的流，仅缓存头部用于嗅探）
    /// </summary>
    /// <param name="stream">输入数据流</param>
    /// <returns>加载后的图像帧</returns>
    public static ImageFrame Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        const int headerSize = 12;
        byte[] header = new byte[headerSize];
        int read;
        Stream decodeStream;
        if (stream.CanSeek)
        {
            long startPos = stream.Position;
            read = ReadHeader(stream, header);
            stream.Position = startPos;
            decodeStream = stream;
        }
        else
        {
            read = ReadHeader(stream, header);
            decodeStream = new PrefixStream(header, read, stream);
        }
        if (read < 2) throw new InvalidDataException("流数据过短");

        // Magic Number Detection
        if (header[0] == 0xFF && header[1] == 0xD8)
            return LoadJpeg(decodeStream);
        
        if (read >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return LoadPng(decodeStream);
            
        if (header[0] == 'B' && header[1] == 'M')
            return LoadBmp(decodeStream);
            
        if (read >= 3 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
            return LoadGif(decodeStream);

        throw new NotSupportedException("无法识别的图像格式");
    }

    private static int ReadHeader(Stream stream, byte[] header)
    {
        int read = 0;
        while (read < header.Length)
        {
            int n = stream.Read(header, read, header.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    private sealed class PrefixStream : Stream
    {
        private readonly byte[] prefix;
        private readonly int prefixLength;
        private int prefixOffset;
        private readonly Stream tail;

        public PrefixStream(byte[] prefix, int prefixLength, Stream tail)
        {
            this.prefix = prefix;
            this.prefixLength = prefixLength;
            this.tail = tail;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            if (prefixOffset < prefixLength)
            {
                int available = prefixLength - prefixOffset;
                int toCopy = Math.Min(available, count);
                Buffer.BlockCopy(prefix, prefixOffset, buffer, offset, toCopy);
                prefixOffset += toCopy;
                total += toCopy;
                if (total == count) return total;
            }
            int n = tail.Read(buffer, offset + total, count - total);
            return total + n;
        }

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            if (prefixOffset < prefixLength)
            {
                int available = prefixLength - prefixOffset;
                int toCopy = Math.Min(available, buffer.Length);
                prefix.AsSpan(prefixOffset, toCopy).CopyTo(buffer);
                prefixOffset += toCopy;
                total += toCopy;
                if (total == buffer.Length) return total;
            }
            int n = tail.Read(buffer.Slice(total));
            return total + n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 从 JPEG 文件加载图像帧，并根据 EXIF 方向进行必要的旋转/翻转
    /// </summary>
    /// <param name="path">JPEG 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadJpeg(string path)
    {
        using var fs = File.OpenRead(path);
        return LoadJpeg(fs);
    }

    /// <summary>
    /// 从 JPEG 流加载图像帧，并根据 EXIF 方向进行必要的旋转/翻转
    /// </summary>
    /// <param name="stream">JPEG 数据流</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadJpeg(Stream stream)
    {
        var decoder = new JpegDecoder();
        var img = decoder.Decode(stream);
        byte[] rgb = img.Buffer;

        if (decoder.ExifOrientation != 1)
        {
            var t = ApplyExifOrientation(rgb, decoder.Width, decoder.Height, decoder.ExifOrientation);
            img.Metadata.Orientation = 1;
            return new ImageFrame(t.width, t.height, t.pixels, img.Metadata);
        }

        return new ImageFrame(decoder.Width, decoder.Height, rgb, img.Metadata);
    }

    /// <summary>
    /// 从 PNG 文件加载图像帧
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadPng(string path)
    {
        using var fs = File.OpenRead(path);
        return LoadPng(fs);
    }

    /// <summary>
    /// 从 PNG 流加载图像帧
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadPng(Stream stream)
    {
        var decoder = new PngDecoder();
        byte[] rgb = decoder.DecodeToRGB(stream);
        return new ImageFrame(decoder.Width, decoder.Height, rgb);
    }

    /// <summary>
    /// 从 BMP 文件加载图像帧
    /// </summary>
    /// <param name="path">BMP 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadBmp(string path)
    {
        using var fs = File.OpenRead(path);
        return LoadBmp(fs);
    }

    /// <summary>
    /// 从 BMP 流加载图像帧
    /// </summary>
    /// <param name="stream">BMP 数据流</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadBmp(Stream stream)
    {
        int width, height;
        byte[] rgb = BmpReader.Read(stream, out width, out height);
        return new ImageFrame(width, height, rgb);
    }

    /// <summary>
    /// 从 GIF 文件加载首帧为图像帧（RGB24）
    /// </summary>
    /// <param name="path">GIF 文件路径</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadGif(string path)
    {
        using var fs = File.OpenRead(path);
        return LoadGif(fs);
    }

    /// <summary>
    /// 从 GIF 流加载首帧为图像帧（RGB24）
    /// </summary>
    /// <param name="stream">GIF 数据流</param>
    /// <returns>图像帧</returns>
    public static ImageFrame LoadGif(Stream stream)
    {
        var dec = new SharpImageConverter.Formats.Gif.GifDecoder();
        var img = dec.DecodeRgb24(stream);
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
        using var fs = File.Create(path);
        SaveAsBmp(fs);
    }

    /// <summary>
    /// 以 BMP 格式保存图像
    /// </summary>
    /// <param name="stream">输出流</param>
    public void SaveAsBmp(Stream stream)
    {
        BmpWriter.Write24(stream, Width, Height, Pixels);
    }

    /// <summary>
    /// 以 PNG 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsPng(string path)
    {
        using var fs = File.Create(path);
        SaveAsPng(fs);
    }

    /// <summary>
    /// 以 PNG 格式保存图像
    /// </summary>
    /// <param name="stream">输出流</param>
    public void SaveAsPng(Stream stream)
    {
        PngWriter.Write(stream, Width, Height, Pixels);
    }

    /// <summary>
    /// 以 JPEG 格式保存图像（默认质量 75）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    public void SaveAsJpeg(string path, int quality = 75)
    {
        using var fs = File.Create(path);
        SaveAsJpeg(fs, quality);
    }

    /// <summary>
    /// 以 JPEG 格式保存图像（默认质量 75）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    public void SaveAsJpeg(Stream stream, int quality = 75)
    {
        JpegEncoder.Write(stream, Width, Height, Pixels, quality);
    }

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量与采样方式）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    public void SaveAsJpeg(string path, int quality, bool subsample420)
    {
        using var fs = File.Create(path);
        SaveAsJpeg(fs, quality, subsample420);
    }

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量与采样方式）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    public void SaveAsJpeg(Stream stream, int quality, bool subsample420)
    {
        JpegEncoder.Write(stream, Width, Height, Pixels, quality, subsample420);
    }

    public void SaveAsJpeg(string path, int quality, bool subsample420, bool keepMetadata)
    {
        using var fs = File.Create(path);
        SaveAsJpeg(fs, quality, subsample420, keepMetadata);
    }

    public void SaveAsJpeg(Stream stream, int quality, bool subsample420, bool keepMetadata)
    {
        JpegEncoder.Write(stream, Width, Height, Pixels, quality, subsample420, Metadata, keepMetadata);
    }

    /// <summary>
    /// 以 GIF 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsGif(string path)
    {
        using var fs = File.Create(path);
        SaveAsGif(fs);
    }

    /// <summary>
    /// 以 GIF 格式保存图像
    /// </summary>
    /// <param name="stream">输出流</param>
    public void SaveAsGif(Stream stream)
    {
        var encoder = new SharpImageConverter.Formats.Gif.GifEncoder();
        encoder.Encode(this, stream);
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
        Metadata.Orientation = 1;
        return new ImageFrame(t.width, t.height, t.pixels, Metadata);
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
