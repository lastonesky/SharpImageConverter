using System;
using System.Collections.Generic;
using System.IO;

namespace PictureSharp;

public class JpegParser
{
    public List<JpegSegment> Segments { get; } = new();
    public Dictionary<byte, JpegQuantTable> QuantTables { get; } = new();
    public Dictionary<(byte, byte), JpegHuffmanTable> HuffmanTables { get; } = new();
    public List<JpegScan> Scans { get; } = new();

    public int Width { get; private set; }
    public int Height { get; private set; }
    public List<(byte id, byte h, byte v, byte quantId)> FrameComponents { get; } = new();
    public int MaxH { get; private set; }
    public int MaxV { get; private set; }
    public ushort RestartInterval { get; private set; }
    // EXIF 方向（1..8），默认 1 表示不旋转/翻转
    public int ExifOrientation { get; private set; } = 1;
    public bool IsProgressive { get; private set; } = false;

    public void Parse(string path)
    {
        T.Assert(File.Exists(path), $"文件不存在: {path}");

        using (FileStream fs = new (path, FileMode.Open, FileAccess.Read))
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
                    fs.ReadExactly(buf, 0, buf.Length);
                    ParseQuantTables(buf);
                }
                // =============== 解析 SOF0 段（基线） ===============
                else if (marker == 0xFFC0)
                {
                    byte[] buf = new byte[segLen - 2];
                    fs.ReadExactly(buf, 0, buf.Length);
                    byte precision = buf[0];
                    Height = (buf[1] << 8) | buf[2];
                    Width = (buf[3] << 8) | buf[4];
                    int nf = buf[5];
                    FrameComponents.Clear();
                    MaxH = 0; MaxV = 0;
                    int pos = 6;
                    for (int i = 0; i < nf; i++)
                    {
                        byte cid = buf[pos++];
                        byte hv = buf[pos++];
                        byte qid = buf[pos++];
                        byte h = (byte)(hv >> 4);
                        byte v = (byte)(hv & 0x0F);
                        FrameComponents.Add((cid, h, v, qid));
                        if (h > MaxH) MaxH = h;
                        if (v > MaxV) MaxV = v;
                    }
                    IsProgressive = false;
                }
                // =============== 解析 SOF2 段（渐进式） ===============
                else if (marker == 0xFFC2)
                {
                    byte[] buf = new byte[segLen - 2];
                    fs.ReadExactly(buf, 0, buf.Length);
                    byte precision = buf[0];
                    Height = (buf[1] << 8) | buf[2];
                    Width = (buf[3] << 8) | buf[4];
                    int nf = buf[5];
                    FrameComponents.Clear();
                    MaxH = 0; MaxV = 0;
                    int pos = 6;
                    for (int i = 0; i < nf; i++)
                    {
                        byte cid = buf[pos++];
                        byte hv = buf[pos++];
                        byte qid = buf[pos++];
                        byte h = (byte)(hv >> 4);
                        byte v = (byte)(hv & 0x0F);
                        FrameComponents.Add((cid, h, v, qid));
                        if (h > MaxH) MaxH = h;
                        if (v > MaxV) MaxV = v;
                    }
                    IsProgressive = true;
                }
                // =============== 解析 APP1 (EXIF) 段，提取方向 ===============
                else if (marker == 0xFFE1)
                {
                    byte[] buf = new byte[segLen - 2];
                    fs.ReadExactly(buf, 0, buf.Length);
                    TryParseExifOrientation(buf);
                }
                // =============== 解析 DHT 段 ===============
                else if (marker == 0xFFC4)
                {
                    ParseHuffmanTables(fs, segLen);
                }
                // =============== 解析 DRI 段 ===============
                else if (marker == 0xFFDD)
                {
                    // Restart Interval, 2 字节无符号
                    int rhi = fs.ReadByte();
                    int rlo = fs.ReadByte();
                    if (rhi == -1 || rlo == -1) throw new Exception("DRI 段不完整");
                    RestartInterval = (ushort)((rhi << 8) | rlo);
                }
                else if (marker == 0xFFDA) // SOS
                {
                    // 注意：segLen 已在上方统一读取，这里不应再次读取。
                    int remaining = segLen - 2;        // 段内容长度（不含长度字节）

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

                    // 读取 Ss, Se, Ah/Al 三个字节（渐进式必需；基线可忽略但仍保存）
                    int Ss = fs.ReadByte();
                    int Se = fs.ReadByte();
                    int AhAl = fs.ReadByte();
                    remaining -= 3;
                    byte Ah = (byte)((AhAl >> 4) & 0x0F);
                    byte Al = (byte)(AhAl & 0x0F);

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
                            if (next == -1) break;
                            if (next == 0x00)
                            {
                                // 0xFF00 表示字节填充，实际数据中的 0xFF
                                continue;
                            }
                            // RSTn 重启标记 (FFD0-FFD7) 出现在熵编码数据中，不能作为扫描结束
                            if (next >= 0xD0 && next <= 0xD7)
                            {
                                continue;
                            }
                            // 其他非填充的 0xFFxx 视为下一个段的标记
                            fs.Position -= 2; // 回到 marker 起始
                            scanEnd = fs.Position;
                            break;
                        }
                    }
                    scanDataLength = scanEnd - scanDataOffset;

                    var js = new JpegScan(nbChannels, comps, (int)scanDataOffset)
                    {
                        DataLength = scanDataLength,
                        Ss = (byte)Ss,
                        Se = (byte)Se,
                        Ah = Ah,
                        Al = Al
                    };
                    Scans.Add(js);

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

    private void TryParseExifOrientation(byte[] app1)
    {
        // APP1 前缀应为 "Exif\0\0"，之后为 TIFF 结构
        if (app1.Length < 8) return;
        if (!(app1[0] == (byte)'E' && app1[1] == (byte)'x' && app1[2] == (byte)'i' && app1[3] == (byte)'f' && app1[4] == 0 && app1[5] == 0))
            return;

        int tiffBase = 6;
        if (app1.Length < tiffBase + 8) return;
        bool littleEndian;
        if (app1[tiffBase + 0] == (byte)'I' && app1[tiffBase + 1] == (byte)'I') littleEndian = true;
        else if (app1[tiffBase + 0] == (byte)'M' && app1[tiffBase + 1] == (byte)'M') littleEndian = false;
        else return;

        ushort ReadU16(int offset)
        {
            if (littleEndian) return (ushort)(app1[offset] | (app1[offset + 1] << 8));
            else return (ushort)((app1[offset] << 8) | app1[offset + 1]);
        }
        uint ReadU32(int offset)
        {
            if (littleEndian) return (uint)(app1[offset] | (app1[offset + 1] << 8) | (app1[offset + 2] << 16) | (app1[offset + 3] << 24));
            else return (uint)((app1[offset] << 24) | (app1[offset + 1] << 16) | (app1[offset + 2] << 8) | app1[offset + 3]);
        }

        ushort magic = ReadU16(tiffBase + 2);
        if (magic != 42) return; // 0x2A
        uint ifd0Offset = ReadU32(tiffBase + 4);
        int ifd0 = tiffBase + (int)ifd0Offset;
        if (ifd0 < 0 || ifd0 + 2 > app1.Length) return;
        ushort numEntries = ReadU16(ifd0);
        int entryBase = ifd0 + 2;
        int entrySize = 12;
        for (int i = 0; i < numEntries; i++)
        {
            int e = entryBase + i * entrySize;
            if (e + entrySize > app1.Length) break;
            ushort tag = ReadU16(e + 0);
            ushort type = ReadU16(e + 2);
            uint count = ReadU32(e + 4);
            int valueOffset = e + 8;
            if (tag == 0x0112) // Orientation
            {
                int orientation = 1;
                if (type == 3 && count >= 1) // SHORT
                {
                    // 值可能直接在 4 字节中（count*2 <= 4），按端序读取前两个字节
                    orientation = littleEndian ? (app1[valueOffset] | (app1[valueOffset + 1] << 8)) : ((app1[valueOffset] << 8) | app1[valueOffset + 1]);
                }
                else
                {
                    // 其他情况：值在偏移处（不常见于 Orientation），尝试读取
                    uint valPtr = ReadU32(valueOffset);
                    int p = tiffBase + (int)valPtr;
                    if (p >= 0 && p + 2 <= app1.Length)
                        orientation = ReadU16(p);
                }
                if (orientation >= 1 && orientation <= 8)
                    ExifOrientation = orientation;
                return; // 找到后返回
            }
        }
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
            fs.ReadExactly(symbols, 0, totalSymbols);
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
    public byte Ss { get; set; }
    public byte Se { get; set; }
    public byte Ah { get; set; }
    public byte Al { get; set; }

    public JpegScan(int nbChannels, (byte, byte, byte)[] comps, long dataOffset)
    {
        NbChannels = nbChannels;
        Components = comps;
        DataOffset = dataOffset;
    }
}
