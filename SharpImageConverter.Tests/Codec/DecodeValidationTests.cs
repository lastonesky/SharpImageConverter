using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Metadata;
using SharpImageConverter.Tests.Helpers;
using Xunit;
using SharpImageConverter.Formats;


namespace SharpImageConverter.Tests
{
    /// <summary>
    /// 解码/编码的输入校验：畸形 marker 与不可写流必须给出明确错误。
    /// </summary>
    public partial class FormatConversionTests
    {
        [Fact]
        public void Jpeg_Write_Rejects_NonWritable_Stream()
        {
            var img = TestImageFactory.CreateGradient(8, 8);
            using var stream = new MemoryStream(Array.Empty<byte>(), writable: false);

            var ex = Assert.Throws<ArgumentException>(() => JpegEncoder.Write(stream, img.Width, img.Height, img.Buffer, 85));
            Assert.Contains("不可写", ex.Message);
        }

        [Fact]
        public void Jpeg_Decode_Rejects_MultipleSofMarkers()
        {
            byte[] data =
            [
                0xFF, 0xD8,
                0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00,
                0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00,
                0xFF, 0xD9
            ];

            var ex = Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(data));
            Assert.Contains("Multiple SOF", ex.Message);
        }

        [Fact]
        public void Jpeg_Decode_Rejects_OversizeDimensions()
        {
            byte[] data =
            [
                0xFF, 0xD8,
                0xFF, 0xC0, 0x00, 0x0B, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x44, 0x00,
                0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
                0xFF, 0xD9
            ];

            // 65535×65535 的 SOF 必须在分配系数/输出缓冲之前就被拒，否则会先申请数 GB 再溢出。
            var ex = Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(data));
            Assert.Contains("超出单幅解码上限", ex.Message);
        }

        [Fact]
        public void Png_Decode_Rejects_TruncatedIhdr()
        {
            // IHDR 声明的长度为 5（< 13）。修复前会读 data[12] 抛 IndexOutOfRangeException。
            byte[] data =
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x05,
                (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                0x00, 0x00, 0x00, 0x02, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                (byte)'I', (byte)'E', (byte)'N', (byte)'D',
                0x00, 0x00, 0x00, 0x00
            ];

            using var s = new MemoryStream(data);
            var ex = Assert.Throws<InvalidDataException>(() => new PngDecoder().DecodeToRGB(s));
            Assert.Contains("IHDR", ex.Message);
        }

        [Fact]
        public void Png_Decode_Rejects_OversizeDimensions()
        {
            // 60000×60000 调色板 PNG：像素数超上限，必须在 Rent 大缓冲之前拒绝。
            byte[] data =
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D,
                (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                0x00, 0x00, 0xEA, 0x60, 0x00, 0x00, 0xEA, 0x60,
                0x08, 0x03, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x03,
                (byte)'P', (byte)'L', (byte)'T', (byte)'E',
                0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x02,
                (byte)'I', (byte)'D', (byte)'A', (byte)'T',
                0x78, 0x9C,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                (byte)'I', (byte)'E', (byte)'N', (byte)'D',
                0x00, 0x00, 0x00, 0x00
            ];

            using var s = new MemoryStream(data);
            var ex = Assert.Throws<InvalidDataException>(() => new PngDecoder().DecodeToRGB(s));
            Assert.Contains("超出单幅解码上限", ex.Message);
        }

        [Fact]
        public void Gif_Decode_Rejects_OversizeCanvas()
        {
            // 60000×60000：修复前 width*height 溢出成负数，抛 ArgumentOutOfRangeException。
            byte[] data =
            [
                (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
                0x60, 0xEA, 0x60, 0xEA,
                0x00, 0x00, 0x00
            ];

            using var s = new MemoryStream(data);
            var ex = Assert.Throws<InvalidDataException>(() => new GifDecoder().DecodeRgb24(s));
            Assert.Contains("超出单幅解码上限", ex.Message);
        }

        [Fact]
        public void Gif_Decode_Rejects_OversizeFrameRect()
        {
            // 画布 2×2，但帧矩形声明 60000×60000：帧索引缓冲同样会 int 溢出。
            byte[] data =
            [
                (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
                0x02, 0x00, 0x02, 0x00,
                0x00, 0x00, 0x00,
                0x2C,
                0x00, 0x00, 0x00, 0x00,
                0x60, 0xEA, 0x60, 0xEA,
                0x00,
                0x02,
                0x02, 0x00, 0x00,
                0x00,
                0x3B
            ];

            using var s = new MemoryStream(data);
            var ex = Assert.Throws<InvalidDataException>(() => new GifDecoder().DecodeRgb24(s));
            Assert.Contains("超出单幅解码上限", ex.Message);
        }

        [Fact]
        public void Gif_Decode_Rejects_InvalidLzwMinCodeSize()
        {
            // LZW 最小码长 12 非法：修复前会按 12 位码取到越过 4096 项码表的下标。
            byte[] data =
            [
                (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
                0x02, 0x00, 0x02, 0x00,
                0x80, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x2C,
                0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x00,
                0x0C,
                0x02, 0x00, 0x00,
                0x00,
                0x3B
            ];

            using var s = new MemoryStream(data);
            var ex = Assert.Throws<InvalidDataException>(() => new GifDecoder().DecodeRgb24(s));
            Assert.Contains("最小码长", ex.Message);
        }

        [Fact]
        public void Gif_Encoder_Rejects_MismatchedRgbaBuffer()
        {
            using var ms = new MemoryStream();
            var ex = Assert.Throws<ArgumentException>(() => new GifEncoder().EncodeRgba(2, 2, new byte[10], ms));
            Assert.Contains("不匹配", ex.Message);
        }

        [Fact]
        public void Gif_Encoder_Rejects_OversizeDimensions()
        {
            using var ms = new MemoryStream();
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new GifEncoder().EncodeRgba(70000, 1, new byte[70000 * 4], ms));
        }

        [Fact]
        public void Save_UnsupportedExtension_DoesNotCreateFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sic-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "out.tiff");
                var frame = new ImageFrame(2, 2, new byte[2 * 2 * 3]);

                Assert.Throws<NotSupportedException>(() => Configuration.Default.Save(frame, path));

                // 修复前会先 new FileStream 再校验扩展名，留下一个 0 字节文件。
                Assert.Empty(Directory.GetFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Save_EncoderFailure_LeavesNoOutputOrTempFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sic-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var config = new Configuration();
                config.RegisterFormat(new PngFormat());
                config.RegisterEncoder<PngFormat>(new ThrowingEncoder());
                string path = Path.Combine(dir, "out.png");
                var frame = new ImageFrame(2, 2, new byte[2 * 2 * 3]);

                Assert.Throws<InvalidOperationException>(() => config.Save(frame, path));

                // 编码中途失败不能留下半截目标文件，也不能留下 .tmp 临时文件。
                Assert.Empty(Directory.GetFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private sealed class ThrowingEncoder : IImageEncoder
        {
            public void EncodeRgb24(string path, Image<Rgb24> image)
                => throw new InvalidOperationException("测试用编码器：立即失败");

            public void EncodeRgb24(Stream stream, Image<Rgb24> image)
                => throw new InvalidOperationException("测试用编码器：立即失败");
        }
    }
}
