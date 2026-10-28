using System;
using System.Buffers;
using System.Buffers.Binary;
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
    private ulong bitBuffer;
    private int bitCount;
    private int pendingMarker;
    private int markerEndOffset;
    private int padByteCount;
    private bool reachedEndOfData;
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
        markerEndOffset = -1;
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
        markerEndOffset = -1;
        padByteCount = 0;
        streamInput = input;
        this.cancellationToken = cancellationToken;
    }

    public readonly bool HasPendingMarker => pendingMarker >= 0;

    /// <summary>
    /// 数据源已被读尽（未遇到真实 marker 就到达末尾）。流式解码时它意味着传输被截断：
    /// 后续 EOI 是 <see cref="FillBitBuffer"/> 用 0xFF 伪造的，不能当作「文件完整」的证据。
    /// </summary>
    public readonly bool ReachedEndOfData => reachedEndOfData;

    public readonly int PendingMarker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => pendingMarker;
    }

    /// <summary>
    /// 已检出 marker 的 code 字节之后在数据源中的位置；-1 表示尚未检出。
    /// 批量 refill 会预取数个字节，<see cref="BytesConsumed"/> 因此领先于真实位流位置；
    /// 需要把文件游标对齐到 marker 时必须用本属性。
    /// </summary>
    public readonly int MarkerEndOffset => markerEndOffset;

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
        markerEndOffset = -1;
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

            reachedEndOfData = true;
            value = 0;
            return false;
        }

        if ((uint)offset >= (uint)data.Length)
        {
            reachedEndOfData = true;
            value = 0;
            return false;
        }

        value = data[offset++];
        bytesConsumed++;
        return true;
    }

    /// <summary>
    /// 目标装载深度（位）。32 位一次装载可让约 4 个符号才 refill 一次；
    /// 上界 32 保证 bitCount + 32 ≤ 64 不溢出 ulong。
    /// </summary>
    private const int FillTarget = 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillBitBuffer(int minBits)
    {
        int target = minBits < FillTarget ? FillTarget : minBits;

        // 快路径：一次装 4 字节。窗口内无 0xFF 时可直接整块压入（约 98.5% 命中）；
        // 命中 0xFF 就退回慢路径，由原有的逐字节填充剥离逻辑处理 0xFF00 与 marker。
        while (bitCount <= 32 && bitCount < target && streamInput is null &&
               (uint)(offset + 4) <= (uint)data.Length)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            if (((~w - 0x01010101u) & ~(~w) & 0x80808080u) != 0)
            {
                break;
            }

            bitBuffer = (bitBuffer << 32) | BinaryPrimitives.ReverseEndianness(w);
            bitCount += 32;
            offset += 4;
            bytesConsumed += 4;
        }

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
                markerEndOffset = bytesConsumed;
                continue;
            }

            if (b == 0xFF)
            {
                if (!TryReadByte(out byte next))
                {
                    pendingMarker = (int)JpegMarker.EOI;
                    markerEndOffset = bytesConsumed;
                    continue;
                }

                while (next == 0xFF)
                {
                    if (!TryReadByte(out next))
                    {
                        pendingMarker = (int)JpegMarker.EOI;
                        markerEndOffset = bytesConsumed;
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
                    markerEndOffset = bytesConsumed;
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

            uint avail = bitCount == 0 ? 0u : (uint)(bitBuffer & ((1UL << bitCount) - 1UL));
            uint pad = (uint)((1 << missing) - 1);
            return ((avail << missing) | pad) & mask;
        }

        return (uint)(bitBuffer >> (bitCount - count)) & ((uint)(1 << count) - 1);
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

    /// <summary>
    /// 缓冲中已确知有 <paramref name="count"/> 位时直接扣减，跳过 refill。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBitsBuffered(int count) => bitCount -= count;

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

        uint raw = PeekBits(size);
        if (bitCount >= size)
        {
            bitCount -= size;
        }
        else
        {
            SkipBits(size);
        }

        int v = (int)raw;
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
        // 熵解码阶段使用同步阻塞读取：JpegBitReader 是 ref struct，无法跨 await 存活，
        // 且熵解码为不可切分的顺序循环（DC 差分链、RST 序列、EOB run），无法在字节边界
        // 暂停恢复。保持同步阻塞以换取实现简单；marker/segment 解析（ReadAsync/ReadByteAsync）
        // 仍是真正的异步读取。
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
