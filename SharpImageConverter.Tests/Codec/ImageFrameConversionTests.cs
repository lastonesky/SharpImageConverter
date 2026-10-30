using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Tests.Helpers;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// 两个图像模型之间的转换：<see cref="Image{TPixel}.ToFrame"/> 与
    /// <see cref="ImageFrame.ToImage{T}"/>。契约是**零拷贝且不换格式**——
    /// 需要换格式（含 BGR 系）时由调用方显式调用 <c>ToRgb24()</c> / <c>ToRgba32()</c>。
    /// </summary>
    public class ImageFrameConversionTests
    {
        private static Image<Rgb24> Rgb(int w, int h)
        {
            var buf = new byte[w * h * 3];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i * 37);
            return new Image<Rgb24>(w, h, buf);
        }

        private static Image<Rgba32> Rgba(int w, int h)
        {
            var buf = new byte[w * h * 4];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i * 53);
            return new Image<Rgba32>(w, h, buf);
        }

        /// <summary>
        /// <see cref="Image{TPixel}.ToFrame"/> 只包装缓冲区：像素数组与元数据实例都共享，
        /// 否则每次跨模型就是一次全图拷贝（143 MP 约 430 MB）。
        /// </summary>
        [Fact]
        public void ToFrame_Shares_Buffer_And_Metadata()
        {
            var image = Rgb(8, 6);
            image.Metadata.HorizontalDpi = 144;

            var frame = image.ToFrame();

            Assert.Equal(8, frame.Width);
            Assert.Equal(6, frame.Height);
            Assert.Equal(ImagePixelFormat.Rgb24, frame.PixelFormat);
            Assert.Same(image.Buffer, frame.Pixels);
            Assert.Same(image.Metadata, frame.Metadata);
        }

        [Fact]
        public void ToFrame_Rgba32_Maps_To_Rgba32()
        {
            var frame = Rgba(4, 4).ToFrame();
            Assert.Equal(ImagePixelFormat.Rgba32, frame.PixelFormat);
        }

        /// <summary>
        /// 灰度没有对应的 <see cref="ImagePixelFormat"/>：必须显式拒绝，
        /// 不能悄悄扩成 RGB24 让调用方以为还是灰度。
        /// </summary>
        [Fact]
        public void ToFrame_Gray8_Throws()
        {
            var gray = new Image<Gray8>(4, 4, new byte[16]);
            var ex = Assert.Throws<NotSupportedException>(() => gray.ToFrame());
            Assert.Contains("Gray8", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 反向转换同样零拷贝：<see cref="ImageFrame.ToImage{T}"/> 的 Buffer 就是帧的 Pixels。
        /// </summary>
        [Fact]
        public void ToImage_Shares_Buffer_And_Metadata()
        {
            var frame = new ImageFrame(5, 3, new byte[5 * 3 * 3]);
            frame.Metadata.VerticalDpi = 96;

            var image = frame.ToImage<Rgb24>();

            Assert.Same(frame.Pixels, image.Buffer);
            Assert.Same(frame.Metadata, image.Metadata);
            Assert.Equal(5, image.Width);
            Assert.Equal(3, image.Height);
        }

        /// <summary>
        /// BGR 系帧没有对应的像素类型，必须报「先规范化」而不是把 B 当 R 交出去。
        /// </summary>
        [Theory]
        [InlineData(ImagePixelFormat.Bgr24)]
        [InlineData(ImagePixelFormat.Bgra32)]
        public void ToImage_Rejects_Bgr_Formats(ImagePixelFormat format)
        {
            int bpp = format == ImagePixelFormat.Bgr24 ? 3 : 4;
            var frame = new ImageFrame(4, 4, new byte[4 * 4 * bpp], format);

            var ex = Assert.Throws<InvalidOperationException>(() => frame.ToImage<Rgb24>());
            Assert.Contains("ToRgb24()", ex.Message, StringComparison.Ordinal);

            var rgbaEx = Assert.Throws<InvalidOperationException>(() => frame.ToImage<Rgba32>());
            Assert.Contains("ToRgba32()", rgbaEx.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ToImage_Gray8_Throws()
        {
            var frame = new ImageFrame(4, 4, new byte[4 * 4 * 3]);
            var ex = Assert.Throws<NotSupportedException>(() => frame.ToImage<Gray8>());
            Assert.Contains("Gray8", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 往返一次像素逐字节不变，且中间没有换库（<see cref="Image{TPixel}"/> 与帧的缓冲区是同一块）。
        /// </summary>
        [Fact]
        public void RoundTrip_Preserves_Pixels()
        {
            var image = Rgba(7, 5);
            byte[] original = (byte[])image.Buffer.Clone();

            var back = image.ToFrame().ToImage<Rgba32>();

            BufferAssert.EqualExact(original, back.Buffer);
            Assert.Same(image.Buffer, back.Buffer);
        }

        /// <summary>
        /// 转换后再保存，应与直接走类型化保存逐字节一致——这正是 H-1 收口后
        /// 「两条模型共用一条保存链路」的可观测形式。
        /// </summary>
        [Fact]
        public void Save_After_Conversion_Matches_Typed_Save()
        {
            var image = Rgb(16, 12);

            using var typed = new MemoryStream();
            Image.Save(image, typed, "png");
            using var converted = new MemoryStream();
            image.ToFrame().SaveAsPng(converted);

            BufferAssert.EqualExact(typed.ToArray(), converted.ToArray());
        }
    }
}
