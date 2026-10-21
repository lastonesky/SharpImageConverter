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
    /// 各格式的编解码往返：像素逐字节或容差一致，元数据保留。
    /// </summary>
    public partial class FormatConversionTests
    {
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
        public void Png_Metadata_Srgb_Roundtrip()
        {
            var img = TestImageFactory.CreateChecker(32, 32, (10, 20, 30), (200, 210, 220), 4);
            img.Metadata.IccProfileKind = IccProfileKind.SRgb;
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            ms.Position = 0;
            var dec = new PngDecoder();
            var rgb = dec.DecodeToRGB(ms);
            Assert.Equal(img.Width, dec.Width);
            Assert.Equal(img.Height, dec.Height);
            Assert.Equal(IccProfileKind.SRgb, dec.Metadata.IccProfileKind);
            Assert.Null(dec.Metadata.IccProfile);
            BufferAssert.EqualExact(img.Buffer, rgb);
        }

        [Fact]
        public void Png_Large_Roundtrip_Exact()
        {
            var img = TestImageFactory.CreateGradient(512, 512);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            ms.Position = 0;
            var dec = new PngDecoder();
            var rgb = dec.DecodeToRGB(ms);
            Assert.Equal(img.Width, dec.Width);
            Assert.Equal(img.Height, dec.Height);
            BufferAssert.EqualExact(img.Buffer, rgb);
        }

        [Fact]
        public void Png_Metadata_IccProfile_Roundtrip()
        {
            var img = TestImageFactory.CreateChecker(16, 16, (10, 20, 30), (200, 210, 220), 2);
            img.Metadata.IccProfile = CreateFakeIccProfile(256);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            ms.Position = 0;
            var dec = new PngDecoder();
            _ = dec.DecodeToRGB(ms);
            Assert.NotNull(dec.Metadata.IccProfile);
            BufferAssert.EqualExact(img.Metadata.IccProfile!, dec.Metadata.IccProfile!);
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
            JpegEncoder.Encode(img, path, new JpegEncoderOptions(99, false, true, true));
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
            JpegEncoder.Write(path, img.Width, img.Height, img.Buffer, new JpegEncoderOptions(90, true, false, true));
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
        public void Jpeg_Repeated_MultiMcu_Encode_Does_Not_Crash()
        {
            var img = TestImageFactory.CreateGradient(257, 257);

            for (int i = 0; i < 6; i++)
            {
                using var ms420 = new MemoryStream();
                JpegEncoder.Write(ms420, img.Width, img.Height, img.Buffer, new JpegEncoderOptions(85, true, false, false));
                byte[] data420 = ms420.ToArray();
                var decoded420 = JpegDecoder.Decode(data420);
                Assert.Equal(img.Width, decoded420.Width);
                Assert.Equal(img.Height, decoded420.Height);

                using var ms444 = new MemoryStream();
                JpegEncoder.Write(ms444, img.Width, img.Height, img.Buffer, new JpegEncoderOptions(85, false, false, false));
                byte[] data444 = ms444.ToArray();
                var decoded444 = JpegDecoder.Decode(data444);
                Assert.Equal(img.Width, decoded444.Width);
                Assert.Equal(img.Height, decoded444.Height);
            }
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
            var jpeg = JpegDecoder.Decode(data);

            Assert.Equal(JpegPixelFormat.Gray8, jpeg.PixelFormat);
            Assert.Equal(w, jpeg.Width);
            Assert.Equal(h, jpeg.Height);

            var decodedGray = jpeg.PixelData.ToArray();
            BufferAssert.AssertMseLessThan(gray, decodedGray, 8000.0);

            File.Delete(path);
        }

        [Fact]
        public async Task Parallel_Save_Load_Formats_No_Exceptions()
        {
            var img = TestImageFactory.CreateGradient(64, 64);
            var exts = new[] { ".bmp", ".png", ".jpg" };
            var tasks = new List<Task>();
            for (int i = 0; i < exts.Length; i++)
            {
                string ext = exts[i];
                for (int k = 0; k < 4; k++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        string path = NewTemp(ext);
                        Image.Save(img, path);
                        var loaded = Image.Load(path);
                        Assert.Equal(img.Width, loaded.Width);
                        Assert.Equal(img.Height, loaded.Height);
                        File.Delete(path);
                    }, TestContext.Current.CancellationToken));
                }
            }
            await Task.WhenAll(tasks);
        }

        [Fact]
        public void Jpeg_Stream_Chunked_Decode_Matches_ByteArray()
        {
            var img = TestImageFactory.CreateGradient(64, 64);
            using var ms = new MemoryStream();
            JpegEncoder.Write(ms, img.Width, img.Height, img.Buffer, 85);
            byte[] data = ms.ToArray();

            var imgA = JpegDecoder.Decode(data);

            using var chunked = new ChunkedReadOnlyStream(data, 11);
            var imgB = JpegDecoder.Decode(chunked);

            Assert.Equal(imgA.Width, imgB.Width);
            Assert.Equal(imgA.Height, imgB.Height);
            Assert.Equal(Hash(imgA.Buffer), Hash(imgB.Buffer));
        }

        [Fact]
        public void Webp_Rgba_Alpha_Roundtrip_Preserves_Transparency()
        {
            int w = 32, h = 32;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool transparent = (x + y) % 3 == 0;
                    rgba[o + 0] = transparent ? (byte)0 : (byte)200;
                    rgba[o + 1] = transparent ? (byte)0 : (byte)50;
                    rgba[o + 2] = transparent ? (byte)0 : (byte)100;
                    rgba[o + 3] = transparent ? (byte)0 : (byte)255;
                }
            }
            var img = new Image<Rgba32>(w, h, rgba);
            using var ms = new MemoryStream();
            var enc = new WebpEncoderAdapterRgba();
            enc.EncodeRgba32(ms, img);
            byte[] data = ms.ToArray();

            using var ms2 = new MemoryStream(data);
            var dec = new WebpDecoderAdapterRgba();
            var loaded = dec.DecodeRgba32(ms2);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
            byte minA = 255;
            byte maxA = 0;
            for (int i = 3; i < loaded.Buffer.Length; i += 4)
            {
                byte a = loaded.Buffer[i];
                if (a < minA) minA = a;
                if (a > maxA) maxA = a;
            }
            Assert.True(minA < maxA);
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
    }
}
