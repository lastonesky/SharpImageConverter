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
        /// 不可 Seek 的流**不得被整读**：只读到文件头所需位置即可。
        /// </summary>
        /// <remarks>
        /// 此前实现是 <c>stream.CopyTo(MemoryStream)</c>：为读 24 字节把整条流搬进内存，
        /// 143 MB 上传即 143 MB 常驻，与「只读文件头」的定位矛盾（§2.10）。
        /// 这里用 512×512 的 PNG（远大于 1 KB）做样本，识别读掉的字节数必须是个零头。
        /// </remarks>
        [Fact]
        public void Identify_NonSeekable_Does_Not_Buffer_Whole_Stream()
        {
            // 随机像素 → 不可压缩，保证样本足够大（棋盘/渐变会被 deflate 压到几 KB 以内）
            var img = TestImageFactory.CreateSolid(256, 256, 0, 0, 0);
            new Random(12345).NextBytes(img.Buffer);
            byte[] png;
            using (var ms = new MemoryStream())
            {
                PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
                png = ms.ToArray();
            }
            Assert.True(png.Length > 65536, $"样本只有 {png.Length} 字节，不足以证明「没有整读」");

            using var counter = new CountingNonSeekableStream(png);
            var info = Image.Identify(counter);

            Assert.Equal(256, info.Width);
            Assert.Equal(256, info.Height);
            Assert.True(
                counter.BytesRead <= 1024,
                $"识别读了 {counter.BytesRead} 字节（整个文件 {png.Length} 字节），仍在整读");
        }

        /// <summary>
        /// 动画 WebP 的帧数要真实：此前 WebP 分支恒传 1 帧，<c>IsAnimated</c> 反过来是 false。
        /// </summary>
        [Fact]
        public void Identify_Animated_Webp_Reports_FrameCount()
        {
            const int frames = 4;
            string path = NewTemp(".webp");
            try
            {
                var list = new System.Collections.Generic.List<Image<Rgb24>>(frames);
                for (int i = 0; i < frames; i++)
                {
                    list.Add(TestImageFactory.CreateChecker(16, 16, ((byte)(i * 40), (byte)20, (byte)30), (200, 200, 200), 2));
                }
                WebpAnimationEncoder.EncodeRgb24(path, list, [100, 100, 100, 100], loopCount: 0, quality: 75f);

                var info = Image.Identify(path);
                Assert.Equal(ImageFormatKind.Webp, info.Format);
                Assert.Equal(frames, info.FrameCount);
                Assert.True(info.IsAnimated);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// APNG 的帧数取自 <c>acTL</c>：此前 PNG 分支只读 IHDR，动画 PNG 也报 1 帧。
        /// </summary>
        [Fact]
        public void Identify_Apng_Reports_FrameCount()
        {
            const uint frames = 6;
            string path = NewTemp(".png");
            try
            {
                File.WriteAllBytes(path, InsertActl(EncodePng(24, 16), frames));

                var info = Image.Identify(path);
                Assert.Equal(ImageFormatKind.Png, info.Format);
                Assert.Equal((int)frames, info.FrameCount);
                Assert.True(info.IsAnimated);
                Assert.Equal(24, info.Width);
                Assert.Equal(16, info.Height);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 普通 PNG（没有 acTL）仍必须是单帧，别把「顺带扫 chunk」变成新的误报源。
        /// </summary>
        [Fact]
        public void Identify_Plain_Png_Still_Reports_Single_Frame()
        {
            var info = Image.Identify(new MemoryStream(EncodePng(9, 7)));
            Assert.Equal(1, info.FrameCount);
            Assert.False(info.IsAnimated);
        }

        /// <summary>
        /// 在 IHDR 之后插入 <c>acTL</c>（长度 8：num_frames + num_plays），CRC 用库里的实现算。
        /// </summary>
        private static byte[] InsertActl(byte[] png, uint frameCount)
        {
            // PNG 结构：8 字节签名 + IHDR（4 长度 + 4 类型 + 13 数据 + 4 CRC）= 33
            const int afterIhdr = 33;

            byte[] chunkData = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunkData, frameCount);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunkData.AsSpan(4), 0);

            byte[] typeAndData = new byte[12];
            "acTL"u8.CopyTo(typeAndData);
            chunkData.CopyTo(typeAndData, 4);

            byte[] chunk = new byte[4 + typeAndData.Length + 4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 8);
            typeAndData.CopyTo(chunk, 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), Crc32.Compute(typeAndData));

            byte[] result = new byte[png.Length + chunk.Length];
            png.AsSpan(0, afterIhdr).CopyTo(result);
            chunk.CopyTo(result, afterIhdr);
            png.AsSpan(afterIhdr).CopyTo(result.AsSpan(afterIhdr + chunk.Length));
            return result;
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
        /// 未支持的格式（TIFF / ICO）即便不提供读写，也要**识别出来并报名字**，
        /// 让调用方区分「文件是 TIFF，本库不支持」与「这根本不是图像」。
        /// </summary>
        [Theory]
        [InlineData("tiff", new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00 }, "TIFF")]
        [InlineData("tiff", new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10 }, "TIFF")]
        [InlineData("ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00 }, "ICO")]
        [InlineData("cur", new byte[] { 0x00, 0x00, 0x02, 0x00, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00 }, "ICO")]
        public void Identify_Recognized_But_Unsupported_Format_Names_Itself(string ext, byte[] header, string expectedName)
        {
            string path = NewTemp("." + ext);
            try
            {
                File.WriteAllBytes(path, header);
                var ex = Assert.Throws<NotSupportedException>(() => Image.Identify(path));
                Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
                Assert.Contains("JPEG", ex.Message, StringComparison.Ordinal); // 顺带给出支持的格式
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 真正无法识别的内容也要把支持的格式列出来（此前只说「无法识别的图像格式」）。
        /// </summary>
        [Fact]
        public void Identify_Unknown_Format_Message_Lists_Supported_Formats()
        {
            string path = NewTemp(".bin");
            try
            {
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("not an image at all"));
                var ex = Assert.Throws<NotSupportedException>(() => Image.Identify(path));
                Assert.Contains("JPEG / PNG / BMP / GIF / WebP", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// <see cref="ImageFrame.Load(Stream, bool)"/> 走同一套嗅探，TIFF 也应报出格式名。
        /// </summary>
        [Fact]
        public void ImageFrame_Load_Tiff_Throws_With_Format_Name()
        {
            var header = new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00 };
            using var ms = new MemoryStream(header);
            var ex = Assert.Throws<NotSupportedException>(() => ImageFrame.Load(ms));
            Assert.Contains("TIFF", ex.Message, StringComparison.Ordinal);
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

        /// <summary>
        /// 不可 Seek 的只读流，并统计被读走的总字节数（用于证明「没有整读」）。
        /// </summary>
        private sealed class CountingNonSeekableStream(byte[] data) : Stream
        {
            private int position;

            public long BytesRead { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(Span<byte> buffer)
            {
                int remaining = data.Length - position;
                if (remaining <= 0) return 0;
                int take = Math.Min(buffer.Length, remaining);
                data.AsSpan(position, take).CopyTo(buffer);
                position += take;
                BytesRead += take;
                return take;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
