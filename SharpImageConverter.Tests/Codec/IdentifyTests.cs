using System;
using System.IO;
using System.Text;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// <see cref="Image.Identify(string)"/> / <see cref="Image.Identify(Stream)"/>：
    /// 只读文件头、不解码像素，且与各格式解码器给出的尺寸一致。
    /// </summary>
    public class IdentifyTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        private static byte[] EncodePng(int w, int h)
        {
            var img = TestImageFactory.CreateChecker(w, h, (10, 20, 30), (200, 210, 220), 4);
            using var ms = new MemoryStream();
            PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
            return ms.ToArray();
        }

        /// <summary>
        /// 五种内置格式都必须识别出正确的格式与尺寸。
        /// </summary>
        [Theory]
        [InlineData("png", ImageFormatKind.Png)]
        [InlineData("bmp", ImageFormatKind.Bmp)]
        [InlineData("gif", ImageFormatKind.Gif)]
        [InlineData("webp", ImageFormatKind.Webp)]
        [InlineData("jpg", ImageFormatKind.Jpeg)]
        public void Identify_Recognizes_Format_And_Size(string ext, ImageFormatKind expected)
        {
            const int w = 33, h = 17;
            string path = NewTemp("." + ext);
            try
            {
                var img = TestImageFactory.CreateChecker(w, h, (10, 20, 30), (200, 210, 220), 3);
                Image.Save(img, path);

                var info = Image.Identify(path);
                Assert.Equal(expected, info.Format);
                Assert.Equal(w, info.Width);
                Assert.Equal(h, info.Height);
                Assert.Equal(1, info.FrameCount);
                Assert.False(info.IsAnimated);
                Assert.Equal((long)w * h, info.PixelCount);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 与解码器实际解码出的尺寸必须一致（避免「嗅探读的偏移量」与「解码器读的」漂移）。
        /// </summary>
        [Fact]
        public void Identify_Matches_Decoded_Size()
        {
            string path = NewTemp(".png");
            try
            {
                File.WriteAllBytes(path, EncodePng(48, 20));
                var info = Image.Identify(path);
                var decoded = ImageFrame.Load(path);
                Assert.Equal(decoded.Width, info.Width);
                Assert.Equal(decoded.Height, info.Height);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 识别只读文件头：流位置必须还原，调用方接着解码不受影响。
        /// </summary>
        [Fact]
        public void Identify_Restores_Stream_Position()
        {
            byte[] png = EncodePng(16, 16);
            using var ms = new MemoryStream(png);
            ms.Position = 0;

            var info = Image.Identify(ms);
            Assert.Equal(0L, ms.Position);
            Assert.Equal(16, info.Width);

            // 识别之后仍能完整解码
            var frame = ImageFrame.Load(ms);
            Assert.Equal(16, frame.Width);
        }

        /// <summary>
        /// 不可 Seek 的流：整读进内存后仍应识别成功。
        /// </summary>
        [Fact]
        public void Identify_NonSeekable_Stream_Works()
        {
            byte[] png = EncodePng(24, 12);
            using var nonSeek = new NonSeekableReadStream(png);
            var info = Image.Identify(nonSeek);
            Assert.Equal(ImageFormatKind.Png, info.Format);
            Assert.Equal(24, info.Width);
            Assert.Equal(12, info.Height);
        }

        /// <summary>
        /// 动图应报出真实帧数（此前帧数信息只在 MemoryGuard 内部可见）。
        /// </summary>
        [Fact]
        public void Identify_Gif_Reports_FrameCount()
        {
            const int frames = 5;
            string path = NewTemp(".gif");
            try
            {
                var frameList = new System.Collections.Generic.List<ImageFrame>(frames);
                for (int i = 0; i < frames; i++)
                {
                    byte[] rgb = TestImageFactory.CreateChecker(8, 8, ((byte)(i * 40), (byte)10, (byte)10), (200, 200, 200), 2).Buffer;
                    frameList.Add(new ImageFrame(8, 8, rgb));
                }

                var enc = new GifEncoder();
                using (var fs = File.Create(path))
                {
                    enc.EncodeAnimation(frameList, [10, 10, 10, 10, 10], loopCount: 0, fs);
                }

                var info = Image.Identify(path);
                Assert.Equal(ImageFormatKind.Gif, info.Format);
                Assert.Equal(frames, info.FrameCount);
                Assert.True(info.IsAnimated);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 渐进式 JPEG 应被标记（此前只有 MemoryGuard 内部知道，公开 API 看不到）。
        /// </summary>
        [Fact]
        public void Identify_Progressive_Jpeg_Is_Flagged()
        {
            string baseline = NewTemp(".jpg");
            string progressive = NewTemp(".jpg");
            try
            {
                var img = TestImageFactory.CreateChecker(64, 48, (10, 20, 30), (200, 210, 220), 4);
                using var ms = new MemoryStream();
                JpegEncoder.Encode(img, ms,
                    new JpegEncoderOptions(85, subsample420: true, keepMetadata: false, enableDiagnostics: false));
                byte[] baselineBytes = ms.ToArray();
                byte[] progressiveBytes = Compression.JpegProgressiveTranscoder.TryTranscode(baselineBytes)
                    ?? throw new InvalidOperationException("基线 JPEG 无法转成渐进式");

                File.WriteAllBytes(baseline, baselineBytes);
                File.WriteAllBytes(progressive, progressiveBytes);

                Assert.False(Image.Identify(baseline).IsProgressive);
                var progInfo = Image.Identify(progressive);
                Assert.True(progInfo.IsProgressive);
                Assert.Equal(64, progInfo.Width);
                Assert.Equal(48, progInfo.Height);
            }
            finally
            {
                File.Delete(baseline);
                File.Delete(progressive);
            }
        }

        /// <summary>
        /// 未知格式应抛 NotSupportedException，而不是返回一个 Width=0 的默认值。
        /// </summary>
        [Fact]
        public void Identify_Unknown_Format_Throws()
        {
            string path = NewTemp(".bin");
            try
            {
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("not an image at all"));
                Assert.Throws<NotSupportedException>(() => Image.Identify(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 格式已识别但头部损坏（此处把 PNG 的 IHDR 类型破坏）应抛 InvalidDataException。
        /// </summary>
        [Fact]
        public void Identify_Corrupt_Header_Throws_InvalidData()
        {
            byte[] png = EncodePng(8, 8);
            png[12] = (byte)'X'; // IHDR -> XHDR
            string path = NewTemp(".png");
            try
            {
                File.WriteAllBytes(path, png);
                Assert.Throws<InvalidDataException>(() => Image.Identify(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 嗅探结果与扩展名无关：PNG 内容存成 .jpg 名，仍应识别为 PNG。
        /// </summary>
        [Fact]
        public void Identify_Ignores_Extension()
        {
            string path = NewTemp(".jpg");
            try
            {
                File.WriteAllBytes(path, EncodePng(20, 10));
                var info = Image.Identify(path);
                Assert.Equal(ImageFormatKind.Png, info.Format);
                Assert.Equal("PNG", info.FormatName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private sealed class NonSeekableReadStream(byte[] data) : Stream
        {
            private readonly byte[] data = data;
            private int position;

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
                int remaining = data.Length - position;
                if (remaining <= 0) return 0;
                int take = Math.Min(count, remaining);
                Buffer.BlockCopy(data, position, buffer, offset, take);
                position += take;
                return take;
            }

            public override int Read(Span<byte> buffer)
            {
                int remaining = data.Length - position;
                if (remaining <= 0) return 0;
                int take = Math.Min(buffer.Length, remaining);
                data.AsSpan(position, take).CopyTo(buffer);
                position += take;
                return take;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
