using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Jpeg;

internal static class ThrowHelper
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidData(string message) => throw new InvalidDataException(message);

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowNotSupported(string message) => throw new NotSupportedException(message);
}
