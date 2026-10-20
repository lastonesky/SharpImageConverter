using Xunit;
using System;
using System.Buffers;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests
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

        /// <summary>
        /// 不可 Seek 的流（分片投递）必须与整块字节解码结果完全一致。
        /// </summary>
        /// <remarks>
        /// 原先 JPEG / WebP / 分片 PNG 各写一个测试，逻辑完全相同，只有数据来源与分片大小不同；
        /// 合并为参数化用例后，三种格式走同一套比对逻辑，并顺带覆盖 1 字节分片（最坏情况）。
        /// JPEG 用真实样本（1000×1000），其余格式用生成的图，避免大样本拖慢。
        /// </remarks>
        [Theory]
        [InlineData("jpeg", 7)]
        [InlineData("jpeg", int.MaxValue)]
        [InlineData("png", 1)]
        [InlineData("png", 7)]
        [InlineData("webp", 7)]
        public void ImageFrame_Load_NonSeekableStream_Matches_ByteArray(string format, int chunkSize)
        {
            byte[] data = CreateEncodedSample(format);
            var frameFromBytes = ImageFrame.Load(data);
            using var stream = new ChunkedReadOnlyStream(data, chunkSize);
            var frameFromStream = ImageFrame.Load(stream);

            Assert.Equal(frameFromBytes.Width, frameFromStream.Width);
            Assert.Equal(frameFromBytes.Height, frameFromStream.Height);
            BufferAssert.EqualExact(frameFromBytes.Pixels, frameFromStream.Pixels);
        }

        private static byte[] CreateEncodedSample(string format)
        {
            if (format == "jpeg")
            {
                return File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
            }

            var img = TestImageFactory.CreateChecker(32, 32, (10, 20, 30), (200, 210, 220), 4);
            using var ms = new MemoryStream();
            if (format == "png")
            {
                PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            }
            else if (format == "webp")
            {
                new WebpEncoderAdapter().EncodeRgb24(ms, img);
            }
            else
            {
                throw new ArgumentException($"未支持的样本格式: {format}", nameof(format));
            }

            return ms.ToArray();
        }

        [Fact]
        public void ImageFrame_Load_NonSeekableStream_TooShort_Throws()
        {
            using var nonSeek = new ChunkedReadOnlyStream(new byte[] { 0xFF }, 1);
            Assert.Throws<InvalidDataException>(() => ImageFrame.Load(nonSeek));
        }

        [Fact]
        public void ImageFrame_Load_InvalidHeader_Throws()
        {
            byte[] data = [0x00, 0x11, 0x22, 0x33, 0x44];
            Assert.Throws<NotSupportedException>(() => ImageFrame.Load(data));
        }

        [Fact]
        public void ImageFrame_Load_ReadOnlyMemory_NonArrayBacked_Matches_ByteArray()
        {
            var img = TestImageFactory.CreateChecker(16, 16, (10, 20, 30), (200, 210, 220), 2);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            byte[] data = ms.ToArray();
            using var manager = new NonArrayBackedMemory(data);
            ReadOnlyMemory<byte> memory = manager.Memory.Slice(0, data.Length);

            var frameFromBytes = ImageFrame.Load(data);
            var frameFromMemory = ImageFrame.Load(memory);
            Assert.Equal(frameFromBytes.Width, frameFromMemory.Width);
            Assert.Equal(frameFromBytes.Height, frameFromMemory.Height);
            BufferAssert.EqualExact(frameFromBytes.Pixels, frameFromMemory.Pixels);
        }

        [Fact]
        public void ImageFrame_Load_ChunkedStream_Truncated_Throws()
        {
            var img = TestImageFactory.CreateChecker(16, 16, (10, 20, 30), (200, 210, 220), 2);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            byte[] data = ms.ToArray();
            using var chunked = new ChunkedReadOnlyStream(data, 5, data.Length / 2);
            // 只接受"数据不完整"这一种受控失败；NRE/IndexOutOfRange 属于解析器缺陷，必须暴露。
            Assert.Throws<InvalidDataException>(() => ImageFrame.Load(chunked));
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

        private sealed class NonArrayBackedMemory(byte[] data) : MemoryManager<byte>
        {
            private readonly byte[] data = data;

            public override Span<byte> GetSpan()
            {
                return data;
            }

            public override MemoryHandle Pin(int elementIndex = 0)
            {
                throw new NotSupportedException();
            }

            public override void Unpin()
            {
            }

            protected override void Dispose(bool disposing)
            {
            }
        }
    }
}
