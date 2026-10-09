using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Core;

/// <summary>
/// 跨架构向量原语。
///
/// 本库原有的向量化实现全部建立在 <c>System.Runtime.Intrinsics.X86</c> 之上
/// （SSE2 / SSSE3 / SSE4.1 / AVX2）。在 arm64 上这些 <c>IsSupported</c> 全部为
/// <c>false</c>，于是所有 <c>if (Ssse3.IsSupported)</c> 形式的守卫整条失效，
/// 程序静默退回标量实现，性能急剧下降（详见 docs/ArmPerfBaseline.md）。
///
/// 本类把这些操作抽象成单一入口，**x86 分支逐字保持原有的 x86 内在函数调用**，
/// 因此对 x86 的行为、产物与性能不产生任何影响；只有 arm64 才会走 NEON 分支。
///
/// 已逐条验证的语义等价性：
/// <list type="bullet">
///   <item><c>AdvSimd.Arm64.VectorTableLookup(byte,byte)</c> 与 <c>Ssse3.Shuffle</c>
///         <strong>不完全等价</strong>：掩码字节落在 <c>16..127</c> 时 pshufb 取
///         <c>mask &amp; 0x0F</c>，而 NEON <c>tbl</c> 返回 0。需要 <c>mask &amp; 0x8F</c>
///         归一化后才逐位等价，见 <see cref="ShuffleBytes"/>。</item>
///   <item><c>AdvSimd.Multiply(Int16,Int16)</c> ≡ <c>Sse2.MultiplyLow</c>（pmullw）：
///         NEON <c>mul</c> 同样只保留低 16 位。</item>
///   <item><c>AdvSimd.Multiply(Int32,Int32)</c> ≡ <c>Sse41.MultiplyLow</c>（pmulld）：
///         同样只保留低 32 位。</item>
///   <item><c>ExtractNarrowingSaturate*</c> ≡ <c>Sse2.PackUnsignedSaturate</c>（packuswb）/
///         <c>Sse2.PackSignedSaturate</c>（packssdw）。</item>
///   <item><c>AdvSimd.Arm64.ZipLow/ZipHigh(byte)</c> ≡ <c>Sse2.UnpackLow/UnpackHigh</c>。</item>
///   <item><c>AdvSimd.ExtractVector128</c> 可用于实现 <c>pslldq</c>/<c>psrldq</c>。</item>
/// </list>
///
/// 所有方法都标 <see cref="MethodImplOptions.AggressiveInlining"/>：<c>IsSupported</c> 是
/// JIT 常量，分支会在编译期被消掉，因此每处调用最终只会留下目标架构的那一条指令。
/// </summary>
internal static class SimdCompat
{
    /// <summary>
    /// 诊断开关：置为 <c>true</c> 时 <see cref="ByteShuffleSupported"/> 与
    /// <see cref="VectorBytesSupported"/> 都返回 <c>false</c>，从而让所有
    /// 以能力属性为守卫的向量化分支退回标量实现。
    /// <para>
    /// 用途是在同一台机器、同一次构建内，把"向量路径产物"与"标量路径产物"
    /// 逐字节对拍（例如验证 NEON 移植是否与标量逐位一致）。生产代码不写该字段，
    /// 默认 <c>false</c>，此时所有分派行为与未引入本开关时完全相同。
    /// </para>
    /// </summary>
    internal static bool ForceScalar { get; set; }

    /// <summary>是否具备可用的 16 字节查表（x86: pshufb / ARM: tbl）。</summary>
    public static bool ByteShuffleSupported => !ForceScalar && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>
    /// 是否具备 16 字节逐字节运算（加/减/位运算、整字节移位）。
    /// x86 侧即 SSE2；arm64 侧 NEON 基础指令集即可满足。
    /// </summary>
    public static bool VectorBytesSupported => !ForceScalar && (Sse2.IsSupported || AdvSimd.IsSupported);

    /// <summary>
    /// 「字节查表 + 32 位低位乘法 + 32 位整型运算」的联合能力，对应 resize 热路径的原始守卫
    /// <c>Ssse3.IsSupported &amp;&amp; Sse41.IsSupported</c>。
    /// <para>
    /// x86 侧保持原样（要求 SSSE3 <b>与</b> SSE4.1 同时具备）；arm64 侧只需基础 NEON：
    /// <c>tbl</c> 对应 pshufb、<c>mul v.4s</c> 对应 pmulld、<c>ushll</c> 对应 pmovzxbd。
    /// SSE2 由 SSSE3 蕴含，故无需单列。
    /// </para>
    /// </summary>
    public static bool ByteShuffleAndPmulldSupported =>
        !ForceScalar && ((Ssse3.IsSupported && Sse41.IsSupported) || AdvSimd.Arm64.IsSupported);

    // ------------------------------------------------------------------
    // 常量
    // ------------------------------------------------------------------

    public static Vector128<byte> ZeroByte => Vector128<byte>.Zero;

    /// <summary>
    /// pshufb 掩码归一化常量：保留 bit7（归零标志）、清零 bit4..6（越界索引折叠）。
    /// 详见 <see cref="ShuffleBytes"/> 的 remarks。
    /// </summary>
    private static readonly Vector128<byte> ShuffleIndexMask = Vector128.Create((byte)0x8F);

    // 本类中的移位量/移位字节数都是调用方契约里的编译期常量。CA1857 会要求
    // 包装层内部也传常量，但包装层只负责架构分派、且全部 AggressiveInlining，
    // JIT 内联后分支会连同常量一起被消掉，故整类关闭该分析器提示。
#pragma warning disable CA1857

    // ------------------------------------------------------------------
    // 查表 / 置换
    // ------------------------------------------------------------------

    /// <summary>pshufb / tbl 的精确等价实现（含越界掩码语义）。</summary>
    /// <remarks>
    /// <para>
    /// 两者对掩码字节的解释<strong>并不相同</strong>，必须显式归一化才能逐位等价：
    /// </para>
    /// <list type="bullet">
    ///   <item>x86 <c>pshufb</c>：掩码 bit7 = 1 ⇒ 该输出字节为 0；否则取
    ///         <strong>低 4 位</strong>作索引（等价于 <c>mask &amp; 0x0F</c>）。
    ///         因此 <c>0x10</c> 取到 <c>v[0]</c>，<c>0x1F</c> 取到 <c>v[15]</c>。</item>
    ///   <item>NEON <c>tbl</c>：索引 <strong>≥ 16 一律返回 0</strong>。
    ///         因此 <c>0x10</c> 返回 0 —— 与 pshufb 相异。</item>
    /// </list>
    /// <para>
    /// 用 <c>mask &amp; 0x8F</c> 归一化后两者完全一致：bit7 被保留（<c>0x80..0x8F</c>
    /// 作为索引 ≥ 16 会归零，正是 pshufb 的归零语义），bit4..6 被清零（正是
    /// pshufb 的 <c>&amp; 0x0F</c>）。代价是每次查表多一条 NEON <c>and</c>。
    /// </para>
    /// <para>
    /// 对本库现有全部掩码（字节取值只落在 <c>0..15</c> 或 <c>0x80</c>）该归一化
    /// 是恒等变换，不改变任何既有产物；对 <c>16..127</c> 这类越界掩码则修正了
    /// 一个潜在的方向性错误。如需零开销，可改用不归一化的
    /// <see cref="ShuffleBytesRaw"/>，但调用方必须自行保证掩码字节满足上述约束。
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShuffleBytes(Vector128<byte> value, Vector128<byte> mask)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(value, mask);
        }

        return AdvSimd.Arm64.VectorTableLookup(value, AdvSimd.And(mask, ShuffleIndexMask));
    }

    /// <summary>
    /// 不做掩码归一化的查表：语义为 NEON <c>tbl</c>（索引 ≥ 16 归零），
    /// <strong>不等于</strong> pshufb 对越界掩码的处理。
    /// 仅当调用方已确认掩码字节 ∈ <c>[0,15] ∪ [128,255]</c> 时可用，此时与
    /// <see cref="ShuffleBytes"/> 结果相同且少一条 <c>and</c>。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShuffleBytesRaw(Vector128<byte> value, Vector128<byte> mask)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(value, mask);
        }

        return AdvSimd.Arm64.VectorTableLookup(value, mask);
    }

    /// <summary>
    /// punpcklbw/hbw、punpcklwd/hwd、punpckldq/hdq、punpcklqdq/hqdq，
    /// 对应 NEON 的 zip1/zip2。
    /// <para>
    /// .NET 的 <c>Sse2.UnpackLow</c> 只提供按元素类型的重载而非泛型方法，
    /// 因此这里必须显式按 T 分派——否则编译器无法在泛型上下文中完成重载解析。
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> UnpackLow<T>(Vector128<T> a, Vector128<T> b) where T : struct
    {
        if (typeof(T) == typeof(byte))
        {
            Vector128<byte> ra = Reinterpret<T, byte>(a);
            Vector128<byte> rb = Reinterpret<T, byte>(b);
            Vector128<byte> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<byte, T>(r);
        }
        else if (typeof(T) == typeof(sbyte))
        {
            Vector128<sbyte> ra = Reinterpret<T, sbyte>(a);
            Vector128<sbyte> rb = Reinterpret<T, sbyte>(b);
            Vector128<sbyte> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<sbyte, T>(r);
        }
        else if (typeof(T) == typeof(short))
        {
            Vector128<short> ra = Reinterpret<T, short>(a);
            Vector128<short> rb = Reinterpret<T, short>(b);
            Vector128<short> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<short, T>(r);
        }
        else if (typeof(T) == typeof(ushort))
        {
            Vector128<ushort> ra = Reinterpret<T, ushort>(a);
            Vector128<ushort> rb = Reinterpret<T, ushort>(b);
            Vector128<ushort> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<ushort, T>(r);
        }
        else if (typeof(T) == typeof(int))
        {
            Vector128<int> ra = Reinterpret<T, int>(a);
            Vector128<int> rb = Reinterpret<T, int>(b);
            Vector128<int> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<int, T>(r);
        }
        else if (typeof(T) == typeof(uint))
        {
            Vector128<uint> ra = Reinterpret<T, uint>(a);
            Vector128<uint> rb = Reinterpret<T, uint>(b);
            Vector128<uint> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<uint, T>(r);
        }
        else if (typeof(T) == typeof(long))
        {
            Vector128<long> ra = Reinterpret<T, long>(a);
            Vector128<long> rb = Reinterpret<T, long>(b);
            Vector128<long> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<long, T>(r);
        }
        else if (typeof(T) == typeof(ulong))
        {
            Vector128<ulong> ra = Reinterpret<T, ulong>(a);
            Vector128<ulong> rb = Reinterpret<T, ulong>(b);
            Vector128<ulong> r = Sse2.IsSupported
                ? Sse2.UnpackLow(ra, rb)
                : AdvSimd.Arm64.ZipLow(ra, rb);
            return Reinterpret<ulong, T>(r);
        }
        throw new NotSupportedException($"UnpackLow<{typeof(T)}> 未实现");
    }

    /// <summary>见 <see cref="UnpackLow{T}"/>。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> UnpackHigh<T>(Vector128<T> a, Vector128<T> b) where T : struct
    {
        if (typeof(T) == typeof(byte))
        {
            Vector128<byte> ra = Reinterpret<T, byte>(a);
            Vector128<byte> rb = Reinterpret<T, byte>(b);
            Vector128<byte> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<byte, T>(r);
        }
        else if (typeof(T) == typeof(sbyte))
        {
            Vector128<sbyte> ra = Reinterpret<T, sbyte>(a);
            Vector128<sbyte> rb = Reinterpret<T, sbyte>(b);
            Vector128<sbyte> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<sbyte, T>(r);
        }
        else if (typeof(T) == typeof(short))
        {
            Vector128<short> ra = Reinterpret<T, short>(a);
            Vector128<short> rb = Reinterpret<T, short>(b);
            Vector128<short> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<short, T>(r);
        }
        else if (typeof(T) == typeof(ushort))
        {
            Vector128<ushort> ra = Reinterpret<T, ushort>(a);
            Vector128<ushort> rb = Reinterpret<T, ushort>(b);
            Vector128<ushort> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<ushort, T>(r);
        }
        else if (typeof(T) == typeof(int))
        {
            Vector128<int> ra = Reinterpret<T, int>(a);
            Vector128<int> rb = Reinterpret<T, int>(b);
            Vector128<int> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<int, T>(r);
        }
        else if (typeof(T) == typeof(uint))
        {
            Vector128<uint> ra = Reinterpret<T, uint>(a);
            Vector128<uint> rb = Reinterpret<T, uint>(b);
            Vector128<uint> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<uint, T>(r);
        }
        else if (typeof(T) == typeof(long))
        {
            Vector128<long> ra = Reinterpret<T, long>(a);
            Vector128<long> rb = Reinterpret<T, long>(b);
            Vector128<long> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<long, T>(r);
        }
        else if (typeof(T) == typeof(ulong))
        {
            Vector128<ulong> ra = Reinterpret<T, ulong>(a);
            Vector128<ulong> rb = Reinterpret<T, ulong>(b);
            Vector128<ulong> r = Sse2.IsSupported
                ? Sse2.UnpackHigh(ra, rb)
                : AdvSimd.Arm64.ZipHigh(ra, rb);
            return Reinterpret<ulong, T>(r);
        }
        throw new NotSupportedException($"UnpackHigh<{typeof(T)}> 未实现");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<TTo> Reinterpret<TFrom, TTo>(Vector128<TFrom> value)
        where TFrom : struct
        where TTo : struct
        => Unsafe.As<Vector128<TFrom>, Vector128<TTo>>(ref value);

    /// <summary>punpcklbw：交错低 8 字节（ZIP1）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> UnpackLowBytes(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.UnpackLow(a, b);
        }

        return AdvSimd.Arm64.ZipLow(a, b);
    }

    /// <summary>punpckhbw：交错高 8 字节（ZIP2）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> UnpackHighBytes(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.UnpackHigh(a, b);
        }

        return AdvSimd.Arm64.ZipHigh(a, b);
    }

    /// <summary>pslldq：整体左移 <paramref name="bytes"/> 字节（0..15），低位补 0。</summary>
    /// <remarks>
    /// NEON 侧用 <c>EXT</c> 合成。语义已实测确认（见 docs/ArmPerfBaseline.md §5）：
    /// <c>ExtractVector128(zero, v, n)</c> = <c>psrldq(v, n)</c>；
    /// <c>ExtractVector128(zero, v, 16-n)</c> = <c>pslldq(v, n)</c>。
    /// <para>
    /// <b><paramref name="bytes"/> = 0 必须单独处理</b>：此时 <c>16-0 = 16</c> 超出了
    /// <c>ExtractVector128</c> 允许的 0..15，会抛 <c>ArgumentOutOfRangeException</c>，
    /// 而 x86 的 <c>pslldq x, 0</c> 是合法的恒等操作。此分支由
    /// <c>SimdCompatTests.ByteShifts_MatchPsrldqAndPslldq</c> 覆盖。
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftLeftBytes(Vector128<byte> value, byte bytes)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.ShiftLeftLogical128BitLane(value, bytes);
        }

        if (bytes == 0)
        {
            return value;
        }

        return AdvSimd.ExtractVector128(Vector128<byte>.Zero, value, (byte)(16 - bytes));
    }

    /// <summary>psrldq：整体右移 <paramref name="bytes"/> 字节（0..15），高位补 0。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightBytes(Vector128<byte> value, byte bytes)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.ShiftRightLogical128BitLane(value, bytes);
        }

        if (bytes == 0)
        {
            return value;
        }

        return AdvSimd.ExtractVector128(value, Vector128<byte>.Zero, bytes);
    }

    // ------------------------------------------------------------------
    // 位运算
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> OrBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.Or(a, b) : AdvSimd.Or(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AndBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.And(a, b) : AdvSimd.And(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> XorBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.Xor(a, b) : AdvSimd.Xor(a, b);

    // ------------------------------------------------------------------
    // 加 / 减
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.Add(a, b) : AdvSimd.Add(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SubtractBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.Subtract(a, b) : AdvSimd.Subtract(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> AddInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.Add(a, b) : AdvSimd.Add(a, b);

    /// <summary>por / NEON orr（按位或，对 16 位通道同样适用）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> OrInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.Or(a, b) : AdvSimd.Or(a, b);

    /// <summary>pminsw / NEON smin（有符号 16 位最小值）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> MinInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.Min(a, b) : AdvSimd.Min(a, b);

    /// <summary>pmaxsw / NEON smax（有符号 16 位最大值）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> MaxInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.Max(a, b) : AdvSimd.Max(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> SubtractInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.Subtract(a, b) : AdvSimd.Subtract(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> AddUInt16(Vector128<ushort> a, Vector128<ushort> b)
        => Sse2.IsSupported ? Sse2.Add(a, b) : AdvSimd.Add(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> SubtractUInt16(Vector128<ushort> a, Vector128<ushort> b)
        => Sse2.IsSupported ? Sse2.Subtract(a, b) : AdvSimd.Subtract(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> AddInt32(Vector128<int> a, Vector128<int> b)
        => Sse2.IsSupported ? Sse2.Add(a, b) : AdvSimd.Add(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> SubtractInt32(Vector128<int> a, Vector128<int> b)
        => Sse2.IsSupported ? Sse2.Subtract(a, b) : AdvSimd.Subtract(a, b);

    // ------------------------------------------------------------------
    // 乘法（低半部分）
    // ------------------------------------------------------------------

    /// <summary>pmullw：16 位乘法取低 16 位。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> MultiplyLowInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.MultiplyLow(a, b) : AdvSimd.Multiply(a, b);

    /// <summary>pmulld：32 位乘法取低 32 位。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MultiplyLowInt32(Vector128<int> a, Vector128<int> b)
        => Sse41.IsSupported ? Sse41.MultiplyLow(a, b) : AdvSimd.Multiply(a, b);

    // ------------------------------------------------------------------
    // 移位
    // ------------------------------------------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> ShiftRightLogicalUInt16(Vector128<ushort> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftRightLogical(value, count) : AdvSimd.ShiftRightLogical(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ShiftRightLogicalInt16(Vector128<short> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftRightLogical(value, count) : AdvSimd.ShiftRightLogical(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ShiftRightLogicalInt32(Vector128<int> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftRightLogical(value, count) : AdvSimd.ShiftRightLogical(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ShiftRightArithmeticInt16(Vector128<short> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftRightArithmetic(value, count) : AdvSimd.ShiftRightArithmetic(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ShiftRightArithmeticInt32(Vector128<int> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftRightArithmetic(value, count) : AdvSimd.ShiftRightArithmetic(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> ShiftLeftLogicalUInt16(Vector128<ushort> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftLeftLogical(value, count) : AdvSimd.ShiftLeftLogical(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> ShiftLeftLogicalInt16(Vector128<short> value, byte count)
        => Sse2.IsSupported ? Sse2.ShiftLeftLogical(value, count) : AdvSimd.ShiftLeftLogical(value, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ShiftLeftLogicalInt32(Vector128<int> value, byte count)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.ShiftLeftLogical(value, count);
        }

        // .NET 的 AdvSimd 没有 Vector128<int> 的 ShiftLeftLogical 重载，
        // 改走 UInt32 视图——按位结果完全一致。
        return AdvSimd.ShiftLeftLogical(value.AsUInt32(), count).AsInt32();
    }

    // ------------------------------------------------------------------
    // 加宽 / 收窄
    // ------------------------------------------------------------------

    /// <summary>punpcklbw+0：低 8 个字节零扩展为 8 个 ushort。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> WidenLowerBytes(Vector128<byte> value)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.UnpackLow(value, Vector128<byte>.Zero).AsUInt16();
        }

        return AdvSimd.ZeroExtendWideningLower(value.GetLower());
    }

    /// <summary>punpckhbw+0：高 8 个字节零扩展为 8 个 ushort。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> WidenUpperBytes(Vector128<byte> value)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.UnpackHigh(value, Vector128<byte>.Zero).AsUInt16();
        }

        return AdvSimd.ZeroExtendWideningUpper(value);
    }

    /// <summary>packuswb：两组 8×int16 无符号饱和收窄为 16×uint8。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> PackUnsignedSaturate(Vector128<short> low, Vector128<short> high)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.PackUnsignedSaturate(low, high);
        }

        Vector64<byte> lower = AdvSimd.ExtractNarrowingSaturateUnsignedLower(low);
        return AdvSimd.ExtractNarrowingSaturateUnsignedUpper(lower, high);
    }

    /// <summary>packssdw：两组 4×int32 有符号饱和收窄为 8×int16。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> PackSignedSaturateInt32(Vector128<int> low, Vector128<int> high)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.PackSignedSaturate(low, high);
        }

        Vector64<short> lower = AdvSimd.ExtractNarrowingSaturateLower(low);
        return AdvSimd.ExtractNarrowingSaturateUpper(lower, high);
    }

    // ------------------------------------------------------------------
    // 最小 / 最大
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MinBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.Min(a, b) : AdvSimd.Min(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MaxBytes(Vector128<byte> a, Vector128<byte> b)
        => Sse2.IsSupported ? Sse2.Max(a, b) : AdvSimd.Max(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> MinUInt16(Vector128<ushort> a, Vector128<ushort> b)
        => Sse41.IsSupported ? Sse41.Min(a, b) : AdvSimd.Min(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> MaxUInt16(Vector128<ushort> a, Vector128<ushort> b)
        => Sse41.IsSupported ? Sse41.Max(a, b) : AdvSimd.Max(a, b);

    // ------------------------------------------------------------------
    // 比较
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> CompareEqualInt32(Vector128<int> a, Vector128<int> b)
        => Sse2.IsSupported ? Sse2.CompareEqual(a, b) : AdvSimd.CompareEqual(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> CompareGreaterThanInt16(Vector128<short> a, Vector128<short> b)
        => Sse2.IsSupported ? Sse2.CompareGreaterThan(a, b) : AdvSimd.CompareGreaterThan(a, b);

    // ------------------------------------------------------------------
    // 浮点 <-> 整数
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ConvertToInt32WithTruncation(Vector128<float> value)
        => Sse2.IsSupported
            ? Sse2.ConvertToVector128Int32WithTruncation(value)
            : AdvSimd.ConvertToInt32RoundToZero(value);

    /// <summary>pmovzxbd：低 4 个字节零扩展为 4 个 int32。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ConvertBytesToInt32(Vector128<byte> value)
    {
        if (Sse41.IsSupported)
        {
            return Sse41.ConvertToVector128Int32(value);
        }

        // NEON 的 ushll 只能加宽一档，byte -> ushort -> uint 走两步；
        // 第二步从 ushort 视图的低半区取 4 个 lane，恰好等价于 pmovzxbd。
        Vector128<ushort> widened = AdvSimd.ZeroExtendWideningLower(value.GetLower());
        return AdvSimd.ZeroExtendWideningLower(widened.GetLower()).AsInt32();
    }

    /// <summary>cvtdq2ps：int32 → float（就近取整到可表示值，不做截断）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ConvertInt32ToSingle(Vector128<int> value)
        => Sse2.IsSupported
            ? Sse2.ConvertToVector128Single(value)
            : AdvSimd.ConvertToSingle(value);

    // ------------------------------------------------------------------
    // 浮点算术 / 最值
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> AddSingle(Vector128<float> a, Vector128<float> b)
        => Sse2.IsSupported ? Sse2.Add(a, b) : AdvSimd.Add(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MultiplySingle(Vector128<float> a, Vector128<float> b)
        => Sse2.IsSupported ? Sse2.Multiply(a, b) : AdvSimd.Multiply(a, b);

    /// <summary>minps / NEON fmin。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MinSingle(Vector128<float> a, Vector128<float> b)
        => Sse2.IsSupported ? Sse2.Min(a, b) : AdvSimd.Min(a, b);

    /// <summary>maxps / NEON fmax。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> MaxSingle(Vector128<float> a, Vector128<float> b)
        => Sse2.IsSupported ? Sse2.Max(a, b) : AdvSimd.Max(a, b);

    // ------------------------------------------------------------------
    // 水平成对运算
    // ------------------------------------------------------------------

    /// <summary>
    /// pmaddwd：把 a/b 按 16 位成对相乘，再把每对乘积相加得到 4 个 int32。
    /// NEON 侧用「加宽乘法 + 成对相加」合成（smull + addp），结果与 pmaddwd 逐位一致
    /// （含两对均为 0x8000×0x8000 时回绕到 0x80000000 的边界情形）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> MultiplyAddAdjacent(Vector128<short> a, Vector128<short> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.MultiplyAddAdjacent(a, b);
        }

        Vector128<int> low = AdvSimd.MultiplyWideningLower(a.GetLower(), b.GetLower());
        Vector128<int> high = AdvSimd.MultiplyWideningUpper(a, b);
        return AdvSimd.Arm64.AddPairwise(low, high);
    }

    /// <summary>phaddd：两组 4×int32 各自成对相加。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> HorizontalAddInt32(Vector128<int> a, Vector128<int> b)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.HorizontalAdd(a, b);
        }

        // NEON addp 与 phaddd 的通道布局完全相同：{a0+a1, a2+a3, b0+b1, b2+b3}
        return AdvSimd.Arm64.AddPairwise(a, b);
    }

    /// <summary>
    /// 4 个 int32 通道的水平之和。
    /// <para>
    /// x86 侧保持原有的 <c>pshufd 0x4E</c> + <c>pshufd 0xB1</c> 两次两级归约；
    /// arm64 侧改用单条 <c>addv s</c>（<see cref="AdvSimd.Arm64.AddAcross(Vector128{int})"/>），
    /// 比照搬两次 <c>tbl</c> 置换少两条指令、少一个依赖链。
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HorizontalSumInt32(Vector128<int> value)
    {
        if (Sse2.IsSupported)
        {
            Vector128<int> t = Sse2.Add(value, Sse2.Shuffle(value, 0x4E)); // 交换两个 64 位半区
            t = Sse2.Add(t, Sse2.Shuffle(t, 0xB1));                       // 交换每半区内的两个 dword
            return t.GetElement(0);
        }

        return AdvSimd.Arm64.AddAcross(value).GetElement(0);
    }

    /// <summary>psadbw：两半各 8 字节的绝对差之和，结果落在 ushort 通道 0 与 4。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> SumAbsoluteDifferences(Vector128<byte> a, Vector128<byte> b)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.SumAbsoluteDifferences(a, b).AsUInt16();
        }

        // uabd -> uaddlp ×3：16×u8 -> 8×u16 -> 4×u32 -> 2×u64，
        // 再把两个 u64 结果搬回 ushort 通道 0 与 4，与 psadbw 的布局对齐。
        Vector128<byte> diff = AdvSimd.AbsoluteDifference(a, b);
        Vector128<ushort> pair16 = AdvSimd.AddPairwiseWidening(diff);
        Vector128<uint> pair32 = AdvSimd.AddPairwiseWidening(pair16);
        Vector128<ulong> pair64 = AdvSimd.AddPairwiseWidening(pair32);
        Vector128<ushort> packed = pair64.AsUInt16();
        return Vector128.Create(
            packed.GetElement(0),
            (ushort)0,
            (ushort)0,
            (ushort)0,
            packed.GetElement(4),
            (ushort)0,
            (ushort)0,
            (ushort)0);
    }

    // ------------------------------------------------------------------
    // 访存
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe Vector128<byte> LoadBytesPtr(byte* address)
    {
        if (Sse2.IsSupported)
        {
            return Sse2.LoadVector128(address);
        }

        // .NET 未提供 AdvSimd.LoadVector128(byte*)；用 LoadUnsafe 表达同一语义，
        // JIT 在 arm64 上会发出 NEON 的无对齐加载。
        return Vector128.LoadUnsafe(ref Unsafe.AsRef<byte>(address));
    }

    // ------------------------------------------------------------------
    // 解交织加载（LD2 / LD3 / LD4）—— ✅ 实测否决，**默认不走LD3**
    //
    // 原假设（见 docs/ArmExclusiveSimd.md §3.2）：arm64 一条 LD3 可顶掉
    // 「3ldr + 9 tbl + 9 and + 6 orr」共 27 条指令，故应显著更快。
    //
    // ✅ 2026-10-09 在 M4 上实测，**该假设不成立，LD3 反而更慢**：
    //
    //   stride   LD3 GB/s   TBL GB/s   LD3/TBL
    //       48        63.17 86.07     0.734
    //       51        67.83        81.67     0.830
    //       45        65.99        86.59     0.762
    //       33        72.17        97.98     0.737
    //
    //   端到端隔离 A/B（同进程同二进制，4096x3072 交替测 9 轮取中位数）：
    //   pshufb 4.689 ms vs LD3 5.131 ms ⇒ **0.914x，即慢 9.4%**。
    //
    // 原因：原推理只数了**指令条数**，没算**单条成本**。M4 的 TBL（含 4 表形式）
    // 吞吐很高，而 LD3 是一条多周期的结构化 load，字节/周期吞吐低于
    // 「3×ldr + 3×tbl + 2×orr」。**在 M4 上，解交织的正确解法是 tbl，不是 LD3。**
    //
    // 因此本类保留 pshufb/tbl 方案作为唯一生产路径；LD3 的入口留着但由
    // <see cref="DisableUnzipLoad"/> 控制，默认 false ⇒ 恒走 tbl。
    // 该开关不是"预留功能"，而是**这条否决结论的可复现凭据**：把它置 true
    // 就能在同一台机器上重跑上面的 A/B。
    // ------------------------------------------------------------------

    /// <summary>
    /// 诊断开关：置 <c>true</c> 时 <see cref="LoadRgb24Unzip3"/> 走 LD3 而非 tbl。
    /// </summary>
    /// <remarks>
    /// 默认 <c>false</c>，此时行为与本类引入前逐字节一致。置 <c>true</c> 可复现
    /// 「LD3 比 tbl 慢约 9%」这一实测结论，用于日后在其他 arm64 核心上重测
    /// （不同微架构的 LD3/TBL 吞吐比可能不同，例如 Neoverse 与 Cortex-A 系列）。
    /// </remarks>
    public static bool DisableUnzipLoad { get; set; }

    /// <summary>
    /// RGB24 解交织加载：从 <paramref name="address"/> 读 48 字节（16 像素），
    /// 输出 R / G / B 三个 16 字节平面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>arm64 走 tbl 而非 LD3</b>——理由见本节上方的实测表格。x86 走 pshufb，
    /// 掩码与 or 的结合顺序与原调用点写法逐字一致，产物与性能零变化。
    /// </para>
    /// <para>
    /// 两条 ARM 路径（tbl 与 LD3）语义等价：LD3 的正确性已在 M4 上实测确认
    /// （一次读 48 字节，三平面与逐字节提取的期望值完全一致），这也反证了
    /// tbl 路径本就正确——否则两条路不可能给出相同产物。
    /// </para>
    /// <para>
    /// <b>调用方必须保证 <paramref name="address"/> 起 48 字节全部可读</b>
    /// （即缓冲区剩余 ≥ 48 字节），尾部不足 16 像素的部分由调用方的标量循环处理。
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void LoadRgb24Unzip3(byte* address, out Vector128<byte> r, out Vector128<byte> g, out Vector128<byte> b)
    {
        if (AdvSimd.Arm64.IsSupported && DisableUnzipLoad)
        {
            // 仅用于复现否决结论；默认不进入。
            (r, g, b) = AdvSimd.Arm64.Load3xVector128AndUnzip(address);
            return;
        }

        Vector128<byte> v0 = LoadBytesPtr(address);
        Vector128<byte> v1 = LoadBytesPtr(address + 16);
        Vector128<byte> v2 = LoadBytesPtr(address + 32);
        r = OrBytes(OrBytes(ShuffleBytes(v0, RgbShufR0), ShuffleBytes(v1, RgbShufR1)), ShuffleBytes(v2, RgbShufR2));
        g = OrBytes(OrBytes(ShuffleBytes(v0, RgbShufG0), ShuffleBytes(v1, RgbShufG1)), ShuffleBytes(v2, RgbShufG2));
        b = OrBytes(OrBytes(ShuffleBytes(v0, RgbShufB0), ShuffleBytes(v1, RgbShufB1)), ShuffleBytes(v2, RgbShufB2));
    }

    /// <summary>
    /// RGB24 → 三平面的解交织掩码（tbl / pshufb 路径专用）。
    /// 每组三个掩码覆盖 48 字节源的三段，越界位置填 <c>0x80</c>（pshufb 归零）。
    /// </summary>
    private static readonly Vector128<byte> RgbShufR0 = Vector128.Create((byte)0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbShufR1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbShufR2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1, 4, 7, 10, 13);
    private static readonly Vector128<byte> RgbShufG0 = Vector128.Create((byte)1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbShufG1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbShufG2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 5, 8, 11, 14);
    private static readonly Vector128<byte> RgbShufB0 = Vector128.Create((byte)2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbShufB1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> RgbShufB2 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 3, 6, 9, 12, 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> LoadBytes(ref byte source, nuint offset)
        => Vector128.LoadUnsafe<byte>(ref source, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreBytes(Vector128<byte> value, ref byte destination, nuint offset)
        => Vector128.StoreUnsafe<byte>(value, ref destination, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<short> LoadInt16(ref short source, nuint offset)
        => Vector128.LoadUnsafe<short>(ref source, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreInt16(Vector128<short> value, ref short destination, nuint offset)
        => Vector128.StoreUnsafe<short>(value, ref destination, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> LoadInt32(ref int source, nuint offset)
        => Vector128.LoadUnsafe<int>(ref source, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreInt32(Vector128<int> value, ref int destination, nuint offset)
        => Vector128.StoreUnsafe<int>(value, ref destination, offset);
}

#pragma warning restore CA1857
