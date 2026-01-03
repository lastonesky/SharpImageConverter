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
            var parser = new JpegParser();
            parser.Parse(path);
            var dec = new JpegDecoder(parser);
            var rgb = dec.DecodeToRGB(path);
            var img = new Image<Rgb24>(parser.Width, parser.Height, rgb);
            if (parser.ExifOrientation != 1)
            {
                var frame = new ImageFrame(parser.Width, parser.Height, rgb);
                frame = frame.ApplyExifOrientation(parser.ExifOrientation);
                img.Update(frame.Width, frame.Height, frame.Pixels);
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
    }
}
