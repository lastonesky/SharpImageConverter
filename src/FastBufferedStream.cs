using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImageConverter;
public unsafe class FastBufferedStream : IDisposable
{
    private Stream _baseStream;
    private byte[] _managedBuffer;
    private MemoryHandle _bufferHandle;
    private byte* _ptrBuffer; // 指向钉住内存的原生指针
    
    private int _bufferSize;
    private int _bufferIndex;
    private int _bufferLength; // 当前缓冲区内有效数据的长度
    private bool _isDisposed;

    public FastBufferedStream(Stream baseStream, int bufferSize = 65536)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _bufferSize = bufferSize;
        
        // 1. 从内存池租借内存
        _managedBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        
        // 2. 钉住内存，获取指针
        _bufferHandle = new Memory<byte>(_managedBuffer).Pin();
        _ptrBuffer = (byte*)_bufferHandle.Pointer;

        // 初始状态设为已读完，强制触发第一次 FillBuffer
        _bufferIndex = 0;
        _bufferLength = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(byte[] destination, int offset, int count)
    {
        int remaining = _bufferLength - _bufferIndex;

        // 场景 A: 快速路径 - 缓冲区内有足够数据
        if (remaining >= count)
        {
            fixed (byte* d = &destination[offset])
            {
                Buffer.MemoryCopy(_ptrBuffer + _bufferIndex, d, count, count);
            }
            _bufferIndex += count;
            return count;
        }

        // 场景 B: 慢速路径 - 需要跨缓冲区读取或直接读取
        return ReadSlow(destination, offset, count);
    }

    private int ReadSlow(byte[] destination, int offset, int count)
    {
        int totalRead = 0;

        // 1. 先把当前缓冲区剩下的读完
        int remaining = _bufferLength - _bufferIndex;
        if (remaining > 0)
        {
            fixed (byte* d = &destination[offset])
            {
                Buffer.MemoryCopy(_ptrBuffer + _bufferIndex, d, remaining, remaining);
            }
            totalRead += remaining;
            _bufferIndex += remaining;
        }

        // 2. 如果请求量依然很大，绕过缓冲区直接读底层流
        if (count - totalRead >= _bufferSize)
        {
            int directRead = _baseStream.Read(destination, offset + totalRead, count - totalRead);
            return totalRead + directRead;
        }

        // 3. 填充缓冲区，再读取剩余部分
        FillBuffer();
        int secondPart = Math.Min(count - totalRead, _bufferLength);
        if (secondPart > 0)
        {
            fixed (byte* d = &destination[offset + totalRead])
            {
                Buffer.MemoryCopy(_ptrBuffer, d, secondPart, secondPart);
            }
            _bufferIndex = secondPart;
            totalRead += secondPart;
        }

        return totalRead;
    }

    private void FillBuffer()
    {
        _bufferLength = _baseStream.Read(_managedBuffer, 0, _bufferSize);
        _bufferIndex = 0;
    }

    // 针对单个字节的高性能读取（霍夫曼解码常用）
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadByte()
    {
        if (_bufferIndex >= _bufferLength)
        {
            FillBuffer();
            if (_bufferLength == 0) return -1;
        }
        return _ptrBuffer[_bufferIndex++];
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _bufferHandle.Dispose(); // 解除 Pin
            if (_managedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_managedBuffer);
            }
            _isDisposed = true;
        }
    }
}