using System.Security.Cryptography;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// optimize 搜索期的「系数直通」不变量：跳过熵编解码后重建的像素，
/// 必须与「编成字节流再完整解码」逐字节相同。
/// </summary>
/// <remarks>
/// 这是把 Huffman 从搜索期摘出去的前提。Huffman 只改变系数的编码形式（熵编解码无损），
/// 所以两条路径应当得到同一个系数集合、同一幅重建像素；一旦哪天编码器的量化或写块顺序
/// 与解码器的读取顺序不一致，这个断言会立刻失败 —— 而只看最终产物 md5 未必能发现，
/// 因为选中的质量可能碰巧没变。
/// </remarks>
[Collection(JpegSimdStateCollection.Name)]
public class JpegCoefficientBypassTests
{
    private static Image<Rgb24> MakeImage(int width, int height)
    {
        var buffer = new byte[width * height * 3];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                buffer[i++] = (byte)((x * 5 + y * 3) & 0xFF);
                buffer[i++] = (byte)((x * 2 + y * 7 + (x * y)) & 0xFF);
                buffer[i++] = (byte)((x ^ (y * 3)) & 0xFF);
            }
        }
        return new Image<Rgb24>(width, height, buffer);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    [InlineData(60, false)]
    public void CoefficientBypass_MatchesBitstreamDecode(int quality, bool subsample420)
    {
        // 97×61：宽高都不是 8/16 的整数倍，覆盖边缘 MCU 的补齐块。
        var image = MakeImage(97, 61);

        using var ms = new MemoryStream();
        JpegEncoder.Encode(image, ms, new JpegEncoderOptions(quality, subsample420, keepMetadata: false));
        byte[] bitstream = ms.ToArray();

        using var rms = new MemoryStream(bitstream, false);
        byte[] fromBitstream = new JpegDecoderAdapter().DecodeRgb24(rms).Buffer;

        using var sink = new JpegCoefficientSink();
        JpegEncoder.EncodeCoefficients(image.Buffer, image.Width, image.Height, quality, subsample420, keepMetadata: false, metadata: null, sink);
        byte[] fromCoefficients = sink.Reconstruct();

        Assert.Equal(fromBitstream.Length, fromCoefficients.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(fromBitstream)),
            Convert.ToHexString(SHA256.HashData(fromCoefficients)));
    }

    [Fact]
    public void CoefficientBypass_ReusesBufferAcrossQualities()
    {
        // 同一几何下反复评估不同质量，应复用同一份系数缓冲且每次结果稳定。
        var image = MakeImage(120, 80);
        using var sink = new JpegCoefficientSink();

        byte[] first = [];
        foreach (int q in new[] { 95, 60, 40, 95 })
        {
            JpegEncoder.EncodeCoefficients(image.Buffer, image.Width, image.Height, q, true, keepMetadata: false, metadata: null, sink);
            byte[] rebuilt = sink.Reconstruct();
            if (q == 95)
            {
                if (first.Length == 0) first = rebuilt;
                else Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(rebuilt)));
            }
        }
    }
}
