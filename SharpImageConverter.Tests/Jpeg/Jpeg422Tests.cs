using System;
using System.IO;
using System.Security.Cryptography;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// 4:2:2（Y 水平 2 倍下采样）编码与解码路径。
/// </summary>
/// <remarks>
/// 4:2:2 在 1.2.0 引入时只补了编码器，解码侧一直回退到「三个整幅平面 + 标量双线性交织」的通用路径
/// （143 MP 单次重建 ~1.2 s），optimize 每评估一档质量就要付一次。这里守住三件事：
/// 编码器真的写出 2×1 采样因子、SIMD 快速重建与通用路径像素一致到可接受程度、
/// 搜索期的系数直通仍与完整比特流解码逐字节相同。
/// </remarks>
[Collection(JpegSimdStateCollection.Name)]
public class Jpeg422Tests
{
    private static Image<Rgb24> MakeImage(int width, int height)
    {
        var buffer = new byte[width * height * 3];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // 带水平方向高频的图案：4:2:2 的水平色度下采样/上采样最容易在这里露馅。
                buffer[i++] = (byte)((x * 7 + y * 3) & 0xFF);
                buffer[i++] = (byte)((x * 11 ^ (y * 5)) & 0xFF);
                buffer[i++] = (byte)((x * 3 + y * 13) & 0xFF);
            }
        }
        return new Image<Rgb24>(width, height, buffer);
    }

    /// <summary>低频平滑图案：色度接近常数，最近邻与双线性上采样的差别可忽略。</summary>
    private static Image<Rgb24> MakeSmoothImage(int width, int height)
    {
        var buffer = new byte[width * height * 3];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                buffer[i++] = (byte)(x * 200 / Math.Max(1, width - 1) + 20);
                buffer[i++] = (byte)(y * 200 / Math.Max(1, height - 1) + 20);
                buffer[i++] = (byte)((x + y) * 100 / Math.Max(1, width + height - 2) + 30);
            }
        }
        return new Image<Rgb24>(width, height, buffer);
    }

    private static byte[] Encode(Image<Rgb24> image, JpegChromaSubsampling subsampling, int quality)
    {
        using var ms = new MemoryStream();
        JpegEncoder.Encode(image, ms, new JpegEncoderOptions(quality, subsampling, keepMetadata: false));
        return ms.ToArray();
    }

    private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data));

    /// <summary>SOF0 里第一个分量的采样因子（高 4 位水平、低 4 位垂直）。</summary>
    private static byte FirstComponentSampling(byte[] jpeg)
    {
        int i = 2;
        while (i + 3 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) break;
            byte marker = jpeg[i + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }
            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (marker == 0xC0)
            {
                // FF C0 len precision height(2) width(2) count components...
                return jpeg[i + 4 + 1 + 2 + 2 + 1 + 1];
            }
            i += 2 + length;
        }
        throw new InvalidOperationException("未找到 SOF0");
    }

    [Theory]
    [InlineData(120, 80)]
    [InlineData(97, 61)]   // 宽高都不是 16/8 的整数倍，覆盖边界 MCU
    [InlineData(33, 17)]
    public void Encoder_Writes422Sampling_AndIsSmallerThan444(int width, int height)
    {
        var image = MakeImage(width, height);
        byte[] yuv422 = Encode(image, JpegChromaSubsampling.Yuv422, 80);
        byte[] yuv444 = Encode(image, JpegChromaSubsampling.Yuv444, 80);

        Assert.Equal(0x21, FirstComponentSampling(yuv422));
        Assert.Equal(0x11, FirstComponentSampling(yuv444));
        Assert.NotEqual(Md5(yuv444), Md5(yuv422));
        Assert.True(yuv422.Length < yuv444.Length,
            $"4:2:2 应比 4:4:4 小，实际 {yuv422.Length} vs {yuv444.Length}");
    }

    [Theory]
    [InlineData(120, 80, 90)]
    [InlineData(97, 61, 60)]
    [InlineData(33, 17, 90)]
    public void SimdReconstruct_MatchesGenericPath(int width, int height, int quality)
    {
        byte[] jpeg = Encode(MakeSmoothImage(width, height), JpegChromaSubsampling.Yuv422, quality);

        // 默认：整数 IDCT + SIMD 交织（本次新增的 2×1 MCU 快速路径）。
        byte[] fast = JpegDecoder.Decode(jpeg).ToRgb24();
        // 浮点 IDCT 会让 TryDecodeInterleavedYCbCrSimd 直接返回 false，落到通用的双线性交织。
        byte[] generic = JpegDecoder.Decode(jpeg, useFloatingPointIdct: true).ToRgb24();

        Assert.Equal(fast.Length, generic.Length);
        var score = QualityMetrics.Compare(fast, generic, width, height, 3);
        // 差异只来自 IDCT 实现与水平上采样滤波器（最近邻 vs 双线性）；
        // 一旦 MCU 的块索引/行序接错，PSNR 会掉到 20 dB 以下。
        Assert.True(score.Psnr > 35, $"4:2:2 SIMD 重建与通用路径偏差过大: {score}");
        Assert.True(score.Ssim > 0.95, $"4:2:2 SIMD 重建与通用路径结构差异过大: {score}");
    }

    [Theory]
    [InlineData(120, 80, 90)]
    [InlineData(97, 61, 60)]
    public void CoefficientBypass422_MatchesBitstreamDecode(int width, int height, int quality)
    {
        var image = MakeImage(width, height);
        byte[] bitstream = Encode(image, JpegChromaSubsampling.Yuv422, quality);
        byte[] fromBitstream = JpegDecoder.Decode(bitstream).ToRgb24();

        using var sink = new JpegCoefficientSink();
        JpegEncoder.EncodeCoefficients(image.Buffer, image.Width, image.Height, quality,
            JpegChromaSubsampling.Yuv422, keepMetadata: false, metadata: null, sink);
        byte[] fromCoefficients = sink.Reconstruct();

        Assert.Equal(fromBitstream.Length, fromCoefficients.Length);
        Assert.Equal(Md5(fromBitstream), Md5(fromCoefficients));
    }
}
