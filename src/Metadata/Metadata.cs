namespace SharpImageConverter.Metadata
{
    public sealed class ImageMetadata
    {
        public int Orientation { get; set; } = 1;

        public byte[]? ExifRaw { get; set; }
        public ExifMetadata? Exif { get; set; }

        public byte[]? IccProfile { get; set; }
        public IccProfileKind IccProfileKind { get; set; } = IccProfileKind.Unknown;

        /// <summary>
        /// 水平分辨率（DPI），0 表示未知。
        /// </summary>
        public double HorizontalDpi { get; set; }

        /// <summary>
        /// 垂直分辨率（DPI），0 表示未知。
        /// </summary>
        public double VerticalDpi { get; set; }

        /// <summary>
        /// 浅拷贝一份元数据（<see cref="Exif"/>、<see cref="ExifRaw"/>、<see cref="IccProfile"/>
        /// 仍是同一引用——它们体积大且极少被原地改）。
        /// </summary>
        /// <remarks>
        /// 用于「要返回新帧，但原帧的元数据不能被改」的场景：
        /// <see cref="ImageFrame.ApplyExifOrientation(int)"/> 需要把方向重置为 1，
        /// 而两个帧默认共享同一个 <see cref="ImageMetadata"/> 实例。
        /// </remarks>
        public ImageMetadata Clone() => new()
        {
            Orientation = Orientation,
            ExifRaw = ExifRaw,
            Exif = Exif,
            IccProfile = IccProfile,
            IccProfileKind = IccProfileKind,
            HorizontalDpi = HorizontalDpi,
            VerticalDpi = VerticalDpi,
        };
    }

    public enum IccProfileKind
    {
        Unknown = 0,
        SRgb = 1,
        AdobeRgb = 2,
        DisplayP3 = 3,
        ProPhotoRgb = 4
    }

    public sealed class ExifMetadata
    {
        public string? Make { get; set; }
        public string? Model { get; set; }
        public string? DateTime { get; set; }
        public string? DateTimeOriginal { get; set; }
        public double? GpsLatitude { get; set; }
        public double? GpsLongitude { get; set; }
    }
}
