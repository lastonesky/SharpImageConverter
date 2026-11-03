using SharpImageConverter.Formats.Webp;
using Xunit;

namespace SharpImageConverter.Tests.Codec;

public class WebpBoolNormalizationTests
{
    [Fact]
    public void DecoderMatchesBitwiseReferenceAcrossRefillsAndTruncatedInputs()
    {
        var random = new Random(624);
        for (int length = 0; length < 128; length++)
        {
            var data = new byte[length + 6];
            random.NextBytes(data);
            var decoder = new Vp8BoolDecoder(data, 3, length);
            uint value = length >= 1 ? (uint)data[3] << 8 : 0;
            if (length >= 2) value |= data[4];
            uint range = 255;
            int position = 3 + Math.Min(2, length), bits = 0;
            bool eof = false;
            for (int i = 0; i < 4000; i++)
            {
                int probability = i < 256 ? i : random.Next(256);
                uint split = 1 + (((range - 1) * (uint)probability) >> 8);
                int expected = value >= split << 8 ? 1 : 0;
                if (expected == 1) { value -= split << 8; range -= split; }
                else range = split;
                while (range < 128)
                {
                    value = (value << 1) & 0xffff;
                    range <<= 1;
                    if (++bits == 8)
                    {
                        bits = 0;
                        if (position < 3 + length) value |= data[position++];
                        else eof = true;
                    }
                }
                Assert.Equal(expected, decoder.GetBit(probability));
                Assert.Equal(eof, decoder.Eof);
            }
        }
    }

    [Fact]
    public void EncoderRoundTripsAllProbabilitiesIncludingEndpoints()
    {
        var random = new Random(741);
        int[] probabilities = new int[20000], values = new int[20000];
        var encoder = new Vp8BoolEncoder();
        // VP8 starts with a zero marker bit, keeping the initial byte below 0xff.
        encoder.PutBit(0, 128);
        for (int i = 0; i < values.Length; i++)
        {
            probabilities[i] = i % 256;
            values[i] = random.Next(2);
            encoder.PutBit(values[i], probabilities[i]);
        }
        byte[] data = encoder.Finish();
        var decoder = new Vp8BoolDecoder(data);
        Assert.Equal(0, decoder.GetBit(128));
        for (int i = 0; i < values.Length; i++)
            Assert.Equal(values[i], decoder.GetBit(probabilities[i]));
    }
}
