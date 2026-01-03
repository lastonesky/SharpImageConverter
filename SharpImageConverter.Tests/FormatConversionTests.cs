using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SharpImageConverter;
using SharpImageConverter.Core;
using Tests.Helpers;
using Xunit;

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
            frame.SaveAsJpeg(path, 99, false, false);
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
        public void Webp_RepeatedEncode_NoUnboundedMemoryGrowth()
        {
            var asm = typeof(Configuration).Assembly;
            var webpCodecType = asm.GetType("SharpImageConverter.Formats.WebpCodec", throwOnError: false);
            if (webpCodecType is null) return;

            var encodeMethod = webpCodecType.GetMethod("EncodeRgba", BindingFlags.Public | BindingFlags.Static);
            if (encodeMethod is null) return;

            Func<byte[], int, int, float, byte[]> encode;
            try
            {
                encode = encodeMethod.CreateDelegate<Func<byte[], int, int, float, byte[]>>();
            }
            catch
            {
                return;
            }

            const int width = 128;
            const int height = 128;
            byte[] rgba = CreateNoiseRgba(width, height);

            try
            {
                for (int i = 0; i < 10; i++)
                {
                    _ = encode(rgba, width, height, 75f);
                }

                ForceGc();
                var p = Process.GetCurrentProcess();
                p.Refresh();
                long baseline = p.PrivateMemorySize64;

                const int iterations = 800;
                for (int i = 0; i < iterations; i++)
                {
                    _ = encode(rgba, width, height, 75f);
                    if ((i % 50) == 0) ForceGc();
                }

                ForceGc();
                p.Refresh();
                long after = p.PrivateMemorySize64;
                long delta = after - baseline;

                Assert.True(delta < 80L * 1024 * 1024, $"WebP 重复编码后 PrivateMemory 增长过大: {delta} bytes");
            }
            catch (DllNotFoundException)
            {
                return;
            }
            catch (BadImageFormatException)
            {
                return;
            }
            catch (EntryPointNotFoundException)
            {
                return;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return;
            }
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
