using System.Text;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// ALPH 头的 preprocessing（level reduction）支持验证。
/// libwebp 的 ALPHInit 只校验 preprocessing ∈ {0,1}，解码阶段不做任何恢复
/// （level reduction 是编码端的有损预处理）；本实现与其保持一致，alpha 平面必须逐像素一致。
/// </summary>
public class WebpAlphPreprocessingTests
{
    [Fact]
    public void Alph_UncompressedLevelReduction_AlphaBitExact()
    {
        const int w = 24, h = 18;
        var alpha = new byte[w * h];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = (byte)(i * 37 + 11); // 覆盖 0..255 全部取值

        var opaque = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            opaque[o] = (byte)(x * 9 + y * 2);
            opaque[o + 1] = (byte)(x * 3 + y * 13);
            opaque[o + 2] = (byte)(x * 7 + y * 5);
            opaque[o + 3] = 255;
        }

        byte[] vp8File = LibWebpTestCodec.EncodeRgba(opaque, w, h, 75f);
        var vp8Container = WebpContainer.Load(vp8File);
        var vp8 = vp8Container.FindChunk(WebpChunkId.Vp8);
        Assert.True(vp8.HasValue);

        // ALPH：compression=0（无压缩）、filter=0、preprocessing=1（level reduction）
        var alph = new byte[1 + alpha.Length];
        alph[0] = 0x10;
        alpha.CopyTo(alph, 1);

        byte[] webp = BuildRiff(
            ("VP8X", Vp8XData(w, h, alpha: true)),
            ("ALPH", alph),
            ("VP8 ", vp8!.Value.GetData(vp8Container.RawData).ToArray()));

        AssertAlphaBitExact(webp, w, h, alpha);
    }

    [Fact]
    public void Alph_CompressedLevelReduction_AlphaBitExact()
    {
        const int w = 28, h = 20;

        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            rgba[o] = (byte)(x * 8 + y);
            rgba[o + 1] = (byte)(y * 11 + x);
            rgba[o + 2] = (byte)(x * y);
            rgba[o + 3] = (byte)((x * 19 + y * 23) & 0xFF);
        }

        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h, 75f);
        var container = WebpContainer.Load(webp);
        Assert.True(container.FindChunk(WebpChunkId.Alph).HasValue, "libwebp 应输出 ALPH chunk");
        Assert.Equal(1, (byte)(container.FindChunk(WebpChunkId.Alph)!.Value.GetData(container.RawData)[0] & 0x03));

        // 只把 preprocessing 改成 1，其余保留了 libwebp 生成的 VP8L 压缩 alpha 数据
        byte[] patched = PatchAlphPreprocessing(webp, 1);

        byte[] expected = LibWebpTestCodec.DecodeRgba(patched, out int ew, out int eh);
        byte[] actual = WebpDecoder.DecodeRgba(patched, out int aw, out int ah);
        Assert.Equal(ew, aw);
        Assert.Equal(eh, ah);
        Assert.Equal(w, aw);
        Assert.Equal(h, ah);
        AssertAlphaEqual(expected, actual, w, h);
    }

    [Fact]
    public void Alph_ReservedPreprocessing_ThrowsBitstreamException()
    {
        const int w = 12, h = 10;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i % 251);
            rgba[i + 1] = (byte)((i * 3) % 253);
            rgba[i + 2] = (byte)((i * 7) % 249);
            rgba[i + 3] = 128;
        }

        byte[] webp = PatchAlphPreprocessing(LibWebpTestCodec.EncodeRgba(rgba, w, h, 75f), 2);
        Assert.Throws<WebpBitstreamException>(
            () => WebpDecoder.DecodeRgba(webp, out _, out _));
    }

    private static void AssertAlphaBitExact(byte[] webp, int w, int h, byte[] expectedAlpha)
    {
        byte[] expected = LibWebpTestCodec.DecodeRgba(webp, out int ew, out int eh);
        byte[] actual = WebpDecoder.DecodeRgba(webp, out int aw, out int ah);
        Assert.Equal(ew, aw);
        Assert.Equal(eh, ah);
        Assert.Equal(w, aw);
        Assert.Equal(h, ah);
        AssertAlphaEqual(expected, actual, w, h);

        // libwebp 不做 level-reduction 的逆运算：解码出的 alpha 就是原始量化值
        for (int i = 0; i < expectedAlpha.Length; i++)
            Assert.Equal(expectedAlpha[i], expected[i * 4 + 3]);
    }

    private static void AssertAlphaEqual(byte[] expected, byte[] actual, int w, int h)
    {
        Assert.Equal(w * h * 4, expected.Length);
        Assert.Equal(w * h * 4, actual.Length);
        int diffs = 0;
        int first = -1;
        for (int i = 0; i < w * h; i++)
        {
            if (expected[i * 4 + 3] != actual[i * 4 + 3])
            {
                if (first < 0) first = i;
                diffs++;
            }
        }
        Assert.True(diffs == 0,
            $"alpha 平面不一致：{diffs}/{w * h} 像素不同，首个在 ({first % w},{(first < 0 ? 0 : first / w)})");
    }

    private static byte[] PatchAlphPreprocessing(byte[] webp, int preprocessing)
    {
        var copy = webp.ToArray();
        int pos = 12; // RIFF(4) + size(4) + WEBP(4)
        while (pos + 8 <= copy.Length)
        {
            string fourcc = Encoding.ASCII.GetString(copy, pos, 4);
            int size = BitConverter.ToInt32(copy, pos + 4);
            if (fourcc == "ALPH")
            {
                int header = pos + 8;
                copy[header] = (byte)((copy[header] & 0xCF) | ((preprocessing & 0x03) << 4));
                return copy;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidOperationException("文件中没有 ALPH chunk");
    }

    private static byte[] Vp8XData(int width, int height, bool alpha)
    {
        var data = new byte[10];
        data[0] = (byte)(alpha ? 0x10 : 0x00);
        WriteUInt24(data, 4, width - 1);
        WriteUInt24(data, 7, height - 1);
        return data;
    }

    private static byte[] BuildRiff(params (string FourCC, byte[] Data)[] chunks)
    {
        int total = 4 + chunks.Sum(c => 8 + c.Data.Length + (c.Data.Length & 1));
        var riff = new byte[8 + total];
        riff[0] = (byte)'R'; riff[1] = (byte)'I'; riff[2] = (byte)'F'; riff[3] = (byte)'F';
        BitConverter.TryWriteBytes(riff.AsSpan(4), (uint)total);
        riff[8] = (byte)'W'; riff[9] = (byte)'E'; riff[10] = (byte)'B'; riff[11] = (byte)'P';
        int pos = 12;
        foreach (var (fourcc, data) in chunks)
        {
            riff[pos] = (byte)fourcc[0];
            riff[pos + 1] = (byte)fourcc[1];
            riff[pos + 2] = (byte)fourcc[2];
            riff[pos + 3] = (byte)fourcc[3];
            BitConverter.TryWriteBytes(riff.AsSpan(pos + 4), (uint)data.Length);
            data.CopyTo(riff, pos + 8);
            pos += 8 + data.Length + (data.Length & 1);
        }
        return riff;
    }

    private static void WriteUInt24(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }
}
