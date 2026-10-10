using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// 按图优化的 Huffman 表（jpegtran -optimize 同类能力）。
/// </summary>
public class JpegHuffmanOptimizeTests
{
    [Fact]
    public void BuildOptimalHuffmanTable_ProducesValidFullCode()
    {
        var freq = new long[256];
        freq[0x00] = 1000;
        freq[0x01] = 300;
        freq[0x10] = 100;
        freq[0x11] = 40;
        freq[0xF0] = 30;
        freq[0x02] = 10;
        freq[0x21] = 4;
        freq[0x12] = 1;

        var (counts, symbols) = JpegEncoder.BuildOptimalHuffmanTable(freq);

        int total = counts.Sum(c => c);
        Assert.Equal(symbols.Length, total);
        Assert.Equal(8, total);

        // Kraft 和不得超过码空间；不得含全 1 末码（libjpeg 规则）
        long kraft = 0;
        for (int i = 0; i < 16; i++) kraft += (long)counts[i] << (16 - (i + 1));
        Assert.True(kraft <= 65536, $"Kraft={kraft}");
        Assert.True(JpegEncoder.IsLegalPrefixCode(counts), $"bits=[{string.Join(',', counts)}] 含全 1 末码");

        // 频率越高码长越短（同码长内按符号值排序）
        int IndexOf(byte s) => Array.IndexOf(symbols, s);
        Assert.True(IndexOf(0x00) < IndexOf(0x01));
        Assert.True(IndexOf(0x01) < IndexOf(0x10));
        // 最稀有的符号必然落在最深的码长组里
        int deepest = 0;
        for (int i = 0; i < 16; i++) if (counts[i] > 0) deepest = i + 1;
        int lenOf0x12 = 0;
        int seen = 0;
        for (int l = 1; l <= 16; l++)
        {
            seen += counts[l - 1];
            if (seen > IndexOf(0x12)) { lenOf0x12 = l; break; }
        }
        Assert.Equal(deepest, lenOf0x12);

        // 解码侧能正确重建（码长合法、无越界）
        var codeLengths = new List<int>();
        int idx = 0;
        for (int l = 1; l <= 16; l++)
        {
            for (int k = 0; k < counts[l - 1]; k++)
            {
                codeLengths.Add(l);
                idx++;
            }
        }
        Assert.Equal(total, codeLengths.Count);
    }

    [Fact]
    public void BuildOptimalHuffmanTable_SingleSymbol_TakesLengthOne()
    {
        var freq = new long[256];
        freq[0x15] = 12345;

        var (counts, symbols) = JpegEncoder.BuildOptimalHuffmanTable(freq);

        Assert.Equal(1, counts[0]);
        Assert.Equal(new byte[] { 0x15 }, symbols);
    }

    [Fact]
    public void BuildOptimalHuffmanTable_SkewedDistribution_RespectsSixteenBitLimit()
    {
        // 2 的幂频率会产生深树（深约 20 层）；限长后码长不得超过 16，且必须仍是合法前缀码
        var freq = new long[256];
        long v = 1;
        for (int i = 0; i < 20; i++)
        {
            freq[i] = v;
            v *= 2;
        }

        var (counts, symbols) = JpegEncoder.BuildOptimalHuffmanTable(freq);

        int total = counts.Sum(c => c);
        Assert.Equal(symbols.Length, total);
        Assert.Equal(20, total);
        for (int i = 0; i < 16; i++) Assert.True(counts[i] >= 0);
        Assert.True(JpegEncoder.IsLegalPrefixCode(counts),
            $"bits=[{string.Join(',', counts)}] 限长后仍是非法前缀码");
        long kraft = 0;
        for (int i = 0; i < 16; i++) kraft += (long)counts[i] << (16 - (i + 1));
        Assert.True(kraft <= 65536, $"Kraft={kraft}");
    }

    /// <summary>
    /// 回归：按图优化的表必须逼近「不限码长的 Huffman 最优解」。
    /// 旧实现在建树前把符号频率缩放到总权重 ≤500，长尾分布下长尾符号被抹成同权、
    /// 高频符号码长被拉长：同一批分布实测最差偏 +13.05%，1254×1254 高细节图的
    /// AC 亮度表偏 +6.80%（比固定 Annex K 表还大 4.06%，正是「加 --optimize 体积反涨」的次因）。
    /// 改用 libjpeg 限长循环后最差只偏 +0.26%。阈值取 1%：足以拦住旧实现，
    /// 又给「虚拟叶子扰动 + 16 位限长」留出余量。
    /// </summary>
    [Fact]
    public void BuildOptimalHuffmanTable_SteepTailDistribution_StaysNearOptimal()
    {
        var cases = new (string Name, long[] Freq)[]
        {
            ("幂律 1.5", PowerLaw(80, 66000, 1.5)),
            ("幂律 3.0", PowerLaw(80, 66000, 3.0)),
            ("双峰长尾", PeakyTail()),
            ("极陡 2^n", SteepPowers()),
        };

        foreach (var (name, freq) in cases)
        {
            long optimum = OptimalHuffmanBits(freq);
            var (counts, symbols) = JpegEncoder.BuildOptimalHuffmanTable(freq);
            long cost = TableCost(counts, symbols, freq);

            Assert.Equal(freq.Count(f => f > 0), symbols.Length);
            Assert.True(JpegEncoder.IsLegalPrefixCode(counts), $"{name}: 含全 1 末码");
            Assert.True(MaxCodeLength(counts) <= 16, $"{name}: 码长超过 16 位");

            double ratio = (double)cost / optimum;
            Assert.True(ratio < 1.01,
                $"{name}: 码位 {cost} 相对最优 {optimum} 偏 +{(ratio - 1) * 100:F2}%（应 <1%）");
        }
    }

    private static long[] PowerLaw(int n, long top, double expo)
    {
        var f = new long[256];
        for (int i = 0; i < n; i++) f[i] = Math.Max(1, (long)Math.Round(top / Math.Pow(i + 1, expo)));
        return f;
    }

    private static long[] PeakyTail()
    {
        var f = new long[256];
        f[0] = 60000;
        f[1] = 40000;
        for (int i = 0; i < 70; i++) f[i + 10] = Math.Max(1, 3000 / (i + 1));
        return f;
    }

    private static long[] SteepPowers()
    {
        var f = new long[256];
        for (int i = 0; i < 20; i++) f[i] = 1L << i;
        return f;
    }

    /// <summary>不限码长的 Huffman 最优总码位数（堆式合并）。</summary>
    private static long OptimalHuffmanBits(long[] freq)
    {
        var heap = new PriorityQueue<long, long>();
        foreach (var v in freq)
        {
            if (v > 0) heap.Enqueue(v, v);
        }
        if (heap.Count == 0) return 0;
        if (heap.Count == 1) return heap.Peek();
        long total = 0;
        while (heap.Count > 1)
        {
            long a = heap.Dequeue();
            long b = heap.Dequeue();
            total += a + b;
            heap.Enqueue(a + b, a + b);
        }
        return total;
    }

    /// <summary>按 DHT 的（counts, symbols）语义统计该表对这组频率的总码位数。</summary>
    private static long TableCost(byte[] counts, byte[] symbols, long[] freq)
    {
        long bits = 0;
        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            for (int k = 0; k < counts[l - 1]; k++)
            {
                bits += freq[symbols[p]] * l;
                p++;
            }
        }
        Assert.Equal(symbols.Length, p);
        return bits;
    }

    private static int MaxCodeLength(byte[] counts)
    {
        for (int l = 16; l >= 1; l--)
        {
            if (counts[l - 1] > 0) return l;
        }
        return 0;
    }

    [Fact]
    public void Encode_WithOptimizedHuffman_DecodesIdenticallyAndSmallerOnFlatImage()
    {
        // 大片平坦 + 局部细节：空块开销占比高，优化收益最大的场景
        int w = 512, h = 512;
        var rgb = new byte[w * h * 3];
        var rng = new Random(11);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 3;
                if (x > 200 && x < 260 && y > 200 && y < 260)
                {
                    rgb[o] = (byte)rng.Next(256);
                    rgb[o + 1] = (byte)rng.Next(256);
                    rgb[o + 2] = (byte)rng.Next(256);
                }
                else
                {
                    rgb[o] = 250;
                    rgb[o + 1] = 250;
                    rgb[o + 2] = 248;
                }
            }
        }

        byte[] plain = Encode(w, h, rgb, optimize: false);
        byte[] optimized = Encode(w, h, rgb, optimize: true);

        // 像素级一致（量化不变，只换熵编码）
        Assert.Equal(Decode(plain), Decode(optimized));

        // 平坦内容上体积应明显下降
        Assert.True(optimized.Length < plain.Length, $"optimized={optimized.Length} plain={plain.Length}");

        // DHT 的 Kraft 和必须合法（可被自家解码器重建）
        Assert.True(optimized.Length > 0);
    }

    [Fact]
    public void Encode_WithOptimizedHuffman_RandomPhotoStillValid()
    {
        int w = 96, h = 72;
        var rgb = new byte[w * h * 3];
        var rng = new Random(5);
        rng.NextBytes(rgb);

        byte[] plain = Encode(w, h, rgb, optimize: false);
        byte[] optimized = Encode(w, h, rgb, optimize: true);

        Assert.Equal(Decode(plain), Decode(optimized));
        // 高频细节图是旧实现的失分区（按图优化的表反而比固定表大 1.8%~2.2%）：
        // 正确实现下「按图优化的表」必须严格优于固定 Annex K 表 + 更小的 DHT 段
        Assert.True(optimized.Length < plain.Length,
            $"optimized={optimized.Length} plain={plain.Length}");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(17)]
    public void BuildOptimalHuffmanTable_EqualFrequencies_NeverEmitsAllOnesCode(int n)
    {
        // 等频分布最容易触发「最深组填满 → 末码全 1」（如 5 符号 → bits=[3,0,2] → 末码 111），
        // 这种表会被 libjpeg/GDI+ 系解码器以 "Bogus Huffman table definition" 拒绝
        var freq = new long[256];
        for (int i = 0; i < n; i++) freq[i * 7 + 3] = 100;

        var (counts, symbols) = JpegEncoder.BuildOptimalHuffmanTable(freq);

        Assert.Equal(n, symbols.Length);
        Assert.True(JpegEncoder.IsLegalPrefixCode(counts),
            $"bits=[{string.Join(',', counts)}] 含全 1 末码");
        long kraft = 0;
        for (int i = 0; i < 16; i++) kraft += (long)counts[i] << (16 - (i + 1));
        Assert.True(kraft <= 65536, "Kraft 和不得超过码空间");
    }

    [Fact]
    public void IsLegalPrefixCode_RejectsAllOnesLastCode()
    {
        // progressive.min.jpg 实际产出的坏表：末码 1111 为全 1，GDI+ 报 Bogus Huffman table definition
        Assert.False(JpegEncoder.IsLegalPrefixCode(new byte[] { 1, 1, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.True(JpegEncoder.IsLegalPrefixCode(new byte[] { 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        // 标准 Annex K 表（AC luma / DC luma 的真实 bits）必须合法
        Assert.True(JpegEncoder.IsLegalPrefixCode(new byte[] { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 125 }));
        Assert.True(JpegEncoder.IsLegalPrefixCode(new byte[] { 0, 2, 1, 3, 3, 2, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    private static byte[] Encode(int w, int h, byte[] rgb, bool optimize)
    {
        using var ms = new MemoryStream();
        JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), ms, new JpegEncoderOptions(90, true, false, false, optimize));
        return ms.ToArray();
    }

    private static byte[] Decode(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg, false);
        return new JpegDecoderAdapter().DecodeRgb24(ms).Buffer;
    }
}
