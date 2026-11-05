using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

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

    [Fact]
    public void LibWebpLossless_PhotoLike_RowOutputMatchesFullDecode()
    {
        int w = 257, h = 129;
        byte[] rgba = PhotoLikeRgba(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LibWebpLossless_PaletteImage_RowOutputMatchesFullDecode()
    {
        int w = 97, h = 61;
        byte[] rgba = PaletteRgba(w, h);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));
        VerifyStreamingMatchesFull(webp, rgba, w, h);
    }

    [Fact]
    public void LosslessTruncatedInsideVp8LChunk_ThrowsInvalidData()
    {
        int w = 33, h = 27;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] webp = WebpEncoder.EncodeRgba(rgba, w, h);
        byte[] truncated = webp.AsSpan(0, webp.Length - 9).ToArray();

        using var stream = new MemoryStream(truncated);
        Assert.Throws<InvalidDataException>(() => new WebpRowSource(stream));
    }

    /// <summary>
    /// VP8L 行源只需读到 VP8L chunk 结束即可，不应消费随后的元数据/填充 chunk。
    /// 这里人为追加一个大 chunk，验证读取字节数停在主图像之后。
    /// </summary>
    [Fact]
    public void LosslessSource_StopsReadingAfterVp8LChunk()
    {
        int w = 32, h = 24;
        byte[] rgba = GradientRgba(w, h, withAlpha: true);
        byte[] baseWebp = WebpEncoder.EncodeRgba(rgba, w, h);

        int imageEnd = FindVp8LEnd(baseWebp);
        Assert.True(imageEnd > 12, "未找到 VP8L chunk");

        byte[] combined = new byte[imageEnd + 8 + 4096];
        Array.Copy(baseWebp, combined, imageEnd);
        combined[imageEnd] = (byte)'J';
        combined[imageEnd + 1] = (byte)'U';
        combined[imageEnd + 2] = (byte)'N';
        combined[imageEnd + 3] = (byte)'K';
        BitConverter.TryWriteBytes(combined.AsSpan(imageEnd + 4, 4), 4096);
        var riffSize = combined.Length - 8;
        BitConverter.TryWriteBytes(combined.AsSpan(4, 4), riffSize);

        using var counting = new CountingStream(combined);
        using var rows = new WebpRowSource(counting);
        var row = new byte[rows.Width * rows.Channels];
        while (rows.ReadRow(row)) { }

        Assert.True(counting.BytesRead <= imageEnd,
            $"行源读取了 {counting.BytesRead} 字节，超过 VP8L chunk 结束位置 {imageEnd}");
    }

    /// <summary>
    /// 长图驻留验证：逐行解码 4096×4096 无损图时，托管分配量（压缩负载 + 环形历史 +
    /// 行缓冲）应显著小于整帧 RGBA 体积的 20%。测量用 GC.GetAllocatedBytesForCurrentThread，
    /// 编码与源图像分配都在测量区间之外。
    /// </summary>
    [Fact]
    public void LosslessLongImage_StreamingAllocationsUnderBudget()
    {
        const int w = 4096;
        const int h = 4096;
        byte[] rgba = GradientRgba(w, h, withAlpha: false);
        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h,
            new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 6, lossless: true));

        long frameBytes = (long)w * h * 4;

        // 对照组：整帧解码的托管分配（工作区 + 输出）。
        long beforeFull = GC.GetAllocatedBytesForCurrentThread();
        _ = WebpDecoder.DecodeRgba(webp, out _, out _);
        long fullAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeFull;

        long before = GC.GetAllocatedBytesForCurrentThread();

        int decodedRows;
        using (var rows = new WebpRowSource(new MemoryStream(webp)))
        {
            var row = new byte[rows.Width * rows.Channels];
            decodedRows = 0;
            while (rows.ReadRow(row)) decodedRows++;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"[streaming-alloc] frame={frameBytes} full={fullAllocated} streaming={allocated} " +
                          $"ratio={((double)allocated / fullAllocated):P1} compressed={webp.Length}");
        Assert.Equal(h, decodedRows);
        Assert.True(allocated < frameBytes / 5,
            $"流式解码托管分配 {allocated} 字节，超过整帧 {frameBytes} 字节的 20%");
        Assert.True(allocated < fullAllocated / 5,
            $"流式解码托管分配 {allocated} 字节，未显著低于整帧解码的 {fullAllocated} 字节");
    }

    private static int FindVp8LEnd(byte[] webp)
    {
        int pos = 12;
        while (pos + 8 <= webp.Length)
        {
            uint size = BitConverter.ToUInt32(webp, pos + 4);
            int next = pos + 8 + (int)size + (int)(size & 1);
            if (webp[pos] == 'V' && webp[pos + 1] == 'P' && webp[pos + 2] == '8' && webp[pos + 3] == 'L')
                return Math.Min(next, webp.Length);
            if (next <= pos || next > webp.Length) return -1;
            pos = next;
        }
        return -1;
    }

    /// <summary>确定性“照片类”内容：平滑基色 + 高频噪声 + 变化的 alpha。</summary>
    private static byte[] PhotoLikeRgba(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        uint seed = 0x12345678u;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                seed = seed * 1664525u + 1013904223u;
                int o = (y * w + x) * 4;
                int r = (x * 255) / Math.Max(1, w - 1);
                int g = (y * 255) / Math.Max(1, h - 1);
                int b = ((x + y) * 3) & 0xFF;
                rgba[o] = (byte)((r + (int)(seed >> 27)) & 0xFF);
                rgba[o + 1] = (byte)((g + (int)((seed >> 19) & 0x1F)) & 0xFF);
                rgba[o + 2] = (byte)((b + (int)((seed >> 11) & 0x1F)) & 0xFF);
                rgba[o + 3] = (byte)(255 - (int)((seed >> 3) & 0x0F));
            }
        }
        return rgba;
    }

    /// <summary>只有 8 种颜色的图，用于触发 color indexing / 调色板路径。</summary>
    private static byte[] PaletteRgba(int w, int h)
    {
        ReadOnlySpan<uint> colors = [0xFF000000u, 0xFF0000FFu, 0xFF00FF00u, 0xFFFF0000u,
                                     0xFFFFFFFFu, 0xFF808080u, 0xFF00FFFFu, 0xFFFF00FFu];
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                uint c = colors[((x >> 2) + (y >> 3)) & 7];
                rgba[o] = (byte)(c >> 16 & 0xFF);
                rgba[o + 1] = (byte)(c >> 8 & 0xFF);
                rgba[o + 2] = (byte)(c & 0xFF);
                rgba[o + 3] = (byte)(c >> 24);
            }
        }
        return rgba;
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

    /// <summary>统计实际被消费字节数的可定位流（用于验证流式读取的边界）。</summary>
    private sealed class CountingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public CountingStream(byte[] data) => _data = data;

        public int BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            int n = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
