using System;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// 优化器算出的候选产物：目标字节、方案描述与画质评估。
    /// </summary>
    /// <remarks>
    /// 各格式优化器只负责「按目标格式算出最优字节」，不碰磁盘；
    /// 由 <see cref="ImageOptimizer"/> 统一决定落盘方式：
    /// 同格式时无收益可保留原图，跨格式时必然写出目标格式文件。
    /// </remarks>
    internal readonly struct OptimizeArtifact
    {
        /// <summary>
        /// 构造产物。
        /// </summary>
        /// <param name="bytes">目标格式字节；无法产出时为 null</param>
        /// <param name="method">方案描述，例如「JPEG 质量 78（4:2:0）」</param>
        /// <param name="quality">相对原图的画质评估</param>
        /// <param name="meetsBar">是否存在满足画质下限的候选</param>
        /// <param name="reason">无法优化的原因，正常时为 null</param>
        public OptimizeArtifact(byte[]? bytes, string? method, ImageQuality quality, bool meetsBar, string? reason = null)
        {
            Bytes = bytes;
            Method = method;
            Quality = quality;
            MeetsBar = meetsBar;
            Reason = reason;
        }

        /// <summary>
        /// 目标格式字节；null 表示没有可用产物。
        /// </summary>
        public byte[]? Bytes { get; }

        /// <summary>
        /// 实际采用的方案描述。
        /// </summary>
        public string? Method { get; }

        /// <summary>
        /// 相对原图的画质评估。
        /// </summary>
        public ImageQuality Quality { get; }

        /// <summary>
        /// 是否存在满足画质下限的候选。为 false 时同格式路径会保留原图。
        /// </summary>
        public bool MeetsBar { get; }

        /// <summary>
        /// 未做优化的原因；正常优化时为 null。
        /// </summary>
        public string? Reason { get; }

        /// <summary>
        /// 构造「无法优化」的产物。
        /// </summary>
        /// <param name="reason">原因描述</param>
        public static OptimizeArtifact Unsupported(string reason) => new(null, null, default, false, reason);
    }
}
