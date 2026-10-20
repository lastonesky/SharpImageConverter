using System.Security.Cryptography;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// optimize 搜索期「样本缓存」的不变量：produce 只跟源图有关、与质量档无关，
/// 所以「采一次 + 各档只量化」必须与「每档从头 produce 一遍」得到逐位相同的量化系数。
/// </summary>
/// <remarks>
/// 两个前提各自可独立失效：
/// ① 采样缓存若少存/错存样本（例如电平平移量改了而缓存仍存旧值），系数会偏；
/// ② 重建输出缓冲被跨候选复用后，若重建没有写满每个像素，上一档的像素会残留进来，
///    而残留只会影响画质分数、未必改变选中的质量 —— 只看产物 md5 是抓不到的。
/// 因此这里比的是重建像素的 SHA-256，并且专门跑一次「先低质量、再高质量」的顺序。
/// </remarks>
[Collection(JpegSimdStateCollection.Name)]
public class JpegSampleCacheTests
{
    private static Image<Rgb24> MakeImage(int width, int height)
    {
        var buffer = new byte[width * height * 3];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                buffer[i++] = (byte)((x * 7 + y * 3) & 0xFF);
                buffer[i++] = (byte)((x * 3 + y * 11 + (x * y >> 3)) & 0xFF);
                buffer[i++] = (byte)(((x ^ y) * 5) & 0xFF);
            }
        }
        return new Image<Rgb24>(width, height, buffer);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Theory]
    [InlineData(30, true)]
    [InlineData(51, true)]
    [InlineData(88, true)]
    [InlineData(30, false)]
    [InlineData(51, false)]
    [InlineData(88, false)]
    public void CachedSamplesMatchFullProduce(int quality, bool subsample420)
    {
        // 非 8/16 对齐的尺寸，覆盖右/下边界的复制填充
        var image = MakeImage(203, 147);
        int w = image.Width, h = image.Height;

        using var direct = new JpegCoefficientSink();
        JpegEncoder.EncodeCoefficients(image.Buffer, w, h, quality, subsample420, false, null, direct);
        string expected = Hash(direct.Reconstruct());

        using var cached = new JpegCoefficientSink();
        using var samples = new JpegSampleCache();
        samples.Capture(image.Buffer, w, h, subsample420);
        JpegEncoder.EncodeFromSamples(samples, w, h, subsample420, quality, cached);

        Assert.Equal(expected, Hash(cached.Reconstruct()));
    }

    [Fact]
    public void CacheSwitchesGeometryBetweenSearchRounds()
    {
        // 4:2:0 达标不了时 JpegOptimizer 会整轮重搜 4:4:4：同一个缓存要能从 6 块/MCU 切到 3 块/MCU。
        var image = MakeImage(163, 119);
        int w = image.Width, h = image.Height;

        using var samples = new JpegSampleCache();
        using var sink = new JpegCoefficientSink();
        samples.Capture(image.Buffer, w, h, true);
        JpegEncoder.EncodeFromSamples(samples, w, h, true, 60, sink);
        _ = sink.Reconstruct();

        samples.Capture(image.Buffer, w, h, false);
        JpegEncoder.EncodeFromSamples(samples, w, h, false, 60, sink);
        string afterSwitch = Hash(sink.Reconstruct());

        using var freshSink = new JpegCoefficientSink();
        using var freshCache = new JpegSampleCache();
        freshCache.Capture(image.Buffer, w, h, false);
        JpegEncoder.EncodeFromSamples(freshCache, w, h, false, 60, freshSink);
        Assert.Equal(Hash(freshSink.Reconstruct()), afterSwitch);

        // 切回 4:2:0 也必须重新采样（Matches 会因几何不符返回 false）
        Assert.False(samples.Matches(w, h, true));
        Assert.True(samples.Matches(w, h, false));
    }

    [Fact]
    public void ReusedOutputBufferDoesNotLeakPreviousCandidate()
    {
        var image = MakeImage(197, 131);
        int w = image.Width, h = image.Height;

        // 同一个 sink 连续重建两档：第二档必须与「全新 sink」逐字节相同。
        using var reused = new JpegCoefficientSink();
        using var samples = new JpegSampleCache();
        samples.Capture(image.Buffer, w, h, true);

        JpegEncoder.EncodeFromSamples(samples, w, h, true, 20, reused);
        _ = reused.Reconstruct();

        JpegEncoder.EncodeFromSamples(samples, w, h, true, 92, reused);
        string afterReuse = Hash(reused.Reconstruct());

        using var fresh = new JpegCoefficientSink();
        JpegEncoder.EncodeFromSamples(samples, w, h, true, 92, fresh);
        Assert.Equal(Hash(fresh.Reconstruct()), afterReuse);
    }
}
