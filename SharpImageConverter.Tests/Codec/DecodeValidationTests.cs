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
        public void Jpeg_Decode_Rejects_ComponentBufferOverflow()
        {
            byte[] data =
            [
                0xFF, 0xD8,
                0xFF, 0xC0, 0x00, 0x0B, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x44, 0x00,
                0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
                0xFF, 0xD9
            ];

            var ex = Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(data));
            Assert.Contains("buffer too large", ex.Message);
        }
    }
}
