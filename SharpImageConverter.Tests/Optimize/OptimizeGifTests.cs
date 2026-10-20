using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpImageConverter.Compression;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// GIF 优化：调色板、帧去重、帧差与透明通道。
    /// </summary>
    public partial class OptimizeTests
    {
        [Fact]
        public void Optimize_StaticGif_KeepsImageValid()
        {
            int w = 64, h = 48;
            var rgb = new byte[w * h * 3];
            var rng = new Random(31);
            rng.NextBytes(rgb);

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input)) new GifEncoder().Encode(new ImageFrame(w, h, rgb), fs);
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);
                Assert.True(File.Exists(output));
                if (!result.KeptOriginal)
                {
                    Assert.True(result.OptimizedSize <= before);
                }
                var decoded = new GifDecoder().DecodeRgb24(output);
                Assert.Equal(w, decoded.Width);
                Assert.Equal(h, decoded.Height);
            }
            finally
            {
                foreach (var f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        [Fact]
        public void Optimize_AnimatedGif_KeepsFrameCount()
        {
            int w = 64, h = 48;
            var frames = new List<ImageFrame>();
            for (int f = 0; f < 3; f++)
            {
                var buf = new byte[w * h * 3];
                for (int p = 0; p < w * h; p++)
                {
                    buf[p * 3] = (byte)((p * 3 + f * 40) % 256);
                    buf[p * 3 + 1] = (byte)((p * 5 + f * 20) % 256);
                    buf[p * 3 + 2] = (byte)((p * 7) % 256);
                }
                frames.Add(new ImageFrame(w, h, buf));
            }

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeAnimation(frames, [100, 100, 100], 0, fs);
                }
                long before = new FileInfo(input).Length;

                var result = ImageOptimizer.Optimize(input, output);

                var animation = new GifDecoder().DecodeAnimationRgb24(output);
                Assert.Equal(3, animation.Frames.Count);
                Assert.Equal(w, animation.Frames[0].Width);
                if (!result.KeptOriginal)
                {
                    Assert.True(result.OptimizedSize <= before, $"{result.OptimizedSize} vs {before}");
                }
            }
            finally
            {
                foreach (var f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        [Fact]
        public void GifEncoder_LocalPalettes_RoundTripsFrames()
        {
            const int w = 4, h = 4;
            byte[] pal0 = [255, 0, 0];
            byte[] pal1 = [0, 255, 0];
            var f0 = new byte[w * h];
            var f1 = new byte[w * h];
            using var ms = new MemoryStream();
            new GifEncoder().EncodeIndexedFrames(
                w, h, globalPalette: null,
                frames: [f0, f1],
                rects: [(0, 0, w, h), (0, 0, w, h)],
                localPalettes: [pal0, pal1],
                frameDurationsMs: [100, 100],
                loopCount: 0,
                transparentIndex: -1,
                disposalMode: 0,
                stream: ms);
            ms.Position = 0;
            var animation = new GifDecoder().DecodeAnimationRgba32(ms);

            Assert.Equal(2, animation.RgbaFrames!.Count);
            Assert.Equal(255, animation.RgbaFrames[0].Buffer[0]);
            Assert.Equal(0, animation.RgbaFrames[0].Buffer[1]);
            Assert.Equal(255, animation.RgbaFrames[0].Buffer[3]);
            Assert.Equal(0, animation.RgbaFrames[1].Buffer[0]);
            Assert.Equal(255, animation.RgbaFrames[1].Buffer[1]);
        }

        [Fact]
        public void Optimize_AnimatedGif_DeduplicatesIdenticalFrames()
        {
            const int w = 64, h = 64;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
            var f0 = new byte[w * h];
            for (int i = 0; i < f0.Length; i++) f0[i] = (byte)(i % 4);
            var f1 = (byte[])f0.Clone();
            var f2 = (byte[])f0.Clone();
            for (int y = 8; y < 24; y++)
            {
                for (int x = 8; x < 24; x++) f2[y * w + x] = 2;
            }

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1, f2], [100, 200, 300], 0, fs);
                }
                var before = new GifDecoder().DecodeAnimationRgba32(input);
                var result = ImageOptimizer.Optimize(input, output,
                    new OptimizeOptions { KeepOriginalWhenNoGain = false, MaxColors = 5 });
                var after = new GifDecoder().DecodeAnimationRgba32(output);

                Assert.False(result.KeptOriginal, result.Reason);
                Assert.Equal(2, before.RgbaFrames!.Count - 1); // 输入 3 帧
                Assert.Equal(2, after.RgbaFrames!.Count);
                Assert.Equal(300, after.FrameDurationsMs[0]);
                for (int i = 0; i < after.RgbaFrames.Count; i++)
                {
                    Assert.True(after.RgbaFrames[i].Buffer.AsSpan().SequenceEqual(before.RgbaFrames[i == 0 ? 0 : 2].Buffer),
                        $"frame {i} / {result.Method}");
                }
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void GifEncoder_Disposal1_TransparentShowsPreviousFrame()
        {
            const int w = 4, h = 4;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 0]; // red, green, transparent slot
            var f0 = new byte[w * h]; // all red
            var f1 = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    f1[y * w + x] = y < 2 ? (byte)1 : (byte)2; // top green, bottom transparent
                }
            }

            using var ms = new MemoryStream();
            new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1], [100, 100], 0, ms,
                transparentIndex: 2, disposalMode: 1);
            ms.Position = 0;
            var animation = new GifDecoder().DecodeAnimationRgb24(ms);

            Assert.Equal(2, animation.Frames.Count);
            var second = animation.Frames[1].Buffer;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    if (y < 2)
                    {
                        Assert.Equal(0, second[o]);
                        Assert.Equal(255, second[o + 1]);
                    }
                    else
                    {
                        Assert.Equal(255, second[o]); // 透明像素保留上一帧的红色
                        Assert.Equal(0, second[o + 1]);
                    }
                }
            }
        }

        [Fact]
        public void Optimize_TransparentAnimatedGif_PreservesAlphaAndFrames()
        {
            const int w = 8, h = 8;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0]; // red, green, blue, transparent slot
            var f0 = new byte[w * h];
            var f1 = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    f0[y * w + x] = x < 4 ? (byte)0 : (byte)3;
                    f1[y * w + x] = x < 4 ? (byte)0 : (byte)1;
                }
            }

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1], [100, 100], 0, fs,
                        transparentIndex: 3, disposalMode: 2);
                }
                var before = new GifDecoder().DecodeAnimationRgba32(input);
                var result = ImageOptimizer.Optimize(input, output,
                    new OptimizeOptions { KeepOriginalWhenNoGain = false, MaxColors = 5 });
                var after = new GifDecoder().DecodeAnimationRgba32(output);

                Assert.False(result.KeptOriginal, $"{result.Reason} / {result.Method}");
                Assert.Equal(before.Frames.Count, after.Frames.Count);
                for (int i = 0; i < before.RgbaFrames!.Count; i++)
                {
                    var expected = before.RgbaFrames[i].Buffer;
                    var actual = after.RgbaFrames![i].Buffer;
                    for (int p = 0; p < expected.Length; p += 4)
                    {
                        Assert.Equal(expected[p + 3], actual[p + 3]);
                        if (expected[p + 3] == 0) continue; // 透明像素的 RGB 不显示
                        Assert.Equal(expected[p], actual[p]);
                        Assert.Equal(expected[p + 1], actual[p + 1]);
                        Assert.Equal(expected[p + 2], actual[p + 2]);
                    }
                }
            }
            finally
            {
                Cleanup(input, output);
            }
        }

        [Fact]
        public void Optimize_AnimatedGif_UsesFrameDiffAndRoundTrips()
        {
            const int w = 32, h = 32;
            byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
            var f0 = new byte[w * h];
            for (int i = 0; i < f0.Length; i++) f0[i] = (byte)(i % 4);
            var f1 = (byte[])f0.Clone();
            for (int y = 8; y < 16; y++)
            {
                for (int x = 8; x < 16; x++) f1[y * w + x] = 2;
            }
            var f2 = (byte[])f0.Clone();

            string input = NewTemp(".gif");
            string output = NewTemp(".gif");
            try
            {
                using (var fs = File.Create(input))
                {
                    new GifEncoder().EncodeIndexed(w, h, palette, [f0, f1, f2], [100, 100, 100], 0, fs);
                }
                long beforeSize = new FileInfo(input).Length;
                var inputAnimation = new GifDecoder().DecodeAnimationRgb24(input);

                var result = ImageOptimizer.Optimize(input, output);

                Assert.False(result.KeptOriginal);
                Assert.True(result.OptimizedSize < beforeSize, $"{result.OptimizedSize} vs {beforeSize}");
                var outputAnimation = new GifDecoder().DecodeAnimationRgb24(output);
                Assert.Equal(inputAnimation.Frames.Count, outputAnimation.Frames.Count);
                for (int i = 0; i < inputAnimation.Frames.Count; i++)
                {
                    Assert.Equal(inputAnimation.Frames[i].Buffer, outputAnimation.Frames[i].Buffer);
                }
            }
            finally
            {
                Cleanup(input, output);
            }
        }
    }
}
