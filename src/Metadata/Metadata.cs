namespace SharpImageConverter.Metadata
{
    /// <summary>
    /// 图像的元数据信息，目前包含 EXIF 方向。
    /// </summary>
    public sealed class ImageMetadata
    {
        /// <summary>
        /// EXIF 方向（1-8），用于描述图像的旋转/翻转状态。
        /// </summary>
        public int Orientation { get; set; }
    }
}
