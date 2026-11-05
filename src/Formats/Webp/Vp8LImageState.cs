namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// 描述一个 VP8L transform 的完整信息（对照 libwebp VP8LTransform）。
/// XSize / YSize = 变换应用前的图像尺寸；SubXSize / SubYSize = sub-sample 后的尺寸
/// （仅 color indexing 会改变尺寸）。
/// </summary>
internal readonly record struct Vp8LTransformInfo(
    Vp8LTransforms.TransformType Type,
    int Bits,
    int XSize,
    int YSize,
    int SubXSize,
    int SubYSize,
    uint[]? Data);

/// <summary>
/// VP8L 图像流的“头部状态”：transform 列表、颜色缓存与 Huffman 表。
/// 整图解码与行增量解码共用这一份解析结果，避免两条路径对位流的解读出现分歧。
/// </summary>
internal sealed class Vp8LImageState
{
    /// <summary>原始图像宽度（所有逆变换还原后的宽度）。</summary>
    public int Width { get; init; }

    /// <summary>原始图像高度（所有逆变换还原后的高度）。</summary>
    public int Height { get; init; }

    /// <summary>变换域宽度（= 所有 transform 应用后的宽度，也是 LZ77 像素流的行宽）。</summary>
    public int DecodeWidth { get; init; }

    /// <summary>变换域高度。</summary>
    public int DecodeHeight { get; init; }

    /// <summary>按位流顺序读取到的 transform 列表（逆变换需逆序应用）。</summary>
    public List<Vp8LTransformInfo> Transforms { get; set; } = new();

    /// <summary>颜色缓存；未启用时为 null。</summary>
    public Vp8LColorCache? ColorCache { get; set; }

    /// <summary>Huffman 表（按 group 紧凑排列，每组 5 张：green/red/blue/alpha/distance）。</summary>
    public HuffmanTable[] Tables { get; set; } = Array.Empty<HuffmanTable>();

    /// <summary>meta-Huffman 的 group 映射图像（按量化精度降采样）；独立 Huffman 模式为 null。</summary>
    public uint[]? HuffmanImage { get; set; }

    /// <summary>meta-Huffman 图像的跨行步长（像素数）。</summary>
    public int HuffStride { get; set; }

    /// <summary>meta-Huffman 的分组精度（group 像素块为 2^precision 见方）。</summary>
    public int HuffPrecision { get; set; }

    /// <summary>实际使用的 Huffman 组数（紧凑编号后的数量）。</summary>
    public int NumGroups { get; set; }
}
