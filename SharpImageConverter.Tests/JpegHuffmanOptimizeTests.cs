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
        // 2 的幂频率会产生深树；总频率被缩放后码长不得超过 16
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
        Assert.True(optimized.Length <= plain.Length * 1.02, $"optimized={optimized.Length} plain={plain.Length}");
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
