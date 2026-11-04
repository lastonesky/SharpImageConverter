using System.Text;
using Xunit;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Tests.Helpers;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 纯 C# WebP 解码器（WebpDecoder）与 libwebp 的 bit-exact 对拍测试。
/// 覆盖：LSB 位序、VP8L 帧头、无变换最简流、RIFF 容器（静态图与动图结构）。
/// 说明：VP8L 无损要求 bit-exact；有损 VP8 只验证“能解码、尺寸正确”，
/// 与 libwebp 的逐像素对齐由 Vp8* 解码器自带的测试负责。
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

    /// <summary>
    /// 用 libwebp 生成 32×32 无损文件，断言纯 C# 容器解析出的尺寸与 libwebp 一致。
    /// </summary>
    [Fact]
    public void Vp8LHeader_LibwebpEncoded32x32_MatchesLibwebp()
    {
        const int w = 32, h = 32;
        byte[] webp = EncodeLossless(GradientRgba(w, h, opaque: true), w, h);

        var lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        var container = WebpContainer.Load(webp);

        Assert.Equal(WebpContainerKind.Vp8L, container.Kind);
        Assert.Equal(lw, container.CanvasWidth);
        Assert.Equal(lh, container.CanvasHeight);
        Assert.Equal(w, container.CanvasWidth);
        Assert.Equal(h, container.CanvasHeight);

        // 全不透明：alpha_is_used 应为 0，且 libwebp 解出的 alpha 全为 255
        Assert.False(container.HasAlpha);
        Assert.False(HasTransparency(lib));
    }

    /// <summary>
    /// 64×48（非方、非 2 的幂）用于避免尺寸读取出错时"碰巧"对得上。
    /// </summary>
    [Fact]
    public void Vp8LHeader_LibwebpEncoded64x48_MatchesLibwebp()
    {
        const int w = 64, h = 48;
        byte[] webp = EncodeLossless(RandomRgba(w, h, seed: 7), w, h);

        var lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        var container = WebpContainer.Load(webp);

        Assert.Equal(lw, container.CanvasWidth);
        Assert.Equal(lh, container.CanvasHeight);
        Assert.Equal(64, container.CanvasWidth);
        Assert.Equal(48, container.CanvasHeight);

        var csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>
    /// 带透明度的文件：VP8L 帧头的 alpha_is_used 与 libwebp 解码结果中的实际透明度一致。
    /// </summary>
    [Fact]
    public void Vp8LHeader_WithAlpha_MatchesTransparency()
    {
        const int w = 64, h = 48;
        byte[] webp = EncodeLossless(AlphaGradientRgba(w, h), w, h);

        var lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        var container = WebpContainer.Load(webp);

        Assert.Equal(lw, container.CanvasWidth);
        Assert.Equal(lh, container.CanvasHeight);

        bool transparent = HasTransparency(lib);
        Assert.True(transparent, "测试数据应包含非不透明像素");
        Assert.Equal(transparent, container.HasAlpha);

        var csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>
    /// EXIF 元数据要求使用 VP8X 扩展容器：验证 VP8X 画布尺寸字段（flags 后 3 字节 reserved）
    /// 与 EXIF chunk 遍历，并与 libwebp 的解码尺寸对齐。
    /// </summary>
    [Fact]
    public void Vp8XHeader_WithExif_MatchesLibwebp()
    {
        const int w = 32, h = 24;
        byte[] plain = EncodeLossless(GradientRgba(w, h, opaque: true), w, h);

        // 从普通 VP8L 文件里取出压缩数据，重新封装成 VP8X + VP8L + EXIF 容器
        var plainContainer = WebpContainer.Load(plain);
        var vp8lChunk = plainContainer.FindChunk(WebpChunkId.Vp8L);
        Assert.True(vp8lChunk.HasValue);
        byte[] vp8lData = vp8lChunk.Value.GetData(plainContainer.RawData).ToArray();

        byte[] webp = BuildRiff(
            ("VP8X", BuildVp8XChunkData(w, h, flags: Vp8XFlags.Exif)),
            ("VP8L", vp8lData),
            ("EXIF", new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 }));

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8X, container.Kind);
        Assert.Equal(w, container.CanvasWidth);
        Assert.Equal(h, container.CanvasHeight);
        Assert.False(container.IsAnimation);
        Assert.False(container.HasAlpha);
        Assert.Single(container.FindAllChunks(WebpChunkId.Exif));

        var lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);

        var csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    // ==================================================================
    //  3. 最简解码路径：无变换、无 color cache、无调色板
    // ==================================================================

    /// <summary>
    /// 手工构造 VP8L 位流：无变换、无 color cache、无 meta-Huffman、5 个单符号 Huffman 表。
    /// 单符号表解码时不消耗任何 bit，因此整幅图像只需 91 个有效位。
    /// 先用 libwebp 确认该位流合法，再断言纯 C# 解码逐像素 bit-exact。
    /// </summary>
    [Fact]
    public void Lossless_HandBuiltNoTransform_OpaqueSolid_BitExact()
    {
        const int w = 8, h = 8;
        byte r = 10, g = 20, b = 30, a = 255;
        byte[] webp = BuildMinimalVp8L(w, h, r, g, b, a, alphaUsed: false);

        byte[] expected = SolidRgba(w, h, r, g, b, a);

        // oracle：libwebp 也必须接受同一个位流
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(w, lw);
        Assert.Equal(h, lh);
        PixelAssert.Equal(expected, lib, lw, lh);

        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        PixelAssert.Equal(expected, csharp, mw, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>
    /// 同上，但像素带半透明 alpha：验证 alpha 通道走同样正确的最简路径。
    /// </summary>
    [Fact]
    public void Lossless_HandBuiltNoTransform_TranslucentSolid_BitExact()
    {
        const int w = 8, h = 8;
        byte r = 200, g = 150, b = 100, a = 128;
        byte[] webp = BuildMinimalVp8L(w, h, r, g, b, a, alphaUsed: true);

        byte[] expected = SolidRgba(w, h, r, g, b, a);
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        PixelAssert.Equal(expected, lib, lw, lh);

        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);
        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        PixelAssert.Equal(expected, csharp, mw, mh);
    }

    /// <summary>libwebp 无损编码的纯色图，逐像素与 libwebp 解码结果对齐。</summary>
    [Fact]
    public void Lossless_SolidColor_BitExact()
    {
        const int w = 32, h = 32;
        byte[] rgba = SolidRgba(w, h, r: 200, g: 0, b: 50, a: 255);

        byte[] webp = EncodeLossless(rgba, w, h);
        Assert.True(IsVp8L(webp), "libwebp 无损编码应生成 VP8L 格式");

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    [Fact]
    public void Lossless_HorizontalGradient_BitExact()
    {
        const int w = 64, h = 16;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            rgba[i + 0] = 255;        // A
            rgba[i + 1] = (byte)(x * 4);   // R 水平渐变
            rgba[i + 2] = (byte)(y * 16);  // G 垂直渐变
            rgba[i + 3] = (byte)(255 - x * 4); // B 反向渐变
        }

        byte[] webp = EncodeLossless(rgba, w, h);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);

        PixelAssert.Equal(rgba, decoded, w, h);
    }

    [Fact]
    public void Lossless_AlphaGradient_BitExact()
    {
        const int w = 32, h = 32;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            rgba[i + 0] = (byte)(x * 8);  // alpha 渐变
            rgba[i + 1] = 128;
            rgba[i + 2] = 64;
            rgba[i + 3] = 32;
        }

        byte[] webp = EncodeLossless(rgba, w, h);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        byte[] libwebpDecoded = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(lw, dw);
        Assert.Equal(lh, dh);

        PixelAssert.Equal(libwebpDecoded, decoded, dw, dh);
        PixelAssert.Equal(rgba, decoded, dw, dh);
    }

    [Fact]
    public void Lossless_Checkerboard_BitExact()
    {
        const int w = 16, h = 16;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            bool isWhite = (x + y) % 2 == 0;
            rgba[i + 0] = 255;
            rgba[i + 1] = isWhite ? (byte)255 : (byte)0;
            rgba[i + 2] = isWhite ? (byte)255 : (byte)0;
            rgba[i + 3] = isWhite ? (byte)255 : (byte)0;
        }

        byte[] webp = EncodeLossless(rgba, w, h);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        byte[] libwebpDecoded = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(lw, dw);
        Assert.Equal(lh, dh);
        PixelAssert.Equal(libwebpDecoded, decoded, dw, dh);
    }

    [Fact]
    public void Lossless_SmallImage_1x1_BitExact()
    {
        var rgba = new byte[] { 255, 100, 150, 200 };
        byte[] webp = EncodeLossless(rgba, 1, 1);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        byte[] libwebpDecoded = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        Assert.Equal(lw, dw);
        Assert.Equal(lh, dh);
        PixelAssert.Equal(libwebpDecoded, decoded, 1, 1);
    }

    [Fact]
    public void Lossless_RandomNoise_BitExact()
    {
        const int w = 32, h = 32;
        var rgba = new byte[w * h * 4];
        var rng = new Random(42);
        rng.NextBytes(rgba);
        for (int i = 0; i < rgba.Length; i += 4)
            rgba[i + 3] = 255; // 强制不透明（R,G,B,A 排列）

        byte[] webp = EncodeLossless(rgba, w, h);

        byte[] libwebpDecoded = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        Assert.Equal(lw, dw);
        Assert.Equal(lh, dh);
        PixelAssert.Equal(libwebpDecoded, decoded, dw, dh);
    }

    /// <summary>
    /// 256×256 随机图触发多组 meta-Huffman（大图会切成多个 Huffman 组），
    /// 与 libwebp 逐像素 bit-exact。
    /// </summary>
    [Fact]
    public void Lossless_LargeImage_BitExact()
    {
        const int w = 256, h = 256;
        var rgba = new byte[w * h * 4];
        var rng = new Random(1234);
        rng.NextBytes(rgba);
        for (int i = 0; i < rgba.Length; i += 4)
            rgba[i + 3] = 255;

        byte[] webp = EncodeLossless(rgba, w, h);

        byte[] libwebpDecoded = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        Assert.Equal(lw, dw);
        Assert.Equal(lh, dh);
        PixelAssert.Equal(libwebpDecoded, decoded, dw, dh);
    }

    // ==================================================================
    //  3b. 变换 / 多表 / ALPH 的 bit-exact 对拍矩阵
    // ==================================================================

    /// <summary>
    /// 照片类图像（平滑块 + 细噪声）会触发 libwebp 默认的 predictor 变换，
    /// 逐像素与 libwebp 解码结果完全一致。
    /// </summary>
    [Fact]
    public void Lossless_PhotoLike_Predictor_BitExact()
    {
        const int w = 256, h = 192;
        byte[] webp = EncodeLossless(PhotoLikeRgba(w, h, seed: 2024), w, h);

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>
    /// R/G/B 高度相关的渐变会触发 subtract green 与 cross-color 变换，
    /// 用于验证系数乘除与变换链逆序。
    /// </summary>
    [Fact]
    public void Lossless_CrossColorSubtractGreen_Gradient_BitExact()
    {
        const int w = 128, h = 96;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            int g = (x + y) & 0xFF;
            rgba[i + 0] = (byte)(g + 12);        // R ≈ G
            rgba[i + 1] = (byte)g;               // G
            rgba[i + 2] = (byte)(g - 20);        // B ≈ G
            rgba[i + 3] = 255;
        }

        byte[] webp = EncodeLossless(rgba, w, h);
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>
    /// 8 色调色板图：libwebp 会自动启用 color indexing 变换（含 packed 索引宽度）。
    /// </summary>
    [Fact]
    public void Lossless_Palette_ColorIndexing_BitExact()
    {
        const int w = 96, h = 72;
        byte[] rgba = PaletteRgba(w, h);

        byte[] webp = EncodeLossless(rgba, w, h);
        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>512×512 大图：触发多组 meta-Huffman（每 tile 一张表）。</summary>
    [Fact]
    public void Lossless_512x512_MultiHuffmanTable_BitExact()
    {
        const int w = 512, h = 512;
        byte[] webp = EncodeLossless(PhotoLikeRgba(w, h, seed: 5150), w, h);

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(lw, mw);
        Assert.Equal(lh, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>极端宽高比 1×N：所有像素在同一行，predictor 首行/末列边界全部命中。</summary>
    [Fact]
    public void Lossless_ExtremeAspect_1x256_BitExact()
    {
        const int w = 1, h = 256;
        byte[] webp = EncodeLossless(PhotoLikeRgba(w, h, seed: 7), w, h);

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    /// <summary>极端宽高比 N×1：只有首行，predictor 首行规则全覆盖。</summary>
    [Fact]
    public void Lossless_ExtremeAspect_256x1_BitExact()
    {
        const int w = 256, h = 1;
        byte[] webp = EncodeLossless(PhotoLikeRgba(w, h, seed: 8), w, h);

        byte[] lib = LibWebpTestCodec.DecodeRgba(webp, out int lw, out int lh);
        byte[] csharp = WebpDecoder.DecodeRgba(webp, out int mw, out int mh);

        Assert.Equal(w, mw);
        Assert.Equal(h, mh);
        PixelAssert.Equal(lib, csharp, mw, mh);
    }

    // ---- ALPH 独立 alpha 通道（合成进 VP8L 结果） ----

    /// <summary>ALPH 未压缩模式（compression=0, filter=0）与 VP8L 主图合成。</summary>
    [Fact]
    public void Alph_RawGradient_ComposedIntoRgba()
    {
        const int w = 32, h = 16;
        byte[] main = GradientRgba(w, h, opaque: true);
        byte[] alpha = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            alpha[y * w + x] = (byte)(x * 8);

        var payload = new byte[1 + alpha.Length];
        payload[0] = 0x00; // compression=0, filter=0, preprocessing=0
        alpha.CopyTo(payload, 1);

        byte[] webp = BuildAlphContainer(w, h, main, payload);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        Assert.Equal(w, dw);
        Assert.Equal(h, dh);

        var expected = (byte[])main.Clone();
        for (int i = 0; i < alpha.Length; i++)
            expected[i * 4 + 3] = alpha[i];
        PixelAssert.Equal(expected, decoded, w, h);
    }

    /// <summary>ALPH horizontal filter（filter=1）：按行累加差分恢复 alpha。</summary>
    [Fact]
    public void Alph_RawHorizontalFilter_InverseApplied()
    {
        const int w = 32, h = 8;
        byte[] main = SolidRgba(w, h, 90, 160, 40, 255);

        byte[] alpha = new byte[w * h];
        var deltas = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            // libwebp 的 horizontal 过滤是跨行的：每行的 pred 初值取「上一行首像素」
            // （首行取 0），之后逐像素滚动。之前这里按「每行独立、从 0 起算」构造，
            // 与被测实现的错误语义相同，导致样例自身掩盖了 bug。
            int pred = y == 0 ? 0 : alpha[(y - 1) * w];
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                alpha[idx] = (byte)(x * 6 + y);
                deltas[idx] = (byte)(alpha[idx] - pred);
                pred = alpha[idx];
            }
        }

        var payload = new byte[1 + deltas.Length];
        payload[0] = 0x04; // compression=0, filter=1
        deltas.CopyTo(payload, 1);

        byte[] webp = BuildAlphContainer(w, h, main, payload);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        var expected = (byte[])main.Clone();
        for (int i = 0; i < alpha.Length; i++)
            expected[i * 4 + 3] = alpha[i];
        PixelAssert.Equal(expected, decoded, w, h);
    }

    /// <summary>ALPH 无损压缩模式（compression=1）：负载是去掉 5 字节帧头的裸 VP8L 位流，取绿通道。</summary>
    [Fact]
    public void Alph_Vp8LCompressed_ComposedIntoRgba()
    {
        const int w = 16, h = 8;
        const byte alphaValue = 137;
        byte[] main = GradientRgba(w, h, opaque: true);

        var writer = new LsbBitWriter();
        writer.WriteBit(0); // 无 transform
        writer.WriteBit(0); // 无 color cache
        writer.WriteBit(0); // 无 meta-Huffman
        WriteSimpleHuffman(writer, alphaValue); // GREEN
        WriteSimpleHuffman(writer, 0);          // RED
        WriteSimpleHuffman(writer, 0);          // BLUE
        WriteSimpleHuffman(writer, 255);        // ALPHA（合成时会丢弃）
        WriteSimpleHuffman(writer, 0);          // DISTANCE

        var bare = writer.ToArray();
        var payload = new byte[1 + bare.Length];
        payload[0] = 0x01; // compression=1, filter=0
        bare.CopyTo(payload, 1);

        byte[] webp = BuildAlphContainer(w, h, main, payload);
        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);

        var expected = (byte[])main.Clone();
        for (int i = 0; i < w * h; i++)
            expected[i * 4 + 3] = alphaValue;
        PixelAssert.Equal(expected, decoded, w, h);
    }

    /// <summary>
    /// ALPH 预处理（level reduction）在解码端不做逆运算（libwebp 也只校验取值），
    /// 因此 preprocessing=1 不再被判为不支持；此处缺少 alpha 数据，应按位流错误报告。
    /// 合法的 preprocessing=1（compression 0/1）解码见 WebpAlphPreprocessingTests。
    /// </summary>
    [Fact]
    public void Alph_PreprocessingWithoutData_ThrowsBitstream()
    {
        const int w = 8, h = 8;
        byte[] main = SolidRgba(w, h, 10, 20, 30, 255);
        byte[] payload = { 0x10 }; // preprocessing=1，但没有任何 alpha 数据

        byte[] webp = BuildAlphContainer(w, h, main, payload);
        Assert.Throws<WebpBitstreamException>(
            () => WebpDecoder.DecodeRgba(webp, out _, out _));
    }

    /// <summary>ALPH 头部保留位非零应视为损坏文件。</summary>
    [Fact]
    public void Alph_ReservedBits_ThrowsBitstream()
    {
        const int w = 8, h = 8;
        byte[] main = SolidRgba(w, h, 10, 20, 30, 255);
        byte[] payload = { 0x40 }; // reserved=1

        byte[] webp = BuildAlphContainer(w, h, main, payload);
        Assert.Throws<WebpBitstreamException>(
            () => WebpDecoder.DecodeRgba(webp, out _, out _));
    }

    /// <summary>有损 VP8 关键帧已由托管解码器支持：应返回正确尺寸的 RGBA，而非抛 Unsupported。</summary>
    [Fact]
    public void Lossy_Vp8_DecodesToRgba()
    {
        const int w = 16, h = 16;
        byte[] webp = LibWebpTestCodec.EncodeRgba(
            GradientRgba(w, h, opaque: true), w, h,
            new WebpEncoderOptions(75f, lossless: false));

        Assert.True(webp.Length > 16);
        Assert.Equal("VP8 ", Encoding.ASCII.GetString(webp, 12, 4));

        byte[] px = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        Assert.Equal(w * h * 4, px.Length);
    }

    // ==================================================================
    //  4. RIFF 容器：静态图 chunk 遍历 + 动图帧结构
    // ==================================================================

    [Fact]
    public void Container_StaticVp8L_TraversesChunks()
    {
        const int w = 32, h = 16;
        byte[] webp = EncodeLossless(GradientRgba(w, h, opaque: true), w, h);

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8L, container.Kind);
        Assert.False(container.IsAnimation);
        Assert.False(container.HasAlphaChunk);
        Assert.Equal(0, container.LoopCount);

        var vp8l = container.FindChunk(WebpChunkId.Vp8L);
        Assert.True(vp8l.HasValue);
        var data = vp8l.Value.GetData(container.RawData);
        Assert.True(data.Length >= 5);
        Assert.Equal(0x2F, data[0]); // VP8L 签名字节，位流起点必须是它
    }

    /// <summary>
    /// 用 libwebp mux 生成 3 帧动图，验证 VP8X/ANIM/ANMF 的分块结构、循环次数、
    /// 每帧矩形与时长、以及 ANMF 内嵌的 VP8L 子 chunk。
    /// 注意：这里只验证容器结构，不做动图帧像素解码（留待下一轮）。
    /// </summary>
    [Fact]
    public void Container_Animated_StructureParsed()
    {
        const int w = 32, h = 24;
        int[] durations = { 100, 150, 200 };
        int loopCount = 2;

        byte[][] frames =
        {
            SolidRgba(w, h, 255, 0, 0, 255),
            SolidRgba(w, h, 0, 255, 0, 255),
            SolidRgba(w, h, 0, 0, 255, 255),
        };

        byte[] webp = LibWebpTestCodec.EncodeAnimatedRgba(
            frames, w, h, durations, loopCount,
            new WebpEncoderOptions(75f, lossless: true));

        var container = WebpContainer.Load(webp);
        Assert.Equal(WebpContainerKind.Vp8X, container.Kind);
        Assert.True(container.IsAnimation);
        Assert.Equal(w, container.CanvasWidth);
        Assert.Equal(h, container.CanvasHeight);
        Assert.Equal(loopCount, container.LoopCount);

        var animFrames = container.FindAllChunks(WebpChunkId.Anmf);
        Assert.Equal(frames.Length, animFrames.Count);

        for (int i = 0; i < animFrames.Count; i++)
        {
            var frame = animFrames[i].GetData(container.RawData);
            Assert.True(frame.Length >= 16, "ANMF 帧头应为 16 字节");

            // ANMF 帧头：X(3) Y(3) W-1(3) H-1(3) duration(3) flags(1)
            int x = ReadUInt24(frame, 0);
            int y = ReadUInt24(frame, 3);
            int fw = ReadUInt24(frame, 6) + 1;
            int fh = ReadUInt24(frame, 9) + 1;
            int duration = ReadUInt24(frame, 12);

            Assert.Equal(0, x);
            Assert.Equal(0, y);
            Assert.Equal(w, fw);
            Assert.Equal(h, fh);
            Assert.Equal(durations[i], duration);

            // ANMF 内部是完整子 chunk：先 4 字节 FourCC，再 4 字节 size
            var sub = frame.Slice(16);
            Assert.True(sub.Length >= 9);
            Assert.Equal("VP8L", Encoding.ASCII.GetString(sub.ToArray(), 0, 4));
            Assert.Equal(0x2F, sub[8]); // VP8L 压缩数据以 0x2F 签名开头
        }

        // 说明：libwebp 的 WebPDecodeRGBAInto 不处理动图（会返回失败），
        // 因此本用例只以容器结构为断言依据；帧像素解码留待下一轮。
    }

    /// <summary>
    /// 4 帧动图逐帧像素解码：每帧覆盖整幅画布且像素全不透明，
    /// 因此解码出的每帧画布快照必须与编码前的输入帧逐像素相同。
    /// </summary>
    [Fact]
    public void Container_Animated_FramePixelDecode_BitExact()
    {
        const int w = 24, h = 18;
        int[] durations = { 100, 150, 200, 250 };

        byte[][] frames =
        {
            GradientRgba(w, h, opaque: true),
            PhotoLikeRgba(w, h, seed: 31),
            CheckerboardRgba(w, h),
            SolidRgba(w, h, 12, 200, 96, 255),
        };

        byte[] webp = LibWebpTestCodec.EncodeAnimatedRgba(
            frames, w, h, durations, loopCount: 3,
            new WebpEncoderOptions(75f, lossless: true));

        var animation = WebpDecoder.DecodeAnimation(webp);
        Assert.Equal(w, animation.CanvasWidth);
        Assert.Equal(h, animation.CanvasHeight);
        Assert.Equal(3, animation.LoopCount);
        Assert.Equal(frames.Length, animation.Frames.Length);

        for (int i = 0; i < frames.Length; i++)
        {
            var frame = animation.Frames[i];
            Assert.Equal(0, frame.X);
            Assert.Equal(0, frame.Y);
            Assert.Equal(w, frame.Width);
            Assert.Equal(h, frame.Height);
            Assert.Equal(durations[i], frame.DurationMs);
            PixelAssert.Equal(frames[i], frame.Rgba, w, h);
        }

        // DecodeRgba 对动图返回末帧画布
        byte[] last = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        PixelAssert.Equal(frames[^1], last, w, h);
    }

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

    /// <summary>
    /// 简单格式（无 VP8X）也应当能从 VP8 关键帧头读取图像尺寸。
    /// </summary>
    [Fact]
    public void Container_SimpleVp8_ExposesImageDimensions()
    {
        const int w = 37, h = 23;
        byte[] webp = LibWebpTestCodec.EncodeRgba(
            GradientRgba(w, h, opaque: true), w, h,
            new WebpEncoderOptions(75f, lossless: false));

        var container = WebpContainer.Load(webp);

        Assert.Equal(WebpContainerKind.Vp8, container.Kind);
        Assert.Equal(w, container.CanvasWidth);
        Assert.Equal(h, container.CanvasHeight);
        Assert.False(container.HasAlpha);
    }

    [Fact]
    public void Container_RiffDeclaredSizeLargerThanData_Throws()
    {
        byte[] webp = EncodeLossless(RandomRgba(8, 8, seed: 77), 8, 8);
        uint riffSize = BitConverter.ToUInt32(webp, 4);
        BitConverter.GetBytes(riffSize + 16u).CopyTo(webp, 4);

        var ex = Assert.Throws<WebpContainerException>(() => WebpContainer.Load(webp));
        Assert.Contains("RIFF", ex.Message);
    }

    [Fact]
    public void Decode_RiffSizeSentinel_StillDecodes()
    {
        const int w = 8, h = 8;
        byte[] rgba = GradientRgba(w, h, opaque: true);
        byte[] webp = EncodeLossless(rgba, w, h);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(webp, 4);

        byte[] decoded = WebpDecoder.DecodeRgba(webp, out int dw, out int dh);
        Assert.Equal(w, dw);
        Assert.Equal(h, dh);
        PixelAssert.Equal(rgba, decoded, w, h);
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
    public void Decode_TruncatedVp8L_ThrowsControlled()
    {
        byte[] webp = EncodeLossless(RandomRgba(16, 16, seed: 99), 16, 16);
        byte[] truncated = webp.AsSpan(0, webp.Length / 2).ToArray();

        Assert.ThrowsAny<WebpException>(() => WebpDecoder.DecodeRgba(truncated, out _, out _));
    }

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

    [Fact]
    public void Decode_BitstreamShortRead_ThrowsControlled()
    {
        byte[] webp = EncodeLossless(RandomRgba(16, 16, seed: 77), 16, 16);
        byte[] corrupted = webp.AsSpan(0, webp.Length - 5).ToArray();

        Assert.ThrowsAny<WebpException>(() => WebpDecoder.DecodeRgba(corrupted, out _, out _));
    }

    // ==================================================================
    //  构造辅助
    // ==================================================================

    private static byte[] EncodeLossless(byte[] rgba, int w, int h)
        => LibWebpTestCodec.EncodeRgba(rgba, w, h, new WebpEncoderOptions(75f, lossless: true));

    /// <summary>R,G,B,A 排列的纯色图。</summary>
    private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i + 0] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }
        return rgba;
    }

    private static byte[] GradientRgba(int w, int h, bool opaque)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            rgba[i + 0] = (byte)(x * 3);
            rgba[i + 1] = (byte)(y * 5);
            rgba[i + 2] = (byte)(x + y);
            rgba[i + 3] = (byte)(opaque ? 255 : ((x + y) & 0xFF));
        }
        return rgba;
    }

    private static byte[] AlphaGradientRgba(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            rgba[i + 0] = 128;
            rgba[i + 1] = 64;
            rgba[i + 2] = 32;
            rgba[i + 3] = (byte)(x * 4); // alpha 渐变
        }
        return rgba;
    }

    private static byte[] RandomRgba(int w, int h, int seed)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(seed);
        rng.NextBytes(rgba);
        for (int i = 0; i < rgba.Length; i += 4)
            rgba[i + 3] = 255;
        return rgba;
    }

    /// <summary>
    /// 照片类测试图：低频渐变叠加径向亮斑，再叠 ±6 的细噪声。
    /// 这种"平滑为主 + 局部噪声"的结构会让 libwebp 选择 predictor 变换，
    /// 同时避免生成整幅随机噪声导致编码器走 LZ77/调色板等其它路径。
    /// </summary>
    private static byte[] PhotoLikeRgba(int w, int h, int seed)
    {
        var rgba = new byte[w * h * 4];
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            double dx = (x - w / 2.0) / Math.Max(1, w);
            double dy = (y - h / 2.0) / Math.Max(1, h);
            double glow = 200.0 * Math.Exp(-(dx * dx + dy * dy) * 6.0);
            int noise = rng.Next(-6, 7);
            rgba[i + 0] = ClampByte(glow + 40 + noise);
            rgba[i + 1] = ClampByte(glow * 0.8 + 90 * x / Math.Max(1, w) + noise);
            rgba[i + 2] = ClampByte(glow * 0.5 + 60 * y / Math.Max(1, h) - noise);
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    /// <summary>8 色调色板、6×6 方块平铺：libwebp 会启用 color indexing。</summary>
    private static byte[] PaletteRgba(int w, int h)
    {
        byte[][] colors =
        {
            new byte[] { 13, 27, 41, 255 },
            new byte[] { 220, 40, 60, 255 },
            new byte[] { 30, 180, 90, 255 },
            new byte[] { 40, 70, 210, 255 },
            new byte[] { 230, 200, 30, 255 },
            new byte[] { 120, 40, 200, 255 },
            new byte[] { 250, 250, 250, 255 },
            new byte[] { 20, 20, 20, 255 },
        };

        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = ((x / 6) + (y / 6) * 3) % colors.Length;
            int i = (y * w + x) * 4;
            colors[idx].CopyTo(rgba, i);
        }
        return rgba;
    }

    private static byte[] CheckerboardRgba(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            bool white = ((x >> 1) + (y >> 1)) % 2 == 0;
            rgba[i + 0] = white ? (byte)250 : (byte)20;
            rgba[i + 1] = white ? (byte)250 : (byte)40;
            rgba[i + 2] = white ? (byte)250 : (byte)60;
            rgba[i + 3] = 255;
        }
        return rgba;
    }

    private static byte ClampByte(double value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return (byte)value;
    }

    /// <summary>
    /// 把"VP8L 主图 + 独立 ALPH chunk"重新封装成 VP8X 容器。
    /// libwebp 的 VP8L 编码器总把 alpha 内嵌在 VP8L 里，不会产出 ALPH，
    /// 因此这里用合成容器验证 ALPH 解码与合成路径。
    /// </summary>
    private static byte[] BuildAlphContainer(int w, int h, byte[] mainRgba, byte[] alphPayload)
    {
        byte[] plain = EncodeLossless(mainRgba, w, h);
        var plainContainer = WebpContainer.Load(plain);
        var vp8lChunk = plainContainer.FindChunk(WebpChunkId.Vp8L);
        Assert.True(vp8lChunk.HasValue, "主图必须是 VP8L");
        byte[] vp8lData = vp8lChunk.Value.GetData(plainContainer.RawData).ToArray();

        return BuildRiff(
            ("VP8X", BuildVp8XChunkData(w, h, Vp8XFlags.Alpha)),
            ("ALPH", alphPayload),
            ("VP8L", vp8lData));
    }

    private static bool HasTransparency(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return true;
        return false;
    }

    private static bool IsVp8L(byte[] webp)
    {
        if (webp.Length < 16) return false;
        return webp[12] == (byte)'V' && webp[13] == (byte)'P'
            && webp[14] == (byte)'8' && webp[15] == (byte)'L';
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data, int offset)
        => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

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

    /// <summary>VP8X chunk 数据：flags(1) + reserved(3) + width-1(3) + height-1(3)。</summary>
    private static byte[] BuildVp8XChunkData(int width, int height, Vp8XFlags flags)
    {
        var data = new byte[10];
        data[0] = (byte)flags;
        data[1] = data[2] = data[3] = 0;
        WriteUInt24(data, 4, width - 1);
        WriteUInt24(data, 7, height - 1);
        return data;
    }

    private static void WriteUInt24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    /// <summary>
    /// 手工构造"最简" VP8L 位流：无变换、无 color cache、无 meta-Huffman，
    /// 绿/红/蓝/alpha/距离 5 张表都是单符号简单 Huffman。
    /// 单符号表不消耗位，所以整幅图只占帧头 40 + 3 个标志位 + 表定义。
    /// </summary>
    private static byte[] BuildMinimalVp8L(
        int width, int height, byte r, byte g, byte b, byte a, bool alphaUsed)
    {
        var writer = new LsbBitWriter();
        writer.WriteBits(0x2F, 8);           // signature
        writer.WriteBits(width - 1, 14);     // width - 1
        writer.WriteBits(height - 1, 14);    // height - 1
        writer.WriteBits(alphaUsed ? 1 : 0, 1);
        writer.WriteBits(0, 3);              // version

        writer.WriteBit(0);                  // 无 transform
        writer.WriteBit(0);                  // 无 color cache
        writer.WriteBit(0);                  // 无 meta-Huffman

        WriteSimpleHuffman(writer, g);       // GREEN
        WriteSimpleHuffman(writer, r);       // RED
        WriteSimpleHuffman(writer, b);       // BLUE
        WriteSimpleHuffman(writer, a);       // ALPHA
        WriteSimpleHuffman(writer, 0);       // DISTANCE

        // 所有表均为单符号：像素循环不需要任何位
        return BuildRiff(("VP8L", writer.ToArray()));
    }

    /// <summary>
    /// simple_code=1, num_symbols=1, 符号用 1 bit（符号 0/1）或 8 bit 表示。
    /// 对照 libwebp ReadHuffmanCode 的简单模式。
    /// </summary>
    private static void WriteSimpleHuffman(LsbBitWriter writer, int symbol)
    {
        writer.WriteBit(1);                    // simple_code = 1
        writer.WriteBits(0, 1);                // num_symbols - 1 = 0
        bool oneBit = symbol <= 1;
        writer.WriteBits(oneBit ? 0 : 1, 1);   // first_symbol_len_code
        writer.WriteBits(symbol, oneBit ? 1 : 8);
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

/// <summary>逐像素断言帮助类。期望与实际均为 R,G,B,A 排列。</summary>
internal static class PixelAssert
{
    public static void Equal(byte[] expected, byte[] actual, int width, int height)
    {
        Assert.True(expected.Length == width * height * 4,
            $"expected 长度 {expected.Length} 与尺寸 {width}x{height} 不匹配");
        Assert.True(actual.Length == width * height * 4,
            $"actual 长度 {actual.Length} 与尺寸 {width}x{height} 不匹配");

        int diffs = 0;
        int firstDiff = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                if (firstDiff < 0) firstDiff = i;
                diffs++;
            }
        }

        if (diffs > 0)
        {
            int x = (firstDiff / 4) % width;
            int y = (firstDiff / 4) / width;
            throw new Xunit.Sdk.XunitException(
                $"像素差异数: {diffs}/{expected.Length}。首个差异在 ({x},{y}) 偏移 {firstDiff}: " +
                $"期望 R={expected[firstDiff / 4 * 4 + 0]} G={expected[firstDiff / 4 * 4 + 1]} " +
                $"B={expected[firstDiff / 4 * 4 + 2]} A={expected[firstDiff / 4 * 4 + 3]}, " +
                $"实际 R={actual[firstDiff / 4 * 4 + 0]} G={actual[firstDiff / 4 * 4 + 1]} " +
                $"B={actual[firstDiff / 4 * 4 + 2]} A={actual[firstDiff / 4 * 4 + 3]}");
        }
    }
}
