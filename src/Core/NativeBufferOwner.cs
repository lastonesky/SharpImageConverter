using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpImageConverter.Core;

internal sealed unsafe class NativeBufferOwner<T> : IDisposable where T : unmanaged
{
    private T* _ptr;
    private int _length;

    private NativeBufferOwner(T* ptr, int length)
    {
        _ptr = ptr;
        _length = length;

        if (ptr is not null && length > 0)
        {
            GC.AddMemoryPressure((long)length * sizeof(T));
        }
    }

    ~NativeBufferOwner()
    {
        DisposeUnmanaged();
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length;
    }

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            T* ptr = _ptr;
            if (ptr is null)
            {
                ThrowObjectDisposed();
            }

            return new Span<T>(ptr, _length);
        }
    }

    public ReadOnlySpan<T> ReadOnlySpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Span;
    }

    public static NativeBufferOwner<T> Allocate(int length, bool clear = false)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return new NativeBufferOwner<T>(null, 0);
        }

        nuint elementCount = checked((nuint)length);
        void* ptr = clear
            ? NativeMemory.AllocZeroed(elementCount, (nuint)sizeof(T))
            : NativeMemory.Alloc(elementCount, (nuint)sizeof(T));

        if (ptr is null)
        {
            throw new OutOfMemoryException();
        }

        return new NativeBufferOwner<T>((T*)ptr, length);
    }

    public static NativeBufferOwner<T> FromSpan(ReadOnlySpan<T> source)
    {
        if (source.IsEmpty)
        {
            return new NativeBufferOwner<T>(null, 0);
        }

        NativeBufferOwner<T> buffer = Allocate(source.Length);
        source.CopyTo(buffer.Span);
        return buffer;
    }

    public void Dispose()
    {
        DisposeUnmanaged();
        GC.SuppressFinalize(this);
    }

    private void DisposeUnmanaged()
    {
        T* ptr;
        fixed (T** pPtr = &_ptr)
        {
            IntPtr* pIntPtr = (IntPtr*)pPtr;
            ptr = (T*)Interlocked.Exchange(ref *pIntPtr, IntPtr.Zero);
        }

        if (ptr is not null)
        {
            long bytes = (long)_length * sizeof(T);
            NativeMemory.Free(ptr);
            GC.RemoveMemoryPressure(bytes);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposed()
    {
        throw new ObjectDisposedException(nameof(NativeBufferOwner<T>));
    }
}
