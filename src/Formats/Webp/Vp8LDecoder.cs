using System;
using System.Runtime.InteropServices;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8L 无损解码器，对照 libwebp 的 DecodeImageStream 递归流程实现。
/// </summary>
public static class Vp8LDecoder
{
    private const int MaxDimension = 16383; // 14-bit 最大尺寸 - 1
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int MIN_TRANSFORM_BITS = 2;
    private const int NUM_TRANSFORM_BITS = 3;
    private const int MIN_HUFFMAN_BITS = 2;
    private const int NUM_HUFFMAN_BITS = 3;
    private const int MaxCacheBits = 11;

    // Huffman 字母表大小（对照 libwebp kAlphabetSize）
    private const int GreenAlphabetBase = NumLiteralCodes + NumLengthCodes; // 280
    private const int RedAlphabetSize = NumLiteralCodes;
    private const int BlueAlphabetSize = NumLiteralCodes;
    private const int AlphaAlphabetSize = NumLiteralCodes;
    private const int DistanceAlphabetSize = 40;

    /// <summary>
    /// 从 VP8L chunk 数据解码到新分配的 RGBA 缓冲区。
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height)
    {
        ParseHeader(data, out width, out height);
        var result = new byte[checked(width * height * 4)];
        DecodeInto(data, result, out _, out _);
        return result;
    }

    /// <summary>
    /// 从 VP8L chunk 数据解码，直接写入调用方提供的 RGBA 缓冲区。
    /// </summary>
    /// <remarks>
    /// 解码期间把 <paramref name="dst"/> 的前 <c>width*height*4</c> 字节当作 ARGB
    /// <see cref="uint"/> 工作区就地做逆变换，最后做一次 ARGB→RGBA 的原地字节交换。
    /// 相比"先分配 uint[] 工作区再分配 byte[] 输出"，整幅内存占用减半
    /// （143 MP 图像从约 1.09 GB 降到约 545 MB）。
    /// </remarks>
    /// <param name="data">VP8L chunk 负载（含 5 字节帧头）。</param>
    /// <param name="dst">输出缓冲区，长度至少 width*height*4。</param>
    /// <param name="width">输出宽度。</param>
    /// <param name="height">输出高度。</param>
    public static void DecodeInto(ReadOnlySpan<byte> data, Span<byte> dst, out int width, out int height)
    {
        if (data.Length < 5) throw new WebpBitstreamException("VP8L 数据过短（需要至少 5 字节帧头）");

        var reader = new LsbBitReader(data);
        ReadHeader(ref reader, out width, out height);

        int pixelBytes = checked(width * height * 4);
        if (dst.Length < pixelBytes)
            throw new ArgumentException($"输出缓冲区不足：需要 {pixelBytes} 字节，实际 {dst.Length}", nameof(dst));

        var argb = MemoryMarshal.Cast<byte, uint>(dst[..pixelBytes]);
        var final = DecodeImageStreamCore(width, height, isLevel0: true, ref reader, argb);

        if (final.Length != argb.Length)
            throw new WebpBitstreamException($"解码输出像素数 {final.Length} 与预期 {argb.Length} 不符");
        if (!final.Overlaps(argb))
            final.CopyTo(argb);

        // ARGB（内存字节序 B,G,R,A）→ RGBA（R,G,B,A），原地交换 R/B 两个通道。
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            argb[i] = (p & 0xFF00FF00u) | ((p >> 16) & 0xFFu) | ((p & 0xFFu) << 16);
        }
    }

    /// <summary>只解析 VP8L 帧头拿宽高，不解码像素（用于预分配零拷贝输出缓冲区）。</summary>
    public static void ReadSize(ReadOnlySpan<byte> data, out int width, out int height)
        => ParseHeader(data, out width, out height);

    /// <summary>解析 VP8L 5 字节帧头（可独立用于只取尺寸）。</summary>
    private static void ParseHeader(ReadOnlySpan<byte> data, out int width, out int height)
    {
        if (data.Length < 5) throw new WebpBitstreamException("VP8L 数据过短（需要至少 5 字节帧头）");
        var reader = new LsbBitReader(data);
        ReadHeader(ref reader, out width, out height);
    }

    private static void ReadHeader(ref LsbBitReader reader, out int width, out int height)
    {
        // ---- VP8L 帧头：signature(8) + width-1(14) + height-1(14) + alpha(1) + version(3) ----
        byte signature = (byte)reader.ReadBits(8);
        if (signature != 0x2F) throw new WebpBitstreamException($"VP8L 签名错误：0x{signature:X2}（应为 0x2F）");

        width = (int)reader.ReadBits(14) + 1;
        height = (int)reader.ReadBits(14) + 1;
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new WebpBitstreamException($"VP8L 尺寸 {width}x{height} 超出规格");
        WebpDecoder.ValidatePixelCount(width, height);

        _ = reader.ReadBits(1); // alpha_used
        int version = (int)reader.ReadBits(3);
        if (version != 0)
            throw new WebpUnsupportedException($"VP8L version={version} 不支持（本实现只支持 version=0）");
    }

    /// <summary>
    /// 解码“裸”VP8L 图像流（无 5 字节帧头），用于 ALPH chunk 的 lossless 压缩负载。
    /// 对照 libwebp VP8LDecodeAlphaHeader 之后的 DecodeImageStream(width, height, 1, ...)。
    /// </summary>
    internal static uint[] DecodeBareStream(ReadOnlySpan<byte> data, int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new WebpBitstreamException($"VP8L 裸流尺寸 {width}x{height} 超出规格");
        var reader = new LsbBitReader(data);
        var buffer = new uint[checked(width * height)];
        var span = DecodeImageStreamCore(width, height, isLevel0: true, ref reader, buffer);
        return span.Length == buffer.Length ? buffer : span.ToArray();
    }

    /// <summary>
    /// 描述一个 transform 的完整信息（对照 libwebp VP8LTransform）。
    /// XSize / YSize = 原始图像尺寸（变换应用前），SubXSize / SubYSize = sub-sample 后（仅 color indexing 会变）。
    /// </summary>
    private readonly record struct TransformInfo(
        Vp8LTransforms.TransformType Type,
        int Bits,
        int XSize,
        int YSize,
        int SubXSize,
        int SubYSize,
        uint[]? Data
    );

    /// <summary>
    /// 递归解码：VP8L 的核心流程。对照 libwebp DecodeImageStream()。
    /// </summary>
    /// <param name="level0Dst">
    /// 仅 level 0 使用：调用方提供的整幅工作区（ARGB）。长度不足时退化为内部新建。
    /// </param>
    private static Span<uint> DecodeImageStreamCore(int xsize, int ysize, bool isLevel0,
                                                    ref LsbBitReader reader, Span<uint> level0Dst)
    {
        int transformXSize = xsize;
        int transformYSize = ysize;
        var transforms = new List<TransformInfo>();

        // ---- 1. 读取 transforms（仅 level 0） ----
        if (isLevel0)
        {
            int transformsSeen = 0;
            while (reader.ReadBit() != 0)
            {
                int type = (int)reader.ReadBits(2);
                if ((transformsSeen & (1 << type)) != 0)
                    throw new WebpBitstreamException($"transform type={type} 在流中重复出现");
                transformsSeen |= 1 << type;
                switch (type)
                {
                    case 0: // PREDICTOR_TRANSFORM
                    case 1: // CROSS_COLOR_TRANSFORM
                    {
                        int bits = MIN_TRANSFORM_BITS + (int)reader.ReadBits(NUM_TRANSFORM_BITS);
                        int subX = SubSampleSize(transformXSize, bits);
                        int subY = SubSampleSize(transformYSize, bits);
                        var dataPixels = DecodeImageStreamCore(subX, subY, isLevel0: false, ref reader, default).ToArray();
                        transforms.Add(new TransformInfo(
                            (Vp8LTransforms.TransformType)type, bits,
                            transformXSize, transformYSize,
                            transformXSize, transformYSize, // predictor/color 不改图像尺寸
                            dataPixels));
                        break;
                    }
                    case 2: // SUBTRACT_GREEN_TRANSFORM
                        transforms.Add(new TransformInfo(
                            Vp8LTransforms.TransformType.SubtractGreen, 0,
                            transformXSize, transformYSize,
                            transformXSize, transformYSize, null));
                        break;
                    case 3: // COLOR_INDEXING_TRANSFORM
                    {
                        int numColors = (int)reader.ReadBits(8) + 1;
                        if (numColors > 256)
                            throw new WebpBitstreamException($"Color indexing: num_colors={numColors} 超出上限 256");
                        int bitsPerPixel;
                        if (numColors > 16) bitsPerPixel = 0;
                        else if (numColors > 4) bitsPerPixel = 1;
                        else if (numColors > 2) bitsPerPixel = 2;
                        else bitsPerPixel = 3;

                        int origX = transformXSize;
                        int origY = transformYSize;
                        // Color Indexing 只在水平方向 sub-sample（pack 索引），垂直方向保持 origY
                        int subX = SubSampleSize(origX, bitsPerPixel);
                        int subY = origY;
                        var palettePixels = DecodeImageStreamCore(numColors, 1, isLevel0: false, ref reader, default).ToArray();
                        // 调色板按字节差分展开并补黑到尾（libwebp ExpandColorMap）。
                        uint[] palette = Vp8LTransforms.ExpandPalette(
                            palettePixels.AsSpan(0, numColors), numColors, bitsPerPixel);
                        transformXSize = subX;
                        transformYSize = subY;
                        transforms.Add(new TransformInfo(
                            Vp8LTransforms.TransformType.ColorIndexing, bitsPerPixel,
                            origX, origY,
                            subX, subY,
                            palette));
                        break;
                    }
                    default:
                        throw new WebpBitstreamException($"未知的 transform type={type}");
                }
            }
        }

        // ---- 2. Color cache ----
        int colorCacheBits = 0;
        if (reader.ReadBit() != 0)
        {
            colorCacheBits = (int)reader.ReadBits(4);
            if (colorCacheBits < 1 || colorCacheBits > MaxCacheBits)
                throw new WebpBitstreamException($"color_cache_bits={colorCacheBits} 超出 1..{MaxCacheBits}");
        }

        var colorCache = colorCacheBits > 0 ? new Vp8LColorCache(colorCacheBits) : null;

        // ---- 3. 读 Huffman 码（对照 libwebp ReadHuffmanCodes）----
        HuffmanTable[] tables;
        int numHuffmanGroups;
        uint[]? huffmanImage = null;
        int[]? groupMapping = null;   // 非 null 时：磁盘 group id -> 紧凑 group id（-1 表示未使用）
        int groupsToRead = 1;         // 位流中实际存在的 group 数量（= max group id + 1）
        int huffStride = 0;
        int huffmanPrecision = 0;

        if (isLevel0 && reader.ReadBit() != 0)
        {
            // === Meta-Huffman 模式 ===
            huffmanPrecision = MIN_HUFFMAN_BITS + (int)reader.ReadBits(NUM_HUFFMAN_BITS);
            int huffXSize = SubSampleSize(transformXSize, huffmanPrecision);
            int huffYSize = SubSampleSize(transformYSize, huffmanPrecision);
            huffmanImage = DecodeImageStreamCore(huffXSize, huffYSize, isLevel0: false, ref reader, default).ToArray();
            huffStride = huffXSize;

            // huffman image 的绿色字节存放 group id（libwebp: (pixel >> 8) & 0xffff）。
            int huffmanPixels = huffmanImage.Length;
            int maxGroup = 1;
            for (int i = 0; i < huffmanPixels; i++)
            {
                int g = (int)((huffmanImage[i] >> 8) & 0xFFFF);
                huffmanImage[i] = (uint)g;
                if (g + 1 > maxGroup) maxGroup = g + 1;
            }
            if (maxGroup > 65536)
                throw new WebpBitstreamException($"meta-Huffman group 数 {maxGroup} 超出 65536");

            groupsToRead = maxGroup;

            // 与 libwebp 相同：组数过大时建立紧凑映射，只保存实际使用的组。
            if (maxGroup > 200 || maxGroup > huffmanPixels)
            {
                var mapping = new int[maxGroup];
                Array.Fill(mapping, -1);
                int used = 0;
                for (int i = 0; i < huffmanPixels; i++)
                {
                    int g = (int)huffmanImage[i];
                    if (mapping[g] == -1) mapping[g] = used++;
                    huffmanImage[i] = (uint)mapping[g];
                }
                numHuffmanGroups = used;
                groupMapping = mapping;
            }
            else
            {
                numHuffmanGroups = maxGroup;
            }

            if (numHuffmanGroups == 1)
            {
                huffmanImage = null;
                huffStride = 0;
                huffmanPrecision = 0;
            }
        }
        else
        {
            // === 独立 Huffman 模式（isLevel0=false 也走这里）===
            numHuffmanGroups = 1;
        }

        int greenAlphabet = GreenAlphabetBase + (colorCacheBits > 0 ? (1 << colorCacheBits) : 0);
        int[] alphabets = { greenAlphabet, RedAlphabetSize, BlueAlphabetSize, AlphaAlphabetSize, DistanceAlphabetSize };
        tables = new HuffmanTable[numHuffmanGroups * 5];
        // 位流中始终有 groupsToRead 组表（含未使用组）；未使用组读入并校验后丢弃。
        for (int g = 0; g < groupsToRead; g++)
        {
            int dest = groupMapping == null ? g : groupMapping[g];
            for (int i = 0; i < 5; i++)
            {
                var table = new HuffmanTable(Vp8LHuffman.ReadOneHuffmanCode(ref reader, alphabets[i]));
                if (dest >= 0) tables[dest * 5 + i] = table;
            }
        }

        // ---- 4. 像素解码 ----
        int totalPixels = transformXSize * transformYSize;
        Span<uint> argb = (isLevel0 && level0Dst.Length >= totalPixels)
            ? level0Dst[..totalPixels]
            : new uint[totalPixels];

        DecodePixels(ref reader, tables, argb, colorCache, transformXSize,
                     huffmanImage, huffStride, huffmanPrecision, numHuffmanGroups);

        // ---- 5. 逆变换（对照 libwebp ApplyInverseTransforms：逆序应用） ----
        // 注意：TransformInfo.XSize/YSize 始终是变换应用前的尺寸。
        // Color Indexing 是最后应用的，所以它的逆变换最先做，会把 argb 从 sub 尺寸上采样回原始尺寸。
        for (int t = transforms.Count - 1; t >= 0; t--)
        {
            var transform = transforms[t];
            switch (transform.Type)
            {
                case Vp8LTransforms.TransformType.Predictor:
                    if (transform.Data != null)
                        Vp8LTransforms.ApplyPredictorInverse(argb,
                                                              transform.XSize, transform.YSize,
                                                              transform.Data, transform.Bits);
                    break;
                case Vp8LTransforms.TransformType.CrossColor:
                    if (transform.Data != null)
                        Vp8LTransforms.ApplyColorTransformInverse(argb,
                                                                   transform.XSize, transform.YSize,
                                                                   transform.Data, transform.Bits);
                    break;
                case Vp8LTransforms.TransformType.SubtractGreen:
                    Vp8LTransforms.ApplySubtractGreenInverse(argb,
                                                             transform.XSize, transform.YSize);
                    break;
                case Vp8LTransforms.TransformType.ColorIndexing:
                    if (transform.Data != null)
                    {
                        var expanded = Vp8LTransforms.ApplyColorIndexingInverse(
                            argb,
                            transform.SubXSize,  // src 宽度 = SubSampleSize(origWidth, bits)
                            transform.YSize,     // src 高度 = origHeight (ColorIndexing 只 sub-sample 水平)
                            transform.XSize, transform.YSize,
                            transform.Data, transform.Bits);

                        // level 0 且工作区足够大时，把上采样结果摊回调用方缓冲区，
                        // 保证整幅只存在一份（33% 体积的调色板图才走这条分支）。
                        if (isLevel0 && level0Dst.Length >= expanded.Length)
                        {
                            expanded.AsSpan().CopyTo(level0Dst);
                            argb = level0Dst[..expanded.Length];
                        }
                        else
                        {
                            argb = expanded;
                        }
                    }
                    break;
            }
        }

        return argb;
    }

    private const int LenCodeLimit = NumLiteralCodes + NumLengthCodes; // 256 + 24 = 280

    /// <summary>
    /// 核心像素循环：用 Huffman + LZ77 解码到 argb 缓冲区。
    /// 对照 libwebp DecodeImageData 的内层循环。支持多组 meta-Huffman。
    /// </summary>
    private static void DecodePixels(ref LsbBitReader reader, HuffmanTable[] tables,
                                     Span<uint> argb, Vp8LColorCache? colorCache, int width,
                                     uint[]? huffmanImage, int huffStride, int huffPrecision, int numGroups)
    {
        if (tables.Length < numGroups * 5)
            throw new WebpBitstreamException($"需要 {numGroups * 5} 个 Huffman 表");

        int totalPixels = argb.Length;
        int colorCacheLimit = LenCodeLimit + (colorCache != null ? colorCache.Size : 0);

        int pos = 0;
        int col = 0;
        int row = 0;
        int groupIdx = 0;

        while (pos < totalPixels)
        {
            // meta-Huffman：每个符号按当前 (col, row) 重新选择 group。
            // libwebp 在符号解码前取 GetHtreeGroupForPos；LZ77 拷贝跨越 tile 后也会重新取，
            // 因此这里无条件按当前位置查表（等价语义，且拷贝跨越边界时不会用错组）。
            if (huffmanImage != null)
            {
                int hIdx = (row >> huffPrecision) * huffStride + (col >> huffPrecision);
                if ((uint)hIdx >= (uint)huffmanImage.Length)
                    throw new WebpBitstreamException($"meta-Huffman 索引 {hIdx} 超出范围 0~{huffmanImage.Length - 1}");
                int g = (int)huffmanImage[hIdx];
                if ((uint)g >= (uint)numGroups)
                    throw new WebpBitstreamException($"meta-Huffman group {g} 超出范围 0~{numGroups - 1}");
                groupIdx = g;
            }

            var greenTable = tables[groupIdx * 5 + 0];
            var redTable = tables[groupIdx * 5 + 1];
            var blueTable = tables[groupIdx * 5 + 2];
            var alphaTable = tables[groupIdx * 5 + 3];
            var distanceTable = tables[groupIdx * 5 + 4];

            int code = greenTable.DecodeSymbol(ref reader);

            if (code < NumLiteralCodes)
            {
                // ---- Literal：一个像素 ----
                byte g = (byte)code;
                byte r = (byte)redTable.DecodeSymbol(ref reader);
                byte b = (byte)blueTable.DecodeSymbol(ref reader);
                byte a = (byte)alphaTable.DecodeSymbol(ref reader);

                uint pixel = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                argb[pos] = pixel;
                if (colorCache != null) colorCache.Insert(pixel);

                pos++;
                col++;
                if (col >= width) { col = 0; row++; }
            }
            else if (code < LenCodeLimit)
            {
                // ---- LZ77 backward reference ----
                int lengthSym = code - NumLiteralCodes;
                int length = Vp8LDistance.DecodeLength(lengthSym, ref reader);
                int distSymbol = distanceTable.DecodeSymbol(ref reader);
                int distance = Vp8LDistance.DecodeDistance(distSymbol, width, ref reader);
                if (distance > pos)
                    throw new WebpBitstreamException($"LZ77 距离 {distance} 超出已解码范围（pos={pos}）");
                if (length > totalPixels - pos)
                    throw new WebpBitstreamException($"LZ77 长度 {length} 超出剩余像素（剩余 {totalPixels - pos}）");
                int copyFrom = pos - distance;
                for (int i = 0; i < length; i++)
                {
                    uint pixel = argb[copyFrom + i];
                    argb[pos + i] = pixel;
                    if (colorCache != null) colorCache.Insert(pixel);
                }

                int advanced = col + length;
                row += advanced / width;
                col = advanced % width;
                pos += length;
            }
            else if (code < colorCacheLimit)
            {
                // ---- Color cache 引用 ----
                if (colorCache == null)
                    throw new WebpBitstreamException("green Huffman 码引用颜色缓存，但 color cache 未启用");
                int key = code - LenCodeLimit;
                uint pixel = colorCache.Lookup(key);
                argb[pos] = pixel;
                colorCache.Insert(pixel);

                pos++;
                col++;
                if (col >= width) { col = 0; row++; }
            }
            else
            {
                throw new WebpBitstreamException($"green Huffman 码 {code} 不在有效区间");
            }
        }

        if (pos != totalPixels)
            throw new WebpBitstreamException($"VP8L 解码像素数 {pos} 与预期 {totalPixels} 不符");
    }

    /// <summary>
    /// 子采样：对照 libwebp 的 VP8LSubSampleSize()。
    /// </summary>
    private static int SubSampleSize(int size, int bits)
    {
        return (size + (1 << bits) - 1) >> bits;
    }
}
