using System;
using System.Collections.Generic;
using System.IO;

public class JpegParser
{
    public List<JpegSegment> Segments { get; } = new();
    public Dictionary<byte, JpegQuantTable> QuantTables { get; } = new();
    public Dictionary<(byte, byte), JpegHuffmanTable> HuffmanTables { get; } = new();
    public List<JpegScan> Scans { get; } = new();

    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Parse(string path)
    {
        T.Assert(File.Exists(path), $"文件不存在: {path}");

        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            int b1 = fs.ReadByte();
            int b2 = fs.ReadByte();
            T.Assert(b1 == 0xFF && b2 == 0xD8, "不是 JPEG 文件头（FF D8）");

            Segments.Add(new JpegSegment(0xFFD8, 0, 0));

            while (fs.Position < fs.Length)
            {
                b1 = fs.ReadByte();
                if (b1 == -1) break;
                if (b1 != 0xFF) continue;

                // 跳过连续的 FF
                do { b2 = fs.ReadByte(); } while (b2 == 0xFF);
                if (b2 == -1) break;

                ushort marker = (ushort)((0xFF << 8) | b2);

                if (marker == 0xFFD9)
                {
                    Segments.Add(new JpegSegment(marker, (int)fs.Position - 2, 0));
                    break;
                }

                int lenHi = fs.ReadByte();
                int lenLo = fs.ReadByte();
                if (lenHi == -1 || lenLo == -1) break;
                int segLen = (lenHi << 8) | lenLo;

                long segStart = fs.Position - 4;
                Segments.Add(new JpegSegment(marker, (int)segStart, segLen));

                // =============== 解析 DQT 段 ===============
                if (marker == 0xFFDB)
                {
                    byte[] buf = new byte[segLen - 2];
                    fs.Read(buf, 0, buf.Length);
                    ParseQuantTables(buf);
                }
                // =============== 解析 SOF0 段 ===============
                else if (marker == 0xFFC0)
                {
                    byte[] buf = new byte[segLen - 2];
                    fs.Read(buf, 0, buf.Length);
                    byte precision = buf[0];
                    Height = (buf[1] << 8) | buf[2];
                    Width = (buf[3] << 8) | buf[4];
                }
                // =============== 解析 DHT 段 ===============
                else if (marker == 0xFFC4)
                {
                    ParseHuffmanTables(fs, segLen);
                }
                else if (marker == 0xFFDA) // SOS
                {
                    lenHi = fs.ReadByte();
                    lenLo = fs.ReadByte();
                    segLen = (lenHi << 8) | lenLo; // 包含长度字节
                    int remaining = segLen - 2;        // 段内容长度

                    int nbChannels = fs.ReadByte();
                    remaining--;

                    var comps = new (byte channelId, byte dcTableId, byte acTableId)[nbChannels];
                    for (int i = 0; i < nbChannels; i++)
                    {
                        int cId = fs.ReadByte();
                        int table = fs.ReadByte();
                        remaining -= 2;
                        comps[i] = ((byte)cId, (byte)(table >> 4), (byte)(table & 0x0F));
                    }

                    // 跳过 Ss, Se, Ah/Al 三个字节
                    fs.Position += 3;
                    remaining -= 3;

                    long scanDataOffset = fs.Position;
                    long scanDataLength;

                    // 搜索下一个 marker (0xFF) 或文件结尾
                    long scanEnd = fs.Length;
                    while (fs.Position < fs.Length)
                    {
                        int b = fs.ReadByte();
                        if (b == 0xFF)
                        {
                            int next = fs.ReadByte();
                            if (next != 0x00)
                            {
                                fs.Position -= 2; // 回到 marker 起始
                                scanEnd = fs.Position;
                                break;
                            }
                        }
                    }
                    scanDataLength = scanEnd - scanDataOffset;

                    Scans.Add(new JpegScan(nbChannels, comps, (int)scanDataOffset) { DataLength = scanDataLength });

                    // 跳到扫描段末尾
                    fs.Position = scanEnd;
                }
                else
                {
                    fs.Position += segLen - 2;
                }
                

            }
        }

        T.Assert(Segments.Count > 0, "未解析出任何段");
        if (Width == 0 || Height == 0)
            Console.WriteLine("⚠️ 未找到 SOF0 段，无法确定图像尺寸。");

        T.Assert(QuantTables.Count > 0, "未找到任何量化表 (FFDB)。");
    }

    private void ParseQuantTables(byte[] buf)
    {
        int pos = 0;
        while (pos < buf.Length)
        {
            byte info = buf[pos++];
            byte precision = (byte)(info >> 4);
            byte id = (byte)(info & 0x0F);

            int elemCount = 64;
            ushort[] values = new ushort[64];
            for (int i = 0; i < elemCount; i++)
            {
                if (precision == 0)
                    values[i] = buf[pos++];
                else
                {
                    values[i] = (ushort)((buf[pos] << 8) | buf[pos + 1]);
                    pos += 2;
                }
            }

            QuantTables[id] = new JpegQuantTable(id, precision, values);
        }
    }
    private void ParseHuffmanTables(FileStream fs, int segLen)
    {
        int bytesRead = 0;
        while (bytesRead < segLen - 2)
        {
            int info = fs.ReadByte();
            if (info == -1) break;
            bytesRead++;

            byte tableClass = (byte)(info >> 4);
            byte tableId = (byte)(info & 0x0F);

            byte[] codeLengths = new byte[16];
            int totalSymbols = 0;
            for (int i = 0; i < 16; i++)
            {
                int b = fs.ReadByte();
                if (b == -1) throw new Exception("DHT 段不完整，code lengths 越界");
                codeLengths[i] = (byte)b;
                totalSymbols += codeLengths[i];
                bytesRead++;
            }

            byte[] symbols = new byte[totalSymbols];
            int n = fs.Read(symbols, 0, totalSymbols);
            if (n != totalSymbols)
                throw new Exception($"DHT 段符号数量 ({totalSymbols}) 超过剩余字节 ({n})");
            bytesRead += totalSymbols;

            HuffmanTables[(tableClass, tableId)] = new JpegHuffmanTable(tableClass, tableId, codeLengths, symbols);
        }
    }


}
public class JpegScan
{
    public int NbChannels { get; }
    public (byte channelId, byte dcTableId, byte acTableId)[] Components { get; }
    public long DataOffset { get; }
    public long DataLength { get; set; } // 在解析结束后计算

    public JpegScan(int nbChannels, (byte, byte, byte)[] comps, long dataOffset)
    {
        NbChannels = nbChannels;
        Components = comps;
        DataOffset = dataOffset;
    }
}