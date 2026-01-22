using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;

namespace SharpImageConverter.Formats.Png
{
    /// <summary>
    /// PNG 解码适配器（RGB24）。
    /// </summary>
    public sealed class PngDecoderAdapter : IImageDecoder
    {
        /// <summary>
        /// 解码 PNG 为 Rgb24 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(string path)
        {
            var dec = new PngDecoder();
            var rgb = dec.DecodeToRGB(path);
            return new Image<Rgb24>(dec.Width, dec.Height, rgb);
        }

        /// <summary>
        /// 解码 PNG 流为 Rgb24 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>Rgb24 图像</returns>
        public Image<Rgb24> DecodeRgb24(Stream stream)
        {
            var dec = new PngDecoder();
            var rgb = dec.DecodeToRGB(stream);
            return new Image<Rgb24>(dec.Width, dec.Height, rgb);
        }
    }

    /// <summary>
    /// PNG 解码适配器（RGBA32）。
    /// </summary>
    public sealed class PngDecoderAdapterRgba : IImageDecoderRgba
    {
        /// <summary>
        /// 解码 PNG 为 Rgba32 图像
        /// </summary>
        /// <param name="path">输入文件路径</param>
        /// <returns>Rgba32 图像</returns>
        public Image<Rgba32> DecodeRgba32(string path)
        {
            var dec = new PngDecoder();
            var rgba = dec.DecodeToRGBA(path);
            return new Image<Rgba32>(dec.Width, dec.Height, rgba);
        }

        /// <summary>
        /// 解码 PNG 流为 Rgba32 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>Rgba32 图像</returns>
        public Image<Rgba32> DecodeRgba32(Stream stream)
        {
            var dec = new PngDecoder();
            var rgba = dec.DecodeToRGBA(stream);
            return new Image<Rgba32>(dec.Width, dec.Height, rgba);
        }
    }

    /// <summary>
    /// PNG 编码适配器（RGB24）。
    /// </summary>
    public sealed class PngEncoderAdapter : IImageEncoder
    {
        /// <summary>
        /// 保存 Rgb24 图像为 PNG 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">Rgb24 图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            PngWriter.Write(path, image.Width, image.Height, image.Buffer);
        }

        /// <summary>
        /// 保存 Rgb24 图像为 PNG 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">Rgb24 图像</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image)
        {
            PngWriter.Write(stream, image.Width, image.Height, image.Buffer);
        }
    }

    /// <summary>
    /// PNG 编码适配器（RGBA32）。
    /// </summary>
    public sealed class PngEncoderAdapterRgba : IImageEncoderRgba
    {
        /// <summary>
        /// 保存 Rgba32 图像为 PNG 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">Rgba32 图像</param>
        public void EncodeRgba32(string path, Image<Rgba32> image)
        {
            PngWriter.WriteRgba(path, image.Width, image.Height, image.Buffer);
        }

        /// <summary>
        /// 保存 Rgba32 图像为 PNG 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">Rgba32 图像</param>
        public void EncodeRgba32(Stream stream, Image<Rgba32> image)
        {
            PngWriter.WriteRgba(stream, image.Width, image.Height, image.Buffer);
        }
    }
}
