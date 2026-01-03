using System;

namespace SharpImageConverter;

/// <summary>
/// JPEG Huffman 表，包含每个码长的数量与符号列表。
/// </summary>
public class JpegHuffmanTable
{
    /// <summary>
    /// 表类型（0=DC，1=AC）
    /// </summary>
    public byte TableClass { get; }  // 0=DC, 1=AC
    /// <summary>
    /// 表编号（0..3）
    /// </summary>
    public byte TableId { get; }
    /// <summary>
    /// 每个码长的符号数量（16 个字节）
    /// </summary>
    public byte[] CodeLengths { get; } = new byte[16]; // 每个码长的符号数量
    /// <summary>
    /// 所有符号按顺序排列
    /// </summary>
    public byte[] Symbols { get; }    // 所有符号按顺序排列
    /// <summary>
    /// 创建 Huffman 表
    /// </summary>
    /// <param name="tableClass">表类型（0=DC，1=AC）</param>
    /// <param name="tableId">表编号</param>
    /// <param name="codeLengths">16 个码长计数</param>
    /// <param name="symbols">符号列表</param>
    public JpegHuffmanTable(byte tableClass, byte tableId, byte[] codeLengths, byte[] symbols)
    {
        TableClass = tableClass;
        TableId = tableId;
        Array.Copy(codeLengths, CodeLengths, 16);
        Symbols = symbols;
    }

    /// <summary>
    /// 在控制台打印 Huffman 表内容（用于调试）
    /// </summary>
    public void Print()
    {
        Console.WriteLine($"Huffman Table: Class={(TableClass==0?"DC":"AC")}, ID={TableId}");
        Console.Write("Code lengths: ");
        for (int i = 0; i < 16; i++)
            Console.Write($"{CodeLengths[i]} ");
        Console.WriteLine($"\nSymbols ({Symbols.Length}): {string.Join(" ", Symbols)}");
    }
}
