using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Webp;

internal static class Vp8TransformSimd
{
    // Four int lanes preserve the scalar kernels' intermediate precision and rounding.
    internal static bool IsSupported => SimdCompat.ByteShuffleAndPmulldSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Transpose(ref Vector128<int> a, ref Vector128<int> b,
        ref Vector128<int> c, ref Vector128<int> d)
    {
        var ab0 = SimdCompat.UnpackLow(a, b);
        var ab1 = SimdCompat.UnpackHigh(a, b);
        var cd0 = SimdCompat.UnpackLow(c, d);
        var cd1 = SimdCompat.UnpackHigh(c, d);
        a = SimdCompat.UnpackLow(ab0.AsInt64(), cd0.AsInt64()).AsInt32();
        b = SimdCompat.UnpackHigh(ab0.AsInt64(), cd0.AsInt64()).AsInt32();
        c = SimdCompat.UnpackLow(ab1.AsInt64(), cd1.AsInt64()).AsInt32();
        d = SimdCompat.UnpackHigh(ab1.AsInt64(), cd1.AsInt64()).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> Mul1(Vector128<int> a)
        => ((a * Vector128.Create(20091)) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<int> Mul2(Vector128<int> a)
        => (a * Vector128.Create(35468)) >> 16;
}
