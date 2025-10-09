using System;

class Program
{
    static void Main()
    {
        Console.WriteLine("Step 5: 解析 JPEG 量化表...");

        string path = @"d:\img-1.jpg";
        JpegParser parser = new JpegParser();
        parser.Parse(path);

        Console.WriteLine($"解析到 {parser.Segments.Count} 个段。");
        Console.WriteLine($"✅ 图像尺寸: {parser.Width} x {parser.Height}");
        Console.WriteLine($"✅ 量化表数量: {parser.QuantTables.Count}");

        foreach (var kv in parser.QuantTables)
        {
            kv.Value.Print();
            Console.WriteLine();
        }

        Console.WriteLine("Step 5 OK: DQT 段解析完成");
        Console.WriteLine($"✅ Huffman 表数量: {parser.HuffmanTables.Count}");
        foreach (var kv in parser.HuffmanTables)
        {
            kv.Value.Print();
            Console.WriteLine();
        }
        Console.WriteLine("Step 6 OK: DHT 段解析完成");
        Console.WriteLine($"✅ 扫描段数量: {parser.Scans.Count}");
        foreach (var scan in parser.Scans)
        {
            Console.WriteLine($"Scan: NbChannels={scan.NbChannels}, DataOffset={scan.DataOffset}, DataLength={scan.DataLength}");
            for (int i = 0; i < scan.NbChannels; i++)
            {
                var c = scan.Components[i];
                Console.WriteLine($"  Channel {c.channelId}: DC={c.dcTableId}, AC={c.acTableId}");
            }
        }
        Console.WriteLine("Step 7 OK: SOS 段解析完成");

    }
}
