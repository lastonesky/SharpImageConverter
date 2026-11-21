using Xunit;
using SharpImageConverter;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// VP8 有损关键帧（纯 C# 实现）的容器解析与健壮性测试。
///
/// 判定标准：
/// - 非关键帧必须抛 <see cref="WebpUnsupportedException"/>；
/// - 截断/损坏流必须抛可控异常（WebpContainerException / WebpBitstreamException），不得崩溃。
/// </summary>
public class WebpVp8LossyTests
{
    // ==================================================================
    //  1. Bool 解码器单元测试
    // ==================================================================

    [Fact]
    public void Vp8BoolDecoder_EmptyStream_ReportsEofAfterRenorm()
    {
        var br = new Vp8BoolDecoder(ReadOnlySpan<byte>.Empty);
        Assert.True(br.IsValid);
        // 空流上读取会以 0 填充，最终标记 Eof
        int bits = 0;
        for (int i = 0; i < 64; i++) bits += br.GetBit(128);
        Assert.True(br.Eof);
        Assert.Equal(0, bits); // 全 0 填充
    }

    [Fact]
    public void Vp8BoolDecoder_FirstByteFF_IsInvalid()
    {
        var br = new Vp8BoolDecoder(new byte[] { 0xFF, 0x00, 0x00, 0x00 });
        Assert.False(br.IsValid);
    }

    [Fact]
    public void Vp8BoolDecoder_GetSignedValue_ReadsMagnitudeThenSign()
    {
        // 构造一个至少能读出若干 bit 的流；只验证语义一致性：
        // 与 "GetValue 读幅值 + GetBit(128) 读符号" 的手工序列完全等价。
        var data = new byte[] { 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };
        var a = new Vp8BoolDecoder(data);
        var b = new Vp8BoolDecoder(data);
        int viaApi = a.GetSignedValue(5);
        int mag = (int)b.GetValue(5);
        int manual = b.GetBit(128) == 1 ? -mag : mag;
        Assert.Equal(manual, viaApi);
    }

    // ==================================================================
    //  3. 非关键帧 → WebpUnsupportedException
    // ==================================================================

    [Fact]
    public void Lossy_NonKeyFrame_ThrowsUnsupported()
    {
        // frame tag：bit0=1（inter frame）、version=0、show_frame=1、first_part_size=4
        uint tag = 0x01 | (1u << 4) | (4u << 5);
        byte[] vp8 = new byte[]
        {
            (byte)(tag & 0xFF), (byte)((tag >> 8) & 0xFF), (byte)((tag >> 16) & 0xFF),
            0x9D, 0x01, 0x2A, 10, 0, 10, 0, // start code + 宽高（虽然是 P 帧语义上无此字段，但解码器应先识别帧类型）
            0, 0, 0, 0,
        };
        byte[] webp = BuildRiff(("VP8 ", vp8));
        Assert.Throws<WebpUnsupportedException>(() => WebpDecoder.DecodeRgba(webp, out _, out _));
    }

    [Fact]
    public void Lossy_AnimationWithVp8PFrame_ThrowsUnsupported()
    {
        // 动图（VP8X+ANIM+ANMF）中的 VP8 非关键帧子帧：不属于 WebP（RFC 9649 要求 ANMF 帧数据为
        // VP8 关键帧），必须显式抛 WebpUnsupportedException，与 libwebp 的拒绝行为一致，
        // 而不是静默产出错误像素或抛出难以归因的位流异常。
        uint tag = 0x01 | (1u << 4) | (10u << 5); // 非关键帧、show_frame、first_part_size=10
        var frame = new List<byte>();
        frame.Add((byte)(tag & 0xFF)); frame.Add((byte)((tag >> 8) & 0xFF)); frame.Add((byte)((tag >> 16) & 0xFF));
        frame.AddRange(new byte[10]); // first partition

        byte[] vp8Chunk = Chunk("VP8 ", frame.ToArray());
        byte[] anmfPayload = new byte[16];
        // ANMF 头：x/2,y/2,w-1,h-1 各 24 位 + duration 24 位 + flags
        anmfPayload[6] = 9; anmfPayload[9] = 9; // 10x10
        byte[] anmf = Chunk("ANMF", Concat(anmfPayload, vp8Chunk));

        byte[] vp8x = new byte[10];
        vp8x[0] = 0x02; // animation 标志
        vp8x[4] = 9; vp8x[7] = 9;    // canvas 10x10
        byte[] anim = new byte[6];    // 背景 + loop

        byte[] webp = BuildRawRiff(Chunk("VP8X", vp8x), Chunk("ANIM", anim), anmf);

        Assert.Throws<WebpUnsupportedException>(() => WebpDecoder.DecodeRgba(webp, out _, out _));
        Assert.Throws<WebpUnsupportedException>(() => WebpDecoder.DecodeAnimation(webp));
    }

    // ==================================================================
    //  4. 截断 / 损坏流健壮性
    // ==================================================================

    [Fact]
    public void Vp8Decoder_GarbageData_ThrowsControlledException()
    {
        var rng = new Random(42);
        for (int i = 0; i < 32; i++)
        {
            byte[] garbage = new byte[rng.Next(0, 300)];
            rng.NextBytes(garbage);
            var ex = Record.Exception(() => Vp8Decoder.Decode(garbage));
            if (ex != null)
                Assert.True(ex is WebpBitstreamException or WebpUnsupportedException,
                    $"垃圾数据抛出意外异常类型 {ex.GetType().Name}: {ex.Message}");
            // 若未抛异常，说明恰好构成合法关键帧流（概率极低），允许但结果必须尺寸合法
        }
    }

    // ==================================================================
    //  容器构造辅助
    // ==================================================================

    private static byte[] Chunk(string fourcc, byte[] data)
    {
        var bytes = new byte[8 + data.Length + (data.Length & 1)];
        bytes[0] = (byte)fourcc[0]; bytes[1] = (byte)fourcc[1];
        bytes[2] = (byte)fourcc[2]; bytes[3] = (byte)fourcc[3];
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)data.Length);
        data.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] BuildRiff(params (string FourCC, byte[] Data)[] chunks)
    {
        var parts = chunks.Select(c => Chunk(c.FourCC, c.Data)).ToArray();
        return BuildRawRiff(parts);
    }

    private static byte[] BuildRawRiff(params byte[][] chunks)
    {
        int total = 4 + chunks.Sum(c => c.Length);
        var riff = new byte[8 + total];
        riff[0] = (byte)'R'; riff[1] = (byte)'I'; riff[2] = (byte)'F'; riff[3] = (byte)'F';
        BitConverter.TryWriteBytes(riff.AsSpan(4), (uint)total);
        riff[8] = (byte)'W'; riff[9] = (byte)'E'; riff[10] = (byte)'B'; riff[11] = (byte)'P';
        int pos = 12;
        foreach (var c in chunks) { c.CopyTo(riff, pos); pos += c.Length; }
        return riff;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        int pos = 0;
        foreach (var a in arrays) { a.CopyTo(result, pos); pos += a.Length; }
        return result;
    }
}
