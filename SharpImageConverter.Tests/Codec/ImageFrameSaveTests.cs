using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// <see cref="ImageFrame"/> 保存路径的回归：alpha 是否保留、扩展名是否被识别、
    /// 不兼容像素格式是否被拒绝而不是静默产出花屏。
    /// </summary>
    public class ImageFrameSaveTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        /// <summary>
        /// 构造棋盘状 RGBA 缓冲区：偶数格半透明/全透明，奇数格不透明。
        /// </summary>
        private static byte[] CreateRgbaChecker(int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool alt = ((x + y) & 1) == 0;
                    rgba[o + 0] = alt ? (byte)10 : (byte)200;
                    rgba[o + 1] = alt ? (byte)20 : (byte)210;
                    rgba[o + 2] = alt ? (byte)30 : (byte)220;
                    rgba[o + 3] = alt ? (byte)0 : (byte)255;
                }
            }

            return rgba;
        }

        /// <summary>
        /// RGBA32 帧存 PNG 时 alpha 必须保留。此前一律先降成 RGB24，alpha 静默消失。
        /// </summary>
        [Fact]
        public void SaveAsPng_Rgba32_Preserves_Alpha()
        {
            const int w = 8, h = 8;
            byte[] rgba = CreateRgbaChecker(w, h);
            var frame = new ImageFrame(w, h, rgba, ImagePixelFormat.Rgba32);

            string path = NewTemp(".png");
            try
            {
                frame.SaveAsPng(path);
                var loaded = Image.LoadRgba32(path);

                Assert.Equal(w, loaded.Width);
                Assert.Equal(h, loaded.Height);
                // PNG 无损：RGBA 四通道必须逐字节还原
                for (int i = 0; i < rgba.Length; i++)
                {
                    Assert.True(rgba[i] == loaded.Buffer[i],
                        $"第 {i} 字节不一致：写 {rgba[i]}，读 {loaded.Buffer[i]}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// BGRA32 帧存 PNG 时要按 RGBA 字节序写出，不能把 B 当 R。
        /// </summary>
        [Fact]
        public void SaveAsPng_Bgra32_Writes_Rgba_Order()
        {
            const int w = 4, h = 4;
            byte[] rgba = CreateRgbaChecker(w, h);
            byte[] bgra = new byte[rgba.Length];
            SimdHelper.SwapRgbaBgra32(rgba, bgra);
            var frame = new ImageFrame(w, h, bgra, ImagePixelFormat.Bgra32);

            string path = NewTemp(".png");
            try
            {
                frame.SaveAsPng(path);
                var loaded = Image.LoadRgba32(path);
                for (int i = 0; i < rgba.Length; i++)
                {
                    Assert.True(rgba[i] == loaded.Buffer[i],
                        $"第 {i} 字节不一致：期望 {rgba[i]}，读 {loaded.Buffer[i]}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 全不透明的 RGBA32 帧存 PNG 必须与同内容 RGB24 帧**逐字节**一致
        /// （<see cref="Formats.Png.PngWriter.WriteRgba"/> 会自动降级为颜色类型 2），
        /// 否则这次 alpha 修复会让所有不透明图平白变大。
        /// </summary>
        [Fact]
        public void SaveAsPng_Opaque_Rgba32_Matches_Rgb24_Bytes()
        {
            const int w = 16, h = 16;
            var rgb = new byte[w * h * 3];
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                rgb[i * 3 + 0] = (byte)(i * 7);
                rgb[i * 3 + 1] = (byte)(i * 13);
                rgb[i * 3 + 2] = (byte)(i * 29);
                rgba[i * 4 + 0] = rgb[i * 3 + 0];
                rgba[i * 4 + 1] = rgb[i * 3 + 1];
                rgba[i * 4 + 2] = rgb[i * 3 + 2];
                rgba[i * 4 + 3] = 255;
            }

            using var msRgb = new MemoryStream();
            new ImageFrame(w, h, rgb).SaveAsPng(msRgb);
            using var msRgba = new MemoryStream();
            new ImageFrame(w, h, rgba, ImagePixelFormat.Rgba32).SaveAsPng(msRgba);

            BufferAssert.EqualExact(msRgb.ToArray(), msRgba.ToArray());
        }

        /// <summary>
        /// <see cref="ImageFrame.Save"/> 必须认识 .webp（此前落在 default 分支抛异常）。
        /// </summary>
        [Fact]
        public void Save_Webp_Is_Supported_And_Roundtrips()
        {
            const int w = 32, h = 24;
            byte[] rgb = CreateRgbGradient(w, h);
            var frame = new ImageFrame(w, h, rgb);

            string path = NewTemp(".webp");
            try
            {
                frame.Save(path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 12);
                Assert.Equal((byte)'R', bytes[0]);
                Assert.Equal((byte)'I', bytes[1]);
                Assert.Equal((byte)'F', bytes[2]);
                Assert.Equal((byte)'F', bytes[3]);
                Assert.Equal((byte)'W', bytes[8]);
                Assert.Equal((byte)'E', bytes[9]);
                Assert.Equal((byte)'B', bytes[10]);
                Assert.Equal((byte)'P', bytes[11]);

                var loaded = ImageFrame.Load(path);
                Assert.Equal(w, loaded.Width);
                Assert.Equal(h, loaded.Height);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// 带 alpha 的帧存 WebP 时必须走 RGBA 编码路径，alpha 不被抹平。
        /// </summary>
        [Fact]
        public void SaveAsWebp_Rgba32_Preserves_Alpha()
        {
            const int w = 8, h = 8;
            byte[] rgba = CreateRgbaChecker(w, h);
            var frame = new ImageFrame(w, h, rgba, ImagePixelFormat.Rgba32);

            string path = NewTemp(".webp");
            try
            {
                frame.SaveAsWebp(path, 100f);
                var loaded = new WebpDecoderAdapterRgba().DecodeRgba32(path);

                int transparent = 0, opaque = 0;
                for (int i = 3; i < loaded.Buffer.Length; i += 4)
                {
                    if (loaded.Buffer[i] == 0) transparent++;
                    else if (loaded.Buffer[i] == 255) opaque++;
                }

                Assert.True(transparent > 0 && opaque > 0,
                    $"alpha 未保留：透明 {transparent} 个，不透明 {opaque} 个");
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// GIF 单帧编码只接受 RGB24：32 位帧会被按 3 字节/像素错位取样，必须拒绝而不是静默花屏。
        /// </summary>
        [Fact]
        public void GifEncoder_Encode_NonRgb24_Frame_Throws()
        {
            var rgba = CreateRgbaChecker(8, 8);
            var frame = new ImageFrame(8, 8, rgba, ImagePixelFormat.Rgba32);
            using var ms = new MemoryStream();

            var ex = Assert.Throws<ArgumentException>(() => new GifEncoder().Encode(frame, ms));
            Assert.Contains("RGB24", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// <see cref="ImageFrame.SaveAsGif"/> 对非 RGB24 帧仍应正常工作（内部先规范化）。
        /// </summary>
        [Fact]
        public void SaveAsGif_NonRgb24_Still_Works()
        {
            var rgba = CreateRgbaChecker(8, 8);
            var frame = new ImageFrame(8, 8, rgba, ImagePixelFormat.Rgba32);

            using var ms = new MemoryStream();
            frame.SaveAsGif(ms);
            byte[] bytes = ms.ToArray();
            Assert.True(bytes.Length > 13);
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        }

        private static byte[] CreateRgbGradient(int w, int h)
        {
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    rgb[o + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                    rgb[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                    rgb[o + 2] = (byte)((x + y) * 128 / Math.Max(1, w + h - 2));
                }
            }

            return rgb;
        }
    }
}
