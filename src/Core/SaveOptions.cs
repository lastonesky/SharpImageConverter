using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 保存选项：**只在需要覆盖已注册编码器的默认值时**提供对应项，未设置的项沿用编码器自身的设置。
    /// </summary>
    /// <remarks>
    /// 选项按调用传入、不写回注册表里的编码器实例——<see cref="Configuration.Default"/> 是进程级单例，
    /// 改它的属性会让并发保存互相踩（这正是 <see cref="JpegEncoderAdapter.Quality"/> 这类
    /// 可变属性的老问题）。因此本类型刻意保持"参数包"形态。
    /// </remarks>
    public sealed class SaveOptions
    {
        /// <summary>
        /// JPEG 参数（质量 / 子采样 / 是否保留元数据 / 优化 Huffman / trellis 量化）。
        /// </summary>
        /// <remarks>
        /// 为 null 时使用已注册 JPEG 编码器的当前设置（默认 q75、4:2:0、保留元数据）。
        /// 目标是 JPEG 以外的格式时该字段被忽略。
        /// </remarks>
        public JpegEncoderOptions? Jpeg { get; set; }

        /// <summary>
        /// WebP 有损质量（0-100）。为 null 时使用已注册 WebP 编码器的当前设置（默认 75）。
        /// </summary>
        public float? WebpQuality { get; set; }

        /// <summary>
        /// 复制一份选项，避免调用方在保存过程中继续改同一个实例。
        /// </summary>
        /// <returns>浅拷贝（字段都是值类型，等同深拷贝）</returns>
        public SaveOptions Clone() => (SaveOptions)MemberwiseClone();
    }
}
