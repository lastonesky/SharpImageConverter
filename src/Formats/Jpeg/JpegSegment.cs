using System;

namespace SharpImageConverter;

/// <summary>
/// 表示 JPEG 文件中的一个段（Marker + 长度 + 数据）。
/// </summary>
public class JpegSegment
{
    /// <summary>
    /// 段标记（如 FFD8、FFE0、FFC0 等）
    /// </summary>
    public ushort Marker { get; }
    /// <summary>
    /// 段在文件中的起始偏移（字节）
    /// </summary>
    public int Offset { get; }
    /// <summary>
    /// 段长度（包含长度字段本身的 2 字节）
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// 使用指定参数创建 JPEG 段描述
    /// </summary>
    /// <param name="marker">段标记</param>
    /// <param name="offset">文件偏移</param>
    /// <param name="length">段长度</param>
    public JpegSegment(ushort marker, int offset, int length)
    {
        Marker = marker;
        Offset = offset;
        Length = length;
    }

    /// <summary>
    /// 返回段的简要字符串表示
    /// </summary>
    public override string ToString()
        => $"Marker=0x{Marker:X4}, Offset={Offset}, Length={Length}";
}
