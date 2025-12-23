using System;
using System.IO;
using Core;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class FormatConversionTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        public static void Bmp_Roundtrip_Exact()
        {
            var img = TestImageFactory.CreateChecker(2, 2, (255, 0, 0), (0, 255, 0));
            string path = NewTemp(".bmp");
            Image.Save(img, path);
            var loaded = Image.Load(path);
            Assert.AreEqual(img.Width, loaded.Width);
            Assert.AreEqual(img.Height, loaded.Height);
            BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
            File.Delete(path);
        }

        public static void Png_Roundtrip_Exact()
        {
            var img = TestImageFactory.CreateChecker(4, 4, (10, 20, 30), (200, 210, 220));
            string path = NewTemp(".png");
            Image.Save(img, path);
            var loaded = Image.Load(path);
            Assert.AreEqual(img.Width, loaded.Width);
            Assert.AreEqual(img.Height, loaded.Height);
            BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
            File.Delete(path);
        }

        public static void Jpeg_Roundtrip_WithTolerance()
        {
            var img = TestImageFactory.CreateGradient(8, 8);
            string path = NewTemp(".jpg");
            var frame = new ImageFrame(img.Width, img.Height, img.Buffer);
            frame.SaveAsJpeg(path, 90);
            var loaded = Image.Load(path);
            Assert.AreEqual(img.Width, loaded.Width);
            Assert.AreEqual(img.Height, loaded.Height);
            BufferAssert.AssertMseLessThan(img.Buffer, loaded.Buffer, 20.0);
            File.Delete(path);
        }
    }
}
