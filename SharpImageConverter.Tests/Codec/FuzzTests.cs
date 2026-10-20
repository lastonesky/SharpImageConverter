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
    /// 变异输入的健壮性：只允许受控失败，禁止解析器内部异常。
    /// </summary>
    public partial class FormatConversionTests
    {
        [Fact]
        public void Fuzz_Mutated_Png_Inputs_FailOnlyWithInvalidData()
        {
            var img = TestImageFactory.CreateGradient(64, 64);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            byte[] data = ms.ToArray();

            AssertMutatedInputsFailOnlyWithInvalidData(data, 1234, mutated =>
            {
                using var s = new MemoryStream(mutated);
                var dec = new PngDecoder();
                _ = dec.DecodeToRGB(s);
            });
        }

        [Fact]
        public void Fuzz_Mutated_Jpeg_Inputs_FailOnlyWithInvalidData()
        {
            var img = TestImageFactory.CreateGradient(64, 64);
            using var ms = new MemoryStream();
            JpegEncoder.Write(ms, img.Width, img.Height, img.Buffer, 85);
            byte[] data = ms.ToArray();

            AssertMutatedInputsFailOnlyWithInvalidData(data, 5678, mutated => _ = JpegDecoder.Decode(mutated));
        }
    }
}
