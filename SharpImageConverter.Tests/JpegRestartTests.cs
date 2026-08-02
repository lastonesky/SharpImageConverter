using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// 覆盖 DRI / restart interval（RSTn）解码路径。
/// 手工构造的 8x16 灰度 JPEG：DRI=1，两个 MCU 之间插入 RST0/RST1。
/// 每个 MCU 的熵数据为 2 字节全 0（DC=0 单符号表 + EOB 单符号表，各 1 位，消费 2 位后
/// 位缓冲残留恰好 1 整字节）——该场景会命中"扫描/RST 边界必须丢弃缓冲残留位才能
/// 探测到真实 0xFF marker"的缺陷回归。
/// </summary>
public sealed class JpegRestartTests
{
    private static byte[] BuildRestartJpeg()
    {
        var b = new List<byte>(128);
        b.AddRange([0xFF, 0xD8]); // SOI
        b.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00]); // DQT（表 0，全 1）
        for (int i = 0; i < 64; i++) b.Add(1);
        b.AddRange([0xFF, 0xDD, 0x00, 0x04, 0x00, 0x01]); // DRI = 1
        b.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x10, 0x00, 0x08, 0x01, 0x01, 0x11, 0x00]); // SOF0 8x16 灰度
        // DC 表：仅类别 0（码长 1）；AC 表：仅 EOB 0x00（码长 1）
        AddDht(b, dc: true, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0]);
        AddDht(b, dc: false, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0x00]);
        b.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]); // SOS
        // 熵数据：MCU1(00 00) RST0 MCU2(00 00) RST1，之后 EOI
        b.AddRange([0x00, 0x00, 0xFF, 0xD0]);
        b.AddRange([0x00, 0x00, 0xFF, 0xD1]);
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    private static void AddDht(List<byte> b, bool dc, byte[] counts, byte[] symbols)
    {
        b.Add(0xFF);
        b.Add(0xC4);
        int len = 2 + 1 + 16 + symbols.Length;
        b.Add((byte)(len >> 8));
        b.Add((byte)len);
        b.Add(dc ? (byte)0x00 : (byte)0x10);
        b.AddRange(counts);
        b.AddRange(symbols);
    }

    [Fact]
    public void Baseline_RestartInterval_ShouldDecode()
    {
        byte[] data = BuildRestartJpeg();

        var img = JpegDecoder.Decode(data);

        Assert.Equal(8, img.Width);
        Assert.Equal(16, img.Height);
        Assert.All(img.Buffer, px => Assert.Equal(128, px));
    }

    [Fact]
    public async Task Baseline_RestartInterval_Streaming_ShouldMatchNonStreaming()
    {
        byte[] data = BuildRestartJpeg();

        var imgSync = JpegDecoder.Decode(data);
        using var ms = new MemoryStream(data);
        var streamResult = await JpegDecoder.DecodeFromStreamAsync(ms);

        Assert.Equal(imgSync.Width, streamResult.Image.Width);
        Assert.Equal(imgSync.Height, streamResult.Image.Height);
        Assert.Equal(imgSync.Buffer, streamResult.Image.Buffer);
    }
}
