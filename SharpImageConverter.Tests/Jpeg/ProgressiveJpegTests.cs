using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SharpImageConverter;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// 渐进式 JPEG：多次扫描的熵数据必须被完整重建，且流式（异步）解码与内存解码逐字节一致。
/// </summary>
[Collection(JpegSimdStateCollection.Name)]
public sealed class ProgressiveJpegTests
{
    /// <summary>
    /// 默认用例：320×240 渐进图，覆盖"多扫描 + 流式读取"两条路径，毫秒级。
    /// </summary>
    [Fact]
    public async Task ProgressiveJpeg_StreamDecode_Matches_NonStreaming()
    {
        byte[] data = BuildSmallProgressiveJpeg();

        var imgSync = JpegDecoder.Decode(data);
        using var ms = new MemoryStream(data);
        var streamResult = await JpegDecoder.DecodeFromStreamAsync(ms);

        Assert.Equal(imgSync.Width, streamResult.Image.Width);
        Assert.Equal(imgSync.Height, streamResult.Image.Height);
        Assert.Equal(Hash(imgSync.ToRgb24()), Hash(streamResult.Image.ToRgb24()));
    }

    /// <summary>
    /// 真实样本（10650×13426，143 MP）上的同一一致性检查。
    /// </summary>
    /// <remarks>
    /// 单次解码约 3 s，两次比对约 6 s，占整个套件的三分之一，故归入 <c>Slow</c>：
    /// 日常跑 <c>dotnet test</c> 默认仍会执行；只想跑快测时用
    /// <c>--filter "Category!=Slow"</c>。保留它的理由是大图渐进扫描的系数缓冲分配
    /// （143 MP 全部系数常驻）只有这个尺寸能压到。
    /// </remarks>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ProgressiveJpeg_StreamDecode_Matches_NonStreaming_HugeSample()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "progressive.jpg");
        byte[] data = File.ReadAllBytes(path);

        var imgSync = JpegDecoder.Decode(data);
        using var ms = new MemoryStream(data);
        var streamResult = await JpegDecoder.DecodeFromStreamAsync(ms);

        Assert.Equal(imgSync.Width, streamResult.Image.Width);
        Assert.Equal(imgSync.Height, streamResult.Image.Height);
        Assert.Equal(Hash(imgSync.ToRgb24()), Hash(streamResult.Image.ToRgb24()));
    }

    /// <summary>
    /// 由基线编码 + 无损渐进转换生成小样本，避免把 143 MP 的例图复制进测试输出。
    /// </summary>
    private static byte[] BuildSmallProgressiveJpeg()
    {
        const int width = 320, height = 240;
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 3;
                rgb[o] = (byte)((x * 255) / (width - 1));
                rgb[o + 1] = (byte)((y * 255) / (height - 1));
                rgb[o + 2] = (byte)(((x >> 3) + (y >> 3)) % 2 == 0 ? 30 : 220);
            }
        }

        using var ms = new MemoryStream();
        JpegEncoder.Encode(new Image<Rgb24>(width, height, rgb), ms,
            new JpegEncoderOptions(85, subsample420: true, keepMetadata: false, enableDiagnostics: false));
        byte[] progressive = JpegProgressiveTranscoder.TryTranscode(ms.ToArray())
            ?? throw new InvalidOperationException("基线 JPEG 无法转成渐进式");
        return progressive;
    }

    private static byte[] Hash(byte[] data)
    {
        return SHA256.HashData(data);
    }
}
