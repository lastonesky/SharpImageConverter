using System;

namespace PictureSharp;

public class JpegHuffmanTable
{
    public byte TableClass { get; }  // 0=DC, 1=AC
    public byte TableId { get; }
    public byte[] CodeLengths { get; } = new byte[16]; // 每个码长的符号数量
    public byte[] Symbols { get; }    // 所有符号按顺序排列

    public JpegHuffmanTable(byte tableClass, byte tableId, byte[] codeLengths, byte[] symbols)
    {
        TableClass = tableClass;
        TableId = tableId;
        Array.Copy(codeLengths, CodeLengths, 16);
        Symbols = symbols;
    }

    public void Print()
    {
        Console.WriteLine($"Huffman Table: Class={(TableClass==0?"DC":"AC")}, ID={TableId}");
        Console.Write("Code lengths: ");
        for (int i = 0; i < 16; i++)
            Console.Write($"{CodeLengths[i]} ");
        Console.WriteLine($"\nSymbols ({Symbols.Length}): {string.Join(" ", Symbols)}");
    }
}
