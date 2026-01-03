using System;

namespace SharpImageConverter;

public class JpegQuantTable
{
    public byte Id { get; }
    public byte Precision { get; }
    public ushort[] Values { get; } = new ushort[64];

    public JpegQuantTable(byte id, byte precision, ushort[] values)
    {
        Id = id;
        Precision = precision;
        Array.Copy(values, Values, 64);
    }

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
