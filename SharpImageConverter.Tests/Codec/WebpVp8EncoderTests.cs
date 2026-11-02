using Xunit;
using SharpImageConverter.Formats.Webp.Managed;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 托管 VP8 有损编码器的单元测试与一致性测试。
/// 主判据：同一码流在 libwebp 与托管解码器下必须逐像素完全一致（不接受近似阈值）。
/// </summary>
public class WebpVp8EncoderTests
{
    // ==================================================================
    //  T0：bool 编码器 ↔ 解码器互逆
    // ==================================================================

    [Fact]
    public void BoolEncoder_RandomBits_RoundTripsWithDecoder()
    {
        var rng = new Random(1234567);
        const int count = 4096;
        int[] bits = new int[count];
        int[] probs = new int[count];
        int[] probSet = { 1, 8, 64, 128, 192, 250, 254, 255 };

        var enc = new Vp8BoolEncoder(256);
        for (int i = 0; i < count; i++)
        {
            bits[i] = rng.Next(2);
            probs[i] = probSet[rng.Next(probSet.Length)];
            enc.PutBit(bits[i], probs[i]);
        }

        byte[] bytes = enc.Finish();
        Assert.NotEmpty(bytes);

        var dec = new Vp8BoolDecoder(bytes);
        Assert.True(dec.IsValid);
        for (int i = 0; i < count; i++)
            Assert.Equal(bits[i], dec.GetBit(probs[i]));
        Assert.False(dec.Eof);
    }

    [Fact]
    public void BoolEncoder_LongRuns_RoundTrip()
    {
        // 全 0 / 全 1 的长游程会触发大量重归一化与 0xFF 延迟输出，专门覆盖进位路径。
        int[] probs = { 254, 1, 128, 250, 8 };
        foreach (int prob in probs)
        {
            foreach (int bit in new[] { 0, 1 })
            {
                var enc = new Vp8BoolEncoder(64);
                const int count = 8192;
                for (int i = 0; i < count; i++)
                    enc.PutBit(bit, prob);

                byte[] bytes = enc.Finish();
                var dec = new Vp8BoolDecoder(bytes);
                Assert.True(dec.IsValid);
                for (int i = 0; i < count; i++)
                    Assert.Equal(bit, dec.GetBit(prob));
            }
        }
    }

    [Fact]
    public void BoolEncoder_UniformLiterals_RoundTrip()
    {
        var rng = new Random(998877);
        for (int bitCount = 1; bitCount <= 12; bitCount++)
        {
            int[] values = new int[64];
            var enc = new Vp8BoolEncoder(64);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = rng.Next(1 << bitCount);
                enc.PutLiteral(values[i], bitCount);
            }

            byte[] bytes = enc.Finish();
            var dec = new Vp8BoolDecoder(bytes);
            for (int i = 0; i < values.Length; i++)
                Assert.Equal((uint)values[i], dec.GetValue(bitCount));
        }
    }

    // ==================================================================
    //  T0：正向变换定标与逆变换对齐
    // ==================================================================

    [Theory]
    [InlineData(7)]
    [InlineData(-7)]
    [InlineData(0)]
    [InlineData(33)]
    public void ForwardDct_ConstantBlock_DcIsEightTimesValue(int value)
    {
        Span<int> residual = stackalloc int[16];
        residual.Fill(value);
        Span<int> coeffs = stackalloc int[16];
        Vp8ForwardTransform.Dct4x4(residual, coeffs);

        // 伴随变换对常值块的 DC 定标：8×value，其余全 0。
        Assert.Equal(8 * value, coeffs[0]);
        for (int i = 1; i < 16; i++)
            Assert.Equal(0, coeffs[i]);

        // 逆变换的 DC 快路径：dc = 8·value + 4 → (dc >> 3) = value。
        byte[] dst = new byte[16];
        Array.Fill(dst, (byte)128);
        short[] c = new short[16];
        c[0] = (short)coeffs[0];
        Vp8Idct.TransformOne(c, dst, 0, 4);
        for (int i = 0; i < 16; i++)
            Assert.Equal(128 + value, dst[i]);
    }

    [Fact]
    public void ForwardWht_ConstantDc_RoundTripsThroughInverseWht()
    {
        int[] values = { 37, -12, 5 };
        foreach (int d in values)
        {
            Span<int> dc = stackalloc int[16];
            dc.Fill(d);
            Span<int> coeffs = stackalloc int[16];
            Vp8ForwardTransform.Wht4x4(dc, coeffs);

            Assert.Equal(8 * d, coeffs[0]);
            for (int i = 1; i < 16; i++)
                Assert.Equal(0, coeffs[i]);

            short[] input = new short[16];
            for (int i = 0; i < 16; i++)
                input[i] = (short)coeffs[i];
            short[] output = new short[256];
            Vp8Idct.TransformWHT(input, output);

            for (int block = 0; block < 16; block++)
                Assert.Equal(d, (int)output[64 * (block >> 2) + 16 * (block & 3)]);
        }
    }

    [Fact]
    public void ForwardDct_RandomResidual_RoundTripsWithinIntegerRounding()
    {
        var rng = new Random(424242);
        for (int trial = 0; trial < 200; trial++)
        {
            int[] residual = new int[16];
            for (int i = 0; i < 16; i++)
                residual[i] = rng.Next(-28, 29);

            int[] coeffs = new int[16];
            Vp8ForwardTransform.Dct4x4(residual, coeffs);

            short[] c = new short[16];
            byte[] dst = new byte[16];
            Array.Fill(dst, (byte)128);
            for (int i = 0; i < 16; i++)
                c[i] = (short)coeffs[i];
            Vp8Idct.TransformOne(c, dst, 0, 4);

            for (int i = 0; i < 16; i++)
            {
                int expected = 128 + residual[i];
                Assert.InRange((int)dst[i], expected - 3, expected + 3);
            }
        }
    }
}
