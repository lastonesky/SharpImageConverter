using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Streaming;

/// <summary>
/// 纯 C# WebP 流式逐行解码（<see cref="WebpRowSource"/>）验收：
/// 逐行结果与整图解码逐像素一致；短读流 / 截断流 / 并发场景行为正确。
/// </summary>
public class WebpStreamingDecodeTests
{
    private static string NewTemp(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);

    [Fact]
    public void LosslessWithAlpha_RowOutputMatchesFullDecode()
    {
        int w = 37, h = 29;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LossyWithAlpha_RowOutputMatchesFullDecode()
    {
        int w = 41, h = 27;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 80f);
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LossyOpaque_RowOutputMatchesFullDecode_AndUsesThreeChannels()
    {
        int w = 33, h = 25;
        byte[] rgba = GradientRgba(w, h, withAlpha: false);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 80f);
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void ShortReadNonSeekableStream_ProducesSameRows()
    {
        int w = 35, h = 23;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
        byte[] expected = WebpDecoder.DecodeRgba(webp, out int ew, out int eh);
        Assert.Equal(w, ew);
        Assert.Equal(h, eh);

        using var shortStream = new ShortReadStream(webp, maxPerRead: 3);
        using var rows = new WebpRowSource(shortStream);
        Assert.Equal(w, rows.Width);
        Assert.Equal(h, rows.Height);
        Assert.Equal(4, rows.Channels);

        var row = new byte[rows.Width * rows.Channels];
        for (int y = 0; y < h; y++)
        {
            Assert.True(rows.ReadRow(row));
            Assert.Equal(expected.AsSpan(y * w * 4, w * 4).ToArray(), row);
        }
        Assert.False(rows.ReadRow(row));
    }

    [Fact]
    public void TruncatedStream_ThrowsInvalidData()
    {
        int w = 20, h = 16;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeLossyRgba(rgba, w, h, 75f);
        byte[] truncated = webp.AsSpan(0, webp.Length / 2).ToArray();

        using var stream = new MemoryStream(truncated);
        Assert.Throws<InvalidDataException>(() => new WebpRowSource(stream));
    }

    [Fact]
    public void EmptyStream_ThrowsInvalidData()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => new WebpRowSource(stream));
    }

    [Fact]
    public void RowBufferTooSmall_ThrowsArgumentException()
    {
        int w = 8, h = 8;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        using var rows = new WebpRowSource(new MemoryStream(webp));
        var tooSmall = new byte[rows.Width * rows.Channels - 1];
        Assert.Throws<ArgumentException>(() => rows.ReadRow(tooSmall));
    }

    [Fact]
    public void ConcurrentInstances_AreIsolated()
    {
        int w = 30, h = 20;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        byte[] expected = WebpDecoder.DecodeRgba(webp, out _, out _);

        string path = NewTemp(".webp");
        try
        {
            File.WriteAllBytes(path, webp);

            var outcomes = new bool[8];
            Parallel.For(0, outcomes.Length, i =>
            {
                using var rows = new WebpRowSource(path);
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Assert.True(rows.ReadRow(row));
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        if (row[x * 4] != expected[o] || row[x * 4 + 1] != expected[o + 1]
                            || row[x * 4 + 2] != expected[o + 2] || row[x * 4 + 3] != expected[o + 3])
                        {
                            outcomes[i] = false;
                            return;
                        }
                    }
                }
                outcomes[i] = true;
            });

            Assert.All(outcomes, ok => Assert.True(ok, "并发流式解码结果与整图解码不一致"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnimatedWebp_IsRejectedExplicitly()
    {
        int w = 20, h = 16;
        var frame = new WebpAnimationFrameInput(new byte[w * h * 4], 50);
        byte[] webp = WebpAnimationWriter.Encode(new[] { frame }, w, h, 0,
            new WebpAnimationEncodeOptions(Lossless: true));

        using var stream = new MemoryStream(webp);
        Assert.Throws<WebpUnsupportedException>(() => new WebpRowSource(stream));
    }

    [Fact]
    public void IsSupported_DetectsWebpByHeader()
    {
        byte[] rgba = GradientRgba(12, 9, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, 12, 9);
        string path = NewTemp(".webp");
        string bogus = NewTemp(".bin");
        try
        {
            File.WriteAllBytes(path, webp);
            File.WriteAllBytes(bogus, new byte[] { 1, 2, 3, 4 });
            Assert.True(WebpRowSource.IsSupported(path));
            Assert.False(WebpRowSource.IsSupported(bogus));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(bogus)) File.Delete(bogus);
        }
    }

    private static void VerifyStreamingMatchesFull(byte[] webp, byte[] sourceRgba, int w, int h)
    {
        byte[] expected = WebpDecoder.DecodeRgba(webp, out int ew, out int eh);
        Assert.Equal(w, ew);
        Assert.Equal(h, eh);

        using var rows = new WebpRowSource(new MemoryStream(webp));
        Assert.Equal(w, rows.Width);
        Assert.Equal(h, rows.Height);

        var row = new byte[rows.Width * rows.Channels];
        for (int y = 0; y < h; y++)
        {
            Assert.True(rows.ReadRow(row));
            for (int x = 0; x < w; x++)
            {
                int full = (y * w + x) * 4;
                int r = x * rows.Channels;
                Assert.True(row[r] == expected[full] && row[r + 1] == expected[full + 1]
                    && row[r + 2] == expected[full + 2],
                    $"y={y} x={x}: RGB 行输出与整图解码不一致");
                if (rows.Channels == 4)
                    Assert.True(row[r + 3] == expected[full + 3], $"y={y} x={x}: alpha 行输出不一致");
            }
        }
        Assert.False(rows.ReadRow(row));
    }

    private static byte[] GradientRgba(int w, int h, bool withAlpha)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                rgba[o] = (byte)(x * 255 / Math.Max(1, w - 1));
                rgba[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                rgba[o + 2] = (byte)((x * 3 + y * 5) & 0xFF);
                rgba[o + 3] = withAlpha ? (byte)((x * 7 + y * 11 + 30) & 0xFF) : (byte)255;
            }
        }
        return rgba;
    }

    /// <summary>每次 Read 最多返回 maxPerRead 字节的非可定位流，用于验证短读处理。</summary>
    private sealed class ShortReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _maxPerRead;
        private int _position;

        public ShortReadStream(byte[] data, int maxPerRead)
        {
            _data = data;
            _maxPerRead = maxPerRead;
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
            if (_position >= _data.Length) return 0;
            int n = Math.Min(Math.Min(count, _maxPerRead), _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
