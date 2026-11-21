using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// ALPH 四种过滤方式（none / horizontal / vertical / gradient）的逆滤波验证。
///
/// 背景：libwebp 的 alpha 过滤是**跨行**的——<c>HorizontalUnfilter_C</c> 的 pred 初值
/// 取上一行首像素，<c>VerticalUnfilter_C</c> 在首行直接退化为 horizontal。曾经的实现
/// 把两种过滤都写成了「按行独立、从 0 起算」，于是 cwebp 默认（alpha_filtering=fast）
/// 产出的垂直过滤流整幅 alpha 都会被解错（实测 256×200 图上 12750/51200 像素错，Δ 最大 255），
/// 而自家编码器只写 filter=0，往返测试发现不了。
/// </summary>
public class WebpAlphFilterTests
{
    /// <summary>
    /// 编码端 <see cref="AlphEncoder.ApplyFilter"/> 必须与本文件里独立写的前向滤波逐字节一致，
    /// 且「前向滤波 → VP8L → 逆滤波」对 4 种方式都能还原出原 alpha。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Alph_ForwardFilter_MatchesReferenceAndRoundTrips(int filter)
    {
        const int w = 131, h = 69;
        var alpha = new byte[w * h];
        var rnd = new Random(20261103 + filter);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            // 混合平坦块、渐变与随机跳变，覆盖行首/列首的退化分支
            int v = ((x / 7 + y / 5) % 3 == 0) ? (x * 2 + y) & 0xFF : rnd.Next(256);
            alpha[y * w + x] = (byte)v;
        }

        var ours = new byte[alpha.Length];
        AlphEncoder.ApplyFilter(alpha, ours, w, h, filter);
        var reference = ForwardFilter(alpha, w, h, filter);
        for (int i = 0; i < alpha.Length; i++)
        {
            Assert.True(reference[i] == ours[i],
                $"filter={filter} 第 {i} 字节（x={i % w}, y={i / w}）：参考={reference[i]} 实现={ours[i]}");
        }

        var enc = AlphEncoder.Encode(alpha, w, h, filter);
        Assert.Equal(filter, (enc[0] >> 2) & 0x03);
        var dec = AlphDecoder.Decode(enc, w, h);
        for (int i = 0; i < alpha.Length; i++)
            Assert.True(alpha[i] == dec[i], $"filter={filter} 往返第 {i} 像素：源={alpha[i]} 解出={dec[i]}");
    }

    /// <summary>自动选择：低色数平面必须落回 none（libwebp 同口径），高色数平滑平面必须选到滤波。</summary>
    [Fact]
    public void Alph_AutoSelector_FollowsLibWebpRule()
    {
        const int w = 128, h = 96;

        // 2 色（0/255）条纹：≤16 色 ⇒ none
        var twoColor = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            twoColor[y * w + x] = (byte)((x / 8) % 2 == 0 ? 0 : 255);
        Assert.Equal(0, (AlphEncoder.Encode(twoColor, w, h)[0] >> 2) & 0x03);

        // 柔和径向渐变（alpha 遮罩的典型形态）：>16 色且未小到触发「不值得试排」的闸门，
        // 滤波必须胜过 none，且体积确实更小。
        const int rw = 256, rh = 256;
        var radial = new byte[rw * rh];
        for (int y = 0; y < rh; y++)
        for (int x = 0; x < rw; x++)
        {
            double dx = x - rw / 2.0, dy = y - rh / 2.0;
            int v = (int)(255 - Math.Sqrt(dx * dx + dy * dy) * 1.4);
            radial[y * rw + x] = (byte)Math.Clamp(v, 0, 255);
        }
        var auto = AlphEncoder.Encode(radial, rw, rh);
        var none = AlphEncoder.Encode(radial, rw, rh, 0);
        Assert.True(auto.Length < none.Length,
            $"径向渐变平面的自动选择应小于 none：auto={auto.Length} none={none.Length}");
        var dec = AlphDecoder.Decode(auto, rw, rh);
        Assert.Equal(radial, dec);
    }

    /// <summary>按 libwebp filters_enc.c 的前向过滤语义造 ALPH 流（filter 位写进头部）。</summary>
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
}
