using System;
using System.Runtime.InteropServices;

namespace SharpImageConverter.Core;

internal sealed unsafe class NativeBufferOwner<T> : IDisposable where T : unmanaged
{
    private T* _ptr;
    private bool _disposed;

    private NativeBufferOwner(T* ptr, int length)
    {
        _ptr = ptr;
        Length = length;
    }

    public int Length { get; }

    public Span<T> Span
    {
        get
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NativeBufferOwner<T>));
            }

            return new Span<T>(_ptr, Length);
        }
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        T* ptr = _ptr;
        _ptr = null;
        if (ptr is not null)
        {
            NativeMemory.Free(ptr);
        }
    }
}
