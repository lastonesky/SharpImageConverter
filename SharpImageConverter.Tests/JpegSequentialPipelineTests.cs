using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Compression;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Processing;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// 串行编码管线（<see cref="JpegExecutionMode.IsSequential"/>，单线程 wasm 走的那条）必须与
/// 并行流水线产出**逐字节相同**的 JPEG。
/// </summary>
/// <remarks>
/// 串行路径在普通桌面机器上默认不可达（<c>ProcessorCount &gt; 1</c>），所以这条测试是它唯一的
/// 常规防线：没有它，任何改动 <c>FillMcuBlocks</c> / <c>DctQuantizeInPlace</c> / Huffman 写块的
/// 人都不会知道还有第二条管线要跟上。
///
/// 两边都通过 <c>forceSequential</c> 显式指定，而不是靠环境变量——环境变量是进程级的，
/// 一次运行只能覆盖一条路径，没法对拍。
/// </remarks>
[Collection(JpegSimdStateCollection.Name)]
public class JpegSequentialPipelineTests
{
    private static byte[] MakeRgb(int width, int height)
    {
        var buffer = new byte[(long)width * height * 3];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                buffer[i++] = (byte)((x * 7 + y * 3) & 0xFF);
                buffer[i++] = (byte)((y * 5 + (x >> 2) + (x * y >> 4)) & 0xFF);
                buffer[i++] = (byte)(((x ^ y) * 11) & 0xFF);
            }
        }
        return buffer;
    }

    private static byte[] MakeGray(int width, int height)
    {
        byte[] rgb = MakeRgb(width, height);
        var gray = new byte[width * height];
        for (int i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)((rgb[i * 3] * 77 + rgb[i * 3 + 1] * 150 + rgb[i * 3 + 2] * 29) >> 8);
        }
        return gray;
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static byte[] EncodeRgb(int width, int height, byte[] rgb, JpegEncoderOptions options, bool? forceSequential)
    {
        using var ms = new MemoryStream();
        JpegEncoder.Write(ms, width, height, rgb, options, forceSequential);
        return ms.ToArray();
    }

    private static byte[] EncodeGray(int width, int height, byte[] gray, bool? forceSequential)
    {
        using var ms = new MemoryStream();
        JpegEncoder.WriteGray8(ms, width, height, gray, 75, forceSequential);
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Sizes()
    {
        // 覆盖：小于一个 MCU、边界差 1 像素、多行多列、非 16 倍数
        yield return new object[] { 1, 1 };
        yield return new object[] { 7, 9 };
        yield return new object[] { 16, 16 };
        yield return new object[] { 17, 33 };
        yield return new object[] { 65, 49 };
        yield return new object[] { 128, 128 };
        yield return new object[] { 333, 251 };
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Sequential_MatchesParallel_Rgb420(int width, int height)
    {
        byte[] rgb = MakeRgb(width, height);
        var options = new JpegEncoderOptions(75, JpegChromaSubsampling.Yuv420, keepMetadata: false);

        Assert.Equal(
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: false)),
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: true)));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Sequential_MatchesParallel_Rgb444(int width, int height)
    {
        byte[] rgb = MakeRgb(width, height);
        var options = new JpegEncoderOptions(75, JpegChromaSubsampling.Yuv444, keepMetadata: false);

        Assert.Equal(
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: false)),
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: true)));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Sequential_MatchesParallel_Gray8(int width, int height)
    {
        byte[] gray = MakeGray(width, height);

        Assert.Equal(
            Hash(EncodeGray(width, height, gray, forceSequential: false)),
            Hash(EncodeGray(width, height, gray, forceSequential: true)));
    }

    /// <summary>
    /// optimizeHuffman 会先跑一趟 <c>CollectHuffmanHistograms</c>（同样有串行版）。
    /// 统计口径若与写位口径不一致，选出的最优表就会不同——体积和字节都会变。
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void Sequential_MatchesParallel_OptimizeHuffman(int width, int height)
    {
        byte[] rgb = MakeRgb(width, height);
        var options = new JpegEncoderOptions(70, JpegChromaSubsampling.Yuv420,
            keepMetadata: false, enableDiagnostics: false, optimizeHuffman: true);

        Assert.Equal(
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: false)),
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: true)));
    }

    /// <summary>
    /// trellis 量化会走标量 FDCT + <c>JpegTrellisQuantizer</c>（其临时缓冲是 ThreadStatic），
    /// 串行与并行都必须得到相同的系数。
    /// </summary>
    [Theory]
    [InlineData(33, 17)]
    [InlineData(64, 64)]
    public void Sequential_MatchesParallel_Trellis(int width, int height)
    {
        byte[] rgb = MakeRgb(width, height);
        var options = new JpegEncoderOptions(80, JpegChromaSubsampling.Yuv420,
            keepMetadata: false, enableDiagnostics: false, optimizeHuffman: false, trellisQuantization: true);

        Assert.Equal(
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: false)),
            Hash(EncodeRgb(width, height, rgb, options, forceSequential: true)));
    }

    /// <summary>
    /// optimize 搜索期走的是 <c>JpegSampleCache</c> + <c>EncodeFromSamples</c>（量化系数旁路），
    /// 是另一条独立的分支，单独覆盖。
    /// </summary>
    [Theory]
    [InlineData(33, 17)]
    [InlineData(64, 48)]
    public void SampleCache_Sequential_MatchesParallel(int width, int height)
    {
        byte[] rgb = MakeRgb(width, height);

        Assert.Equal(Hash(Quantize(width, height, rgb, forceSequential: false)),
                     Hash(Quantize(width, height, rgb, forceSequential: true)));

        static byte[] Quantize(int w, int h, byte[] rgb, bool? forceSequential)
        {
            var sink = new HashingSink();
            using var cache = new JpegSampleCache();
            cache.Capture(rgb, w, h, JpegChromaSubsampling.Yuv420);
            JpegEncoder.EncodeFromSamples(cache, w, h, JpegChromaSubsampling.Yuv420, 75, sink,
                trellisQuantization: false, forceSequential: forceSequential);
            return sink.ToArray();
        }
    }

    /// <summary>
    /// 反复调用同一条串行路径：检查 DC 预测、行缓冲这类可跨次残留的状态没有漏到下一次调用。
    /// </summary>
    [Fact]
    public void ForceSequential_IsRepeatable()
    {
        byte[] rgb = MakeRgb(96, 80);
        var options = new JpegEncoderOptions(85, JpegChromaSubsampling.Yuv420, keepMetadata: false);

        byte[] parallel = EncodeRgb(96, 80, rgb, options, forceSequential: false);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Hash(parallel), Hash(EncodeRgb(96, 80, rgb, options, forceSequential: true)));
        }
    }

    /// <summary>
    /// <c>forceSequential: true</c> 必须真的改变执行路径，否则上面那些比对会「两边都跑并行」而
    /// 全部通过，测试就成了摆设。
    /// </summary>
    /// <remarks>
    /// 判据：<see cref="JpegExecutionMode.IsSequential"/> 在创建串行路径用的行缓冲时被读取，
    /// 这里用 <c>SIC_JPEG_PIPELINE</c> 无法在本进程内切换，所以改为直接断言
    /// <c>forceSequential</c> 覆盖与全局判据的组合逻辑（<c>IsSequentialFor</c>），
    /// 再加上一条「串行模式下不依赖线程池」的间接证据：在多核机器上串行编码单线程完成，
    /// 若误走并行管线，产物虽相同但会经过 Task.Run —— 这里不便观测，故只保证覆盖逻辑本身正确。
    /// </remarks>
    [Fact]
    public void IsSequentialFor_OverrideWins()
    {
        Assert.True(JpegExecutionMode.IsSequentialFor(true));
        Assert.False(JpegExecutionMode.IsSequentialFor(false));
        Assert.Equal(JpegExecutionMode.IsSequential, JpegExecutionMode.IsSequentialFor(null));
    }

    /// <summary>
    /// 端到端：JPEG → BMP 的逐行转换会用到 <c>JpegBaselineFusedRowSource</c>（含它的同步模式），
    /// 产出的像素必须与整幅解码路径逐字节相同。
    /// </summary>
    /// <remarks>
    /// 同步模式下这个类不再「一次预解码整幅系数到 List」，而是边读边解；一旦行推进写错，
    /// 最典型的表现是行错位或末行重复——像素比对能抓到，只看「能解出图」抓不到。
    /// 4:4:4 单独跑一次：它的每行块比 4:2:0 大一倍，也是原先预解码内存开销最重的分支。
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JpegRowSource_StreamingBmp_MatchesFullDecode(bool subsample420)
    {
        int width = 67, height = 53;
        byte[] rgb = MakeRgb(width, height);
        var options = new JpegEncoderOptions(90,
            subsample420 ? JpegChromaSubsampling.Yuv420 : JpegChromaSubsampling.Yuv444, keepMetadata: false);
        byte[] jpeg = EncodeRgb(width, height, rgb, options, forceSequential: null);

        string dir = Path.Combine(Path.GetTempPath(), "sic-seqrows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.jpg");
        string rowPath = Path.Combine(dir, "rows.bmp");
        try
        {
            File.WriteAllBytes(input, jpeg);
            StreamingConversion.Convert(input, rowPath, ImageOptimizer.ImageKind.Bmp, gray: false);

            // 逐行解出来的 BMP（54 字节头 + top-down + 24 位 BGR + 每行 4 字节对齐）
            // 与整幅解码的像素逐字节比对。注意 BMP 是 BGR 顺序（BmpRowSink 里 d = s+2, s+1, s）。
            byte[] bmp = File.ReadAllBytes(rowPath);
            ImageFrame full = ImageFrame.LoadJpeg(input);
            int bmpStride = (width * 3 + 3) & ~3;
            Assert.Equal(54 + bmpStride * height, bmp.Length);

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * width * 3;
                int dstRow = 54 + y * bmpStride;
                for (int x = 0; x < width; x++)
                {
                    int s = srcRow + x * 3;
                    int d = dstRow + x * 3;
                    Assert.True(
                        full.Pixels[s] == bmp[d + 2] && full.Pixels[s + 1] == bmp[d + 1] && full.Pixels[s + 2] == bmp[d],
                        $"第 {y} 行第 {x} 列像素不一致（subsample420={subsample420}）：" +
                        $"解码=({full.Pixels[s]},{full.Pixels[s + 1]},{full.Pixels[s + 2]}) " +
                        $"BMP=({bmp[d + 2]},{bmp[d + 1]},{bmp[d]})");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>把 sink 收到的系数按序号记下来，用于对拍。</summary>
    /// <remarks>
    /// 并行分支会从多个线程调 <see cref="Accept"/>（且顺序不保证），所以这里按序号存进字典，
    /// 而不是顺序追加到流里。
    /// </remarks>
    private sealed class HashingSink : IMcuCoefficientSink
    {
        private readonly Dictionary<int, byte[]> _mcus = new();
        private readonly object _gate = new();
        private byte[] _header = Array.Empty<byte>();
        private int _maxSequence = -1;

        public void Begin(int width, int height, JpegChromaSubsampling subsampling, ReadOnlySpan<byte> lumaQuant, ReadOnlySpan<byte> chromaQuant)
        {
            var header = new byte[12 + lumaQuant.Length + chromaQuant.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), width);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), height);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), (int)subsampling);
            lumaQuant.CopyTo(header.AsSpan(12));
            chromaQuant.CopyTo(header.AsSpan(12 + lumaQuant.Length));
            _header = header;
        }

        public void Accept(int sequence, ReadOnlySpan<int> blocks, int blockCount, byte[] order)
        {
            var entry = new byte[blockCount + blockCount * 64 * 4];
            order.AsSpan(0, blockCount).CopyTo(entry);
            for (int i = 0; i < blockCount * 64; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    entry.AsSpan(blockCount + i * 4, 4), blocks[i]);
            }

            lock (_gate)
            {
                _mcus[sequence] = entry;
                if (sequence > _maxSequence) _maxSequence = sequence;
            }
        }

        public byte[] ToArray()
        {
            using var stream = new MemoryStream();
            stream.Write(_header);
            for (int i = 0; i <= _maxSequence; i++)
            {
                byte[] entry = _mcus[i];
                stream.Write(BitConverter.GetBytes(i));
                stream.Write(entry);
            }
            return stream.ToArray();
        }
    }
}
