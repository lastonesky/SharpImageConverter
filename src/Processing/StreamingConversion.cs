using System;
using System.IO;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Processing
{
    /// <summary>
    /// 低内存逐行转换：源格式解码与 BMP/PNG 编码都不需要整幅像素。
    /// </summary>
    /// <remarks>
    /// 只覆盖“两端都能逐行处理”的组合：
    /// BMP / PNG / GIF（首帧）/ WebP / JPEG → BMP / PNG / GIF（单帧）/ JPEG。
    /// JPEG 解码按 MCU 行 IDCT，编码按 MCU 行读入并立即熵编码；目标 WebP 仍需要整幅输入，
    /// GIF 编码需要整帧建调色板，这里只做到单帧缓冲（不是逐行）。
    /// </remarks>
    internal static class StreamingConversion
    {
        internal static bool IsSupported(ImageFormatKind source, ImageFormatKind target, string inputPath)
        {
            if (target is not (ImageFormatKind.Bmp or ImageFormatKind.Png
                or ImageFormatKind.Gif or ImageFormatKind.Jpeg)) return false;
            return source switch
            {
                ImageFormatKind.Bmp => BmpRowSource.IsSupported(inputPath),
                ImageFormatKind.Png => PngRowSource.IsSupported(inputPath),
                ImageFormatKind.Gif => GifRowSource.IsSupported(inputPath),
                ImageFormatKind.Webp => WebpRowSource.IsSupported(inputPath),
                ImageFormatKind.Jpeg => JpegRowSource.IsSupported(inputPath),
                _ => false,
            };
        }

        internal static void Convert(
            string inputPath,
            string outputPath,
            ImageFormatKind target,
            bool gray,
            int jpegQuality = 75,
            JpegChromaSubsampling jpegSubsampling = JpegChromaSubsampling.Yuv420,
            bool jpegProgressive = false,
            bool useFloatingPointIdct = false)
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using IRowImageSource source = CreateSource(inputPath, useFloatingPointIdct);
            if (target == ImageFormatKind.Jpeg)
            {
                using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                {
                    JpegEncoder.EncodeStreaming(output, source, jpegQuality, jpegSubsampling, keepMetadata: false, metadata: null);
                }
                if (jpegProgressive)
                {
                    byte[] encoded = File.ReadAllBytes(outputPath);
                    byte[]? progressive = JpegProgressiveTranscoder.TryTranscode(encoded);
                    if (progressive != null && progressive.Length < encoded.Length)
                    {
                        File.WriteAllBytes(outputPath, progressive);
                    }
                }
                return;
            }

            using IRowImageSink sink = CreateSink(outputPath, target, source, gray);
            var row = new byte[source.Width * 4];
            int rows = 0;
            while (source.ReadRow(row))
            {
                sink.WriteRow(row, source.Channels);
                rows++;
            }
            if (rows != source.Height)
            {
                throw new InvalidDataException($"逐行解码只得到 {rows}/{source.Height} 行");
            }
            sink.Complete();
        }

        private static IRowImageSource CreateSource(string inputPath, bool useFloatingPointIdct = false)
        {
            var kind = ImageOptimizer.DetectFormat(inputPath);
            return kind switch
            {
                ImageFormatKind.Bmp => new BmpRowSource(inputPath),
                ImageFormatKind.Png => PngRowSource.Open(inputPath),
                ImageFormatKind.Gif => new GifRowSource(inputPath),
                ImageFormatKind.Webp => new WebpRowSource(inputPath),
                ImageFormatKind.Jpeg => JpegRowSource.Open(inputPath, useFloatingPointIdct),
                _ => throw new NotSupportedException($"格式 {kind} 不支持逐行解码"),
            };
        }

        private static IRowImageSink CreateSink(
            string outputPath,
            ImageFormatKind target,
            IRowImageSource source,
            bool gray)
            => target switch
            {
                ImageFormatKind.Bmp => new BmpRowSink(outputPath, source.Width, source.Height, gray),
                ImageFormatKind.Png => new PngRowSink(
                    outputPath, source.Width, source.Height, source.Channels, source.HasAlpha, gray),
                ImageFormatKind.Gif => new GifRowSink(
                    outputPath, source.Width, source.Height, source.Channels, gray),
                _ => throw new NotSupportedException($"目标格式 {target} 不支持逐行编码"),
            };
    }
}
