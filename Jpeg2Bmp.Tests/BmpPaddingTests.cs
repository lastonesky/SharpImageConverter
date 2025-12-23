using System;
using System.IO;
using Core;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class BmpPaddingTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        public static void Bmp_RowPadding_Roundtrip_Exact()
        {
            for (int w = 1; w <= 5; w++)
            {
                int h = 3;
                var img = TestImageFactory.CreateChecker(w, h, (10, 20, 30), (200, 210, 220));
                string path = NewTemp(".bmp");
                Image.Save(img, path);
                var loaded = Image.Load(path);
                Assert.AreEqual(w, loaded.Width);
                Assert.AreEqual(h, loaded.Height);
                BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
                File.Delete(path);
            }
        }
    }
}
