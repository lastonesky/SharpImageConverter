using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;

namespace SharpImageConverter.Formats.Gif
{
    /// <summary>
    /// GIF 编码器适配器（RGB24）
    /// </summary>
    public sealed class GifEncoderAdapter : IImageEncoder
    {
        /// <summary>
        /// 将 RGB24 图像编码为 GIF 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            var encoder = new GifEncoder();
            using var fs = File.Create(path);
            var frame = new ImageFrame(image.Width, image.Height, image.Buffer);
            encoder.Encode(frame, fs);
        }

        /// <summary>
        /// 将 RGB24 图像编码为 GIF 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image)
        {
            var encoder = new GifEncoder();
            var frame = new ImageFrame(image.Width, image.Height, image.Buffer);
            encoder.Encode(frame, stream);
        }
    }

    /// <summary>
    /// GIF 编码器适配器（RGBA32）
    /// </summary>
    public sealed class GifEncoderAdapterRgba : IImageEncoderRgba
    {
        /// <summary>
        /// 将 RGBA32 图像编码为 GIF 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(string path, Image<Rgba32> image)
        {
            var encoder = new GifEncoder();
            using var fs = File.Create(path);
            encoder.EncodeRgba(image.Width, image.Height, image.Buffer, fs);
        }

        /// <summary>
        /// 将 RGBA32 图像编码为 GIF 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(Stream stream, Image<Rgba32> image)
        {
            var encoder = new GifEncoder();
            encoder.EncodeRgba(image.Width, image.Height, image.Buffer, stream);
        }
    }

    /// <summary>
    /// GIF 解码器适配器（RGBA32）
    /// </summary>
    public sealed class GifDecoderRgbaAdapter : IImageDecoderRgba
    {
        /// <summary>
        /// 解码 GIF 文件为 RGBA32 图像
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(string path)
        {
            var dec = new GifDecoder();
            return dec.DecodeRgba32(path);
        }

        /// <summary>
        /// 解码 GIF 流为 RGBA32 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(Stream stream)
        {
            var dec = new GifDecoder();
            return dec.DecodeRgba32(stream);
        }
    }
}
