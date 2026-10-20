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
    /// examples/ 真实语料上的解码稳定性与格式判定。
    /// </summary>
    public partial class FormatConversionTests
    {
        [Fact]
        public void Corpus_Examples_Jpeg_Decode_Is_Stable()
        {
            string[] files =
            [
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "video-001.cmyk.jpeg"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "video-001.rgb.jpeg")
            ];

            foreach (string path in files)
            {
                byte[] data = File.ReadAllBytes(path);
                var img1 = JpegDecoder.Decode(data);
                using var ms = new MemoryStream(data);
                var img2 = JpegDecoder.Decode(ms);
                Assert.Equal(img1.Width, img2.Width);
                Assert.Equal(img1.Height, img2.Height);
                Assert.Equal(Hash(img1.Buffer), Hash(img2.Buffer));
            }

            // CMYK 样本没有 Adobe APP14：解码器必须自行判定反相。
            // 补上"解出来不是纯色"的判据 —— 只看「不抛异常」会漏掉反相判错。
            var cmyk = JpegDecoder.Decode(File.ReadAllBytes(files[0]));
            byte[] cmykRgb = cmyk.ToRgb24();
            Assert.Equal(150, cmyk.Width);
            Assert.Equal(103, cmyk.Height);
            Assert.True(cmykRgb.Max() - cmykRgb.Min() > 16, "CMYK 解码结果接近纯色，反相判定可能出错");
        }
    }
}
