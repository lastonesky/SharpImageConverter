using System;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 像素类型接口。
    /// </summary>
    public interface IPixel
    {
        /// <summary>
        /// 每像素字节数
        /// </summary>
        int BytesPerPixel { get; }
    }

    /// <summary>
    /// 24 位 RGB 像素（8 位 R、G、B）。
    /// </summary>
    public struct Rgb24 : IPixel
    {
        /// <summary>
        /// 红色分量（0-255）
        /// </summary>
        public byte R;
        /// <summary>
        /// 绿色分量（0-255）
        /// </summary>
        public byte G;
        /// <summary>
        /// 蓝色分量（0-255）
        /// </summary>
        public byte B;
        /// <summary>
        /// 每像素字节数（3）
        /// </summary>
        public int BytesPerPixel => 3;
    }

    /// <summary>
    /// 32 位 RGBA 像素（8 位 R、G、B、A）。
    /// </summary>
    public struct Rgba32 : IPixel
    {
        /// <summary>
        /// 红色分量（0-255）
        /// </summary>
        public byte R;
        /// <summary>
        /// 绿色分量（0-255）
        /// </summary>
        public byte G;
        /// <summary>
        /// 蓝色分量（0-255）
        /// </summary>
        public byte B;
        /// <summary>
        /// 透明度分量（0-255）
        /// </summary>
        public byte A;
        /// <summary>
        /// 每像素字节数（4）
        /// </summary>
        public int BytesPerPixel => 4;
    }

    /// <summary>
    /// 8 位灰度像素。
    /// </summary>
    public struct Gray8 : IPixel
    {
        /// <summary>
        /// 灰度值（0-255）
        /// </summary>
        public byte V;
        /// <summary>
        /// 每像素字节数（1）
        /// </summary>
        public int BytesPerPixel => 1;
    }
}
