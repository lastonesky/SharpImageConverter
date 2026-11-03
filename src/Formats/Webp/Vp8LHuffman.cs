namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8L 的 Huffman 码长读取，严格对照 libwebp 的 ReadHuffmanCode / ReadHuffmanCodeLengths 实现。
/// </summary>
internal static class Vp8LHuffman
{
    // 常量（对照 libwebp format_constants.h / vp8l_dec.c）
    private const int NumCodeLengthCodes = 19;
    private const int DefaultCodeLength = 8;

    private static readonly int[] CodeLengthCodeOrder = new[]
    {
        17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
    };

    private static readonly int[] CodeLengthExtraBits = { 2, 3, 7 };
    private static readonly int[] CodeLengthRepeatOffsets = { 3, 3, 11 };

    /// <summary>
    /// 从位流中读取单个 Huffman 表的码长数组。
    /// 对照 libwebp 的 ReadHuffmanCode()。
    /// </summary>
    /// <param name="reader">位流读取器。</param>
    /// <param name="alphabetSize">期望的符号数量。</param>
    /// <returns>码长数组（长度=alphabetSize），至少有一个非零码长。</returns>
    public static int[] ReadOneHuffmanCode(ref LsbBitReader reader, int alphabetSize)
    {
        int[] codeLengths = new int[alphabetSize];

        // 1 bit: simple_code
        int simpleCode = reader.ReadBit();

        if (simpleCode != 0)
        {
            // 简单模式：只有 1 或 2 个符号码长为 1
            int numSymbols = (int)reader.ReadBits(1) + 1;  // 1 or 2
            int firstSymbolLenCode = (int)reader.ReadBits(1);
            int symbol = (int)reader.ReadBits(firstSymbolLenCode == 0 ? 1 : 8);
            if (symbol < alphabetSize)
                codeLengths[symbol] = 1;
            if (numSymbols == 2)
            {
                symbol = (int)reader.ReadBits(8);
                if (symbol < alphabetSize)
                    codeLengths[symbol] = 1;
            }
        }
        else
        {
            // 标准 Huffman 编码模式
            int numCodes = (int)reader.ReadBits(4) + 4;  // 4..19
            if (numCodes > NumCodeLengthCodes)
                throw new WebpBitstreamException($"Huffman: num_codes={numCodes} 超出 {NumCodeLengthCodes}");

            int[] codeLengthCodeLengths = new int[NumCodeLengthCodes];
            for (int i = 0; i < numCodes; i++)
            {
                int cl = (int)reader.ReadBits(3);
                codeLengthCodeLengths[CodeLengthCodeOrder[i]] = cl;
            }

            // 构建 meta-Huffman 表（19 符号）
            var metaTable = new HuffmanTable(codeLengthCodeLengths);

            // max_symbol：libwebp 中这是"最多读取多少个码长元符号"的计数器，
            // 不是码长数组的目标下标。缺省 = alphabet_size。
            int maxSymbol = alphabetSize;
            if (reader.ReadBit() != 0)
            {
                int lengthNbits = 2 + 2 * (int)reader.ReadBits(3);  // 2, 4, 6, 8
                maxSymbol = 2 + (int)reader.ReadBits(lengthNbits);
                if (maxSymbol > alphabetSize)
                    throw new WebpBitstreamException(
                        $"Huffman: max_symbol={maxSymbol} 超出 alphabet_size={alphabetSize}");
            }

            // 用 meta 表解码实际的 code lengths：最多读取 maxSymbol 个元符号（或填满字母表为止）。
            int sym = 0;
            int symbolsToRead = maxSymbol;
            int prevCodeLen = DefaultCodeLength;
            while (sym < alphabetSize && symbolsToRead-- > 0)
            {
                int codeLenSym = metaTable.DecodeSymbol(ref reader);
                if (codeLenSym < 16)
                {
                    codeLengths[sym++] = codeLenSym;
                    if (codeLenSym != 0) prevCodeLen = codeLenSym;
                }
                else if (codeLenSym == 16 || codeLenSym == 17 || codeLenSym == 18)
                {
                    int slot = codeLenSym - 16;
                    int extraBits = CodeLengthExtraBits[slot];
                    int repeatOffset = CodeLengthRepeatOffsets[slot];
                    int repeat = (int)reader.ReadBits(extraBits) + repeatOffset;
                    if (sym + repeat > alphabetSize)
                        throw new WebpBitstreamException($"Huffman: repeat {repeat} 超出 alphabet_size={alphabetSize}");
                    int length = codeLenSym == 16 ? prevCodeLen : 0;
                    for (int k = 0; k < repeat; k++)
                    {
                        codeLengths[sym++] = length;
                    }
                }
                else
                {
                    throw new WebpBitstreamException($"Huffman: 非法的 code_length_code={codeLenSym}");
                }
            }
        }

        // 验证：至少有一个非零码长
        bool hasNonZero = false;
        for (int j = 0; j < alphabetSize; j++)
        {
            if (codeLengths[j] != 0) { hasNonZero = true; break; }
        }
        if (!hasNonZero)
            throw new WebpBitstreamException("Huffman 表全部码长为零");

        return codeLengths;
    }
}
