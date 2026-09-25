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
        public bool EnableDithering { get; set; } = true;

        /// <summary>
        /// 量化/抖动方案；默认八叉树 + Bayer（更快）。设回 WuFloydSteinberg 复用原实现。
        /// </summary>
        public GifQuantizerKind QuantizerKind { get; set; } = GifQuantizerKind.OctreeBayer;

        /// <summary>
        /// Bayer 抖动幅度（默认 8）。调大会放大颗粒与缩放摩尔纹。
        /// </summary>
        public int DitherStrength { get; set; } = 8;

        /// <summary>
        /// 是否采集编码各阶段耗时并输出诊断日志。
        /// </summary>
        public bool EnableDiagnostics { get; set; }

        /// <summary>
        /// 诊断日志输出委托，为 null 时回落到 Trace。
        /// </summary>
        public Action<string>? DiagnosticsLog { get; set; }

        /// <summary>
        /// 最近一次编码的耗时统计，未开启诊断时为 null。
        /// </summary>
        public GifTiming? LastTiming { get; private set; }

        /// <summary>
        /// 将 RGB24 图像编码为 GIF 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(string path, Image<Rgb24> image)
        {
            var encoder = CreateEncoder();
            using var fs = File.Create(path);
            var frame = new ImageFrame(image.Width, image.Height, image.Buffer);
            encoder.Encode(frame, fs);
            LastTiming = encoder.LastTiming;
        }

        /// <summary>
        /// 将 RGB24 图像编码为 GIF 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgb24(Stream stream, Image<Rgb24> image)
        {
            var encoder = CreateEncoder();
            var frame = new ImageFrame(image.Width, image.Height, image.Buffer);
            encoder.Encode(frame, stream);
            LastTiming = encoder.LastTiming;
        }

        private GifEncoder CreateEncoder() => new()
        {
            EnableDithering = EnableDithering,
            QuantizerKind = QuantizerKind,
            DitherStrength = DitherStrength,
            EnableDiagnostics = EnableDiagnostics,
            DiagnosticsLog = DiagnosticsLog,
        };
    }

    /// <summary>
    /// GIF 编码器适配器（RGBA32）
    /// </summary>
    public sealed class GifEncoderAdapterRgba : IImageEncoderRgba
    {
        public bool EnableDithering { get; set; } = true;

        /// <summary>
        /// 量化/抖动方案；默认八叉树 + Bayer（更快）。设回 WuFloydSteinberg 复用原实现。
        /// </summary>
        public GifQuantizerKind QuantizerKind { get; set; } = GifQuantizerKind.OctreeBayer;

        /// <summary>
        /// Bayer 抖动幅度（默认 8）。调大会放大颗粒与缩放摩尔纹。
        /// </summary>
        public int DitherStrength { get; set; } = 8;

        /// <summary>
        /// 是否采集编码各阶段耗时并输出诊断日志。
        /// </summary>
        public bool EnableDiagnostics { get; set; }

        /// <summary>
        /// 诊断日志输出委托，为 null 时回落到 Trace。
        /// </summary>
        public Action<string>? DiagnosticsLog { get; set; }

        /// <summary>
        /// 最近一次编码的耗时统计，未开启诊断时为 null。
        /// </summary>
        public GifTiming? LastTiming { get; private set; }

        /// <summary>
        /// 将 RGBA32 图像编码为 GIF 文件
        /// </summary>
        /// <param name="path">输出路径</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(string path, Image<Rgba32> image)
        {
            var encoder = CreateEncoder();
            using var fs = File.Create(path);
            encoder.EncodeRgba(image.Width, image.Height, image.Buffer, fs);
            LastTiming = encoder.LastTiming;
        }

        /// <summary>
        /// 将 RGBA32 图像编码为 GIF 流
        /// </summary>
        /// <param name="stream">输出流</param>
        /// <param name="image">输入图像</param>
        public void EncodeRgba32(Stream stream, Image<Rgba32> image)
        {
            var encoder = CreateEncoder();
            encoder.EncodeRgba(image.Width, image.Height, image.Buffer, stream);
            LastTiming = encoder.LastTiming;
        }

        private GifEncoder CreateEncoder() => new()
        {
            EnableDithering = EnableDithering,
            QuantizerKind = QuantizerKind,
            DitherStrength = DitherStrength,
            EnableDiagnostics = EnableDiagnostics,
            DiagnosticsLog = DiagnosticsLog,
        };
    }

    /// <summary>
    /// GIF 解码器适配器（RGBA32）
    /// </summary>
    public sealed class GifDecoderRgbaAdapter : IImageDecoderRgba
    {
        /// <summary>
        /// 是否采集解码各阶段耗时并输出诊断日志。
        /// </summary>
        public bool EnableDiagnostics { get; set; }

        /// <summary>
        /// 诊断日志输出委托，为 null 时回落到 Trace。
        /// </summary>
        public Action<string>? DiagnosticsLog { get; set; }

        /// <summary>
        /// 最近一次解码的耗时统计，未开启诊断时为 null。
        /// </summary>
        public GifTiming? LastTiming { get; private set; }

        /// <summary>
        /// 解码 GIF 文件为 RGBA32 图像
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(string path)
        {
            var dec = CreateDecoder();
            var image = dec.DecodeRgba32(path);
            LastTiming = dec.LastTiming;
            return image;
        }

        /// <summary>
        /// 解码 GIF 流为 RGBA32 图像
        /// </summary>
        /// <param name="stream">输入流</param>
        /// <returns>RGBA32 图像</returns>
        public Image<Rgba32> DecodeRgba32(Stream stream)
        {
            var dec = CreateDecoder();
            var image = dec.DecodeRgba32(stream);
            LastTiming = dec.LastTiming;
            return image;
        }

        private GifDecoder CreateDecoder() => new()
        {
            EnableDiagnostics = EnableDiagnostics,
            DiagnosticsLog = DiagnosticsLog,
        };
    }
}
