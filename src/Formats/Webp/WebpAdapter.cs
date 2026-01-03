using System;
using SharpImageConverter.Core;
using SharpImageConverter;
using System.IO;

namespace SharpImageConverter.Formats
{
    /// <summary>
    /// WebP 解码器适配器（RGB24）
    /// </summary>
    public sealed class WebpDecoderAdapter : IImageDecoder
    {
        /// <summary>
        /// 解码 WebP 文件为 RGB24 图像
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>RGB24 图像</returns>
        public Image<Rgb24> DecodeRgb24(string path)
        {
            var rgba = WebpCodec.DecodeRgba(File.ReadAllBytes(path), out int width, out int height);
            var rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j + 0] = rgba[i + 0];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
            return new Image<Rgb24>(width, height, rgb);
        }
    }

    /// <summary>
    /// WebP 解码器适配器（RGBA32）
    /// </summary>
    public sealed class WebpDecoderAdapterRgba : IImageDecoderRgba
    {
        /// <summary>
        /// 解码 WebP 文件为 RGBA32 图像
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(string path)
        {
            var rgba = WebpCodec.DecodeRgba(File.ReadAllBytes(path), out int width, out int height);
            return new Image<Rgba32>(width, height, rgba);
        }
    }

    /// <summary>
    /// WebP 编码器适配器（RGB24）
    /// </summary>
    public sealed class WebpEncoderAdapter : IImageEncoder
    {
        /// <summary>
        /// 将 RGB24 图像编码为 WebP 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            var rgba = new byte[image.Width * image.Height * 4];
            for (int i = 0, j = 0; j < image.Buffer.Length; i += 4, j += 3)
            {
                rgba[i + 0] = image.Buffer[j + 0];
                rgba[i + 1] = image.Buffer[j + 1];
                rgba[i + 2] = image.Buffer[j + 2];
                rgba[i + 3] = 255;
            }
            var webp = WebpCodec.EncodeRgba(rgba, image.Width, image.Height, 75f);
            File.WriteAllBytes(path, webp);
        }
    }

    /// <summary>
    /// WebP 编码器适配器（RGBA32）
    /// </summary>
    public sealed class WebpEncoderAdapterRgba : IImageEncoderRgba
    {
        /// <summary>
        /// 将 RGBA32 图像编码为 WebP 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(string path, Image<Rgba32> image)
        {
            var webp = WebpCodec.EncodeRgba(image.Buffer, image.Width, image.Height, 75f);
            File.WriteAllBytes(path, webp);
        }
    }
}
