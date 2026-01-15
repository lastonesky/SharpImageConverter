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
            Width = width;
            Height = height;
            Buffer = buffer;
            Metadata = metadata ?? new ImageMetadata();
        }

        /// <summary>
        /// 更新图像的尺寸与像素缓冲区
        /// </summary>
        /// <param name="width">新宽度</param>
        /// <param name="height">新高度</param>
        /// <param name="buffer">新像素缓冲区</param>
        public void Update(int width, int height, byte[] buffer)
        {
            Width = width;
            Height = height;
            Buffer = buffer;
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
        /// 保存 Rgb24 图像到指定路径（根据扩展名选择格式）
        /// </summary>
        /// <param name="image">Rgb24 图像</param>
        /// <param name="path">输出文件路径</param>
        public static void Save(Image<Rgb24> image, string path)
        {
            Configuration.Default.SaveRgb24(image, path);
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
        /// 保存 Rgba32 图像到指定路径（根据扩展名选择格式）
        /// </summary>
        /// <param name="image">Rgba32 图像</param>
        /// <param name="path">输出文件路径</param>
        public static void Save(Image<Rgba32> image, string path)
        {
            Configuration.Default.SaveRgba32(image, path);
        }
    }
}
