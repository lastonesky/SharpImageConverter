using System;
using System.IO;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 图像格式描述与探测接口。
    /// </summary>
    public interface IImageFormat
    {
        /// <summary>
        /// 格式名称（如 JPEG、PNG）
        /// </summary>
        string Name { get; }
        /// <summary>
        /// 支持的文件扩展名（如 .jpg、.png）
        /// </summary>
        string[] Extensions { get; }
        /// <summary>
        /// 判断输入流是否匹配该格式
        /// </summary>
        /// <param name="s">输入数据流</param>
        /// <returns>匹配则为 true，否则为 false</returns>
        bool IsMatch(Stream s);
    }

    /// <summary>
    /// 将文件解码为 Rgb24 图像的解码器接口。
    /// </summary>
    public interface IImageDecoder
    {
        /// <summary>
        /// 解码为 Rgb24 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        Image<Rgb24> DecodeRgb24(string path);
    }

    /// <summary>
    /// 将文件解码为 Rgba32 图像的解码器接口。
    /// </summary>
    public interface IImageDecoderRgba
    {
        /// <summary>
        /// 解码为 Rgba32 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgba32 图像</returns>
        Image<Rgba32> DecodeRgba32(string path);
    }

    /// <summary>
    /// 将 Rgb24 图像编码并保存的编码器接口。
    /// </summary>
    public interface IImageEncoder
    {
        /// <summary>
        /// 保存 Rgb24 图像到指定路径
        /// </summary>
        /// <param name="path">输出文件路径</param>
        /// <param name="image">Rgb24 图像</param>
        void EncodeRgb24(string path, Image<Rgb24> image);
    }

    /// <summary>
    /// 将 Rgba32 图像编码并保存的编码器接口。
    /// </summary>
    public interface IImageEncoderRgba
    {
        /// <summary>
        /// 保存 Rgba32 图像到指定路径
        /// </summary>
        /// <param name="path">输出文件路径</param>
        /// <param name="image">Rgba32 图像</param>
        void EncodeRgba32(string path, Image<Rgba32> image);
    }

    /// <summary>
    /// 图像基础信息接口。
    /// </summary>
    public interface IImageInfo
    {
        /// <summary>
        /// 宽度（像素）
        /// </summary>
        int Width { get; }
        /// <summary>
        /// 高度（像素）
        /// </summary>
        int Height { get; }
    }
}
