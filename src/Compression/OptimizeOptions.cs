using System;
using System.IO.Compression;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 智能有损压缩（TinyPNG 式）的选项。
    /// </summary>
    public sealed class OptimizeOptions
    {
        private double? _minPerceptualPsnr;

        /// <summary>
        /// 目标画质（0-100）。数值越高越保守（体积越大）。默认 88。
        /// </summary>
        /// <remarks>
        /// 该值会映射成感知质量下限 <see cref="MinPerceptualPsnr"/>：
        /// 优化器在保证画质不低于下限的前提下，尽可能选择体积最小的方案。
        /// </remarks>
        public int TargetQuality { get; set; } = 88;

        /// <summary>
        /// 感知质量下限（dB，块状平均后的 PSNR）。
        /// 未显式设置时由 <see cref="TargetQuality"/> 推导；想精细控制可直接赋值。
        /// </summary>
        /// <remarks>
        /// 标定依据（900x600 合成图，4x4 块平均 PSNR）：
        /// PNG 256 色 + 抖动在自然照片上约 48 dB、128 色约 43.6 dB、64 色约 40 dB；
        /// JPEG 照片在 q95 约 46.6 dB、q80 约 45.3 dB、q65 约 43.8 dB。
        /// 取 33 + 0.14*TargetQuality，使均衡档（88）落在 45.3 dB：
        /// 照片基本停在 256 色 / q80 附近，与「肉眼无感」的主观感受一致。
        /// </remarks>
        public double MinPerceptualPsnr
        {
            get => _minPerceptualPsnr ?? 33.0 + 0.14 * TargetQuality;
            set => _minPerceptualPsnr = value;
        }

        /// <summary>
        /// 逐像素 PSNR 下限（dB）。默认 30。
        /// </summary>
        /// <remarks>
        /// 感知度量先做 4x4 块平均，对「高频细节被抹平」不敏感（纯噪点图尤其明显），
        /// 因此再兜一条逐像素下限，避免为了压体积把画面糊掉或抖动过头。
        /// 30 dB 大致对应「256 色抖动后仍保留纹理，但 32 色以下会被拦下」的位置。
        /// </remarks>
        public double MinRawPsnr { get; set; } = 30.0;

        /// <summary>
        /// Y 通道 SSIM 均值下限（0-1）。默认 0.95。
        /// </summary>
        /// <remarks>
        /// V2 指标：块边长随像素数放大，抓结构相似度而不是单纯像素误差。
        /// 标定语料上当前合格产物 SSIM 均值 0.984~0.991，取 0.95 留出余量；
        /// 它能拦住「4×4 块平均 PSNR 很高、但结构被抹平」的候选（棋盘格实测 SSIM≈0.004）。
        /// </remarks>
        public double MinSsim { get; set; } = 0.95;

        /// <summary>
        /// 最差 1% 块的 SSIM 下限（0-1）。默认 0.75。
        /// </summary>
        /// <remarks>
        /// 抓局部糊掉/块效应；标定语料上当前合格产物最差 1% 约 0.827~0.957，取 0.75 留出余量。
        /// </remarks>
        public double MinWorstSsim { get; set; } = 0.75;

        /// <summary>
        /// 判断某次候选结果是否达标（同时满足感知下限与逐像素下限）。
        /// </summary>
        /// <param name="quality">候选画质</param>
        public bool Accept(ImageQuality quality)
            => quality.Meets(MinPerceptualPsnr, MinRawPsnr, MinSsim, MinWorstSsim);

        /// <summary>
        /// PNG / GIF 调色板颜色数上限（2-256）。默认 256。
        /// </summary>
        public int MaxColors { get; set; } = 256;

        /// <summary>
        /// 量化时是否启用 Floyd–Steinberg 误差扩散抖动。默认开启。
        /// 关闭后体积更小，但渐变区域容易出现色带。
        /// </summary>
        public bool EnableDithering { get; set; } = true;

        /// <summary>
        /// 是否剥离 EXIF / ICC 等元数据。默认开启（元数据常占几 KB 到上百 KB）。
        /// </summary>
        public bool StripMetadata { get; set; } = true;

        /// <summary>
        /// PNG 搜索结束后，是否把赢家再按 <see cref="CompressionLevel.SmallestSize"/>（zlib 9）重压一遍。
        /// 默认开启。搜索期仍用 zlib 6（候选之间排序够用），只对最终产物付一次高压缩成本。
        /// </summary>
        public bool PngMaxDeflateOnWinner { get; set; } = true;

        /// <summary>
        /// <see cref="PngMaxDeflateOnWinner"/> 的像素数上限。默认 8 MP。
        /// </summary>
        /// <remarks>
        /// zlib 6→9 的成本约为 7~10 倍：4.19 MP 调色板 91.7 → 682.0 ms、真彩色 280.3 → 2905.3 ms。
        /// 小图把这笔时间换 2.6%~6.6% 体积是划算的；143 MP 上会多花几十秒，故默认只对 ≤8 MP 生效。
        /// 需要极限压缩时可调大该值。
        /// </remarks>
        public long PngMaxDeflatePixelThreshold { get; set; } = 8_000_000;

        /// <summary>
        /// 跨格式转到不支持 alpha 的格式（JPEG / BMP）时，透明像素合成使用的背景色（0-255）。
        /// 默认白色。目标格式支持 alpha（PNG / WebP / 单帧 GIF）时忽略该值。
        /// </summary>
        public (byte R, byte G, byte B) MatteColor { get; set; } = (255, 255, 255);

        /// <summary>
        /// 固定的 JPEG 质量（1-100）。为 null 时按画质下限自动搜索最小可用质量。
        /// </summary>
        public int? JpegQuality { get; set; }

        /// <summary>
        /// JPEG 自动搜索的质量下界，防止为压体积把图压糊。默认 40。
        /// </summary>
        public int JpegMinQuality { get; set; } = 40;

        /// <summary>
        /// WebP 自动搜索的质量下界。默认 20。
        /// </summary>
        /// <remarks>
        /// 该值独立于 <see cref="JpegMinQuality"/>。画质口径修正为 4 通道后，q≈30 在 143 MP 照片上
        /// 仍能达标，q40 会比必要值多约 8%~14% 体积。大图搜索已改为「降采样到 ≤16 MP 搜 q，
        /// 再用全图编码复评」，因此放开到 20 不再让 143 MP 的候选数从 1 涨到 19。
        /// 达标判定（<see cref="Accept"/>）本身会拦住过低的 q，下调下界不会绕过画质门槛。
        /// </remarks>
        public int WebpMinQuality { get; set; } = 20;

        /// <summary>
        /// 大图（&gt;20 MP）使用的最小搜索下界，默认 40。
        /// </summary>
        /// <remarks>
        /// 143 MP 实测：下界从 40 降到 20 能把照片从 q40/1,080,478 B 搜到 q34/996,350 B（−7.8%），
        /// 但全图搜索的候选数/耗时从 ~3.4 s 涨到 ~18.9 s，端到端 14.5 s → 30.4 s。
        /// 默认因此对大图保持 40；需要这 8% 时把本值和 <see cref="WebpMinQuality"/> 都设为 20。
        /// </remarks>
        public int WebpLargeImageMinQuality { get; set; } = 40;

        /// <summary>是否对低色数/图形类图额外尝试无损 WebP。默认开启。</summary>
        public bool WebpTryLossless { get; set; } = true;

        /// <summary>
        /// GIF 动画是否在全局调色板候选之外再尝试「每帧局部调色板」，按体积取小。默认开启。
        /// </summary>
        /// <remarks>
        /// 局部调色板通常画质更好，但会失去帧间差分（不同帧不能共用同一索引表）；
        /// 因此只在整帧 + 局部表的体积确实更小时采用。帧数很多时会多付一次逐帧量化成本。
        /// </remarks>
        public bool GifLocalPalettes { get; set; } = true;

        /// <summary>
        /// 是否对 WebP 做小范围编码参数搜索（<c>alpha_quality</c> / <c>filter_strength</c> /
        /// <c>sns_strength</c> / <c>segments</c> / <c>preprocessing</c>）。**默认关闭**，
        /// 只对 ≤ <see cref="WebpParamSearchMaxPixels"/> 的小图生效。
        /// </summary>
        /// <remarks>
        /// 搜索期在已选出的质量上用便宜档 <c>method 2</c> 做坐标下降（每个参数只试给定网格），
        /// 只接受「仍达标且体积更小」的组合；赢家参数再交给产出档重编。
        /// 参数搜索的候选数较多，大图上每个候选都要几秒，故默认只服务小图。
        /// 2026-10 语料实测：收益 0%~1.3%，耗时 +0.3~2.9 s（2~4 倍），且多数激进参数
        /// （sns / segments）会把最差 1% 块 SSIM 压到 0.75 以下而被 V2 判据拒绝，
        /// 因此默认关闭，仅保留给显式调参/离线消融。可用 <c>--opt-webp-params</c> 打开。
        /// </remarks>
        public bool WebpParamSearch { get; set; } = false;

        /// <summary>WebP 参数搜索的像素数上限。默认 1.5 MP。</summary>
        public long WebpParamSearchMaxPixels { get; set; } = 1_500_000;

        /// <summary>尝试无损 WebP 的唯一颜色数上限（默认 4096）。</summary>
        public int WebpLosslessMaxUniqueColors { get; set; } = 4096;

        /// <summary>尝试无损 WebP 的像素数上限（默认 20 MP）。</summary>
        public long WebpLosslessMaxPixels { get; set; } = 20_000_000;

        /// <summary>
        /// JPEG 是否按图像统计生成最优 Huffman 表（两遍编码，编码耗时实测约 2x，见 1.9~2.4x）。
        /// 对大片平坦区域的图（扫描件、截图、大留白照片）可再省 20%~40%，默认开启。
        /// </summary>
        public bool JpegOptimizeHuffman { get; set; } = true;

        /// <summary>
        /// JPEG 是否在 4:2:0 达标后仍然搜索 4:4:4，并按体积取小。默认开启。
        /// </summary>
        /// <remarks>
        /// 4:4:4 通常更大，但色度高频丰富的图可能在更低 q 下反而更小、观感更好；
        /// 关闭后恢复旧行为（4:2:0 不达标时才回退 4:4:4），可省一次完整搜索。
        /// </remarks>
        public bool JpegCompareSubsampling { get; set; } = true;

        /// <summary>
        /// JPEG 是否启用 trellis 量化（RD 优化的量化系数选择）。默认关闭（P3-1c 离线评估后决定）。
        /// </summary>
        /// <remarks>
        /// 每块用 DP 在 {0, 最近值, 最近值±1} 里选「失真 + λ×码率」最小的系数组合，
        /// 可主动归零不划算的小系数。代价是标量 FDCT + 每块 64×64 状态转移，
        /// 比默认 SIMD 最近值量化慢一个数量级，故默认关闭，仅用于离线评估/显式开启。
        /// </remarks>
        public bool JpegTrellisQuantization { get; set; }

        /// <summary>
        /// JPEG 产出阶段是否再转一次 progressive（SOF2）并与 baseline 按体积取小。默认开启。
        /// </summary>
        /// <remarks>
        /// 只做 spectral selection（DC 扫描 + 每分量一条 AC 扫描），不做 successive approximation；
        /// 复用 baseline 已算出的量化系数，不重新量化，像素不变。增加一次熵解码 + 熵编码的成本。
        /// </remarks>
        public bool JpegProgressive { get; set; } = true;

        /// <summary>
        /// PNG 无损重写是否额外尝试自适应行滤波（逐行在 5 种滤波器中选最优）。默认开启。
        /// </summary>
        /// <remarks>
        /// 开启后 <b>Up 与自适应各完整编码一遍、按实际字节数取小</b>，不是「一律用自适应」：
        /// 自适应是 MSAD 启发式，在照片上会明显劣于固定 Up（143 MP 实测大 19.2%，见
        /// <c>PngOptimizer.EncodeLosslessSmallest</c>），必须实测。
        /// 关闭则只试 Up（省一遍编码，适合体积不敏感、只求快的批量场景）。
        /// </remarks>
        public bool AdaptiveFiltering { get; set; } = true;

        /// <summary>
        /// 优化后反而变大时是否保留原图（直接复制源文件）。默认开启。
        /// </summary>
        public bool KeepOriginalWhenNoGain { get; set; } = true;

        /// <summary>
        /// 至少节省该比例（0-1）才算「有收益」，否则保留原图。默认 0（只要变小就采用）。
        /// 与 <see cref="KeepOriginalWhenNoGain"/> 配合使用。
        /// </summary>
        public double MinSavingRatio { get; set; }

        /// <summary>
        /// 同格式重编码已是有损格式（JPEG / WebP）时，至少要比原图小该比例才采用有损结果，
        /// 否则保留原图。默认 0.10（10%）。
        /// </summary>
        /// <remarks>
        /// 对已经是 JPEG/WebP 的源图，重编码会再损失一代画质。只为省 1%~5% 体积就重编码
        /// 不划算；该门槛让 optimize 在「收益不足」时保持原文件不变。
        /// 跨格式转换不适用（目标格式不同，必须写出）。
        /// 与 <see cref="MinSavingRatio"/> 同时生效时取较大者。
        /// </remarks>
        public double MinLossyGainRatio { get; set; } = 0.10;

        /// <summary>
        /// 日志输出委托，用于观察候选方案的体积与画质。默认不输出。
        /// </summary>
        /// <summary>
        /// optimize 的内存预算（字节）。0 表示按「当前可用内存」自动推导。
        /// </summary>
        /// <remarks>
        /// 在解码/搜索之前按文件头宽高估算峰值；超过预算时直接拒绝 optimize（保留原图），
        /// 避免进入分配阶段后才发现 OOM。可用 CLI <c>--memory-limit</c>（MB）或环境变量
        /// <c>SIC_MEMORY_LIMIT_MB</c> 覆盖。估算使用格式经验系数，属于保守近似而非精确模型。
        /// </remarks>
        public long MemoryLimitBytes { get; set; }

        /// <summary>
        /// 是否禁用内存预检。默认 false。禁用后回到“先分配、OOM 再回退”的行为。
        /// </summary>
        public bool DisableMemoryGuard { get; set; }

        public Action<string>? Log { get; set; }

        /// <summary>
        /// 保守档：目标画质 95，几乎看不出差别，收益相对小。
        /// </summary>
        public static OptimizeOptions Conservative => new() { TargetQuality = 95 };

        /// <summary>
        /// 均衡档（默认）：目标画质 88。
        /// </summary>
        public static OptimizeOptions Balanced => new();

        /// <summary>
        /// 激进档：目标画质 75，体积优先，放大看能看出差别。
        /// </summary>
        public static OptimizeOptions Aggressive => new() { TargetQuality = 75 };

        /// <summary>
        /// 校验并修正参数，返回规范化后的副本。
        /// </summary>
        /// <param name="options">原始选项</param>
        public static OptimizeOptions Normalize(OptimizeOptions? options)
        {
            var o = options ?? Balanced;
            o.MaxColors = Math.Clamp(o.MaxColors, 2, 256);
            o.TargetQuality = Math.Clamp(o.TargetQuality, 0, 100);
            o.JpegMinQuality = Math.Clamp(o.JpegMinQuality, 1, 95);
            o.WebpMinQuality = Math.Clamp(o.WebpMinQuality, 1, 95);
            o.WebpLargeImageMinQuality = Math.Clamp(o.WebpLargeImageMinQuality, 1, 95);
            o.WebpLosslessMaxUniqueColors = Math.Clamp(o.WebpLosslessMaxUniqueColors, 1, 1_000_000);
            o.WebpLosslessMaxPixels = Math.Max(0, o.WebpLosslessMaxPixels);
            o.WebpParamSearchMaxPixels = Math.Max(0, o.WebpParamSearchMaxPixels);
            if (o.JpegQuality.HasValue) o.JpegQuality = Math.Clamp(o.JpegQuality.Value, 1, 100);
            o.MinSavingRatio = Math.Clamp(o.MinSavingRatio, 0, 0.99);
            o.MinLossyGainRatio = Math.Clamp(o.MinLossyGainRatio, 0, 0.99);
            o.MinSsim = Math.Clamp(o.MinSsim, 0, 1);
            o.MinWorstSsim = Math.Clamp(o.MinWorstSsim, 0, 1);
            o.MemoryLimitBytes = Math.Max(0, o.MemoryLimitBytes);
            return o;
        }
    }
}
