namespace SharpImageConverter.Formats.Webp.Managed;

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
    /// 从 VP8L chunk 数据解码。
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height)
    {
        if (data.Length < 5) throw new WebpBitstreamException("VP8L 数据过短（需要至少 5 字节帧头）");

        var reader = new LsbBitReader(data);

        // ---- 解析 VP8L 帧头（共 40 bits = 5 字节） ----
        byte signature = (byte)reader.ReadBits(8);
        if (signature != 0x2F) throw new WebpBitstreamException($"VP8L 签名错误：0x{signature:X2}（应为 0x2F）");

        uint wBits = reader.ReadBits(14);
        width = (int)wBits + 1;
        uint hBits = reader.ReadBits(14);
        height = (int)hBits + 1;

        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new WebpBitstreamException($"VP8L 尺寸 {width}x{height} 超出规格");

        int alphaUsed = (int)reader.ReadBits(1);
        int version = (int)reader.ReadBits(3);
        if (version != 0)
            throw new WebpUnsupportedException($"VP8L version={version} 不支持（本实现只支持 version=0）");

        // ---- 递归解码主图像 ----
        var pixels = DecodeImageStream(width, height, isLevel0: true, ref reader);

        // ---- 输出 ARGB → R,G,B,A 字节序列（与 libwebp WebPDecodeRGBAInto 对齐） ----
        var result = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            result[i * 4 + 0] = (byte)((p >> 16) & 0xFF); // R
            result[i * 4 + 1] = (byte)((p >> 8) & 0xFF);  // G
            result[i * 4 + 2] = (byte)(p & 0xFF);         // B
            result[i * 4 + 3] = (byte)((p >> 24) & 0xFF); // A
        }

        return result;
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
        return DecodeImageStream(width, height, isLevel0: true, ref reader);
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
    private static uint[] DecodeImageStream(int xsize, int ysize, bool isLevel0,
                                            ref LsbBitReader reader)
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
                        var dataPixels = DecodeImageStream(subX, subY, isLevel0: false, ref reader);
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
                        var palettePixels = DecodeImageStream(numColors, 1, isLevel0: false, ref reader);
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
            huffmanImage = DecodeImageStream(huffXSize, huffYSize, isLevel0: false, ref reader);
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
        var argb = new uint[totalPixels];
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
                        Vp8LTransforms.ApplyPredictorInverse(argb.AsSpan(),
                                                              transform.XSize, transform.YSize,
                                                              transform.Data, transform.Bits);
                    break;
                case Vp8LTransforms.TransformType.CrossColor:
                    if (transform.Data != null)
                        Vp8LTransforms.ApplyColorTransformInverse(argb.AsSpan(),
                                                                   transform.XSize, transform.YSize,
                                                                   transform.Data, transform.Bits);
                    break;
                case Vp8LTransforms.TransformType.SubtractGreen:
                    Vp8LTransforms.ApplySubtractGreenInverse(argb.AsSpan(),
                                                             transform.XSize, transform.YSize);
                    break;
                case Vp8LTransforms.TransformType.ColorIndexing:
                    if (transform.Data != null)
                        argb = Vp8LTransforms.ApplyColorIndexingInverse(
                            argb.AsSpan(),
                            transform.SubXSize,  // src 宽度 = SubSampleSize(origWidth, bits)
                            transform.YSize,     // src 高度 = origHeight (ColorIndexing 只 sub-sample 水平)
                            transform.XSize, transform.YSize,
                            transform.Data, transform.Bits);
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
