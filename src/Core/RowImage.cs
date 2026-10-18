using System;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 逐行图像源：供低内存转换路径使用，每次只暴露一行像素。
    /// </summary>
    /// <remarks>
    /// 行数据统一为交错 RGB 或 RGBA。调用方按 <see cref="Channels"/> 解释：
    /// 3 = R,G,B；4 = R,G,B,A。实现必须允许顺序调用 <see cref="ReadRow"/>，
    /// 读完 <see cref="Height"/> 行后返回 false。
    /// </remarks>
    internal interface IRowImageSource : IDisposable
    {
        int Width { get; }
        int Height { get; }

        /// <summary>3 或 4。</summary>
        int Channels { get; }

        /// <summary>源图是否包含有意义的 alpha（仅用于选择输出 PNG 颜色类型）。</summary>
        bool HasAlpha { get; }

        /// <summary>读取下一行；行缓冲长度至少为 <c>Width * Channels</c>。</summary>
        bool ReadRow(Span<byte> row);
    }

    /// <summary>
    /// 逐行图像汇：供低内存转换路径使用。
    /// </summary>
    internal interface IRowImageSink : IDisposable
    {
        /// <summary>写入一行；行布局由创建时传入的源通道数决定。</summary>
        void WriteRow(ReadOnlySpan<byte> row, int channels);

        /// <summary>完成写入（flush / 写尾部块）。重复调用应无害。</summary>
        void Complete();
    }
}
