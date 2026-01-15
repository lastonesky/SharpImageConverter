using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;

namespace SharpImageConverter.Formats
{
    /// <summary>
    /// JPEG 解码适配器（RGB24），支持 EXIF 方向处理。
    /// </summary>
    public sealed class JpegDecoderAdapter : IImageDecoder
    {
        /// <summary>
        /// 解码 JPEG 为 Rgb24 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            return DecodeRgb24(fs);
        }

        /// <summary>
        /// 解码 JPEG 为 Rgb24 图像
        /// </summary>
        /// <param name="stream">输入数据流</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(Stream stream)
        {
            var decoder = new JpegDecoder();
            var img = decoder.Decode(stream);
            if (decoder.ExifOrientation != 1)
            {
                var frame = new ImageFrame(img.Width, img.Height, img.Buffer, img.Metadata);
                frame = frame.ApplyExifOrientation(decoder.ExifOrientation);
                img.Update(frame.Width, frame.Height, frame.Pixels);
                img.Metadata.Orientation = 1;
            }
            return img;
        }
    }

    /// <summary>
    /// JPEG 编码适配器（RGB24）。
    /// </summary>
    public sealed class JpegEncoderAdapter : IImageEncoder
    {
        /// <summary>
        /// 保存 Rgb24 图像为 JPEG 文件（默认质量 75）
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">Rgb24 图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            JpegEncoder.Write(path, image.Width, image.Height, image.Buffer, 75);
        }

        /// <summary>
        /// 保存 Rgb24 图像为 JPEG 流（默认质量 75）
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">Rgb24 图像</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image)
        {
            JpegEncoder.Write(stream, image.Width, image.Height, image.Buffer, 75);
        }
    }
}
