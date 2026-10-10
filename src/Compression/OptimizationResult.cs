using System;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 单张图像的优化结果。
    /// </summary>
    public sealed class OptimizationResult
    {
        /// <summary>
        /// 输入文件路径。
        /// </summary>
        public string InputPath { get; init; } = string.Empty;

        /// <summary>
        /// 输出文件路径。
        /// </summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>
        /// 源文件体积（字节）。
        /// </summary>
        public long OriginalSize { get; init; }

        /// <summary>
        /// 优化后体积（字节）。
        /// </summary>
        public long OptimizedSize { get; init; }

        /// <summary>
        /// 节省的字节数，负数表示变大。
        /// </summary>
        public long SavedBytes => OriginalSize - OptimizedSize;

        /// <summary>
        /// 节省比例（0-1），负数表示变大。
        /// </summary>
        public double SavedRatio => OriginalSize <= 0 ? 0 : (double)SavedBytes / OriginalSize;

        /// <summary>
        /// 实际采用的方案描述，例如「PNG 调色板 128 色 + 抖动」。
        /// </summary>
        public string Method { get; init; } = string.Empty;

        /// <summary>
        /// 相对原图的画质评估。
        /// </summary>
        public ImageQuality Quality { get; init; }

        /// <summary>
        /// 是否因无收益而直接复制了原图。
        /// </summary>
        public bool KeptOriginal { get; init; }

        /// <summary>
        /// 是否为跨格式转换（源格式与目标格式不同）。
        /// 此时 <see cref="OriginalSize"/> 与 <see cref="OptimizedSize"/> 是两种格式的体积对比，
        /// <see cref="SavedRatio"/> 仅供参考。
        /// </summary>
        public bool Converted { get; init; }

        /// <summary>
        /// 未做优化的原因（如格式不支持、动画含透明通道等），正常优化时为空。
        /// </summary>
        public string? Reason { get; init; }

        /// <inheritdoc />
        public override string ToString()
        {
            if (Reason != null)
            {
                return $"{InputPath}: 未优化（{Reason}）";
            }
            if (KeptOriginal)
            {
                return $"{InputPath}: 无收益，已保留原图（{OptimizedSize} 字节 vs 原 {OriginalSize} 字节）";
            }
            if (Converted)
            {
                return $"{InputPath} → {OutputPath}: {OriginalSize} → {OptimizedSize} 字节，{Method}，{Quality}";
            }
            return $"{InputPath}: {OriginalSize} → {OptimizedSize} 字节（省 {SavedRatio:P1}），{Method}，{Quality}";
        }
    }
}
