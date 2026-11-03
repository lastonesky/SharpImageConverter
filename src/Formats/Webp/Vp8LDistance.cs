namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8L LZ77 反向引用距离/长度映射，严格对照 libwebp GetCopyDistance / GetCopyLength / PlaneCodeToDistance。
/// </summary>
internal static class Vp8LDistance
{
    private const int CodeToPlaneCodes = 120;

    /// <summary>PlaneCodeToDistance 用的查找表（libwebp kCodeToPlane）。</summary>
    private static readonly byte[] CodeToPlane =
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a,
        0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04,
        0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45,
        0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
        0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e,
        0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f, 0x64, 0x6c, 0x42, 0x4e,
        0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
        0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d,
        0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    /// <summary>
    /// 获取"distance prefix" 对应的实际值（可能是 distance 或 length）。
    /// 对照 libwebp GetCopyDistance / GetCopyLength（两者公式完全一致）。
    /// </summary>
    /// <param name="prefixSymbol">0..39 之间的 Huffman 解码值</param>
    /// <param name="reader">位流读取器</param>
    public static int GetCopyValue(int prefixSymbol, ref LsbBitReader reader)
    {
        if (prefixSymbol < 4)
            return prefixSymbol + 1;

        int extraBits = (prefixSymbol - 2) >> 1;
        int offset = (2 + (prefixSymbol & 1)) << extraBits;
        return offset + (int)reader.ReadBits(extraBits) + 1;
    }

    /// <summary>
    /// 将 distance prefix（GetCopyDistance 的结果）进一步映射到 2D 平面距离。
    /// 对照 libwebp PlaneCodeToDistance。
    /// </summary>
    /// <param name="xsize">图像宽度</param>
    /// <param name="planeCode">GetCopyDistance 返回值</param>
    /// <returns>2D 平面线性距离（≥1）</returns>
    public static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > CodeToPlaneCodes)
            return planeCode - CodeToPlaneCodes;

        int distCode = CodeToPlane[planeCode - 1];
        int yoffset = distCode >> 4;
        int xoffset = 8 - (distCode & 0xF);
        int dist = yoffset * xsize + xoffset;
        return dist >= 1 ? dist : 1;
    }

    /// <summary>
    /// LZ77 backward reference：解码距离并应用 PlaneCodeToDistance。
    /// </summary>
    /// <param name="distSymbol">distance Huffman 解码值（0..39）</param>
    /// <param name="xsize">图像宽度</param>
    /// <param name="reader">位流读取器</param>
    public static int DecodeDistance(int distSymbol, int xsize, ref LsbBitReader reader)
    {
        if (distSymbol < 0 || distSymbol > 39)
            throw new WebpBitstreamException($"距离码 {distSymbol} 超出范围 0-39");

        int distCode = GetCopyValue(distSymbol, ref reader);
        return PlaneCodeToDistance(xsize, distCode);
    }

    /// <summary>
    /// LZ77 backward reference：解码匹配长度。
    /// green Huffman 码值 = 256 + lengthSymbol（lengthSymbol 0..23）。
    /// </summary>
    /// <param name="lenSymbol">0..23 之间的 length prefix 符号</param>
    /// <param name="reader">位流读取器</param>
    public static int DecodeLength(int lenSymbol, ref LsbBitReader reader)
    {
        if (lenSymbol < 0 || lenSymbol > 23)
            throw new WebpBitstreamException($"长度 prefix 码 {lenSymbol} 超出范围 0-23");

        return GetCopyValue(lenSymbol, ref reader);
    }
}
