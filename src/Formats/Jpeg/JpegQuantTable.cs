using System;

namespace SharpImageConverter;

/// <summary>
/// JPEG 量化表（8 位或 16 位，包含 64 个系数）。
/// </summary>
public class JpegQuantTable
{
    /// <summary>
    /// 表编号（0..3）
    /// </summary>
    public byte Id { get; }
    /// <summary>
    /// 精度（0 表示 8 位，1 表示 16 位）
    /// </summary>
    public byte Precision { get; }
    /// <summary>
    /// 64 个量化系数，按自然顺序存储
    /// </summary>
    public ushort[] Values { get; } = new ushort[64];

    /// <summary>
    /// 创建量化表
    /// </summary>
    /// <param name="id">表编号</param>
    /// <param name="precision">精度（0/1）</param>
    /// <param name="values">64 个系数</param>
    public JpegQuantTable(byte id, byte precision, ushort[] values)
    {
        Id = id;
        Precision = precision;
        Array.Copy(values, Values, 64);
    }

    /// <summary>
    /// 在控制台打印量化表内容（用于调试）
    /// </summary>
    public void Print()
    {
        Console.WriteLine($"Quantization Table ID={Id}, Precision={(Precision == 0 ? 8 : 16)}-bit");
        for (int i = 0; i < 64; i++)
        {
            Console.Write($"{Values[i],4}");
            if ((i + 1) % 8 == 0)
                Console.WriteLine();
        }
    }
}
