using System.Buffers;
using System.Runtime.CompilerServices;

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

    public int PendingMarker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => pendingMarker;
    }

    public int BytesConsumed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => offset;
    }

    public int BitCount
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
