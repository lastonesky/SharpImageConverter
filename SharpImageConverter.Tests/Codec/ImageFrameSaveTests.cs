using System;
using System.IO;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
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

        /// <summary>
        /// 动画编码与单帧编码同一类前提：逐帧都按 3 字节/像素切缓冲区，
        /// 32 位帧必须被拒绝（此前 <c>EncodeAnimation</c> 完全不校验）。
        /// </summary>
        [Fact]
        public void GifEncoder_EncodeAnimation_NonRgb24_Frame_Throws()
        {
            var rgb = CreateRgbGradient(8, 8);
            var rgba = CreateRgbaChecker(8, 8);
            var frames = new[]
            {
                new ImageFrame(8, 8, rgb, ImagePixelFormat.Rgb24),
                new ImageFrame(8, 8, rgba, ImagePixelFormat.Rgba32),
            };

            using var ms = new MemoryStream();
            var ex = Assert.Throws<ArgumentException>(() => new GifEncoder().EncodeAnimation(frames, [100, 100], 0, ms));
            Assert.Contains("第 1 帧", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, ms.Length); // 入口就拒绝，不产出半个文件
        }

        /// <summary>
        /// GIF 只有一份逻辑屏幕尺寸，尺寸不一的帧会被按首帧宽高重新解释（花屏），入口必须拒绝。
        /// </summary>
        [Fact]
        public void GifEncoder_EncodeAnimation_SizeMismatch_Throws()
        {
            var rgb8 = CreateRgbGradient(8, 8);
            var rgb4 = CreateRgbGradient(4, 4);
            var frames = new[]
            {
                new ImageFrame(8, 8, rgb8, ImagePixelFormat.Rgb24),
                new ImageFrame(4, 4, rgb4, ImagePixelFormat.Rgb24),
            };

            using var ms = new MemoryStream();
            var ex = Assert.Throws<ArgumentException>(() => new GifEncoder().EncodeAnimation(frames, [100, 100], 0, ms));
            Assert.Contains("首帧 8x8", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 帧与时长数量不一致时，旧实现靠索引越界兜底；现在应当在入口给出可读的异常。
        /// </summary>
        [Fact]
        public void GifEncoder_EncodeAnimation_DurationCountMismatch_Throws()
        {
            var rgb = CreateRgbGradient(8, 8);
            var frames = new[]
            {
                new ImageFrame(8, 8, rgb, ImagePixelFormat.Rgb24),
                new ImageFrame(8, 8, rgb, ImagePixelFormat.Rgb24),
            };

            using var ms = new MemoryStream();
            var ex = Assert.Throws<ArgumentException>(() => new GifEncoder().EncodeAnimation(frames, [100], 0, ms));
            Assert.Contains("时长数", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 合法输入（同尺寸 RGB24 + 时长对齐）仍应正常产出 GIF89a 动画。
        /// </summary>
        [Fact]
        public void GifEncoder_EncodeAnimation_Valid_Frames_Writes_Header()
        {
            var rgb = CreateRgbGradient(8, 8);
            var frames = new[]
            {
                new ImageFrame(8, 8, rgb, ImagePixelFormat.Rgb24),
                new ImageFrame(8, 8, rgb, ImagePixelFormat.Rgb24),
                new ImageFrame(8, 8, rgb, ImagePixelFormat.Rgb24),
            };

            using var ms = new MemoryStream();
            new GifEncoder().EncodeAnimation(frames, [100, 100, 100], 0, ms);

            byte[] bytes = ms.ToArray();
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
            Assert.Equal(0x3B, bytes[^1]); // trailer
        }

        /// <summary>
        /// 帧保存与类型化保存现在共用同一条链路（<see cref="Configuration.Save(ImageFrame, Stream, string, SaveOptions?)"/>）：
        /// 同一份像素 + 同一份元数据，两条 API 写 .jpg 必须逐字节相同。
        /// </summary>
        [Fact]
        public void SaveAsJpeg_Default_Keeps_Metadata_Like_Typed_Path()
        {
            // 用带 EXIF 的真实语料：帧路径此前两参重载走 JpegEncoder.Write 的默认
            // keepMetadata:false，帧存 JPEG 静默丢 EXIF，而类型化保存保留——同一次「保存」两种口径。
            string src = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "5_star_base.jpg");
            if (!File.Exists(src)) return; // 语料缺失时跳过（与本目录其他用例一致）

            var frame = ImageFrame.Load(src);
            Assert.True(frame.Metadata.ExifRaw is { Length: > 0 }, "语料应当带 EXIF，否则本用例失去意义");

            using var kept = new MemoryStream();
            frame.SaveAsJpeg(kept, 75);
            using var dropped = new MemoryStream();
            frame.SaveAsJpeg(dropped, 75, subsample420: true, keepMetadata: false);
            Assert.True(kept.Length > dropped.Length,
                $"默认应保留 EXIF：保留 {kept.Length} 字节，丢弃 {dropped.Length} 字节");

            var typed = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels, frame.Metadata);
            using var typedMs = new MemoryStream();
            Configuration.Default.SaveRgb24(typed, typedMs, ".jpg");
            BufferAssert.EqualExact(typedMs.ToArray(), kept.ToArray());
        }

        /// <summary>
        /// <see cref="SaveOptions.Jpeg"/> 必须真的传到编码器：同质量下开 trellis 的产物必然不同。
        /// 此前 <see cref="ImageFrame.SaveAsJpeg(string, int)"/> 只能表达质量与子采样，
        /// trellis / 优化 Huffman 这类参数从帧侧根本传不进去。
        /// </summary>
        [Fact]
        public void SaveOptions_Jpeg_Reaches_Encoder()
        {
            byte[] rgb = CreateRgbGradient(64, 48);
            var frame = new ImageFrame(64, 48, rgb);

            using var plain = new MemoryStream();
            Configuration.Default.Save(frame, plain, ".jpg",
                new SaveOptions { Jpeg = new JpegEncoderOptions(80, true, true, false) });
            using var trellis = new MemoryStream();
            Configuration.Default.Save(frame, trellis, ".jpg",
                new SaveOptions { Jpeg = new JpegEncoderOptions(80, true, true, false, optimizeHuffman: false, trellisQuantization: true) });

            Assert.NotEqual(plain.ToArray(), trellis.ToArray());
        }

        /// <summary>
        /// 逐次保存传的 WebP 质量不能写回 <see cref="Configuration.Default"/> 里的共享编码器：
        /// 否则一次 q10 保存会污染同进程后续所有默认保存。
        /// </summary>
        [Fact]
        public void SaveOptions_Webp_Does_Not_Pollute_Shared_Encoder()
        {
            byte[] rgb = CreateRgbGradient(64, 48);
            var frame = new ImageFrame(64, 48, rgb);

            using var before = new MemoryStream();
            Configuration.Default.Save(frame, before, ".webp");
            using var low = new MemoryStream();
            Configuration.Default.Save(frame, low, ".webp", new SaveOptions { WebpQuality = 10f });
            using var after = new MemoryStream();
            Configuration.Default.Save(frame, after, ".webp");

            BufferAssert.EqualExact(before.ToArray(), after.ToArray());
            Assert.True(low.Length < before.Length,
                $"q10 应小于默认 q75：{low.Length} vs {before.Length}");
        }

        /// <summary>
        /// 未注册的扩展名要给出可操作消息（附已注册扩展名列表），而不是字典异常。
        /// </summary>
        [Fact]
        public void Save_Frame_Unregistered_Extension_Throws_With_Hint()
        {
            var frame = new ImageFrame(4, 4, CreateRgbGradient(4, 4));
            using var ms = new MemoryStream();
            var ex = Assert.Throws<NotSupportedException>(() => Configuration.Default.Save(frame, ms, ".foo"));
            Assert.Contains("不支持的输出格式", ex.Message, StringComparison.Ordinal);
            Assert.Contains(".png", ex.Message, StringComparison.Ordinal);
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
