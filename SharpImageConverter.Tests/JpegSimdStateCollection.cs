using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// 会把 <c>SimdJpegEncodePipeline</c> 的全局开关（<c>ColorSupported</c> / <c>FdctSupported</c>）
/// 临时翻转的测试，必须与所有跑 JPEG 编解码的测试串行。
/// </summary>
/// <remarks>
/// 这两个开关是 <c>static</c> 的：<see cref="JpegEncoderSimdTests"/> 为了拿标量基线会把它们置 false，
/// 在 try/finally 里恢复。但 xUnit 默认让不同测试类并行，窗口期内别的 JPEG 测试就会跑到
/// 「颜色转换或 FDCT 的标量实现」上。标量与 SIMD 在边界 MCU 上并不保证逐位相同，
/// 于是「同一份输入编两遍」的字节比较会偶发不等
/// （实测：<c>JpegCoefficientBypassTests.CoefficientBypass_MatchesBitstreamDecode(q=60, 4:2:0)</c>
/// 在首轮全量运行里失败过一次，之后 8 轮全过 —— 典型的数据竞争特征）。
///
/// 生产代码从不写这两个属性，所以这不是产品缺陷；隔离范围仅限测试：
/// 本 collection 内部串行，且 <c>DisableParallelization = true</c> 让它也不与其他 collection 并行。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JpegSimdStateCollection
{
    public const string Name = "JpegSimdState";
}
