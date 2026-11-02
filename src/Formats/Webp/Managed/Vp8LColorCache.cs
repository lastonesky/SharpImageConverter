namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// VP8L 颜色缓存：最近使用的颜色值表。
/// 当 color_cache_bits &gt; 0 时启用，缓存大小 = 1 &lt;&lt; color_cache_bits。
/// 解码时 green Huffman 码 ≥ 280（NUM_LITERAL_CODES+NUM_LENGTH_CODES）表示引用缓存。
/// 哈希算法严格对照 libwebp color_cache_utils.h：hash = (argb * 0x1e35a7bd) >> (32 - bits)。
/// </summary>
internal sealed class Vp8LColorCache
{
    private const uint HashMultiplier = 0x1e35a7bdu;

    private readonly int _bits;

    /// <summary>缓存条目数。</summary>
    public int Size { get; }

    /// <summary>缓存条目，每个是 ARGB（A 在高字节，便于直接写入像素缓冲区）。</summary>
    public uint[] Entries { get; }

    public Vp8LColorCache(int colorCacheBits)
    {
        if (colorCacheBits < 1 || colorCacheBits > 11)
            throw new WebpBitstreamException($"color_cache_bits 超出范围 1-11: {colorCacheBits}");

        _bits = colorCacheBits;
        Size = 1 << colorCacheBits;
        Entries = new uint[Size];
    }

    /// <summary>
    /// 向缓存写入一个颜色值（按 VP8LColorCacheInsert 语义：hash 取模后覆盖）。
    /// 解码器对每个产出的像素都必须调用本方法。
    /// </summary>
    public void Insert(uint argb)
    {
        Entries[ComputeHash(argb, _bits)] = argb;
    }

    /// <summary>
    /// 从缓存读取一个颜色值（按 key 直接索引）。
    /// </summary>
    public uint Lookup(int key)
    {
        if ((uint)key >= (uint)Size)
            throw new WebpBitstreamException($"颜色缓存索引 {key} 超出范围 0~{Size - 1}");
        return Entries[key];
    }

    /// <summary>
    /// 计算颜色哈希（对照 libwebp VP8LHashPix：uint32 乘法回绕后右移）。
    /// </summary>
    public static int ComputeHash(uint argb, int bits)
    {
        return (int)((argb * HashMultiplier) >> (32 - bits));
    }
}
