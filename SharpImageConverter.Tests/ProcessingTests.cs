using Xunit;
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class ProcessingTests
    {
        [Fact]
        public void Resize_2x2_To_1x1_Downscale_Uses_AreaAverage()
        {
            var img = TestImageFactory.CreateChecker(2, 2, (100, 110, 120), (200, 210, 220));
            ImageExtensions.Mutate(img, ctx => ctx.Resize(1, 1));
            Assert.Equal(1, img.Width);
            Assert.Equal(1, img.Height);
            int expectedR = (100 + 200 + 200 + 100) / 4;
            int expectedG = (110 + 210 + 210 + 110) / 4;
            int expectedB = (120 + 220 + 220 + 120) / 4;
            Assert.Equal(expectedR, img.Buffer[0]);
            Assert.Equal(expectedG, img.Buffer[1]);
            Assert.Equal(expectedB, img.Buffer[2]);
        }

        [Fact]
        public void Resize_3x3_To_6x6_Gradient_Is_Monotonic()
        {
            int sw = 3, sh = 3;
            var img = TestImageFactory.CreateGradient(sw, sh);
            ImageExtensions.Mutate(img, ctx => ctx.Resize(6, 6));
            Assert.Equal(6, img.Width);
            Assert.Equal(6, img.Height);
            int width = img.Width, height = img.Height;
            var dst = img.Buffer;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width * 3;
                for (int x = 1; x < width; x++)
                {
                    int prev = rowOffset + (x - 1) * 3;
                    int curr = rowOffset + x * 3;
                    Assert.True(dst[prev + 0] <= dst[curr + 0]);
                }
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 1; y < height; y++)
                {
                    int prev = ((y - 1) * width + x) * 3;
                    int curr = (y * width + x) * 3;
                    Assert.True(dst[prev + 1] <= dst[curr + 1]);
                }
            }
        }

        [Fact]
        public void ResizeBilinear_2x2_To_3x3_Center_Is_Average()
        {
            var img = TestImageFactory.CreateChecker(2, 2, (0, 0, 0), (255, 255, 255));
            ImageExtensions.Mutate(img, ctx => ctx.ResizeBilinear(3, 3));
            Assert.Equal(3, img.Width);
            Assert.Equal(3, img.Height);
            int d = (1 * img.Width + 1) * 3;
            Assert.Equal(128, img.Buffer[d + 0]);
            Assert.Equal(128, img.Buffer[d + 1]);
            Assert.Equal(128, img.Buffer[d + 2]);
        }

        [Fact]
        public void Grayscale_Formula_Matches()
        {
            var img = TestImageFactory.CreateSolid(1, 1, 10, 200, 50);
            ImageExtensions.Mutate(img, ctx => ctx.Grayscale());
            int y = (77 * 10 + 150 * 200 + 29 * 50) >> 8;
            Assert.Equal((byte)y, img.Buffer[0]);
            Assert.Equal((byte)y, img.Buffer[1]);
            Assert.Equal((byte)y, img.Buffer[2]);
        }

        [Fact]
        public void ResizeToFit_PreservesAspectRatio()
        {
            var wide = TestImageFactory.CreateSolid(400, 200, 1, 2, 3);
            ImageExtensions.Mutate(wide, ctx => ctx.ResizeToFit(320, 240));
            Assert.Equal(320, wide.Width);
            Assert.Equal(160, wide.Height);

            var tall = TestImageFactory.CreateSolid(200, 400, 1, 2, 3);
            ImageExtensions.Mutate(tall, ctx => ctx.ResizeToFit(320, 240));
            Assert.Equal(120, tall.Width);
            Assert.Equal(240, tall.Height);
        }
    }
}
