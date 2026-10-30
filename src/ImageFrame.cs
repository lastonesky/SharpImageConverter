using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Core;
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
    Rgb24,
    /// <summary>
    /// 每像素 24 位的 BGR 格式（8 位 B、G、R，与 RGB24 仅是通道顺序相反）
    /// </summary>
    Bgr24,
    /// <summary>
    /// 每像素 32 位的 BGRA 格式（8 位 B、G、R、A）
    /// </summary>
    Bgra32,
    /// <summary>
    /// 每像素 32 位的 RGBA 格式（8 位 R、G、B、A，与 BGRA32 仅是通道顺序相反）
    /// </summary>
    Rgba32
}

/// <summary>
/// 表示一帧图像数据（支持 Rgb24 / Bgr24 / Bgra32 / Rgba32 像素格式），包含宽高与像素缓冲区。
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
    /// 像素格式（Rgb24 / Bgr24 / Bgra32 / Rgba32）
    /// </summary>
    public ImagePixelFormat PixelFormat => _pixelFormat;
    /// <summary>
    /// 像素数据缓冲区。长度与格式相关：Rgb24/Bgr24 为 Width * Height * 3；Bgra32/Rgba32 为 Width * Height * 4。
    /// 24 位格式按 R/G/B 或 B/G/R 顺序排列，32 位格式按 B/G/R/A 或 R/G/B/A 顺序排列。
    /// </summary>
    public byte[] Pixels { get; }

    public ImageMetadata Metadata { get; }

    private ImagePixelFormat _pixelFormat;

    /// <summary>
    /// 创建一个新的图像帧
    /// </summary>
    /// <param name="width">图像宽度</param>
    /// <param name="height">图像高度</param>
    /// <param name="pixels">像素缓冲区（长度需与 width * height * 每像素字节数 匹配）</param>
    /// <param name="pixelFormat">像素格式，默认 Rgb24</param>
    /// <param name="metadata">图像元数据</param>
    public ImageFrame(int width, int height, byte[] pixels, ImagePixelFormat pixelFormat = ImagePixelFormat.Rgb24, ImageMetadata? metadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width, nameof(width));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height, nameof(height));
        ArgumentNullException.ThrowIfNull(pixels, nameof(pixels));

        int bpp = GetBytesPerPixel(pixelFormat);
        if (pixels.Length != checked(width * height * bpp))
            throw new ArgumentException($"{pixelFormat} 像素长度不匹配：应为 width * height * {bpp} 字节", nameof(pixels));

        Width = width;
        Height = height;
        _pixelFormat = pixelFormat;
        Pixels = pixels;
        Metadata = metadata ?? new ImageMetadata();
    }

    private static int GetBytesPerPixel(ImagePixelFormat format) => format switch
    {
        ImagePixelFormat.Rgb24 => 3,
        ImagePixelFormat.Bgr24 => 3,
        ImagePixelFormat.Bgra32 => 4,
        ImagePixelFormat.Rgba32 => 4,
        _ => throw new NotSupportedException($"不支持的像素格式：{format}")
    };

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
        using var rom = new ReadOnlyMemoryStream(data);
        return Load(rom);
    }

    /// <summary>
    /// 从流加载图像（自动根据文件头识别格式，支持不可 Seek 的流，仅缓存头部用于嗅探）
    /// </summary>
    /// <param name="stream">输入数据流</param>
    /// <param name="requireComplete">
    /// 严格模式：要求数据完整（JPEG 需要真实的 EOI、GIF 需要帧数据解满）。
    /// 默认关闭时，传输在扫描/数据块边界被截断会被当作「文件结束」，返回一张残缺图像而不报错；
    /// 网络输入建议开启。PNG/BMP/WebP 无论开关都会拒绝截断数据。
    /// </param>
    /// <returns>加载后的图像帧</returns>
    public static ImageFrame Load(Stream stream, bool requireComplete = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        const int headerSize = ImageHeaderReader.SniffBytes;
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

        // 格式嗅探统一走 ImageHeaderReader（与 Configuration / ImageOptimizer / Image.Identify 同一实现）
        var kind = ImageHeaderReader.Sniff(header.AsSpan(0, read));
        switch (kind)
        {
            case ImageFormatKind.Jpeg:
                return LoadJpeg(decodeStream, useStreamingDecoder: requireComplete, requireEoi: requireComplete);
            case ImageFormatKind.Png:
                return LoadPng(decodeStream);
            case ImageFormatKind.Bmp:
                return LoadBmp(decodeStream);
            case ImageFormatKind.Gif:
                return LoadGif(decodeStream, requireComplete);
            case ImageFormatKind.Webp:
                return LoadWebp(decodeStream);
            default:
                throw new NotSupportedException(
                    Core.ImageInfo.IsRecognizedButUnsupported(kind)
                        // TIFF / ICO 能识别但不读写：报出格式名，别让调用方去猜这个文件是什么
                        ? Core.ImageInfo.UnsupportedMessage(kind)
                        : $"无法识别的图像格式（当前支持 {Core.ImageInfo.SupportedFormatList}）");
        }
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

    private sealed class PrefixStream(byte[] prefix, int prefixLength, Stream tail) : Stream
    {
        private readonly byte[] prefix = prefix;
        private readonly int prefixLength = prefixLength;
        private int prefixOffset;
        private readonly Stream tail = tail;

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
            int n = tail.Read(buffer[total..]);
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

    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : Stream
    {
        private readonly ReadOnlyMemory<byte> memory = memory;
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => memory.Length;
        public override long Position
        {
            get => position;
            set
            {
                if (value < 0 || value > memory.Length) throw new ArgumentOutOfRangeException(nameof(value));
                position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            int available = memory.Length - position;
            if (available <= 0) return 0;
            int toCopy = Math.Min(available, buffer.Length);
            memory.Span.Slice(position, toCopy).CopyTo(buffer);
            position += toCopy;
            return toCopy;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long basePosition = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => position,
                SeekOrigin.End => memory.Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            long newPosition = basePosition + offset;
            if (newPosition < 0 || newPosition > memory.Length) throw new IOException("尝试将位置移动到无效范围");
            position = (int)newPosition;
            return newPosition;
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
        return LoadJpeg(stream, useStreamingDecoder: false, useFloatingPointIdct: false);
    }

    public static ImageFrame LoadJpeg(Stream stream, bool useStreamingDecoder, bool useFloatingPointIdct = false, bool requireEoi = false)
    {
        if (!useStreamingDecoder)
        {
            var img = JpegDecoder.Decode(stream, useFloatingPointIdct);
            byte[] rgb = img.ToRgb24();
            int orientation = img.Metadata.Orientation;
            if (orientation != 1)
            {
                var t = ApplyExifOrientationInPlace(rgb, img.Width, img.Height, orientation);
                img.Metadata.Orientation = 1;
                return new ImageFrame(t.width, t.height, t.pixels, metadata: img.Metadata);
            }

            return new ImageFrame(img.Width, img.Height, rgb, metadata: img.Metadata);
        }

        var streaming = JpegDecoder.DecodeFromStreamAsync(
            stream,
            cancellationToken: default,
            useFloatingPointIdct: useFloatingPointIdct,
            requireEoi: requireEoi).GetAwaiter().GetResult();
        var jpeg = streaming.Image;
        byte[] rgbStream = jpeg.ToRgb24();
        int streamOrientation = streaming.ExifOrientation;
        if (streamOrientation != 1)
        {
            var t = ApplyExifOrientationInPlace(rgbStream, jpeg.Width, jpeg.Height, streamOrientation);
            jpeg.Metadata.Orientation = 1;
            return new ImageFrame(t.width, t.height, t.pixels, metadata: jpeg.Metadata);
        }

        return new ImageFrame(jpeg.Width, jpeg.Height, rgbStream, metadata: jpeg.Metadata);
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
        byte[] rgb = BmpReader.Read(stream, out int width, out int height, out _, out _);
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
    public static ImageFrame LoadGif(Stream stream, bool requireComplete = false)
    {
        var dec = new Formats.Gif.GifDecoder();
        var img = dec.DecodeRgb24(stream, requireComplete);
        return new ImageFrame(img.Width, img.Height, img.Buffer);
    }

    public static ImageFrame LoadWebp(string path)
    {
        using var fs = File.OpenRead(path);
        return LoadWebp(fs);
    }

    public static ImageFrame LoadWebp(Stream stream)
    {
        var dec = new WebpDecoderAdapter();
        var img = dec.DecodeRgb24(stream);
        return new ImageFrame(img.Width, img.Height, img.Buffer);
    }

    /// <summary>
    /// 保存图像到指定路径（根据扩展名选择格式）
    /// </summary>
    /// <remarks>
    /// 派发与编码全部交给 <see cref="Core.Configuration.Default"/>——这是唯一实现
    /// （扩展名经注册表解析到编码器，alpha 帧优先走 RGBA 编码器）。
    /// 此前这里自带一份 switch，与 <c>Configuration</c> 各写一套，于是
    /// 「帧能读进来但写不出去」「存 JPEG 指定不了子采样/trellis」这类缺口反复出现。
    /// </remarks>
    /// <param name="path">输出文件路径</param>
    public void Save(string path) => Core.Configuration.Default.Save(this, path);

    /// <summary>
    /// 保存图像到指定路径（根据扩展名选择格式），可覆盖编码参数。
    /// </summary>
    /// <param name="path">输出文件路径</param>
    /// <param name="options">保存选项；null 表示用已注册编码器的默认设置</param>
    public void Save(string path, Core.SaveOptions? options) => Core.Configuration.Default.Save(this, path, options);

    /// <summary>
    /// 以 BMP 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsBmp(string path) => Core.Configuration.Default.SaveAs(this, path, ".bmp");

    /// <summary>
    /// 以 BMP 格式保存图像
    /// </summary>
    /// <param name="stream">输出流</param>
    public void SaveAsBmp(Stream stream) => Core.Configuration.Default.Save(this, stream, ".bmp");

    /// <summary>
    /// 以 PNG 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsPng(string path) => Core.Configuration.Default.SaveAs(this, path, ".png");

    /// <summary>
    /// 以 PNG 格式保存图像。
    /// </summary>
    /// <remarks>
    /// 32 位带 alpha 的帧（<see cref="ImagePixelFormat.Rgba32"/> /
    /// <see cref="ImagePixelFormat.Bgra32"/>）走颜色类型 6，alpha 会保留；
    /// 像素全不透明时 <see cref="PngWriter.WriteRgba(Stream, int, int, byte[], ImageMetadata?)"/>
    /// 会自动降级为颜色类型 2，产物与按 RGB24 写入逐字节一致。
    /// 此前这里一律先降成 RGB24，导致 alpha 静默丢弃。
    /// </remarks>
    /// <param name="stream">输出流</param>
    public void SaveAsPng(Stream stream) => Core.Configuration.Default.Save(this, stream, ".png");

    /// <summary>
    /// 以 JPEG 格式保存图像。
    /// </summary>
    /// <remarks>
    /// 默认保留元数据（与 <c>Configuration.SaveRgb24</c> 一致）。此前两参与三参重载
    /// 走 <c>JpegEncoder.Write</c> 的默认 <c>keepMetadata: false</c>，帧存 JPEG 会静默丢掉 EXIF，
    /// 而同一个图走类型化保存却能保留——同一次「保存」有两种口径。
    /// 需要丢弃元数据请用带 <c>keepMetadata</c> 的重载，或传
    /// <see cref="Core.SaveOptions.Jpeg"/>。
    /// </remarks>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    public void SaveAsJpeg(string path, int quality = 75) => SaveAsJpeg(path, quality, true, true);

    /// <summary>
    /// 以 JPEG 格式保存图像（默认质量 75）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    public void SaveAsJpeg(Stream stream, int quality = 75) => SaveAsJpeg(stream, quality, true, true);

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量与采样方式）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    public void SaveAsJpeg(string path, int quality, bool subsample420) => SaveAsJpeg(path, quality, subsample420, true);

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量与采样方式）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    public void SaveAsJpeg(Stream stream, int quality, bool subsample420) => SaveAsJpeg(stream, quality, subsample420, true);

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量、采样方式与是否保留元数据）
    /// </summary>
    /// <param name="path">输出路径</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    /// <param name="keepMetadata">是否写出 EXIF/ICC 等元数据</param>
    public void SaveAsJpeg(string path, int quality, bool subsample420, bool keepMetadata)
        => Core.Configuration.Default.SaveAs(this, path, ".jpg", JpegOptions(quality, subsample420, keepMetadata));

    /// <summary>
    /// 以 JPEG 格式保存图像（指定质量、采样方式与是否保留元数据）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="quality">JPEG 质量（1-100）</param>
    /// <param name="subsample420">是否使用 4:2:0 子采样</param>
    /// <param name="keepMetadata">是否写出 EXIF/ICC 等元数据</param>
    public void SaveAsJpeg(Stream stream, int quality, bool subsample420, bool keepMetadata)
        => Core.Configuration.Default.Save(this, stream, ".jpg", JpegOptions(quality, subsample420, keepMetadata));

    private static Core.SaveOptions JpegOptions(int quality, bool subsample420, bool keepMetadata)
        => new() { Jpeg = new JpegEncoderOptions(quality, subsample420, keepMetadata) };

    /// <summary>
    /// 以 GIF 格式保存图像
    /// </summary>
    /// <param name="path">输出路径</param>
    public void SaveAsGif(string path) => Core.Configuration.Default.SaveAs(this, path, ".gif");

    /// <summary>
    /// 以 GIF 格式保存图像
    /// </summary>
    /// <param name="stream">输出流</param>
    public void SaveAsGif(Stream stream) => Core.Configuration.Default.Save(this, stream, ".gif");

    /// <summary>
    /// 以 WebP 格式保存图像（默认质量 75）
    /// </summary>
    /// <remarks>
    /// 带 alpha 的帧走 RGBA 编码路径（VP8L/VP8X 带 ALPH 块），alpha 保留；
    /// 其余格式先规范化为 RGB24。
    /// </remarks>
    /// <param name="path">输出路径</param>
    /// <param name="quality">有损质量（0-100）</param>
    public void SaveAsWebp(string path, float quality = 75f)
        => Core.Configuration.Default.SaveAs(this, path, ".webp", new Core.SaveOptions { WebpQuality = quality });

    /// <summary>
    /// 以 WebP 格式保存图像（默认质量 75）
    /// </summary>
    /// <param name="stream">输出流</param>
    /// <param name="quality">有损质量（0-100）</param>
    public void SaveAsWebp(Stream stream, float quality = 75f)
        => Core.Configuration.Default.Save(this, stream, ".webp", new Core.SaveOptions { WebpQuality = quality });

    /// <summary>
    /// 按 EXIF 方向对图像进行旋转/翻转并返回新图像。
    /// </summary>
    /// <remarks>
    /// 返回的帧与当前帧**不共享元数据**：新帧的方向为 1（已烘焙进像素），
    /// 因此这里复制一份 <see cref="ImageMetadata"/> 再改副本。此前直接写
    /// <c>Metadata.Orientation</c>，而两个帧共享同一个实例，「返回新图、原图不变」不成立
    /// ——调用方的原帧 EXIF 会被悄悄改成 1。
    /// </remarks>
    /// <param name="orientation">EXIF 方向值（1-8）</param>
    /// <returns>应用方向后的新图像帧（<paramref name="orientation"/> 为 1 时返回当前帧本身）</returns>
    public ImageFrame ApplyExifOrientation(int orientation)
    {
        if (orientation == 1) return this;
        var t = ApplyExifOrientation(Pixels, Width, Height, orientation);
        var meta = Metadata.Clone();
        meta.Orientation = 1;
        return new ImageFrame(t.width, t.height, t.pixels, metadata: meta);
    }

    /// <summary>
    /// 零拷贝转成类型化图像 <see cref="Image{TPixel}"/>（共享像素缓冲区与元数据实例）。
    /// </summary>
    /// <remarks>
    /// 只做「包装」不做「换格式」：帧是 Rgba32 就只接受 <see cref="Rgba32"/>，
    /// 帧是 Bgr24/Bgra32 则没有对应像素类型（<see cref="ImagePixelFormat"/> 的 BGR 系
    /// 在 <see cref="Core.Rgb24"/> / <see cref="Core.Rgba32"/> 之外没有 struct）。需要换格式请先调用
    /// <see cref="ToRgb24"/> / <see cref="ToRgba32"/>，那会真的复制并做 SIMD 换序。
    /// 反向转换见 <see cref="Core.Image{TPixel}.ToFrame"/>。
    /// </remarks>
    /// <typeparam name="T">像素类型（Rgb24 / Rgba32）</typeparam>
    /// <returns>共享同一缓冲区与元数据的类型化图像</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> 没有对应的帧像素格式</exception>
    /// <exception cref="InvalidOperationException">帧的像素格式与 <typeparamref name="T"/> 不一致</exception>
    public Image<T> ToImage<T>() where T : struct, Core.IPixel
    {
        ImagePixelFormat expected;
        if (typeof(T) == typeof(Core.Rgb24))
        {
            expected = ImagePixelFormat.Rgb24;
        }
        else if (typeof(T) == typeof(Core.Rgba32))
        {
            expected = ImagePixelFormat.Rgba32;
        }
        else
        {
            throw new NotSupportedException(
                $"像素类型 {typeof(T).Name} 没有对应的 ImagePixelFormat（帧只支持 Rgb24 / Rgba32）");
        }

        if (PixelFormat != expected)
        {
            throw new InvalidOperationException(
                $"帧的像素格式是 {PixelFormat}，与请求的 {typeof(T).Name} 不一致；"
                + "请先调用 ToRgb24() / ToRgba32() 规范化（BGR 系没有对应的像素类型）");
        }
        return new Image<T>(Width, Height, Pixels, Metadata);
    }

    /// <summary>
    /// 返回 RGB24 格式的图像帧。若当前不是 Rgb24，会分配新的缓冲区并做 SIMD 转换，不修改原帧。
    /// </summary>
    public ImageFrame ToRgb24()
    {
        switch (PixelFormat)
        {
            case ImagePixelFormat.Rgb24:
                return this;
            case ImagePixelFormat.Bgr24:
            {
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length);
                SimdHelper.SwapRgbBgr24(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Rgb24, Metadata);
            }
            case ImagePixelFormat.Bgra32:
            {
                var dst = GC.AllocateUninitializedArray<byte>((Pixels.Length / 4) * 3);
                SimdHelper.ConvertBgra32ToRgb24(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Rgb24, Metadata);
            }
            case ImagePixelFormat.Rgba32:
            {
                var dst = GC.AllocateUninitializedArray<byte>((Pixels.Length / 4) * 3);
                SimdHelper.PackRgbaToRgb(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Rgb24, Metadata);
            }
            default:
                throw new NotSupportedException($"不支持的像素格式：{PixelFormat}");
        }
    }

    /// <summary>
    /// 返回 BGR24 格式的图像帧。若当前不是 Bgr24，会分配新的缓冲区并做 SIMD 转换，不修改原帧。
    /// </summary>
    public ImageFrame ToBgr24()
    {
        switch (PixelFormat)
        {
            case ImagePixelFormat.Bgr24:
                return this;
            case ImagePixelFormat.Rgb24:
            {
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length);
                SimdHelper.SwapRgbBgr24(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Bgr24, Metadata);
            }
            case ImagePixelFormat.Bgra32:
            {
                var dst = GC.AllocateUninitializedArray<byte>((Pixels.Length / 4) * 3);
                SimdHelper.ConvertBgra32ToBgr24(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Bgr24, Metadata);
            }
            case ImagePixelFormat.Rgba32:
            {
                var dst = GC.AllocateUninitializedArray<byte>((Pixels.Length / 4) * 3);
                SimdHelper.ConvertRgba32ToBgr24(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Bgr24, Metadata);
            }
            default:
                throw new NotSupportedException($"不支持的像素格式：{PixelFormat}");
        }
    }

    /// <summary>
    /// 返回 BGRA32 格式的图像帧（缺失时 alpha 置 255）。若当前不是 Bgra32，会分配新的缓冲区并做 SIMD 转换，不修改原帧。
    /// </summary>
    public ImageFrame ToBgra32()
    {
        switch (PixelFormat)
        {
            case ImagePixelFormat.Bgra32:
                return this;
            case ImagePixelFormat.Rgb24:
            {
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length / 3 * 4);
                SimdHelper.ConvertRgb24ToBgra32(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Bgra32, Metadata);
            }
            case ImagePixelFormat.Bgr24:
            {
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length / 3 * 4);
                SimdHelper.ConvertBgr24ToBgra32(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Bgra32, Metadata);
            }
            case ImagePixelFormat.Rgba32:
            {
                // 同是 32bpp，仅交换 R/B；复用等长缓冲
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length);
                SimdHelper.SwapRgbaBgra32(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Bgra32, Metadata);
            }
            default:
                throw new NotSupportedException($"不支持的像素格式：{PixelFormat}");
        }
    }

    /// <summary>
    /// 返回 RGBA32 格式的图像帧（缺失时 alpha 置 255）。若当前不是 Rgba32，会分配新的缓冲区并做 SIMD 转换，不修改原帧。
    /// </summary>
    public ImageFrame ToRgba32()
    {
        switch (PixelFormat)
        {
            case ImagePixelFormat.Rgba32:
                return this;
            case ImagePixelFormat.Rgb24:
            {
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length / 3 * 4);
                SimdHelper.ExpandRgbToRgba(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Rgba32, Metadata);
            }
            case ImagePixelFormat.Bgr24:
            {
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length / 3 * 4);
                SimdHelper.ConvertBgr24ToRgba32(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Rgba32, Metadata);
            }
            case ImagePixelFormat.Bgra32:
            {
                // 同是 32bpp，仅交换 R/B；复用等长缓冲
                var dst = GC.AllocateUninitializedArray<byte>(Pixels.Length);
                SimdHelper.SwapRgbaBgra32(Pixels, dst);
                return new ImageFrame(Width, Height, dst, ImagePixelFormat.Rgba32, Metadata);
            }
            default:
                throw new NotSupportedException($"不支持的像素格式：{PixelFormat}");
        }
    }

    /// <summary>
    /// 就地在同一位深内交换 R 与 B 通道（alpha 保持不变），零额外内存分配：
    /// 24 位为 Rgb24 ⇄ Bgr24，32 位为 Bgra32 ⇄ Rgba32。
    /// 注意：位深变化（如 Rgb24 ⇄ Bgra32，每像素 3 ⇄ 4 字节）无法就地完成，请改用相应的 To* 方法。
    /// </summary>
    public void SwapRgbBgrInPlace()
    {
        switch (PixelFormat)
        {
            case ImagePixelFormat.Rgb24:
                SimdHelper.SwapRgbBgr24(Pixels, Pixels);
                _pixelFormat = ImagePixelFormat.Bgr24;
                break;
            case ImagePixelFormat.Bgr24:
                SimdHelper.SwapRgbBgr24(Pixels, Pixels);
                _pixelFormat = ImagePixelFormat.Rgb24;
                break;
            case ImagePixelFormat.Bgra32:
                // 32 位就地交换：SIMD 每批恰读/写 16 字节，无需部分写入保护
                SimdHelper.SwapRgbaBgra32(Pixels, Pixels);
                _pixelFormat = ImagePixelFormat.Rgba32;
                break;
            case ImagePixelFormat.Rgba32:
                SimdHelper.SwapRgbaBgra32(Pixels, Pixels);
                _pixelFormat = ImagePixelFormat.Bgra32;
                break;
            default:
                throw new InvalidOperationException("SwapRgbBgrInPlace 仅适用于 24 位（Rgb24/Bgr24）或 32 位（Bgra32/Rgba32）格式");
        }
    }

    private static (byte[] pixels, int width, int height) ApplyExifOrientation(byte[] src, int width, int height, int orientation)
    {
        int newW;
        int newH;
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

        // 旋转结果每个字节都会被覆写，跳过 new byte[] 的无谓清零
        byte[] dst = GC.AllocateUninitializedArray<byte>(newW * newH * 3);

        // 转置类（5-8）的分块边长：源/目标各占 Tile*Tile*3 字节，32 时约 3KB，稳落 L1
        const int Tile = 32;

        // 方向分支提到循环外：原先每个像素都要走一次 switch，而且 case 4 只能逐像素拷。
        switch (orientation)
        {
            case 2:
            case 3:
                // 水平镜像 / 180 度：目标行 = 源行的反序（按像素粒度反转）
                for (int y = 0; y < height; y++)
                {
                    int srcRow = (orientation == 2 ? y : height - 1 - y) * width * 3;
                    int dstRow = y * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        int srcIdx = srcRow + x * 3;
                        int dstIdx = dstRow + (width - 1 - x) * 3;
                        dst[dstIdx + 0] = src[srcIdx + 0];
                        dst[dstIdx + 1] = src[srcIdx + 1];
                        dst[dstIdx + 2] = src[srcIdx + 2];
                    }
                }
                break;

            case 4:
                // 垂直翻转：整行原样搬，直接走向量化的块拷贝
                for (int y = 0; y < height; y++)
                {
                    src.AsSpan((height - 1 - y) * width * 3, width * 3)
                       .CopyTo(dst.AsSpan(y * width * 3, width * 3));
                }
                break;

            // 5-8 是转置类：不加分块时目标端按列写入，跨度是整个行宽，
            // 工作集远超缓存导致每次写入都 miss。按 Tile 走之后源/目标各自的
            // 工作集只有 Tile*Tile*3 字节，稳定落在 L1 里。
            case 5:
                for (int ty = 0; ty < height; ty += Tile)
                {
                    int yEnd = Math.Min(ty + Tile, height);
                    for (int tx = 0; tx < width; tx += Tile)
                    {
                        int xEnd = Math.Min(tx + Tile, width);
                        for (int y = ty; y < yEnd; y++)
                        {
                            int srcRow = y * width * 3;
                            for (int x = tx; x < xEnd; x++)
                            {
                                int srcIdx = srcRow + x * 3;
                                int dstIdx = (x * newW + y) * 3;
                                dst[dstIdx + 0] = src[srcIdx + 0];
                                dst[dstIdx + 1] = src[srcIdx + 1];
                                dst[dstIdx + 2] = src[srcIdx + 2];
                            }
                        }
                    }
                }
                break;

            case 6:
                for (int ty = 0; ty < height; ty += Tile)
                {
                    int yEnd = Math.Min(ty + Tile, height);
                    for (int tx = 0; tx < width; tx += Tile)
                    {
                        int xEnd = Math.Min(tx + Tile, width);
                        for (int y = ty; y < yEnd; y++)
                        {
                            int srcRow = y * width * 3;
                            int dstBase = (height - 1 - y) * 3;
                            for (int x = tx; x < xEnd; x++)
                            {
                                int srcIdx = srcRow + x * 3;
                                int dstIdx = x * newW * 3 + dstBase;
                                dst[dstIdx + 0] = src[srcIdx + 0];
                                dst[dstIdx + 1] = src[srcIdx + 1];
                                dst[dstIdx + 2] = src[srcIdx + 2];
                            }
                        }
                    }
                }
                break;

            case 7:
                for (int ty = 0; ty < height; ty += Tile)
                {
                    int yEnd = Math.Min(ty + Tile, height);
                    for (int tx = 0; tx < width; tx += Tile)
                    {
                        int xEnd = Math.Min(tx + Tile, width);
                        for (int y = ty; y < yEnd; y++)
                        {
                            int srcRow = y * width * 3;
                            int dstBase = (height - 1 - y) * 3;
                            for (int x = tx; x < xEnd; x++)
                            {
                                int srcIdx = srcRow + x * 3;
                                int dstIdx = (width - 1 - x) * newW * 3 + dstBase;
                                dst[dstIdx + 0] = src[srcIdx + 0];
                                dst[dstIdx + 1] = src[srcIdx + 1];
                                dst[dstIdx + 2] = src[srcIdx + 2];
                            }
                        }
                    }
                }
                break;

            case 8:
                for (int ty = 0; ty < height; ty += Tile)
                {
                    int yEnd = Math.Min(ty + Tile, height);
                    for (int tx = 0; tx < width; tx += Tile)
                    {
                        int xEnd = Math.Min(tx + Tile, width);
                        for (int y = ty; y < yEnd; y++)
                        {
                            int srcRow = y * width * 3;
                            for (int x = tx; x < xEnd; x++)
                            {
                                int srcIdx = srcRow + x * 3;
                                int dstIdx = ((width - 1 - x) * newW + y) * 3;
                                dst[dstIdx + 0] = src[srcIdx + 0];
                                dst[dstIdx + 1] = src[srcIdx + 1];
                                dst[dstIdx + 2] = src[srcIdx + 2];
                            }
                        }
                    }
                }
                break;
        }
        return (dst, newW, newH);
    }

    private static (byte[] pixels, int width, int height) ApplyExifOrientationInPlace(byte[] src, int width, int height, int orientation)
    {
        switch (orientation)
        {
            case 1:
                return (src, width, height);
            case 2:
            {
                for (int y = 0; y < height; y++)
                {
                    int left = (y * width) * 3;
                    int right = (y * width + (width - 1)) * 3;
                    while (left < right)
                    {
                        byte r = src[left];
                        byte g = src[left + 1];
                        byte b = src[left + 2];
                        src[left] = src[right];
                        src[left + 1] = src[right + 1];
                        src[left + 2] = src[right + 2];
                        src[right] = r;
                        src[right + 1] = g;
                        src[right + 2] = b;
                        left += 3;
                        right -= 3;
                    }
                }
                return (src, width, height);
            }
            case 3:
            {
                int total = width * height;
                int left = 0;
                int right = (total - 1) * 3;
                while (left < right)
                {
                    byte r = src[left];
                    byte g = src[left + 1];
                    byte b = src[left + 2];
                    src[left] = src[right];
                    src[left + 1] = src[right + 1];
                    src[left + 2] = src[right + 2];
                    src[right] = r;
                    src[right + 1] = g;
                    src[right + 2] = b;
                    left += 3;
                    right -= 3;
                }
                return (src, width, height);
            }
            case 4:
            {
                int rowBytes = width * 3;
                int top = 0;
                int bottom = (height - 1) * rowBytes;
                while (top < bottom)
                {
                    for (int i = 0; i < rowBytes; i++)
                    {
                        byte tmp = src[top + i];
                        src[top + i] = src[bottom + i];
                        src[bottom + i] = tmp;
                    }
                    top += rowBytes;
                    bottom -= rowBytes;
                }
                return (src, width, height);
            }
            default:
                return ApplyExifOrientation(src, width, height, orientation);
        }
    }
}
