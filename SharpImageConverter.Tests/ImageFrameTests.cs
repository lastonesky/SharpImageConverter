using Xunit;
using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Formats.Png;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class ImageFrameTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        private static string ExamplePath(string name)
        {
            return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", name);
        }

        [Fact]
        public void ImageFrame_Save_And_Load_Bmp_Png_Jpeg()
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
            Assert.Equal(w, fBmp.Width);
            Assert.Equal(h, fBmp.Height);
            Assert.Equal(w, fPng.Width);
            Assert.Equal(h, fPng.Height);
            File.Delete(bmp);
            File.Delete(png);
            File.Delete(jpg);
        }

        [Fact]
        public void ImageFrame_Load_NonSeekableStream_Matches_ByteArray()
        {
            string path = ExamplePath("Amish-Noka-Dresser.jpg");
            byte[] data = File.ReadAllBytes(path);
            var frameFromBytes = ImageFrame.Load(data);
            using var nonSeek = new NonSeekableReadOnlyStream(data);
            var frameFromStream = ImageFrame.Load(nonSeek);
            Assert.Equal(frameFromBytes.Width, frameFromStream.Width);
            Assert.Equal(frameFromBytes.Height, frameFromStream.Height);
            BufferAssert.EqualExact(frameFromBytes.Pixels, frameFromStream.Pixels);
        }

        [Fact]
        public void ImageFrame_Load_NonSeekableStream_TooShort_Throws()
        {
            using var nonSeek = new NonSeekableReadOnlyStream(new byte[] { 0xFF });
            Assert.Throws<InvalidDataException>(() => ImageFrame.Load(nonSeek));
        }

        [Fact]
        public void ImageFrame_Load_InvalidHeader_Throws()
        {
            byte[] data = [0x00, 0x11, 0x22, 0x33, 0x44];
            Assert.Throws<NotSupportedException>(() => ImageFrame.Load(data));
        }

        [Fact]
        public void ImageFrame_Load_ChunkedStream_Matches_ByteArray()
        {
            var img = TestImageFactory.CreateChecker(32, 32, (10, 20, 30), (200, 210, 220), 4);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            byte[] data = ms.ToArray();

            var frameFromBytes = ImageFrame.Load(data);
            using var chunked = new ChunkedReadOnlyStream(data, 7);
            var frameFromStream = ImageFrame.Load(chunked);
            Assert.Equal(frameFromBytes.Width, frameFromStream.Width);
            Assert.Equal(frameFromBytes.Height, frameFromStream.Height);
            BufferAssert.EqualExact(frameFromBytes.Pixels, frameFromStream.Pixels);
        }

        [Fact]
        public void ImageFrame_Load_ChunkedStream_Truncated_Throws()
        {
            var img = TestImageFactory.CreateChecker(16, 16, (10, 20, 30), (200, 210, 220), 2);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            byte[] data = ms.ToArray();
            using var chunked = new ChunkedReadOnlyStream(data, 5, data.Length / 2);
            Assert.ThrowsAny<Exception>(() => ImageFrame.Load(chunked));
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

        [Fact]
        public void ApplyExifOrientation_All_1_To_8()
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
                Assert.Equal(m.newW, dst.Width);
                Assert.Equal(m.newH, dst.Height);
                int s00 = (0 * w + 0) * 3;
                int d00 = (m.dy * m.newW + m.dx) * 3;
                Assert.Equal(src.Pixels[s00 + 0], dst.Pixels[d00 + 0]);
                Assert.Equal(src.Pixels[s00 + 1], dst.Pixels[d00 + 1]);
                Assert.Equal(src.Pixels[s00 + 2], dst.Pixels[d00 + 2]);
                var m10 = Map(1, 0, w, h, orientation);
                int s10 = (0 * w + 1) * 3;
                int d10 = (m10.dy * m10.newW + m10.dx) * 3;
                Assert.Equal(src.Pixels[s10 + 0], dst.Pixels[d10 + 0]);
                Assert.Equal(src.Pixels[s10 + 1], dst.Pixels[d10 + 1]);
                Assert.Equal(src.Pixels[s10 + 2], dst.Pixels[d10 + 2]);
                var m01 = Map(0, 1, w, h, orientation);
                int s01 = (1 * w + 0) * 3;
                int d01 = (m01.dy * m01.newW + m01.dx) * 3;
                Assert.Equal(src.Pixels[s01 + 0], dst.Pixels[d01 + 0]);
                Assert.Equal(src.Pixels[s01 + 1], dst.Pixels[d01 + 1]);
                Assert.Equal(src.Pixels[s01 + 2], dst.Pixels[d01 + 2]);
            }
        }

        private sealed class NonSeekableReadOnlyStream : Stream
        {
            private readonly Stream inner;

            public NonSeekableReadOnlyStream(byte[] data)
            {
                inner = new MemoryStream(data, writable: false);
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
                return inner.Read(buffer, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                return inner.Read(buffer);
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

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class ChunkedReadOnlyStream : Stream
        {
            private readonly byte[] data;
            private readonly int maxChunk;
            private readonly int maxBytes;
            private int position;

            public ChunkedReadOnlyStream(byte[] data, int maxChunk, int? maxBytes = null)
            {
                this.data = data;
                this.maxChunk = maxChunk > 0 ? maxChunk : 1;
                this.maxBytes = Math.Min(maxBytes ?? data.Length, data.Length);
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
                if (position >= maxBytes) return 0;
                int remaining = maxBytes - position;
                int take = Math.Min(count, Math.Min(maxChunk, remaining));
                Buffer.BlockCopy(data, position, buffer, offset, take);
                position += take;
                return take;
            }

            public override int Read(Span<byte> buffer)
            {
                if (position >= maxBytes) return 0;
                int remaining = maxBytes - position;
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
