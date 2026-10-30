using Xunit;
using System;
using System.IO;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// 格式注册表的行为约定：<see cref="Configuration.Register"/> 注册的东西必须真的被
    /// Load/Save 用上，缺失解码器时不能把 KeyNotFoundException 漏给调用方。
    /// 回归口径见 docs/Review-2026-10-30.md §1.3。
    /// </summary>
    public class ConfigurationRegistryTests
    {
        private sealed class FooFormat : IImageFormat
        {
            public string Name => "FOO";
            public string[] Extensions => [".foo"];
            public bool IsMatch(Stream s) => true; // 故意永远匹配：便于观察按扩展名/按内容的分派
        }

        private sealed class FooDecoder : IImageDecoder
        {
            public Image<Rgb24> DecodeRgb24(string path) => throw new NotSupportedException();
            public Image<Rgb24> DecodeRgb24(Stream stream)
                => new(2, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        }

        private sealed class FooEncoder : IImageEncoder
        {
            public static readonly byte[] Payload = [0x46, 0x4F, 0x4F];
            public void EncodeRgb24(string path, Image<Rgb24> image) => File.WriteAllBytes(path, Payload);
            public void EncodeRgb24(Stream stream, Image<Rgb24> image) => stream.Write(Payload);
        }

        /// <summary>只有 Rgba32 解码器：LoadRgb24 应降通道复用而不是报错。</summary>
        private sealed class RgbaOnlyFormat : IImageFormat
        {
            public string Name => "RGBAONLY";
            public string[] Extensions => [".rgbaonly"];
            public bool IsMatch(Stream s) => true;
        }

        private sealed class RgbaOnlyDecoder : IImageDecoderRgba
        {
            public Image<Rgba32> DecodeRgba32(string path) => throw new NotSupportedException();
            public Image<Rgba32> DecodeRgba32(Stream stream)
                => new(1, 1, [10, 20, 30, 0]); // alpha=0 应被丢弃
        }

        private static string TempPath(string ext)
            => Path.Combine(Path.GetTempPath(), "sic-registry-" + Guid.NewGuid().ToString("N") + ext);

        [Fact]
        public void Registered_Encoder_Is_Used_By_Save()
        {
            var cfg = new Configuration();
            cfg.Register(new FooFormat(), new FooDecoder(), new FooEncoder());
            string path = TempPath(".foo");
            try
            {
                cfg.SaveRgb24(new Image<Rgb24>(2, 2, new byte[12]), path);
                Assert.Equal(FooEncoder.Payload, File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Registered_Format_Without_Decoder_Is_Skipped_Not_Thrown_As_KeyNotFound()
        {
            var cfg = new Configuration();
            cfg.RegisterFormat(new FooFormat()); // 只登记格式，没有解码器
            string path = TempPath(".bin");
            File.WriteAllBytes(path, [9, 9, 9, 9]);
            try
            {
                // 关键：异常类型必须是领域异常，不能是 Dictionary 的 KeyNotFoundException
                Assert.Throws<NotSupportedException>(() => cfg.LoadRgb24(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Rgba_Only_Format_Still_Serves_LoadRgb24_By_Dropping_Alpha()
        {
            // 注意：Register() 要求同时给 IImageDecoder（Rgb24），无法表达"只有 RGBA 解码器"，
            // 因此这里用 RegisterFormat + RegisterDecoderRgba 组合。
            var cfg = new Configuration();
            cfg.RegisterFormat(new RgbaOnlyFormat());
            cfg.RegisterDecoderRgba<RgbaOnlyFormat>(new RgbaOnlyDecoder());
            string path = TempPath(".rgbaonly");
            File.WriteAllBytes(path, [1, 2, 3]);
            try
            {
                var img = cfg.LoadRgb24(path);
                Assert.Equal(new byte[] { 10, 20, 30 }, img.Buffer);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Unregistered_Extension_Throws_NotSupported()
        {
            var cfg = new Configuration();
            var ex = Assert.Throws<NotSupportedException>(
                () => cfg.SaveRgb24(new Image<Rgb24>(2, 2, new byte[12]), TempPath(".tiff")));
            Assert.Contains(".tiff", ex.Message);
        }

        [Fact]
        public void Normalize_Returns_Clamped_Copy_And_Leaves_Input_Untouched()
        {
            var options = new OptimizeOptions { TargetQuality = 999, MaxColors = -5 };

            var normalized = OptimizeOptions.Normalize(options);

            Assert.NotSame(options, normalized);
            Assert.Equal(999, options.TargetQuality);
            Assert.Equal(-5, options.MaxColors);
            Assert.Equal(100, normalized.TargetQuality);
            Assert.Equal(2, normalized.MaxColors);
        }
    }
}
