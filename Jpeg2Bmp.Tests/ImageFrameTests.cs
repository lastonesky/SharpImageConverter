using System;
using System.IO;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class ImageFrameTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        public static void ImageFrame_Save_And_Load_Bmp_Png_Jpeg()
        {
            int w = 4, h = 3;
            var buf = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    buf[o + 0] = (byte)(x * 30 + 10);
                    buf[o + 1] = (byte)(y * 40 + 5);
                    buf[o + 2] = (byte)(x + y);
                }
            }
            var frame = new ImageFrame(w, h, buf);
            string bmp = NewTemp(".bmp");
            string png = NewTemp(".png");
            string jpg = NewTemp(".jpg");
            frame.Save(bmp);
            frame.Save(png);
            frame.SaveAsJpeg(jpg, 90);
            var fBmp = ImageFrame.Load(bmp);
            var fPng = ImageFrame.Load(png);
            var fJpg = ImageFrame.Load(jpg);
            Assert.AreEqual(w, fBmp.Width);
            Assert.AreEqual(h, fBmp.Height);
            Assert.AreEqual(w, fPng.Width);
            Assert.AreEqual(h, fPng.Height);
            File.Delete(bmp);
            File.Delete(png);
            File.Delete(jpg);
        }

        private static (int dx, int dy, int newW, int newH) Map(int x, int y, int w, int h, int orientation)
        {
            int newW = w, newH = h;
            if (orientation is 5 or 6 or 7 or 8) { newW = h; newH = w; }
            int dx, dy;
            switch (orientation)
            {
                case 1: dx = x; dy = y; break;
                case 2: dx = (w - 1 - x); dy = y; break;
                case 3: dx = (w - 1 - x); dy = (h - 1 - y); break;
                case 4: dx = x; dy = (h - 1 - y); break;
                case 5: dx = y; dy = x; break;
                case 6: dx = (h - 1 - y); dy = x; break;
                case 7: dx = (h - 1 - y); dy = (w - 1 - x); break;
                case 8: dx = y; dy = (w - 1 - x); break;
                default: dx = x; dy = y; break;
            }
            return (dx, dy, newW, newH);
        }

        public static void ApplyExifOrientation_All_1_To_8()
        {
            int w = 2, h = 3;
            var buf = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    buf[o + 0] = (byte)(x * 80 + 10);
                    buf[o + 1] = (byte)(y * 60 + 5);
                    buf[o + 2] = (byte)(x + y);
                }
            }
            var src = new ImageFrame(w, h, buf);
            for (int orientation = 1; orientation <= 8; orientation++)
            {
                var dst = src.ApplyExifOrientation(orientation);
                var m = Map(0, 0, w, h, orientation);
                Assert.AreEqual(m.newW, dst.Width);
                Assert.AreEqual(m.newH, dst.Height);
                int s00 = (0 * w + 0) * 3;
                int d00 = (m.dy * m.newW + m.dx) * 3;
                Assert.AreEqual(src.Pixels[s00 + 0], dst.Pixels[d00 + 0]);
                Assert.AreEqual(src.Pixels[s00 + 1], dst.Pixels[d00 + 1]);
                Assert.AreEqual(src.Pixels[s00 + 2], dst.Pixels[d00 + 2]);
                var m10 = Map(1, 0, w, h, orientation);
                int s10 = (0 * w + 1) * 3;
                int d10 = (m10.dy * m10.newW + m10.dx) * 3;
                Assert.AreEqual(src.Pixels[s10 + 0], dst.Pixels[d10 + 0]);
                Assert.AreEqual(src.Pixels[s10 + 1], dst.Pixels[d10 + 1]);
                Assert.AreEqual(src.Pixels[s10 + 2], dst.Pixels[d10 + 2]);
                var m01 = Map(0, 1, w, h, orientation);
                int s01 = (1 * w + 0) * 3;
                int d01 = (m01.dy * m01.newW + m01.dx) * 3;
                Assert.AreEqual(src.Pixels[s01 + 0], dst.Pixels[d01 + 0]);
                Assert.AreEqual(src.Pixels[s01 + 1], dst.Pixels[d01 + 1]);
                Assert.AreEqual(src.Pixels[s01 + 2], dst.Pixels[d01 + 2]);
            }
        }
    }
}
