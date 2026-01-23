using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Jpeg;

internal sealed class HuffmanDecodingTable
{
    private readonly ushort[] fast = new ushort[1 << FastBits];
    private readonly int[] minCode = new int[17];
    private readonly int[] maxCode = new int[17];
    private readonly int[] valPtr = new int[17];
    private readonly byte[] huffVal = new byte[256];
    private readonly short[] left = new short[512];
    private readonly short[] right = new short[512];
    private readonly short[] symbol = new short[512];
    private int nodeCount;

    private const int FastBits = 9;

    public void Build(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        if (bits.Length != 16)
        {
            ThrowHelper.ThrowInvalidData("Invalid Huffman bits length.");
        }

        int total = 0;
        for (int i = 0; i < 16; i++)
        {
            total += bits[i];
        }

        if (total == 0 || total > 256 || values.Length < total)
        {
            ThrowHelper.ThrowInvalidData("Invalid Huffman table.");
        }

        values.Slice(0, total).CopyTo(huffVal);

        Array.Clear(fast);
        nodeCount = 1;
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = -1;
            right[i] = -1;
            symbol[i] = -1;
        }

        for (int i = 0; i < 17; i++)
        {
            minCode[i] = 0;
            maxCode[i] = -1;
            valPtr[i] = 0;
        }

        Span<byte> huffSize = stackalloc byte[257];
        Span<int> huffCode = stackalloc int[257];

        int k = 0;
        for (int i = 1; i <= 16; i++)
        {
            int num = bits[i - 1];
            for (int j = 0; j < num; j++)
            {
                huffSize[k++] = (byte)i;
            }
        }
        huffSize[k] = 0;

        int code = 0;
        int si = huffSize[0];
        k = 0;
        while (huffSize[k] != 0)
        {
            while (huffSize[k] == si)
            {
                huffCode[k] = code;
                code++;
                k++;
            }

            code <<= 1;
            si++;
        }

        int p = 0;
        for (int l = 1; l <= 16; l++)
        {
            int num = bits[l - 1];
            if (num == 0)
            {
                maxCode[l] = -1;
                continue;
            }

            valPtr[l] = p;
            minCode[l] = huffCode[p];
            p += num;
            maxCode[l] = huffCode[p - 1];
        }

        for (int i = 0; i < total; i++)
        {
            int s = huffSize[i];
            if (s <= FastBits)
            {
                int hcode = huffCode[i] << (FastBits - s);
                int max = 1 << (FastBits - s);
                ushort packed = (ushort)((s << 8) | huffVal[i]);
                for (int j = 0; j < max; j++)
                {
                    fast[hcode + j] = packed;
                }
            }

            int codeBits = huffCode[i];
            int node = 0;
            for (int bitPos = s - 1; bitPos >= 0; bitPos--)
            {
                int bit = (codeBits >> bitPos) & 1;
                ref short child = ref (bit == 0 ? ref left[node] : ref right[node]);
                if (child < 0)
                {
                    if (nodeCount >= left.Length)
                    {
                        ThrowHelper.ThrowInvalidData("Huffman tree too large.");
                    }

                    child = (short)nodeCount++;
                }

                node = child;
            }

            symbol[node] = huffVal[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(ref JpegBitReader reader)
    {
        uint c = reader.PeekBits(FastBits);
        ushort f = fast[c];
        if (f != 0)
        {
            int s = f >> 8;
            reader.SkipBits(s);
            return (byte)f;
        }

        return DecodeSlow(ref reader);
    }

    private int DecodeSlow(ref JpegBitReader reader)
    {
        int code = 0;
        for (int s = 1; s <= 16; s++)
        {
            code = (code << 1) | (int)reader.ReadBits(1);
            int max = maxCode[s];
            int min = minCode[s];
            if (max >= 0 && code >= min && code <= max)
            {
                int idx = valPtr[s] + (code - min);
                if ((uint)idx >= (uint)huffVal.Length)
                {
                    ThrowHelper.ThrowInvalidData($"Invalid Huffman table index (idx={idx}, s={s}, code={code}, bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}).");
                }

                return huffVal[idx];
            }
        }

        ThrowHelper.ThrowInvalidData($"Invalid Huffman code (bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}).");
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(ref JpegStreamBitReader reader)
    {
        uint c = reader.PeekBits(FastBits);
        ushort f = fast[c];
        if (f != 0)
        {
            int s = f >> 8;
            reader.SkipBits(s);
            return (byte)f;
        }

        return DecodeSlow(ref reader);
    }

    private int DecodeSlow(ref JpegStreamBitReader reader)
    {
        int code = 0;
        for (int s = 1; s <= 16; s++)
        {
            code = (code << 1) | (int)reader.ReadBits(1);
            int max = maxCode[s];
            int min = minCode[s];
            if (max >= 0 && code >= min && code <= max)
            {
                int idx = valPtr[s] + (code - min);
                if ((uint)idx >= (uint)huffVal.Length)
                {
                    ThrowHelper.ThrowInvalidData($"Invalid Huffman table index (idx={idx}, s={s}, code={code}, bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}).");
                }

                return huffVal[idx];
            }
        }

        ThrowHelper.ThrowInvalidData($"Invalid Huffman code (bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}).");
        return 0;
    }
}
