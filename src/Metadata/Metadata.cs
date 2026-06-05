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
