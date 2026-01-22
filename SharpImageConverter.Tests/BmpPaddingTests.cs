using Xunit;
using System;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class BmpPaddingTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        [Fact]
        public void Bmp_RowPadding_Roundtrip_Exact()
        {
            for (int w = 1; w <= 5; w++)
            {
                int h = 3;
                var img = TestImageFactory.CreateChecker(w, h, (10, 20, 30), (200, 210, 220));
                string path = NewTemp(".bmp");
                Image.Save(img, path);
                var loaded = Image.Load(path);
                Assert.Equal(w, loaded.Width);
                Assert.Equal(h, loaded.Height);
                BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
                File.Delete(path);
            }
        }

        [Fact]
        public void Bmp_8bit_Grayscale_CanBeReadAsRgb24()
        {
            int w = 4, h = 3;
            var gray = new byte[w * h];
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(i * 17);
            }
            string path = NewTemp(".bmp");
            BmpWriter.Write8(path, w, h, gray);
            var loaded = Image.Load(path);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
            for (int i = 0; i < gray.Length; i++)
            {
                int o = i * 3;
                Assert.Equal(gray[i], loaded.Buffer[o + 0]);
                Assert.Equal(gray[i], loaded.Buffer[o + 1]);
                Assert.Equal(gray[i], loaded.Buffer[o + 2]);
            }
            File.Delete(path);
        }
    }
}
