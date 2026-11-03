using SharpImageConverter.Core;
using SharpImageConverter.Formats.Webp;
using Xunit;

namespace SharpImageConverter.Tests.Compat;

[CollectionDefinition("Webp SIMD", DisableParallelization = true)]
public class WebpSimdCollection;

[Collection("Webp SIMD")]
public class WebpSimdTests
{
    [Fact]
    public void ForwardTransforms_MatchScalarForSignedResidualsAndDc()
    {
        var random = new Random(361);
        int[] input = new int[16], expected = new int[16], actual = new int[16];
        for (int n = 0; n < 10000; n++)
        {
            for (int i = 0; i < 16; i++)
                input[i] = n < 512 ? (i == n % 16 ? n - 256 : 0) : random.Next(-255, 256);
            Vp8ForwardTransform.Dct4x4Scalar(input, expected);
            Vp8ForwardTransform.Dct4x4(input, actual);
            Assert.Equal(expected, actual);

            // WHT receives block DC coefficients, whose range exceeds a residual.
            for (int i = 0; i < 16; i++) input[i] = random.Next(-2040, 2041);
            Vp8ForwardTransform.Wht4x4Scalar(input, expected);
            Vp8ForwardTransform.Wht4x4(input, actual);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void StridedLoopFilters_MatchGatherScatterIncludingUntouchedPixels()
    {
        var random = new Random(932);
        bool saved = SimdCompat.ForceScalar;
        int changed = 0;
        try
        {
            for (int iteration = 0; iteration < 500; iteration++)
            foreach (int stride in new[] { 24, 37, 64 })
            foreach (int variant in new[] { 0, 1, 2, 3 })
            {
                var source = new byte[stride * 24];
                int center = random.Next(0, 256);
                for (int i = 0; i < source.Length; i++)
                    source[i] = (byte)Math.Clamp(center + random.Next(-20, 21), 0, 255);
                byte[] expected = (byte[])source.Clone(), actual = (byte[])source.Clone();
                int off = 3 * stride + 8;
                int threshold = random.Next(0, 64), inner = random.Next(0, 64), hev = random.Next(0, 4);
                void Filter(byte[] data)
                {
                    switch (variant)
                    {
                        case 0: Vp8LoopFilter.HFilter16(data, off, stride, threshold, inner, hev); break;
                        case 1: Vp8LoopFilter.HFilter16i(data, off, stride, threshold, inner, hev); break;
                        case 2: Vp8LoopFilter.HFilter16Simple(data, off, stride, threshold); break;
                        case 3: Vp8LoopFilter.HFilter8(data, off, stride, threshold, inner, hev); break;
                    }
                }
                SimdCompat.ForceScalar = true;
                Filter(expected);
                SimdCompat.ForceScalar = false;
                Filter(actual);
                Assert.Equal(expected, actual);
                if (!source.AsSpan().SequenceEqual(actual)) changed++;
            }
        }
        finally { SimdCompat.ForceScalar = saved; }
        Assert.True(changed > 0, "Test inputs must exercise active filtering.");
    }

    [Fact]
    public void YuvUpsampling_RegisterInterleaveMatchesBufferedInterleaveAtBorders()
    {
        var random = new Random(844);
        bool saved = SimdCompat.ForceScalar;
        try
        {
            for (int width = 1; width <= 67; width++)
            for (int height = 1; height <= 7; height++)
            {
                int strideY = width + 3, strideUv = (width + 1) / 2 + 2;
                var y = new byte[strideY * height];
                var u = new byte[strideUv * ((height + 1) / 2)];
                var v = new byte[u.Length];
                random.NextBytes(y); random.NextBytes(u); random.NextBytes(v);
                SimdCompat.ForceScalar = true;
                byte[] expected = Vp8YuvToRgb.ConvertToRgba(y, u, v, width, height, strideY, strideUv);
                SimdCompat.ForceScalar = false;
                byte[] actual = Vp8YuvToRgb.ConvertToRgba(y, u, v, width, height, strideY, strideUv);
                Assert.Equal(expected, actual);
            }
        }
        finally { SimdCompat.ForceScalar = saved; }
    }

}
