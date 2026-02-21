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
                    }));
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

            var decA = new JpegDecoder();
            var imgA = decA.Decode(data);

            using var chunked = new ChunkedReadOnlyStream(data, 11);
            var decB = new JpegDecoder();
            var imgB = decB.Decode(chunked);

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
        public void Fuzz_Mutated_Png_Inputs_Do_Not_Hang()
        {
            var img = TestImageFactory.CreateGradient(64, 64);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            byte[] data = ms.ToArray();
            var rng = new Random(1234);
            for (int i = 0; i < 40; i++)
            {
                byte[] mutated = MutateBytes(data, rng, 3);
                try
                {
                    using var s = new MemoryStream(mutated);
                    var dec = new PngDecoder();
                    _ = dec.DecodeToRGB(s);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void Fuzz_Mutated_Jpeg_Inputs_Do_Not_Hang()
        {
            var img = TestImageFactory.CreateGradient(64, 64);
            using var ms = new MemoryStream();
            JpegEncoder.Write(ms, img.Width, img.Height, img.Buffer, 85);
            byte[] data = ms.ToArray();
            var rng = new Random(5678);
            for (int i = 0; i < 40; i++)
            {
                byte[] mutated = MutateBytes(data, rng, 3);
                try
                {
                    var dec = new JpegDecoder();
                    _ = dec.Decode(mutated);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void Performance_Gate_Encode_Decode_Png_Jpeg()
        {
            var img = TestImageFactory.CreateGradient(512, 512);
            ForceGc();
            long memBefore = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 3; i++)
            {
                using var ms = new MemoryStream();
                PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
                ms.Position = 0;
                var dec = new PngDecoder();
                _ = dec.DecodeToRGB(ms);
            }
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000);
            ForceGc();
            long memAfter = GC.GetTotalMemory(true);
            Assert.True(memAfter - memBefore < 200_000_000);

            sw.Restart();
            for (int i = 0; i < 3; i++)
            {
                using var ms = new MemoryStream();
                JpegEncoder.Write(ms, img.Width, img.Height, img.Buffer, 85);
                byte[] data = ms.ToArray();
                var dec = new JpegDecoder();
                _ = dec.Decode(data);
            }
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000);
        }

        [Fact]
        public void Corpus_Examples_Jpeg_Decode_Is_Stable()
        {
            string[] files =
            [
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "video-001.cmyk.jpeg"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "video-001.rgb.jpeg")
            ];

            foreach (string path in files)
            {
                byte[] data = File.ReadAllBytes(path);
                var dec1 = new JpegDecoder();
                var img1 = dec1.Decode(data);
                using var ms = new MemoryStream(data);
                var dec2 = new JpegDecoder();
                var img2 = dec2.Decode(ms);
                Assert.Equal(img1.Width, img2.Width);
                Assert.Equal(img1.Height, img2.Height);
                Assert.Equal(Hash(img1.Buffer), Hash(img2.Buffer));
            }
        }

        [Fact]
        public void Cli_EndToEnd_Convert_Png_To_Jpeg()
        {
            var img = TestImageFactory.CreateGradient(128, 128);
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            Image.Save(img, input);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project Cli -- {input} {output}",
                WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc!.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
            Assert.True(File.Exists(output));
            var loaded = Image.Load(output);
            Assert.Equal(img.Width, loaded.Width);
            Assert.Equal(img.Height, loaded.Height);
            File.Delete(input);
            File.Delete(output);
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

        private static byte[] CreateFakeIccProfile(int size)
        {
            var data = new byte[size];
            uint x = 123456789u;
            for (int i = 0; i < data.Length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] = (byte)x;
            }
            return data;
        }

        private static byte[] MutateBytes(byte[] data, Random rng, int flips)
        {
            var buf = new byte[data.Length];
            Buffer.BlockCopy(data, 0, buf, 0, data.Length);
            for (int i = 0; i < flips; i++)
            {
                int idx = rng.Next(0, buf.Length);
                int bit = 1 << rng.Next(0, 8);
                buf[idx] = (byte)(buf[idx] ^ bit);
            }
            return buf;
        }

        private static byte[] Hash(byte[] data)
        {
            return SHA256.HashData(data);
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private sealed class ChunkedReadOnlyStream : Stream
        {
            private readonly byte[] data;
            private readonly int maxChunk;
            private int position;

            public ChunkedReadOnlyStream(byte[] data, int maxChunk)
            {
                this.data = data;
                this.maxChunk = maxChunk > 0 ? maxChunk : 1;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (position >= data.Length) return 0;
                int remaining = data.Length - position;
                int take = Math.Min(count, Math.Min(maxChunk, remaining));
                Buffer.BlockCopy(data, position, buffer, offset, take);
                position += take;
                return take;
            }

            public override int Read(Span<byte> buffer)
            {
                if (position >= data.Length) return 0;
                int remaining = data.Length - position;
                int take = Math.Min(buffer.Length, Math.Min(maxChunk, remaining));
                data.AsSpan(position, take).CopyTo(buffer);
                position += take;
                return take;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
