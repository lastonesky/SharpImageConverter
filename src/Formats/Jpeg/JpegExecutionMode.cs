using System;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// JPEG 编解码的执行模式判据：哪些路径必须退化成「当前线程上就地做完」。
/// </summary>
/// <remarks>
/// <para>
/// <b>起因</b>：单线程 wasm 运行时里阻塞等待（<c>Monitor.Wait</c> / <c>SemaphoreSlim.Wait</c>）会抛
/// <c>PlatformNotSupportedException</c> —— 运行时故意不支持，避免整页死锁。所以凡是「一个阶段
/// 阻塞等另一个阶段」的结构都必须有一条不阻塞的实现。
/// </para>
/// <para>
/// <b>为什么不用 <c>Environment.ProcessorCount &lt;= 1</c> 直接判</b>：它问的是「有几个核」，
/// 而需要回答的是「这个运行时能不能阻塞等待」。两者不等价 ——
/// 桌面 <c>DOTNET_PROCESSOR_COUNT=1</c> 时只有一个核却能正常阻塞；开了
/// <c>WasmEnableThreads</c> 的 wasm 多个核也能阻塞。把「核数」当「能力」用，等于
/// 在判据里藏了一条「已知这些组合不会出现」的假设，将来加目标平台或运行时行为变化时会静默走错分支。
/// </para>
/// <para>
/// <b>当前口径</b>：<c>ProcessorCount &lt;= 1</c> ⇒ 串行。这仍然覆盖不了「多核但不可阻塞」的运行时
/// （目前不存在），但它把判据收敛到一处、有名字、有注释，而且可以用 <c>SIC_JPEG_PIPELINE</c>
/// 显式覆盖来做同二进制 A/B 与回归对拍（见 <see cref="PipelineOverrideVariable"/>）。真正的能力探测
/// （例如运行时拒绝 Monitor 时捕获一次并降级）留待真出现这种运行时再做，不预先写猜测性代码。
/// </para>
/// <para>
/// <b>为什么是「退化」而不是「优化」</b>：串行路径只为可用性存在。并行管线实测快 1.5~2.4x
/// （rgb 1000² 36.8 vs 88.7 ms），有核时绝不会主动选串行。
/// </para>
/// </remarks>
internal static class JpegExecutionMode
{
    /// <summary>
    /// 强制串行管线的环境变量开关：<c>1</c>/<c>true</c> 强制串行，<c>0</c>/<c>false</c> 强制并行，
    /// 未设置或其它值按 <see cref="Environment.ProcessorCount"/> 推导。仅用于测试与 A/B。
    /// </summary>
    internal const string PipelineOverrideVariable = "SIC_JPEG_PIPELINE";

    private static readonly bool s_overrideSequential = ReadOverride();

    /// <summary>是否走「无阻塞、调用线程上就地完成」的实现。</summary>
    /// <remarks>
    /// 编码与解码的各条流水线必须用**同一判据**，否则会出现「编码器以为能并行、行源以为不能」
    /// 这类不对称的产物比较基准。
    /// </remarks>
    internal static bool IsSequential => s_overrideSequential || Environment.ProcessorCount <= 1;

    /// <summary>
    /// 按显式参数决定是否串行；<paramref name="forceSequential"/> 为 <c>null</c> 时用全局判据。
    /// </summary>
    /// <remarks>
    /// 给需要「在同一进程内跑串行分支」的测试用：环境变量在类初始化时就读完了，改它无效，
    /// 而回归测试要在一次运行里对拍串行与并行两条路径的产物。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSequentialFor(bool? forceSequential)
        => forceSequential ?? IsSequential;

    private static bool ReadOverride()
    {
        string? raw = Environment.GetEnvironmentVariable(PipelineOverrideVariable);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim() switch
        {
            "1" or "true" or "TRUE" or "True" or "on" or "ON" => true,
            _ => false,
        };
    }
}
