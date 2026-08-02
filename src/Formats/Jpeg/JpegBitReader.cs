using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// JPEG 熵编码位流读取器。支持两种数据源：
/// <list type="bullet">
/// <item><see cref="ReadOnlySpan{T}"/>：内存解码，零拷贝读取完整熵数据；</item>
/// <item><see cref="JpegStreamInput"/>：流式解码，按需阻塞读取。</item>
/// </list>
/// 两种模式共享同一套 0xFF/0x00 填充剥离、marker 探测与 EOI padding 逻辑。
/// </summary>
internal ref struct JpegBitReader
{
    private ReadOnlySpan<byte> data;
    private int offset;
    private int bytesConsumed;
    private uint bitBuffer;
    private int bitCount;
    private int pendingMarker;
    private int padByteCount;
    private readonly JpegStreamInput? streamInput;
    private readonly CancellationToken cancellationToken;

    public JpegBitReader(ReadOnlySpan<byte> entropyData)
    {
        data = entropyData;
        offset = 0;
        bytesConsumed = 0;
        bitBuffer = 0;
        bitCount = 0;
        pendingMarker = -1;
        padByteCount = 0;
        streamInput = null;
        cancellationToken = default;
    }

    public JpegBitReader(JpegStreamInput input, CancellationToken cancellationToken)
    {
        data = default;
        offset = 0;
        bytesConsumed = 0;
        bitBuffer = 0;
        bitCount = 0;
        pendingMarker = -1;
        padByteCount = 0;
        streamInput = input;
        this.cancellationToken = cancellationToken;
    }

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

    /// <summary>
    /// 丢弃缓冲中所有未消费位（含对齐后的整字节余量），强制后续读取从数据源重新装载。
    /// 用于扫描/RST 边界：这些边界前数据已字节对齐，必须看到真实的 0xFF marker 序列。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DiscardBufferedBits()
    {
        bitCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadByte(out byte value)
    {
        if (streamInput is not null)
        {
            if (streamInput.TryReadByteBlocking(cancellationToken, out value))
            {
                bytesConsumed++;
                return true;
            }

            value = 0;
            return false;
        }

        if ((uint)offset >= (uint)data.Length)
        {
            value = 0;
            return false;
        }

        value = data[offset++];
        bytesConsumed++;
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

    public async ValueTask<ushort> ReadU16Async(CancellationToken cancellationToken)
    {
        byte b1 = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        byte b2 = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        return (ushort)((b1 << 8) | b2);
    }

    public async ValueTask SkipAsync(int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (bufferPos < bufferLen)
            {
                int available = bufferLen - bufferPos;
                int toSkip = Math.Min(available, count);
                bufferPos += toSkip;
                count -= toSkip;
            }
            else
            {
                // If count is large, we could seek the stream, but JPEG streams are often not seekable.
                // Just read and discard.
                bufferLen = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                bufferPos = 0;
                if (bufferLen == 0)
                {
                    ThrowHelper.ThrowInvalidData("Unexpected end of file.");
                }
            }
        }
    }

    public bool TryReadByteBlocking(CancellationToken cancellationToken, out byte value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bufferPos >= bufferLen)
        {
            bufferLen = stream.Read(buffer, 0, buffer.Length);
            bufferPos = 0;
            if (bufferLen == 0)
            {
                value = 0;
                return false;
            }
        }

        value = buffer[bufferPos++];
        return true;
    }

    public ValueTask DisposeAsync()
    {
        ArrayPool<byte>.Shared.Return(buffer);
        return ValueTask.CompletedTask;
    }
}
