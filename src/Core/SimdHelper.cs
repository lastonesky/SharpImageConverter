using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImageConverter.Core;

internal static class SimdHelper
{
    public const int DefaultAlignment = 64;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NormalizeAlignment(int alignment)
    {
        if (alignment <= 0) throw new ArgumentOutOfRangeException(nameof(alignment));
        if (alignment < IntPtr.Size) alignment = IntPtr.Size;
        if (!IsPowerOfTwo(alignment))
        {
            alignment = (int)BitOperations.RoundUpToPowerOf2((uint)alignment);
        }
        return alignment;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundUpToMultiple(int value, int multiple)
    {
        if (multiple <= 0) throw new ArgumentOutOfRangeException(nameof(multiple));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        int remainder = value % multiple;
        if (remainder == 0) return value;
        return checked(value + (multiple - remainder));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundDownToMultiple(int value, int multiple)
    {
        if (multiple <= 0) throw new ArgumentOutOfRangeException(nameof(multiple));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value - (value % multiple);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAligned(nuint address, int alignment)
    {
        alignment = NormalizeAlignment(alignment);
        return (address & (nuint)(alignment - 1)) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPaddedLength(int length, int padMultiple)
    {
        if (padMultiple <= 1) return length;
        return RoundUpToMultiple(length, padMultiple);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetVectorPaddedByteLength(int length)
    {
        return RoundUpToMultiple(length, Vector<byte>.Count);
    }

    public static AlignedBuffer<T> AllocateAligned<T>(int length, int alignment = DefaultAlignment, bool clear = false) where T : unmanaged
    {
        return AlignedBuffer<T>.Allocate(length, alignment, clear);
    }

    public static AlignedBuffer<byte> AllocateAlignedBytes(int length, int alignment = DefaultAlignment, bool clear = false, int padToMultiple = 0)
    {
        int padded = GetPaddedLength(length, padToMultiple);
        return AlignedBuffer<byte>.Allocate(padded, alignment, clear);
    }

    public static AlignedBuffer<byte> CopyToAlignedBytes(ReadOnlySpan<byte> source, int alignment = DefaultAlignment, int padToMultiple = 0, byte padValue = 0)
    {
        int length = source.Length;
        int padded = GetPaddedLength(length, padToMultiple);
        var buffer = AlignedBuffer<byte>.Allocate(padded, alignment, clear: false);
        if (length != 0)
        {
            source.CopyTo(buffer.Span.Slice(0, length));
        }
        if (padded != length)
        {
            buffer.Span.Slice(length, padded - length).Fill(padValue);
        }
        return buffer;
    }
}

internal unsafe sealed class AlignedBuffer<T> : IDisposable where T : unmanaged
{
    private void* _ptr;
    private readonly int _length;
    private readonly int _alignment;
    private readonly nuint _byteLength;
    private bool _disposed;

    private AlignedBuffer(void* ptr, int length, int alignment, nuint byteLength)
    {
        _ptr = ptr;
        _length = length;
        _alignment = alignment;
        _byteLength = byteLength;
    }

    ~AlignedBuffer()
    {
        Dispose(false);
    }

    public int Length => _length;
    public int Alignment => _alignment;
    public nuint ByteLength => _byteLength;
    public nint Address => (nint)_ptr;

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AlignedBuffer<T>));
            return new Span<T>(_ptr, _length);
        }
    }

    public Span<byte> Bytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AlignedBuffer<T>));
            return new Span<byte>(_ptr, checked((int)_byteLength));
        }
    }

    public static AlignedBuffer<T> Allocate(int length, int alignment, bool clear)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        alignment = SimdHelper.NormalizeAlignment(alignment);

        if (length == 0)
        {
            return new AlignedBuffer<T>(null, 0, alignment, 0);
        }

        nuint byteLength = checked((nuint)length * (nuint)sizeof(T));
        void* ptr = NativeMemory.AlignedAlloc(byteLength, (nuint)alignment);
        if (ptr is null) throw new OutOfMemoryException();
        if (clear)
        {
            NativeMemory.Clear(ptr, byteLength);
        }

        return new AlignedBuffer<T>(ptr, length, alignment, byteLength);
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AlignedBuffer<T>));
        if (_ptr is null || _byteLength == 0) return;
        NativeMemory.Clear(_ptr, _byteLength);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        void* ptr = _ptr;
        _ptr = null;
        if (ptr is not null)
        {
            NativeMemory.AlignedFree(ptr);
        }
    }
}
