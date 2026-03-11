using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Core;

internal static class SimdHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddBytesInPlace(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        int length = destination.Length;
        if (source.Length < length) length = source.Length;
        int i = 0;

        if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            int simd = Vector<byte>.Count;
            var mask = new Vector<ushort>(0xFF);
            for (; i <= length - simd; i += simd)
            {
                var a = new Vector<byte>(destination.Slice(i));
                var b = new Vector<byte>(source.Slice(i));
                Vector.Widen(a, out Vector<ushort> aLow, out Vector<ushort> aHigh);
                Vector.Widen(b, out Vector<ushort> bLow, out Vector<ushort> bHigh);
                var resLow = (aLow + bLow) & mask;
                var resHigh = (aHigh + bHigh) & mask;
                Vector.Narrow(resLow, resHigh).CopyTo(destination.Slice(i));
            }
        }

        for (; i < length; i++)
        {
            destination[i] = (byte)(destination[i] + source[i]);
        }
    }
}
