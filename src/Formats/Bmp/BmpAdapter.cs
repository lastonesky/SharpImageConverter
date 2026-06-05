using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using SharpImageConverter.Metadata;
using System.IO;

namespace SharpImageConverter.Formats.Bmp
{
    /// <summary>
    /// BMP 解码适配器（RGB24）。
    /// </summary>
    public sealed class BmpDecoderAdapter : IImageDecoder
    {
        /// <summary>
        /// 解码 BMP 为 Rgb24 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return DecodeRgb24(fs);
        }

        /// <summary>
        /// 解码 BMP 为 Rgb24 图像
        /// </summary>
        /// <param name="stream">输入数据流</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(Stream stream)
        {
            var rgb = BmpReader.Read(stream, out int width, out int height, out double xDpi, out double yDpi);
            var metadata = new ImageMetadata();
            if (xDpi > 0) metadata.HorizontalDpi = xDpi;
            if (yDpi > 0) metadata.VerticalDpi = yDpi;
            return new Image<Rgb24>(width, height, rgb, metadata);
        }
    }

    /// <summary>
    /// BMP 编码适配器（RGB24）。
    /// </summary>
    public sealed class BmpEncoderAdapter : IImageEncoder
    {
        /// <summary>
        /// 保存 Rgb24 图像为 BMP 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">Rgb24 图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            BmpWriter.Write24(path, image.Width, image.Height, image.Buffer, image.Metadata);
        }

        /// <summary>
        /// 保存 Rgb24 图像为 BMP 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">Rgb24 图像</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image)
        {
            BmpWriter.Write24(stream, image.Width, image.Height, image.Buffer, image.Metadata);
        }
    }
}
