using System.Text;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// ALPH 四种过滤方式（none / horizontal / vertical / gradient）的逆滤波验证。
///
/// 背景：libwebp 的 alpha 过滤是**跨行**的——<c>HorizontalUnfilter_C</c> 的 pred 初值
/// 取上一行首像素，<c>VerticalUnfilter_C</c> 在首行直接退化为 horizontal。曾经的实现
/// 把两种过滤都写成了「按行独立、从 0 起算」，于是 cwebp 默认（alpha_filtering=fast）
/// 产出的垂直过滤流整幅 alpha 都会被解错（实测 256×200 图上 12750/51200 像素错，Δ 最大 255），
/// 而自家编码器只写 filter=0，往返测试发现不了。
///
/// 本文件用「按 libwebp 前向语义自造 ALPH 流」的方式覆盖全部 4 档：前两档同时验证
/// 测试台本身（与 libwebp 解码结果一致），gradient 档 libwebp 自己极少选用，
/// 只能这样黑盒覆盖。
/// </summary>
public class WebpAlphFilterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Alph_AllFilters_AlphaBitExactAgainstLibWebp(int filter)
    {
        const int w = 129, h = 77;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            rgba[o] = (byte)(x * 2);
            rgba[o + 1] = (byte)(y * 3);
            rgba[o + 2] = (byte)(x ^ y);
            rgba[o + 3] = (byte)((x * 2 + y * 3) & 0xFF);
        }

        byte[] webp = BuildAlphaFile(rgba, w, h, filter);

        byte[] expected = LibWebpTestCodec.DecodeRgba(webp, out int ew, out int eh);
        byte[] actual = WebpDecoder.DecodeRgba(webp, out int aw, out int ah);
        Assert.Equal(w, ew);
        Assert.Equal(h, eh);
        Assert.Equal(ew, aw);
        Assert.Equal(eh, ah);

        // libwebp 用前向过滤 + 逆过滤还原出原始 alpha；两者都必须逐像素等于源 alpha
        for (int i = 0; i < w * h; i++)
        {
            Assert.Equal(rgba[i * 4 + 3], expected[i * 4 + 3]);
            Assert.Equal(rgba[i * 4 + 3], actual[i * 4 + 3]);
        }
    }

    /// <summary>
    /// 反向验证：libwebp 真实产出的 ALPH（alpha_filtering=fast/best 会选到 horizontal 或
    /// vertical）必须能逐像素还原。这一条不依赖我们自己构造的位流。
    /// </summary>
    [Fact]
    public void Alph_LibWebpProducedAlpha_MatchesLibWebp()
    {
        const int w = 64, h = 48;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            rgba[o] = (byte)(x * 4);
            rgba[o + 1] = (byte)(y * 5);
            rgba[o + 2] = (byte)(x + y);
            rgba[o + 3] = (byte)(y * 255 / (h - 1));
        }

        byte[] webp = LibWebpTestCodec.EncodeRgba(rgba, w, h, 75f);
        var container = WebpContainer.Load(webp);
        var alph = container.FindChunk(WebpChunkId.Alph);
        Assert.True(alph.HasValue, "libwebp 应输出 ALPH chunk");
        int filter = (alph!.Value.GetData(container.RawData)[0] >> 2) & 0x03;

        byte[] expected = LibWebpTestCodec.DecodeRgba(webp, out _, out _);
        byte[] actual = WebpDecoder.DecodeRgba(webp, out _, out _);
        for (int i = 0; i < w * h; i++)
        {
            Assert.True(expected[i * 4 + 3] == actual[i * 4 + 3],
                $"filter={filter} 时 alpha 在第 {i} 个像素不一致：libwebp={expected[i * 4 + 3]} ours={actual[i * 4 + 3]}");
        }
    }

    /// <summary>按 libwebp filters_enc.c 的前向过滤语义造 ALPH 流（filter 位写进头部）。</summary>
    private static byte[] BuildAlphaFile(byte[] rgba, int w, int h, int filter)
    {
        var alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++) alpha[i] = rgba[i * 4 + 3];

        byte[] filtered = ForwardFilter(alpha, w, h, filter);

        // comp=1（VP8L）压缩过滤后的平面，再把 filter 位改到头部
        byte[] alph = AlphEncoder.Encode(filtered, w, h);
        alph[0] = (byte)((alph[0] & 0x03) | (filter << 2));

        var opaque = (byte[])rgba.Clone();
        for (int i = 0; i < w * h; i++) opaque[i * 4 + 3] = 255;
        var vp8Container = WebpContainer.Load(LibWebpTestCodec.EncodeRgba(opaque, w, h, 75f));
        var vp8 = vp8Container.FindChunk(WebpChunkId.Vp8);
        Assert.True(vp8.HasValue);

        return BuildRiff(
            ("VP8X", Vp8XData(w, h)),
            ("ALPH", alph),
            ("VP8 ", vp8!.Value.GetData(vp8Container.RawData).ToArray()));
    }

    private static byte[] ForwardFilter(byte[] data, int width, int height, int filter)
    {
        var result = new byte[data.Length];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowStart + x;
                int pred;
                if (filter == 0)
                {
                    pred = 0;
                }
                else if (y == 0)
                {
                    pred = x == 0 ? 0 : data[rowStart + x - 1]; // 首行一律退化为 horizontal
                }
                else
                {
                    int top = data[rowStart - width + x];
                    int left = x == 0 ? 0 : data[idx - 1];
                    int topLeft = x == 0 ? 0 : data[rowStart - width + x - 1];
                    pred = filter switch
                    {
                        1 => x == 0 ? data[rowStart - width] : data[idx - 1],
                        2 => top,
                        _ => Math.Clamp(left + top - topLeft, 0, 255),
                    };
                }
                result[idx] = (byte)(data[idx] - pred);
            }
        }
        return result;
    }

    private static byte[] Vp8XData(int width, int height)
    {
        var data = new byte[10];
        data[0] = 0x10; // alpha 标志
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
            var id = Encoding.ASCII.GetBytes(fourcc);
            id.CopyTo(riff, pos);
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
