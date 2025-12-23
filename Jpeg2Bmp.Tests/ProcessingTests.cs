using Core;
using Processing;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class ProcessingTests
    {
        public static void Resize_2x2_To_1x1_Picks_TopLeft()
        {
            var img = TestImageFactory.CreateChecker(2, 2, (100, 110, 120), (200, 210, 220));
            ImageExtensions.Mutate(img, ctx => ctx.Resize(1, 1));
            Assert.AreEqual(1, img.Width);
            Assert.AreEqual(1, img.Height);
            Assert.AreEqual(100, img.Buffer[0]);
            Assert.AreEqual(110, img.Buffer[1]);
            Assert.AreEqual(120, img.Buffer[2]);
        }

        public static void Resize_3x3_To_6x6_NearestNeighborMapping()
        {
            int sw = 3, sh = 3;
            var img = TestImageFactory.CreateGradient(sw, sh);
            ImageExtensions.Mutate(img, ctx => ctx.Resize(6, 6));
            Assert.AreEqual(6, img.Width);
            Assert.AreEqual(6, img.Height);
            int width = img.Width, height = img.Height;
            var dst = img.Buffer;
            var src = TestImageFactory.CreateGradient(sw, sh).Buffer;
            int[] xs = new[] { 0, 1, 5 };
            int[] ys = new[] { 0, 2, 5 };
            foreach (var y in ys)
            {
                foreach (var x in xs)
                {
                    int sy = (int)((long)y * sh / height);
                    int sx = (int)((long)x * sw / width);
                    int s = (sy * sw + sx) * 3;
                    int d = (y * width + x) * 3;
                    Assert.AreEqual(src[s + 0], dst[d + 0]);
                    Assert.AreEqual(src[s + 1], dst[d + 1]);
                    Assert.AreEqual(src[s + 2], dst[d + 2]);
                }
            }
        }

        public static void Grayscale_Formula_Matches()
        {
            var img = TestImageFactory.CreateSolid(1, 1, 10, 200, 50);
            ImageExtensions.Mutate(img, ctx => ctx.Grayscale());
            int y = (77 * 10 + 150 * 200 + 29 * 50) >> 8;
            Assert.AreEqual((byte)y, img.Buffer[0]);
            Assert.AreEqual((byte)y, img.Buffer[1]);
            Assert.AreEqual((byte)y, img.Buffer[2]);
        }
    }
}
