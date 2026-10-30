using System;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 图像容器格式。
    /// </summary>
    /// <remarks>
    /// 这里的枚举是**全库唯一**的格式标识：格式嗅探、内存预检、流式转换与
    /// <see cref="Image.Identify(string)"/> 共用同一套值，新增格式时只需改
    /// <see cref="ImageHeaderReader"/> 与注册表各一处，不再出现「三份嗅探各自漂移」。
    /// </remarks>
    public enum ImageFormatKind
    {
        /// <summary>无法识别</summary>
        Unknown = 0,
        /// <summary>JPEG（JFIF/EXIF，baseline 或 progressive）</summary>
        Jpeg = 1,
        /// <summary>PNG</summary>
        Png = 2,
        /// <summary>GIF（87a/89a，单帧或动画）</summary>
        Gif = 3,
        /// <summary>WebP（VP8 / VP8L / VP8X）</summary>
        Webp = 4,
        /// <summary>BMP</summary>
        Bmp = 5,
    }

    /// <summary>
    /// 只读文件头得到的图像信息：尺寸、格式与少量结构标志。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="Image.Identify(string)"/> / <see cref="Image.Identify(System.IO.Stream)"/> 产生。
    /// 只解析文件头，**不解码像素**：143 MP 的 JPEG 识别耗时在微秒级，
    /// 可用于批量筛选、内存预检、生成缩略图前的尺寸校验。
    /// </remarks>
    public readonly struct ImageInfo : IEquatable<ImageInfo>
    {
        internal ImageInfo(ImageFormatKind format, int width, int height, int frameCount, bool isProgressive)
        {
            Format = format;
            Width = width;
            Height = height;
            FrameCount = frameCount;
            IsProgressive = isProgressive;
        }

        /// <summary>容器格式</summary>
        public ImageFormatKind Format { get; }

        /// <summary>格式的展示名（JPEG / PNG / GIF / WebP / BMP），未知时为 "Unknown"</summary>
        public string FormatName => GetFormatName(Format);

        /// <summary>宽度（像素）</summary>
        public int Width { get; }

        /// <summary>高度（像素）</summary>
        public int Height { get; }

        /// <summary>像素总数（<see cref="Width"/> × <see cref="Height"/>，以 long 返回避免溢出）</summary>
        public long PixelCount => (long)Width * Height;

        /// <summary>
        /// 帧数。GIF 为实际图像块数量（≥1）；其它格式恒为 1。
        /// </summary>
        public int FrameCount { get; }

        /// <summary>是否为渐进式 JPEG（SOF2/SOF6/SOF10/SOF14）。其它格式恒为 false。</summary>
        public bool IsProgressive { get; }

        /// <summary>是否为动图（帧数大于 1）</summary>
        public bool IsAnimated => FrameCount > 1;

        internal static string GetFormatName(ImageFormatKind kind) => kind switch
        {
            ImageFormatKind.Jpeg => "JPEG",
            ImageFormatKind.Png => "PNG",
            ImageFormatKind.Gif => "GIF",
            ImageFormatKind.Webp => "WebP",
            ImageFormatKind.Bmp => "BMP",
            _ => "Unknown",
        };

        /// <summary>值相等比较</summary>
        /// <param name="other">另一个图像信息</param>
        public bool Equals(ImageInfo other) =>
            Format == other.Format
            && Width == other.Width
            && Height == other.Height
            && FrameCount == other.FrameCount
            && IsProgressive == other.IsProgressive;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is ImageInfo other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Format, Width, Height, FrameCount, IsProgressive);

        /// <summary>相等运算符</summary>
        public static bool operator ==(ImageInfo left, ImageInfo right) => left.Equals(right);

        /// <summary>不等运算符</summary>
        public static bool operator !=(ImageInfo left, ImageInfo right) => !left.Equals(right);

        /// <inheritdoc/>
        public override string ToString() =>
            $"{FormatName} {Width}x{Height}" +
            (IsAnimated ? $" x{FrameCount}f" : string.Empty) +
            (IsProgressive ? " (progressive)" : string.Empty);
    }
}
