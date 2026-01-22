using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using Tests.Helpers;
using Xunit;
using SharpImageConverter.Formats;

namespace Jpeg2Bmp.Tests
{
    public class FormatConversionTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        [Fact]
        public void Bmp_Roundtrip_Exact()
        {
            var img = TestImageFactory.CreateChecker(2, 2, (255, 0, 0), (0, 255, 0));
            string path = NewTemp(".bmp");
            Image.Save(img, path);
            var loaded = Image.Load(path);
            Assert.Equal(img.Width, loaded.Width);
            Assert.Equal(img.Height, loaded.Height);
            BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
            File.Delete(path);
        }

        [Fact]
        public void Png_Roundtrip_Exact()
        {
            var img = TestImageFactory.CreateChecker(4, 4, (10, 20, 30), (200, 210, 220));
            string path = NewTemp(".png");
            Image.Save(img, path);
            var loaded = Image.Load(path);
            Assert.Equal(img.Width, loaded.Width);
            Assert.Equal(img.Height, loaded.Height);
            BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
            File.Delete(path);
        }

        [Fact]
        public void Jpeg_Roundtrip_WithTolerance()
        {
            int w = 8, h = 8;
            var buf = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)(w <= 1 ? 0 : (x * 255) / (w - 1));
                    int o = (y * w + x) * 3;
                    buf[o + 0] = v;
                    buf[o + 1] = v;
                    buf[o + 2] = v;
                }
            }
            var img = new Image<Rgb24>(w, h, buf);
            string path = NewTemp(".jpg");
            var frame = new ImageFrame(img.Width, img.Height, img.Buffer);
            JpegEncoder.DebugPrintConfig = true;
            frame.SaveAsJpeg(path, 99, false);
            var loaded = Image.Load(path);
            Assert.Equal(img.Width, loaded.Width);
            Assert.Equal(img.Height, loaded.Height);
            BufferAssert.AssertMseLessThan(img.Buffer, loaded.Buffer, 5000.0);
            for (int x = 0; x < w - 1; x++)
            {
                long sumA = 0;
                long sumB = 0;
                for (int y = 0; y < h; y++)
                {
                    int oa = (y * w + x) * 3;
                    int ob = (y * w + (x + 1)) * 3;
                    sumA += loaded.Buffer[oa + 0];
                    sumB += loaded.Buffer[ob + 0];
                }
                Assert.True(sumA <= sumB + 5);
            }
            File.Delete(path);
        }

        [Fact]
        public void Jpeg_Roundtrip_DefaultSettings_NoSevereColorShift()
        {
            int w = 64, h = 64;
            var buf = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte r = (byte)(40 + (x * 150) / (w - 1));
                    byte g = (byte)(50 + (y * 140) / (h - 1));
                    byte b = (byte)(60 + ((x + y) * 120) / (2 * (w - 1)));
                    int o = (y * w + x) * 3;
                    buf[o + 0] = r;
                    buf[o + 1] = g;
                    buf[o + 2] = b;
                }
            }

            var img = new Image<Rgb24>(w, h, buf);
            string path = NewTemp(".jpg");
            JpegEncoder.DebugPrintConfig = true;
            JpegEncoder.Write(path, img.Width, img.Height, img.Buffer, 90);
            var loaded = Image.Load(path);
            Assert.Equal(img.Width, loaded.Width);
            Assert.Equal(img.Height, loaded.Height);

            BufferAssert.AssertMseLessThan(img.Buffer, loaded.Buffer, 15000.0);

            long sumR0 = 0, sumG0 = 0, sumB0 = 0;
            long sumR1 = 0, sumG1 = 0, sumB1 = 0;
            for (int i = 0; i < img.Buffer.Length; i += 3)
            {
                sumR0 += img.Buffer[i + 0];
                sumG0 += img.Buffer[i + 1];
                sumB0 += img.Buffer[i + 2];
                sumR1 += loaded.Buffer[i + 0];
                sumG1 += loaded.Buffer[i + 1];
                sumB1 += loaded.Buffer[i + 2];
            }
            int pixels = w * h;
            int meanR0 = (int)(sumR0 / pixels);
            int meanG0 = (int)(sumG0 / pixels);
            int meanB0 = (int)(sumB0 / pixels);
            int meanR1 = (int)(sumR1 / pixels);
            int meanG1 = (int)(sumG1 / pixels);
            int meanB1 = (int)(sumB1 / pixels);

            Assert.True(Math.Abs(meanR0 - meanR1) <= 15, $"R 均值偏差过大: {meanR0} vs {meanR1}");
            Assert.True(Math.Abs(meanG0 - meanG1) <= 15, $"G 均值偏差过大: {meanG0} vs {meanG1}");
            Assert.True(Math.Abs(meanB0 - meanB1) <= 15, $"B 均值偏差过大: {meanB0} vs {meanB1}");

            File.Delete(path);
        }

        [Fact]
        public void Jpeg_Gray8_Encodes_As_SingleComponent_Gray()
        {
            int w = 16, h = 16;
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)((x * 255) / (w - 1));
                    gray[y * w + x] = v;
                }
            }

            var img = new Image<Gray8>(w, h, gray);
            string path = NewTemp(".jpg");
            Image.Save(img, path);

            byte[] data = File.ReadAllBytes(path);
            var jpeg = StaticJpegDecoder.Decode(data);

            Assert.Equal(JpegPixelFormat.Gray8, jpeg.PixelFormat);
            Assert.Equal(w, jpeg.Width);
            Assert.Equal(h, jpeg.Height);

            var decodedGray = jpeg.PixelData.ToArray();
            BufferAssert.AssertMseLessThan(gray, decodedGray, 8000.0);

            File.Delete(path);
        }

        [Fact]
        public void Gif_Animated_To_Webp_Animated_Works()
        {
            byte[] gifBytes = CreateTinyAnimatedGif1x1();
            using var gifStream = new MemoryStream(gifBytes);
            var dec = new GifDecoder();
            var anim = dec.DecodeAnimationRgb24(gifStream);
            Assert.True(anim.Frames.Count >= 2);

            using var outStream = new MemoryStream();
            WebpAnimationEncoder.EncodeRgb24(outStream, anim.Frames, anim.FrameDurationsMs, anim.LoopCount, 75f);
            byte[] webp = outStream.ToArray();

            Assert.True(webp.Length >= 16);
            Assert.Equal((byte)'R', webp[0]);
            Assert.Equal((byte)'I', webp[1]);
            Assert.Equal((byte)'F', webp[2]);
            Assert.Equal((byte)'F', webp[3]);
            Assert.Equal((byte)'W', webp[8]);
            Assert.Equal((byte)'E', webp[9]);
            Assert.Equal((byte)'B', webp[10]);
            Assert.Equal((byte)'P', webp[11]);

            Assert.True(IndexOfAscii(webp, "ANIM") >= 0);
            Assert.True(IndexOfAscii(webp, "ANMF") >= 0);
        }

        private static int IndexOfAscii(byte[] data, string needle)
        {
            if (data.Length == 0) return -1;
            if (string.IsNullOrEmpty(needle)) return 0;

            byte[] n = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i <= data.Length - n.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < n.Length; j++)
                {
                    if (data[i + j] != n[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        private static byte[] CreateTinyAnimatedGif1x1()
        {
            return
            [
                0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
                0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
                0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,

                0x21, 0xFF, 0x0B,
                0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30,
                0x03, 0x01, 0x00, 0x00, 0x00,

                0x21, 0xF9, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00,
                0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0x02, 0x02, 0x44, 0x01, 0x00,

                0x21, 0xF9, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00,
                0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0x02, 0x02, 0x4C, 0x01, 0x00,

                0x3B
            ];
        }

        private static byte[] CreateNoiseRgba(int width, int height)
        {
            var rgba = new byte[width * height * 4];
            uint x = 2463534242u;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                rgba[i + 0] = (byte)x;
                rgba[i + 1] = (byte)(x >> 8);
                rgba[i + 2] = (byte)(x >> 16);
                rgba[i + 3] = 255;
            }
            return rgba;
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
