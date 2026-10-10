using Xunit;
using System;
using System.IO;
using SharpImageConverter;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// 验证 ImageFrame 的 BGR24 / BGRA32 兼容转换：构造、To* 转换、就地交换，以及
    /// 非 RGB24 帧在 Save 时被正确规范化为 RGB24（颜色不串色）。
    /// </summary>
    public class BgrConversionTests
    {
        private static byte[] MakeRgb24(int pixels, int seed)
        {
            var data = new byte[pixels * 3];
            uint x = (uint)seed;
            for (int i = 0; i < data.Length; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                data[i] = (byte)(x >> 24);
            }
            return data;
        }

        private static byte[] ToBgr(byte[] rgb)
        {
            var bgr = (byte[])rgb.Clone();
            for (int p = 0; p + 2 < bgr.Length; p += 3)
            {
                byte t = bgr[p];
                bgr[p] = bgr[p + 2];
                bgr[p + 2] = t;
            }
            return bgr;
        }

        [Fact]
        public void Bgr24_Frame_ToRgb24_SwapsChannels()
        {
            int w = 5, h = 7;
            var rgb = MakeRgb24(w * h, 123);
            var frame = new ImageFrame(w, h, ToBgr(rgb), ImagePixelFormat.Bgr24);
            Assert.Equal(ImagePixelFormat.Bgr24, frame.PixelFormat);

            var converted = frame.ToRgb24();
            Assert.Equal(ImagePixelFormat.Rgb24, converted.PixelFormat);
            Assert.Equal(rgb, converted.Pixels);
        }

        [Fact]
        public void SwapRgbBgrInPlace_TogglesFormatAndSwaps()
        {
            int w = 4, h = 4;
            var rgb = MakeRgb24(w * h, 7);
            var frame = new ImageFrame(w, h, rgb);
            var expectedBgr = ToBgr(rgb);

            frame.SwapRgbBgrInPlace();
            Assert.Equal(ImagePixelFormat.Bgr24, frame.PixelFormat);
            Assert.Equal(expectedBgr, frame.Pixels);

            // 再换一次回到 RGB24，数值还原
            frame.SwapRgbBgrInPlace();
            Assert.Equal(ImagePixelFormat.Rgb24, frame.PixelFormat);
            Assert.Equal(rgb, frame.Pixels);
        }

        [Fact]
        public void Bgra32_Frame_ToRgb24_DropsAlphaAndSwaps()
        {
            int w = 6, h = 3;
            int pixels = w * h;
            var bgra = new byte[pixels * 4];
            var rgbExpected = new byte[pixels * 3];
            uint x = 99;
            for (int p = 0; p < pixels; p++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte b = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte g = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte r = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte a = (byte)(x >> 24);
                int si = p * 4, di = p * 3;
                bgra[si] = b; bgra[si + 1] = g; bgra[si + 2] = r; bgra[si + 3] = a;
                rgbExpected[di] = r; rgbExpected[di + 1] = g; rgbExpected[di + 2] = b;
            }

            var frame = new ImageFrame(w, h, bgra, ImagePixelFormat.Bgra32);
            var converted = frame.ToRgb24();
            Assert.Equal(ImagePixelFormat.Rgb24, converted.PixelFormat);
            Assert.Equal(rgbExpected, converted.Pixels);
        }

        [Fact]
        public void Rgb24_ToBgra32_RoundTripColor()
        {
            int w = 8, h = 5;
            var rgb = MakeRgb24(w * h, 555);
            var bgra = new ImageFrame(w, h, rgb).ToBgra32();
            Assert.Equal(ImagePixelFormat.Bgra32, bgra.PixelFormat);

            var back = bgra.ToRgb24();
            Assert.Equal(rgb, back.Pixels);
        }

        [Fact]
        public void Bgr24_Frame_Save_RoundTrips()
        {
            int w = 4, h = 4;
            var rgb = MakeRgb24(w * h, 321);
            var frame = new ImageFrame(w, h, ToBgr(rgb), ImagePixelFormat.Bgr24);

            string png = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                frame.Save(png);
                var reloaded = ImageFrame.Load(png);
                Assert.Equal(rgb, reloaded.Pixels);
            }
            finally
            {
                if (File.Exists(png)) File.Delete(png);
            }
        }

        [Fact]
        public void Bgra32_Frame_Save_RoundTrips()
        {
            int w = 4, h = 4;
            int pixels = w * h;
            var bgra = new byte[pixels * 4];
            var rgb = new byte[pixels * 3];
            uint x = 71;
            for (int p = 0; p < pixels; p++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte b = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte g = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte r = (byte)(x >> 24);
                int si = p * 4, di = p * 3;
                bgra[si] = b; bgra[si + 1] = g; bgra[si + 2] = r; bgra[si + 3] = 200;
                rgb[di] = r; rgb[di + 1] = g; rgb[di + 2] = b;
            }

            var frame = new ImageFrame(w, h, bgra, ImagePixelFormat.Bgra32);
            string png = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                frame.Save(png);
                var reloaded = ImageFrame.Load(png);
                Assert.Equal(rgb, reloaded.Pixels);
            }
            finally
            {
                if (File.Exists(png)) File.Delete(png);
            }
        }

        [Fact]
        public void Rgba32_Frame_ToRgb24_DropsAlphaKeepsOrder()
        {
            int w = 6, h = 3;
            int pixels = w * h;
            var rgba = new byte[pixels * 4];
            var rgbExpected = new byte[pixels * 3];
            uint x = 131;
            for (int p = 0; p < pixels; p++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte r = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte g = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte b = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte a = (byte)(x >> 24);
                int si = p * 4, di = p * 3;
                rgba[si] = r; rgba[si + 1] = g; rgba[si + 2] = b; rgba[si + 3] = a;
                rgbExpected[di] = r; rgbExpected[di + 1] = g; rgbExpected[di + 2] = b;
            }

            var frame = new ImageFrame(w, h, rgba, ImagePixelFormat.Rgba32);
            Assert.Equal(ImagePixelFormat.Rgba32, frame.PixelFormat);
            var converted = frame.ToRgb24();
            Assert.Equal(ImagePixelFormat.Rgb24, converted.PixelFormat);
            Assert.Equal(rgbExpected, converted.Pixels);
        }

        [Fact]
        public void Rgb24_ToRgba32_AddsOpaqueAlpha_AndRoundTrips()
        {
            int w = 8, h = 5;
            int pixels = w * h;
            var rgb = MakeRgb24(pixels, 777);
            var rgba = new ImageFrame(w, h, rgb).ToRgba32();
            Assert.Equal(ImagePixelFormat.Rgba32, rgba.PixelFormat);
            Assert.Equal(pixels * 4, rgba.Pixels.Length);

            for (int p = 0; p < pixels; p++)
            {
                Assert.Equal(rgb[p * 3 + 0], rgba.Pixels[p * 4 + 0]);
                Assert.Equal(rgb[p * 3 + 1], rgba.Pixels[p * 4 + 1]);
                Assert.Equal(rgb[p * 3 + 2], rgba.Pixels[p * 4 + 2]);
                Assert.Equal((byte)255, rgba.Pixels[p * 4 + 3]);
            }

            Assert.Equal(rgb, rgba.ToRgb24().Pixels);
        }

        [Fact]
        public void SwapRgbBgrInPlace_Toggles32BitFormat_AndPreservesAlpha()
        {
            int w = 4, h = 3;
            int pixels = w * h;
            var bgra = new byte[pixels * 4];
            var expectedRgba = new byte[pixels * 4];
            uint x = 21;
            for (int p = 0; p < pixels; p++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte b = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte g = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte r = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte a = (byte)(x >> 24);
                int si = p * 4;
                bgra[si] = b; bgra[si + 1] = g; bgra[si + 2] = r; bgra[si + 3] = a;
                expectedRgba[si] = r; expectedRgba[si + 1] = g; expectedRgba[si + 2] = b; expectedRgba[si + 3] = a;
            }

            var frame = new ImageFrame(w, h, bgra, ImagePixelFormat.Bgra32);
            frame.SwapRgbBgrInPlace();
            Assert.Equal(ImagePixelFormat.Rgba32, frame.PixelFormat);
            Assert.Equal(expectedRgba, frame.Pixels);

            // 再换一次回到 BGRA32，数值还原
            frame.SwapRgbBgrInPlace();
            Assert.Equal(ImagePixelFormat.Bgra32, frame.PixelFormat);
            Assert.Equal(bgra, frame.Pixels);
        }

        [Fact]
        public void Bgra32_ToRgba32_IsNonDestructive_AndRoundTrips()
        {
            int w = 5, h = 4;
            int pixels = w * h;
            var bgra = new byte[pixels * 4];
            uint x = 202;
            for (int i = 0; i < bgra.Length; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                bgra[i] = (byte)(x >> 24);
            }

            var frame = new ImageFrame(w, h, bgra, ImagePixelFormat.Bgra32);
            var rgba = frame.ToRgba32();
            Assert.Equal(ImagePixelFormat.Rgba32, rgba.PixelFormat);
            Assert.Equal(ImagePixelFormat.Bgra32, frame.PixelFormat);   // 原帧不被修改
            Assert.Equal(bgra, frame.Pixels);

            var back = rgba.ToBgra32();
            Assert.Equal(ImagePixelFormat.Bgra32, back.PixelFormat);
            Assert.Equal(bgra, back.Pixels);
        }

        [Fact]
        public void Rgba32_Bgr24_CrossConversions()
        {
            int w = 5, h = 4;
            int pixels = w * h;
            var rgba = new byte[pixels * 4];
            var bgrExpected = new byte[pixels * 3];
            uint x = 45;
            for (int p = 0; p < pixels; p++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte r = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte g = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte b = (byte)(x >> 24);
                int si = p * 4, di = p * 3;
                rgba[si] = r; rgba[si + 1] = g; rgba[si + 2] = b; rgba[si + 3] = 77;
                bgrExpected[di] = b; bgrExpected[di + 1] = g; bgrExpected[di + 2] = r;
            }

            var frame = new ImageFrame(w, h, rgba, ImagePixelFormat.Rgba32);
            var bgr = frame.ToBgr24();
            Assert.Equal(ImagePixelFormat.Bgr24, bgr.PixelFormat);
            Assert.Equal(bgrExpected, bgr.Pixels);

            // BGR24 -> RGBA32：交换回 R/B 并补 alpha=255
            var back = bgr.ToRgba32();
            Assert.Equal(ImagePixelFormat.Rgba32, back.PixelFormat);
            for (int p = 0; p < pixels; p++)
            {
                Assert.Equal(rgba[p * 4 + 0], back.Pixels[p * 4 + 0]);
                Assert.Equal(rgba[p * 4 + 1], back.Pixels[p * 4 + 1]);
                Assert.Equal(rgba[p * 4 + 2], back.Pixels[p * 4 + 2]);
                Assert.Equal((byte)255, back.Pixels[p * 4 + 3]);
            }
        }

        [Fact]
        public void Rgba32_Frame_Save_RoundTrips()
        {
            int w = 4, h = 4;
            int pixels = w * h;
            var rgba = new byte[pixels * 4];
            var rgb = new byte[pixels * 3];
            uint x = 93;
            for (int p = 0; p < pixels; p++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte r = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte g = (byte)(x >> 24);
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                byte b = (byte)(x >> 24);
                int si = p * 4, di = p * 3;
                rgba[si] = r; rgba[si + 1] = g; rgba[si + 2] = b; rgba[si + 3] = 128;
                rgb[di] = r; rgb[di + 1] = g; rgb[di + 2] = b;
            }

            var frame = new ImageFrame(w, h, rgba, ImagePixelFormat.Rgba32);
            string png = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                frame.Save(png);
                var reloaded = ImageFrame.Load(png);
                Assert.Equal(rgb, reloaded.Pixels);
            }
            finally
            {
                if (File.Exists(png)) File.Delete(png);
            }
        }

        [Fact]
        public void Invalid_BufferLength_ForFormat_Throws()
        {
            // Bgr24 需要 width*height*3；故意给 4 字节/像素长度应报错
            var bad = new byte[4 * 4 * 4];
            Assert.Throws<ArgumentException>(() => new ImageFrame(4, 4, bad, ImagePixelFormat.Bgr24));

            // Rgba32 需要 width*height*4；故意给 3 字节/像素长度应报错
            var bad32 = new byte[4 * 4 * 3];
            Assert.Throws<ArgumentException>(() => new ImageFrame(4, 4, bad32, ImagePixelFormat.Rgba32));
        }
    }
}
