using System;

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
        /// 判断某次候选结果是否达标（同时满足感知下限与逐像素下限）。
        /// </summary>
        /// <param name="quality">候选画质</param>
        public bool Accept(ImageQuality quality) => quality.Meets(MinPerceptualPsnr, MinRawPsnr);

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
        /// 固定的 JPEG 质量（1-100）。为 null 时按画质下限自动搜索最小可用质量。
        /// </summary>
        public int? JpegQuality { get; set; }

        /// <summary>
        /// JPEG 自动搜索的质量下界，防止为压体积把图压糊。默认 40。
        /// </summary>
        public int JpegMinQuality { get; set; } = 40;

        /// <summary>
        /// JPEG 是否按图像统计生成最优 Huffman 表（两遍编码，编码耗时约翻倍）。
        /// 对大片平坦区域的图（扫描件、截图、大留白照片）可再省 20%~40%，默认开启。
        /// </summary>
        public bool JpegOptimizeHuffman { get; set; } = true;

        /// <summary>
        /// PNG 输出是否使用自适应行滤波（逐行在 5 种滤波器中选最优）。默认开启。
        /// </summary>
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
        /// 日志输出委托，用于观察候选方案的体积与画质。默认不输出。
        /// </summary>
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
            if (o.JpegQuality.HasValue) o.JpegQuality = Math.Clamp(o.JpegQuality.Value, 1, 100);
            o.MinSavingRatio = Math.Clamp(o.MinSavingRatio, 0, 0.99);
            return o;
        }
    }
}
