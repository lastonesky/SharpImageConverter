using System;
using System.IO;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 通用图像类型，包含宽度、高度与像素缓冲区。
    /// </summary>
    public sealed class Image<TPixel> where TPixel : struct, IPixel
    {
        /// <summary>
        /// 图像宽度（像素）
        /// </summary>
        public int Width { get; private set; }
        /// <summary>
        /// 图像高度（像素）
        /// </summary>
        public int Height { get; private set; }
        /// <summary>
        /// 像素数据缓冲区
        /// </summary>
        public byte[] Buffer { get; private set; }

        public ImageMetadata Metadata { get; }

        /// <summary>
        /// 使用指定尺寸与缓冲区创建图像
        /// </summary>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="buffer">像素缓冲区</param>
        public Image(int width, int height, byte[] buffer, ImageMetadata? metadata = null)
        {
            ValidateDimensionsAndBuffer(width, height, buffer);
            Width = width;
            Height = height;
            Buffer = buffer;
            Metadata = metadata ?? new ImageMetadata();
        }

        /// <summary>
        /// 零拷贝转成 <see cref="ImageFrame"/>（共享像素缓冲区与元数据实例）。
        /// </summary>
        /// <remarks>
        /// 两个模型长期并存且分工固定：本类型保留「编译期像素类型」与处理管线，
        /// <see cref="ImageFrame"/> 负责流式加载、BGR 互操作与统一的保存链路。
        /// 转换只包装缓冲区、不复制像素，因此任一侧改像素另一侧立刻可见。
        /// <see cref="Gray8"/> 没有对应的 <see cref="ImagePixelFormat"/>，会抛
        /// <see cref="NotSupportedException"/>（灰度写盘走 <c>Configuration.SaveGray8</c>）。
        /// </remarks>
        /// <returns>共享同一缓冲区与元数据的图像帧</returns>
        /// <exception cref="NotSupportedException">当前像素类型没有对应的帧格式</exception>
        public ImageFrame ToFrame()
        {
            ImagePixelFormat format;
            if (typeof(TPixel) == typeof(Rgb24))
            {
                format = ImagePixelFormat.Rgb24;
            }
            else if (typeof(TPixel) == typeof(Rgba32))
            {
                format = ImagePixelFormat.Rgba32;
            }
            else
            {
                throw new NotSupportedException(
                    $"像素类型 {typeof(TPixel).Name} 没有对应的 ImagePixelFormat"
                    + "（帧支持 Rgb24 / Rgba32；Gray8 请用 Configuration.SaveGray8）");
            }
            return new ImageFrame(Width, Height, Buffer, format, Metadata);
        }

        /// <summary>
        /// 更新图像的尺寸与像素缓冲区
        /// </summary>
        /// <param name="width">新宽度</param>
        /// <param name="height">新高度</param>
        /// <param name="buffer">新像素缓冲区</param>
        public void Update(int width, int height, byte[] buffer)
        {
            ValidateDimensionsAndBuffer(width, height, buffer);
            Width = width;
            Height = height;
            Buffer = buffer;
        }

        private static void ValidateDimensionsAndBuffer(int width, int height, byte[] buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(width, nameof(width));
            ArgumentOutOfRangeException.ThrowIfNegative(height, nameof(height));
            ArgumentNullException.ThrowIfNull(buffer, nameof(buffer));

            var pixel = default(TPixel);
            int bytesPerPixel = pixel.BytesPerPixel;
            if (bytesPerPixel <= 0)
            {
                // 这是像素类型实现（IPixel）的错误，不是调用方参数错误，
                // 因此不能把 "TPixel" 当作 paramName 传给 ArgumentOutOfRangeException。
                throw new InvalidOperationException(
                    $"像素类型 {typeof(TPixel).Name} 的 BytesPerPixel 必须为正数，实际为 {bytesPerPixel}");
            }

            long expectedLength = (long)width * height * bytesPerPixel;
            if (expectedLength > int.MaxValue)
            {
                throw new ArgumentException("图像尺寸超出可表示的缓冲区范围");
            }
            if (buffer.Length != expectedLength)
            {
                throw new ArgumentException("像素缓冲区长度与尺寸不匹配", nameof(buffer));
            }
        }
    }

    /// <summary>
    /// 简化的加载/保存入口。
    /// </summary>
    public static class Image
    {
        /// <summary>
        /// 加载为 Rgb24 图像（自动识别格式）
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public static Image<Rgb24> Load(string path)
        {
            return Configuration.Default.LoadRgb24(path);
        }

        /// <summary>
        /// 从流加载为 Rgb24 图像（按文件头自动识别格式）。
        /// </summary>
        /// <remarks>
        /// 流必须可 Seek。需要处理不可 Seek 的网络流时用
        /// <see cref="ImageFrame.Load(Stream, bool)"/>。
        /// </remarks>
        /// <param name="stream">输入数据流</param>
        /// <param name="extensionHint">可选的扩展名提示（如 ".png"），仅影响解码器尝试顺序</param>
        /// <returns>Rgb24 图像</returns>
        public static Image<Rgb24> Load(Stream stream, string? extensionHint = null)
        {
            return Configuration.Default.LoadRgb24(stream, extensionHint);
        }

        /// <summary>
        /// 保存 Rgb24 图像到指定路径（根据扩展名选择格式）
        /// </summary>
        /// <param name="image">Rgb24 图像</param>
        /// <param name="path">输出文件路径</param>
        public static void Save(Image<Rgb24> image, string path)
        {
            Configuration.Default.SaveRgb24(image, path);
        }

        /// <summary>
        /// 保存 Rgb24 图像到流（按扩展名选择格式）。
        /// </summary>
        /// <param name="image">Rgb24 图像</param>
        /// <param name="stream">输出流（不负责关闭）</param>
        /// <param name="extension">目标格式扩展名（".png" 或 "png"）</param>
        public static void Save(Image<Rgb24> image, Stream stream, string extension)
        {
            Configuration.Default.SaveRgb24(image, stream, extension);
        }

        public static void Save(Image<Gray8> image, string path)
        {
            Configuration.Default.SaveGray8(image, path);
        }

        /// <summary>
        /// 加载为 Rgba32 图像（自动识别格式）
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgba32 图像</returns>
        public static Image<Rgba32> LoadRgba32(string path)
        {
            return Configuration.Default.LoadRgba32(path);
        }

        /// <summary>
        /// 从流加载为 Rgba32 图像（按文件头自动识别格式）。
        /// </summary>
        /// <param name="stream">输入数据流（必须可 Seek）</param>
        /// <param name="extensionHint">可选的扩展名提示（如 ".png"）</param>
        /// <returns>Rgba32 图像</returns>
        public static Image<Rgba32> LoadRgba32(Stream stream, string? extensionHint = null)
        {
            return Configuration.Default.LoadRgba32(stream, extensionHint);
        }

        /// <summary>
        /// 保存 Rgba32 图像到指定路径（根据扩展名选择格式）
        /// </summary>
        /// <param name="image">Rgba32 图像</param>
        /// <param name="path">输出文件路径</param>
        public static void Save(Image<Rgba32> image, string path)
        {
            Configuration.Default.SaveRgba32(image, path);
        }

        /// <summary>
        /// 保存 Rgba32 图像到流（按扩展名选择格式；格式不支持 alpha 时自动降通道）。
        /// </summary>
        /// <param name="image">Rgba32 图像</param>
        /// <param name="stream">输出流（不负责关闭）</param>
        /// <param name="extension">目标格式扩展名（".png" 或 "png"）</param>
        public static void Save(Image<Rgba32> image, Stream stream, string extension)
        {
            Configuration.Default.SaveRgba32(image, stream, extension);
        }

        /// <summary>
        /// 只读文件头，识别格式并取尺寸/帧数/渐进标志，**不解码像素**。
        /// </summary>
        /// <remarks>
        /// 用于批量筛选、内存预检、解码前校验等场景：143 MP 的 JPEG 识别在微秒级，
        /// 而完整解码要几百毫秒。
        /// </remarks>
        /// <param name="path">输入文件路径</param>
        /// <returns>图像基本信息</returns>
        /// <exception cref="NotSupportedException">文件头无法识别为已支持的格式</exception>
        /// <exception cref="InvalidDataException">格式已识别但文件头损坏，读不出宽高</exception>
        public static ImageInfo Identify(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IdentifyCore(fs);
        }

        /// <summary>
        /// 只读文件头，识别格式并取尺寸/帧数/渐进标志，**不解码像素**。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 可 Seek 的流：解析完成后位置还原到起始处，调用方可以接着解码。
        /// </para>
        /// <para>
        /// 不可 Seek 的流：**不会被整读进内存**，只会顺序读到文件头所需的位置
        /// （GIF 要扫到 trailer 才能数帧、动画 WebP 要数 <c>ANMF</c>，其余格式读到 SOF/IHDR 即止），
        /// 内存开销与文件大小无关。代价是流被消费掉且无法回退——需要接着解码请自行缓冲。
        /// </para>
        /// </remarks>
        /// <param name="stream">输入数据流</param>
        /// <returns>图像基本信息</returns>
        /// <exception cref="NotSupportedException">文件头无法识别为已支持的格式</exception>
        /// <exception cref="InvalidDataException">格式已识别但文件头损坏，读不出宽高</exception>
        public static ImageInfo Identify(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            return IdentifyCore(stream);
        }

        private static ImageInfo IdentifyCore(Stream stream)
        {
            bool canSeek = stream.CanSeek;
            long start = canSeek ? stream.Position : 0;
            try
            {
                Span<byte> head = stackalloc byte[ImageHeaderReader.SniffBytes];
                int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                var kind = ImageHeaderReader.Sniff(head[..read]);
                if (kind == ImageFormatKind.Unknown)
                {
                    throw new NotSupportedException($"无法识别的图像格式（当前支持 {ImageInfo.SupportedFormatList}）");
                }
                if (ImageInfo.IsRecognizedButUnsupported(kind))
                {
                    // TIFF / ICO 能识别但不读写：报出格式名，别让调用方自己去猜文件是什么。
                    throw new NotSupportedException(ImageInfo.UnsupportedMessage(kind));
                }

                ImageInfo info;
                bool ok;
                if (canSeek)
                {
                    stream.Position = start;
                    ok = ImageHeaderReader.TryReadInfo(stream, kind, out info);
                }
                else
                {
                    // 不可 Seek 无法回退，把已读的嗅探字节回放给解析器——否则它会从第 12 字节开始解析。
                    // 只回放这十几个字节，内存与文件大小无关（此前是把整条流复制进 MemoryStream）。
                    using var replay = new PrefixStream(head[..read].ToArray(), stream);
                    ok = ImageHeaderReader.TryReadInfo(replay, kind, out info);
                }
                if (!ok)
                {
                    throw new InvalidDataException($"{ImageInfo.GetFormatName(kind)} 文件头损坏，无法读出宽高");
                }

                return info;
            }
            finally
            {
                if (canSeek) stream.Position = start;
            }
        }

        /// <summary>
        /// 只读流包装：先吐出 <paramref name="prefix"/> 里已读过的字节，再继续读底层流。
        /// </summary>
        /// <remarks>
        /// 只用于「嗅探时已经消费了若干字节、但底层流不可 Seek」的场景。
        /// 本身不可 Seek（因为底层不可 Seek），不支持写。
        /// </remarks>
        private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
        {
            private int _offset;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(Span<byte> buffer)
            {
                if (_offset < prefix.Length)
                {
                    int take = Math.Min(buffer.Length, prefix.Length - _offset);
                    prefix.AsSpan(_offset, take).CopyTo(buffer);
                    _offset += take;
                    return take;
                }
                return inner.Read(buffer);
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
