using System;

namespace SharpImageConverter;

internal class HuffmanDecodingTable
{
    public JpegHuffmanTable Table { get; }
    public int[] MaxCode { get; } = new int[17];
    public int[] MinCode { get; } = new int[17];
    public int[] ValPtr { get; } = new int[17];
    public byte[] FastBits { get; } = new byte[256];
    public byte[] FastSymbols { get; } = new byte[256];

    public HuffmanDecodingTable(JpegHuffmanTable table)
    {
        Table = table;
        GenerateTables();
    }

    private void GenerateTables()
    {
        int p = 0;
        int[] huffsize = new int[257];
        int[] huffcode = new int[257];

        for (int i = 1; i <= 16; i++)
        {
            for (int j = 1; j <= Table.CodeLengths[i - 1]; j++)
            {
                if (p >= 256)
                {
                    throw new InvalidOperationException($"Huffman table overflow. p={p}, i={i}");
                }
                huffsize[p++] = i;
            }
        }
        huffsize[p] = 0;

        int code = 0;
        int si = huffsize[0];
        p = 0;
        while (huffsize[p] != 0)
        {
            while (huffsize[p] == si)
            {
                huffcode[p++] = code;
                code++;
            }
            code <<= 1;
            si++;
        }

        int jIdx = 0;
        for (int i = 0; i < 17; i++) MaxCode[i] = -1;

        for (int i = 1; i <= 16; i++)
        {
            if (Table.CodeLengths[i - 1] == 0)
            {
                MaxCode[i] = -1;
            }
            else
            {
                ValPtr[i] = jIdx;
                MinCode[i] = huffcode[jIdx];
                MaxCode[i] = huffcode[jIdx + Table.CodeLengths[i - 1] - 1];
                jIdx += Table.CodeLengths[i - 1];
            }
        }

        Array.Clear(FastBits, 0, FastBits.Length);
        Array.Clear(FastSymbols, 0, FastSymbols.Length);

        int total = Math.Min(jIdx, Table.Symbols.Length);
        for (int i = 0; i < total; i++)
        {
            int size = huffsize[i];
            if (size <= 0 || size > 16) continue;
            if (size > 8) continue;

            int code8 = huffcode[i] << (8 - size);
            int fill = 1 << (8 - size);
            byte sym = Table.Symbols[i];
            for (int j = 0; j < fill; j++)
            {
                int idx = code8 | j;
                FastBits[idx] = (byte)size;
                FastSymbols[idx] = sym;
            }
        }
    }
}
