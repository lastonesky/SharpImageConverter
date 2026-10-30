using System;
using System.Collections.Generic;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Processing;

namespace SharpImageConverter;

/// <summary>
/// CLI 解析后的全部选项。
/// </summary>
/// <remarks>
/// 从 <c>Program.cs</c> 抽出来单独成文件：解析逻辑本身留在 <c>Program.ParseOptions</c>，
/// 这里只描述「有哪些开关、默认值是什么」，避免 1800 行的入口文件里再夹一份数据定义。
/// </remarks>
internal sealed class CliOptions(string inputPath)
{
    /// <summary>输入文件或目录路径（第一个位置参数）。</summary>
    public string InputPath { get; } = inputPath;

    /// <summary>输出文件或目录路径（第二个位置参数，可省略）。</summary>
    public string? OutputPath { get; set; }

    /// <summary>解析过程中无法识别的参数（未知开关或多出来的位置参数）。非空即用法错误。</summary>
    public List<string> UnknownArguments { get; } = [];

    /// <summary>JPEG 质量（1-100）。</summary>
    public int? JpegQuality { get; set; }

    /// <summary>是否使用 4:2:0 子采样。</summary>
    public bool? Subsample420 { get; set; }

    /// <summary>JPEG 色度子采样方式。</summary>
    public JpegChromaSubsampling? JpegSubsampling { get; set; }

    /// <summary>是否保留元数据（EXIF/ICC）。</summary>
    public bool KeepMetadata { get; set; }

    /// <summary>是否把 GIF 动画拆成单帧序列。</summary>
    public bool GifFrames { get; set; }

    /// <summary>GIF 编码是否启用抖动。</summary>
    public bool Dithering { get; set; } = true;

    /// <summary>是否使用浮点 IDCT（仅在 4:4:4 等支持路径上生效）。</summary>
    public bool UseFloatIdct { get; set; }

    /// <summary>是否强制走逐行流式解码。</summary>
    public bool UseStreamingDecoder { get; set; }

    /// <summary>严格模式：要求 JPEG 数据完整（真实 EOI）。</summary>
    public bool RequireEoi { get; set; }

    /// <summary>是否转为灰度。</summary>
    public bool Gray { get; set; }

    /// <summary>输出 JPEG 分阶段耗时（走 <c>Trace</c>）。</summary>
    public bool JpegDebug { get; set; }

    /// <summary>输出 GIF 分阶段耗时。</summary>
    public bool GifDebug { get; set; }

    /// <summary>GIF 编码基准重复次数；为 null 表示不做基准。</summary>
    public int? GifBench { get; set; }

    /// <summary>JPEG 编码基准重复次数；为 null 表示不做基准。</summary>
    public int? JpegBench { get; set; }

    /// <summary>GIF 量化器："octree"（默认，八叉树 + Bayer）或 "wu"/"legacy"/"fs"。</summary>
    public string? GifQuantizer { get; set; }

    /// <summary>Bayer 抖动幅度（默认 8 = 一个量化步长），仅 octree 量化器生效。</summary>
    public int GifDitherStrength { get; set; } = 8;

    /// <summary>图像处理操作序列（resize/grayscale 等），按给出顺序依次执行。</summary>
    public List<Action<ImageProcessingContext>> Operations { get; } = [];

    /// <summary>输入是否为目录。</summary>
    public bool IsDirectoryInput { get; set; }

    /// <summary>目录输入时是否递归子目录。</summary>
    public bool Recursive { get; set; }

    /// <summary>统一的目标扩展名（<c>--to</c>）。</summary>
    public string? OutputExtension { get; set; }

    /// <summary>目录批量的外层并行度（<c>--parallel</c>）。</summary>
    public int? Parallelism { get; set; }

    /// <summary>目录批量时跳过已存在的输出。</summary>
    public bool SkipExisting { get; set; }

    /// <summary>是否走智能压缩。</summary>
    public bool Optimize { get; set; }

    /// <summary>optimize 的目标画质。</summary>
    public int? OptimizeQuality { get; set; }

    /// <summary>WebP 小图的最低搜索质量。</summary>
    public int? OptimizeWebpMin { get; set; }

    /// <summary>WebP 大图的最低搜索质量。</summary>
    public int? OptimizeWebpLargeMin { get; set; }

    /// <summary>有损压缩的最小收益比（百分比）。</summary>
    public int? OptimizeLossyMinGain { get; set; }

    /// <summary>SSIM 下限（百分比）。</summary>
    public int? OptimizeMinSsim { get; set; }

    /// <summary>最差分块 SSIM 下限（百分比）。</summary>
    public int? OptimizeMinWorstSsim { get; set; }

    /// <summary>PNG/GIF 调色板最大颜色数。</summary>
    public int? MaxColors { get; set; }

    /// <summary>optimize 时不使用抖动。</summary>
    public bool NoDither { get; set; }

    /// <summary>optimize 时不对 PNG 赢家补 zlib-9 重压。</summary>
    public bool NoPngDeflate9 { get; set; }

    /// <summary>是否输出渐进式 JPEG。</summary>
    public bool JpegProgressive { get; set; }

    /// <summary>optimize 时不输出渐进式 JPEG。</summary>
    public bool NoJpegProgressive { get; set; }

    /// <summary>optimize 时比较不同色度子采样。</summary>
    public bool JpegSubsampleCompare { get; set; }

    /// <summary>optimize 时不比较色度子采样。</summary>
    public bool NoJpegSubsampleCompare { get; set; }

    /// <summary>是否启用 JPEG RD 优化量化（trellis）。</summary>
    public bool JpegTrellis { get; set; }

    /// <summary>内存预算（MB）。</summary>
    public long? MemoryLimitMb { get; set; }

    /// <summary>跳过内存预检。</summary>
    public bool NoMemoryGuard { get; set; }

    /// <summary>强制走流式转换。</summary>
    public bool ForceStreaming { get; set; }

    /// <summary>optimize 时输出候选评估日志。</summary>
    public bool OptimizeVerbose { get; set; }

    /// <summary>最小体积收益比（百分比），低于该值保留原图。</summary>
    public int? MinSaving { get; set; }

    /// <summary>
    /// 允许 WebP 编码绕过 <c>WebpCodec</c> 的全局 EncodeLock，从而让**外层**文件级并行真正并发。
    /// 只在「纯转换路径 + 外层已并行」时由目录批量打开：该路径没有候选并行可叠加，
    /// 而默认的 EncodeLock 会把外层并行整体串行化。
    /// </summary>
    public bool ParallelWebpEncode { get; set; }

    /// <summary>
    /// libwebp 的 <c>config.method</c>（0~6），null 表示用库默认（<see cref="WebpEncoderOptions"/> 的 3）。
    /// </summary>
    public int? WebpMethod { get; set; }

    /// <summary>
    /// 机器可读输出：结果以单个 JSON 对象打印到 stdout，逐文件的进度行不再打印。
    /// 退出码语义不变（0/1/2），脚本可同时用两者判断。
    /// </summary>
    public bool Json { get; set; }

    /// <summary>
    /// 安静模式：不打印任何进度/汇总信息，只把错误写到 stderr。
    /// 与 <see cref="Json"/> 同时给出时以安静为准（两者都不打印）——明确要求"别说话"优先。
    /// </summary>
    public bool Quiet { get; set; }
}
