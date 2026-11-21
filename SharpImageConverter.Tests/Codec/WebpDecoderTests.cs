using System.Text;
using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 纯 C# WebP 解码器（WebpDecoder）的基础回归测试。
/// 覆盖：LSB 位序、VP8L 帧头、RIFF 容器解析与截断/损坏输入健壮性。
/// </summary>
public class WebpDecoderTests
{
    // ==================================================================
    //  1. 位流读取器：锁定 LSB-first 位序与边界行为
    // ==================================================================

    /// <summary>
    /// 0xA5 = 1010_0101，0x3C = 0011_1100。
    /// LSB-first 逐 bit 顺序应为：1,0,1,0,0,1,0,1, 0,0,1,1,1,1,0,0。
    /// </summary>
    [Fact]
    public void LsbBitReader_ReadBit_FollowsLeastSignificantFirstOrder()
    {
        var data = new byte[] { 0xA5, 0x3C };
        var reader = new LsbBitReader(data);

        int[] expected = { 1, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 0, 0 };
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], reader.ReadBit());

        Assert.Equal(16, reader.TotalBitsRead);
    }

    /// <summary>
    /// ReadBits(n) 把先读到的 bit 放在最低位（不是 MSB 拼装）。
    /// </summary>
    [Fact]
    public void LsbBitReader_ReadBits_PacksFirstBitAsLowestBit()
    {
        var data = new byte[] { 0xA5, 0x3C };

        var r4 = new LsbBitReader(data);
        Assert.Equal(0x5u, r4.ReadBits(4)); // bits 0..3 = 1,0,1,0 → 0b0101

        var r8 = new LsbBitReader(data);
        Assert.Equal(0xA5u, r8.ReadBits(8));

        var r12 = new LsbBitReader(data);
        // 低 8 位 0xA5，随后 4 位来自 0x3C 的低半字节 0xC → 0x0CA5
        Assert.Equal(0x0CA5u, r12.ReadBits(12));
        Assert.Equal(12, r12.TotalBitsRead);
    }

    /// <summary>
    /// ReadSigned 的约定：先读 (nbits-1) 位绝对值，再读 1 位符号（1 = 负）。
    /// </summary>
    [Fact]
    public void LsbBitReader_ReadSigned_UsesTrailingSignBit()
    {
        // bits: b0=1, b1=0 → +1；b2=1, b3=1 → -1
        var reader = new LsbBitReader(new byte[] { 0b0000_1101 });

        Assert.Equal(1, reader.ReadSigned(2));
        Assert.Equal(-1, reader.ReadSigned(2));
        Assert.Equal(4, reader.TotalBitsRead);
    }

    /// <summary>
    /// 读到末尾必须抛可控异常，而不是返回垃圾值或越界。
    /// </summary>
    [Fact]
    public void LsbBitReader_ReadPastEnd_ThrowsControlled()
    {
        var reader = new LsbBitReader(new byte[] { 0xFF });
        for (int i = 0; i < 8; i++)
            Assert.Equal(1, reader.ReadBit());

        AssertReadBitThrows(reader);
        AssertReadBitsThrows(new LsbBitReader(new byte[] { 0xFF }), 9);
    }

    private static void AssertReadBitThrows(LsbBitReader reader)
    {
        bool threw = false;
        try { reader.ReadBit(); }
        catch (WebpBitstreamException) { threw = true; }
        Assert.True(threw, "越过位流末尾的 ReadBit 应抛出 WebpBitstreamException");
    }

    private static void AssertReadBitsThrows(LsbBitReader reader, int count)
    {
        bool threw = false;
        try { reader.ReadBits(count); }
        catch (WebpBitstreamException) { threw = true; }
        Assert.True(threw, $"越过位流末尾的 ReadBits({count}) 应抛出 WebpBitstreamException");
    }

    // ==================================================================
    //  2. VP8L 帧头：signature(8) + width-1(14) + height-1(14) + alpha(1) + version(3)
    // ==================================================================

    /// <summary>
    /// 手工按规范构造 5 字节帧头（32×48，alpha=1，version=0），验证解析结果。
    /// 这是对"width 与 height 之间不得有任何填充位"的直接锁定：
    /// 若 height 的 14 位读取窗口偏移 2 位，48 会被读成 48&lt;&lt;2 之类的值。
    /// </summary>
    [Fact]
    public void Vp8LHeader_HandBuiltBytes_ParsesExactDimensions()
    {
        byte[] header = BuildVp8LFrameHeader(32, 48, alphaUsed: true);
        byte[] webp = BuildRiff(("VP8L", header));

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8L, container.Kind);
        Assert.Equal(32, container.CanvasWidth);
        Assert.Equal(48, container.CanvasHeight);
        Assert.True(container.HasAlpha);
        Assert.False(container.IsAnimation);
    }

    [Fact]
    public void Vp8LHeader_HandBuiltBytes_AlphaZero()
    {
        byte[] header = BuildVp8LFrameHeader(1, 1, alphaUsed: false);
        byte[] webp = BuildRiff(("VP8L", header));

        var container = WebpContainer.Load(webp);
        Assert.Equal(1, container.CanvasWidth);
        Assert.Equal(1, container.CanvasHeight);
        Assert.False(container.HasAlpha);
    }

    // ==================================================================
    //  4. RIFF 容器解析
    // ==================================================================

    [Fact]
    public void Container_InvalidRiff_Throws()
    {
        Assert.Throws<WebpContainerException>(
            () => WebpContainer.Load(new byte[] { 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Container_EmptyData_Throws()
    {
        Assert.Throws<WebpContainerException>(() => WebpContainer.Load(Array.Empty<byte>()));
    }

    [Fact]
    public void Container_TruncatedHeader_Throws()
    {
        Assert.Throws<WebpContainerException>(() => WebpContainer.Load(new byte[] { 0x52, 0x49, 0x46, 0x46 }));
    }

    [Fact]
    public void Vp8LReservedPredictorModes_FallBackToBlackLikeLibWebp()
    {
        Assert.Equal(0xFF000000u, Vp8LTransforms.Predictor(14, 0x11223344u, 0x55667788u, 0xAABBCCDDu, 0xEEFF0011u));
        Assert.Equal(0xFF000000u, Vp8LTransforms.Predictor(15, 0x11223344u, 0x55667788u, 0xAABBCCDDu, 0xEEFF0011u));
    }

    // ==================================================================
    //  5. 截断 / 损坏文件健壮性
    // ==================================================================

    [Fact]
    public void Decode_TruncatedVp8L_BarelyValidHeader_Throws()
    {
        // 有效 RIFF + VP8L 头（9 字节），但后面的位流为空
        byte[] bad = new byte[21];
        bad[0] = 0x52; bad[1] = 0x49; bad[2] = 0x46; bad[3] = 0x46;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(bad, 4);
        bad[8] = 0x57; bad[9] = 0x45; bad[10] = 0x42; bad[11] = 0x50;
        bad[12] = (byte)'V'; bad[13] = (byte)'P'; bad[14] = (byte)'8'; bad[15] = (byte)'L';
        BitConverter.GetBytes(10u).CopyTo(bad, 16);
        bad[20] = 0x2F; // signature

        Assert.ThrowsAny<WebpException>(() => WebpDecoder.DecodeRgba(bad, out _, out _));
    }

    // ==================================================================
    //  构造辅助
    // ==================================================================

    /// <summary>
    /// 按 VP8L 规范手工构造 5 字节帧头：
    /// signature(8)=0x2F → width-1(14) → height-1(14) → alpha_is_used(1) → version(3)=0。
    /// 字段之间没有任何填充位。
    /// </summary>
    private static byte[] BuildVp8LFrameHeader(int width, int height, bool alphaUsed)
    {
        var writer = new LsbBitWriter();
        writer.WriteBits(0x2F, 8);
        writer.WriteBits(width - 1, 14);
        writer.WriteBits(height - 1, 14);
        writer.WriteBits(alphaUsed ? 1 : 0, 1);
        writer.WriteBits(0, 3);
        return writer.ToArray();
    }

    /// <summary>按 RIFF/WEBP 规范封装若干 chunk（奇数长度自动补 0 填充）。</summary>
    private static byte[] BuildRiff(params (string fourCc, byte[] data)[] chunks)
    {
        int total = 12;
        foreach (var chunk in chunks)
            total += 8 + chunk.data.Length + (chunk.data.Length & 1);

        var result = new byte[total];
        result[0] = (byte)'R'; result[1] = (byte)'I'; result[2] = (byte)'F'; result[3] = (byte)'F';
        BitConverter.GetBytes(total - 8).CopyTo(result, 4);
        result[8] = (byte)'W'; result[9] = (byte)'E'; result[10] = (byte)'B'; result[11] = (byte)'P';

        int pos = 12;
        foreach (var chunk in chunks)
        {
            Encoding.ASCII.GetBytes(chunk.fourCc).CopyTo(result, pos);
            BitConverter.GetBytes(chunk.data.Length).CopyTo(result, pos + 4);
            chunk.data.CopyTo(result, pos + 8);
            pos += 8 + chunk.data.Length;

            if ((chunk.data.Length & 1) != 0) result[pos++] = 0; // RIFF 填充字节
        }
        return result;
    }

    /// <summary>测试用的 LSB-first 位写入器（与 LsbBitReader 的读取约定互逆）。</summary>
    private sealed class LsbBitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bitPos;

        public void WriteBit(int bit)
        {
            if (_bitPos == 0) _bytes.Add((byte)0);
            if ((bit & 1) != 0) _bytes[^1] |= (byte)(1 << _bitPos);
            _bitPos = (_bitPos + 1) & 7;
        }

        public void WriteBits(int value, int count)
        {
            for (int i = 0; i < count; i++)
                WriteBit((value >> i) & 1);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
