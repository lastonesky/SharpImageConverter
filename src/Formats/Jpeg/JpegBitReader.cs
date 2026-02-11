using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Jpeg;

internal ref struct JpegBitReader
{
    private ReadOnlySpan<byte> data;
    private int offset;
    private uint bitBuffer;
    private int bitCount;
    private int pendingMarker;
    private int padByteCount;

    public JpegBitReader(ReadOnlySpan<byte> entropyData)
    {
        data = entropyData;
        offset = 0;
        bitBuffer = 0;
        bitCount = 0;
        pendingMarker = -1;
        padByteCount = 0;
    }

    public bool HasPendingMarker => pendingMarker >= 0;

    public readonly int PendingMarker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => pendingMarker;
    }

    public readonly int BytesConsumed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => offset;
    }

    public readonly int BitCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => bitCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearPendingMarker() => pendingMarker = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        bitBuffer = 0;
        bitCount = 0;
        pendingMarker = -1;
        padByteCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        bitCount -= bitCount & 7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadByte(out byte value)
    {
        if ((uint)offset >= (uint)data.Length)
        {
            value = 0;
            return false;
        }

        value = data[offset++];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillBitBuffer(int minBits)
    {
        while (bitCount < minBits)
        {
            if (pendingMarker == (int)JpegMarker.EOI && padByteCount < 4)
            {
                bitBuffer = (bitBuffer << 8) | 0xFFu;
                bitCount += 8;
                padByteCount++;
                continue;
            }

            if (pendingMarker >= 0)
            {
                break;
            }

            if (!TryReadByte(out byte b))
            {
                pendingMarker = (int)JpegMarker.EOI;
                continue;
            }

            if (b == 0xFF)
            {
                if (!TryReadByte(out byte next))
                {
                    pendingMarker = (int)JpegMarker.EOI;
                    continue;
                }

                while (next == 0xFF)
                {
                    if (!TryReadByte(out next))
                    {
                        pendingMarker = (int)JpegMarker.EOI;
                        break;
                    }
                }

                if (pendingMarker >= 0)
                {
                    break;
                }

                if (next == 0x00)
                {
                    b = 0xFF;
                }
                else
                {
                    pendingMarker = next;
                    if (pendingMarker == (int)JpegMarker.EOI)
                    {
                        continue;
                    }

                    break;
                }
            }

            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count)
    {
        FillBitBuffer(count);
        if (bitCount < count)
        {
            int missing = count - bitCount;
            uint mask = (uint)((1 << count) - 1);
            if (missing >= 32)
            {
                return mask;
            }

            uint avail = bitCount == 0 ? 0u : (bitBuffer & ((1u << bitCount) - 1u));
            uint pad = (uint)((1 << missing) - 1);
            return ((avail << missing) | pad) & mask;
        }

        return (bitBuffer >> (bitCount - count)) & ((uint)(1 << count) - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count)
    {
        if (count <= 0)
        {
            return;
        }

        FillBitBuffer(count);
        if (bitCount < count)
        {
            ThrowHelper.ThrowInvalidData($"Unexpected end of entropy-coded data (needBits={count}, haveBits={bitCount}, bytesConsumed={offset}, pendingMarker={pendingMarker}).");
        }

        bitCount -= count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        uint v = PeekBits(count);
        SkipBits(count);
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReceiveAndExtend(int size)
    {
        if (size == 0)
        {
            return 0;
        }

        int v = (int)ReadBits(size);
        int vt = 1 << (size - 1);
        if (v < vt)
        {
            v += (-1 << size) + 1;
        }

        return v;
    }
}

internal sealed class JpegStreamInput(Stream stream, int bufferSize = 16 * 1024) : IAsyncDisposable
{
    private readonly Stream stream = stream;
    private readonly byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    private int bufferPos;
    private int bufferLen;

    public async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < dest.Length)
        {
            if (bufferPos < bufferLen)
            {
                int available = bufferLen - bufferPos;
                int toCopy = Math.Min(available, dest.Length - total);
                buffer.AsMemory(bufferPos, toCopy).CopyTo(dest.Slice(total, toCopy));
                bufferPos += toCopy;
                total += toCopy;
                continue;
            }

            bufferLen = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            bufferPos = 0;
            if (bufferLen == 0)
            {
                break;
            }
        }

        return total;
    }

    public async ValueTask ReadExactAsync(Memory<byte> dest, CancellationToken cancellationToken)
    {
        int read = await ReadAsync(dest, cancellationToken).ConfigureAwait(false);
        if (read != dest.Length)
        {
            ThrowHelper.ThrowInvalidData("Unexpected end of file.");
        }
    }

    public async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (bufferPos >= bufferLen)
        {
            bufferLen = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            bufferPos = 0;
            if (bufferLen == 0)
            {
                ThrowHelper.ThrowInvalidData("Unexpected end of file.");
            }
        }

        return buffer[bufferPos++];
    }

    public byte ReadByteBlocking(CancellationToken cancellationToken)
    {
        ValueTask<byte> vt = ReadByteAsync(cancellationToken);
        if (vt.IsCompletedSuccessfully)
        {
            return vt.Result;
        }

        return vt.AsTask().GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        ArrayPool<byte>.Shared.Return(buffer);
        return ValueTask.CompletedTask;
    }
}

internal struct JpegStreamBitReader(JpegStreamInput input, CancellationToken cancellationToken)
{
    private readonly JpegStreamInput input = input;
    private readonly CancellationToken cancellationToken = cancellationToken;
    private uint bitBuffer = 0;
    private int bitCount = 0;
    private int pendingMarker = -1;
    private int padByteCount = 0;
    private int bytesConsumed = 0;

    public readonly bool HasPendingMarker => pendingMarker >= 0;

    public readonly int PendingMarker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => pendingMarker;
    }

    public readonly int BytesConsumed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => bytesConsumed;
    }

    public readonly int BitCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => bitCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearPendingMarker() => pendingMarker = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        bitBuffer = 0;
        bitCount = 0;
        pendingMarker = -1;
        padByteCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        bitCount -= bitCount & 7;
    }

    private bool TryReadByte(out byte value)
    {
        try
        {
            value = input.ReadByteBlocking(cancellationToken);
            bytesConsumed++;
            return true;
        }
        catch (InvalidDataException)
        {
            value = 0;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillBitBuffer(int minBits)
    {
        while (bitCount < minBits)
        {
            if (pendingMarker == (int)JpegMarker.EOI && padByteCount < 4)
            {
                bitBuffer = (bitBuffer << 8) | 0xFFu;
                bitCount += 8;
                padByteCount++;
                continue;
            }

            if (pendingMarker >= 0)
            {
                break;
            }

            if (!TryReadByte(out byte b))
            {
                pendingMarker = (int)JpegMarker.EOI;
                continue;
            }

            if (b == 0xFF)
            {
                if (!TryReadByte(out byte next))
                {
                    pendingMarker = (int)JpegMarker.EOI;
                    continue;
                }

                while (next == 0xFF)
                {
                    if (!TryReadByte(out next))
                    {
                        pendingMarker = (int)JpegMarker.EOI;
                        break;
                    }
                }

                if (pendingMarker >= 0)
                {
                    break;
                }

                if (next == 0x00)
                {
                    b = 0xFF;
                }
                else
                {
                    pendingMarker = next;
                    if (pendingMarker == (int)JpegMarker.EOI)
                    {
                        continue;
                    }

                    break;
                }
            }

            bitBuffer = (bitBuffer << 8) | b;
            bitCount += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count)
    {
        FillBitBuffer(count);
        if (bitCount < count)
        {
            int missing = count - bitCount;
            uint mask = (uint)((1 << count) - 1);
            if (missing >= 32)
            {
                return mask;
            }

            uint avail = bitCount == 0 ? 0u : (bitBuffer & ((1u << bitCount) - 1u));
            uint pad = (uint)((1 << missing) - 1);
            return ((avail << missing) | pad) & mask;
        }

        return (bitBuffer >> (bitCount - count)) & ((uint)(1 << count) - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int count)
    {
        if (count <= 0)
        {
            return;
        }

        FillBitBuffer(count);
        if (bitCount < count)
        {
            ThrowHelper.ThrowInvalidData($"Unexpected end of entropy-coded data (needBits={count}, haveBits={bitCount}, bytesConsumed={bytesConsumed}, pendingMarker={pendingMarker}).");
        }

        bitCount -= count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        uint v = PeekBits(count);
        SkipBits(count);
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReceiveAndExtend(int size)
    {
        if (size == 0)
        {
            return 0;
        }

        int v = (int)ReadBits(size);
        int vt = 1 << (size - 1);
        if (v < vt)
        {
            v += (-1 << size) + 1;
        }

        return v;
    }
}
