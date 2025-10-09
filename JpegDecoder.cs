using System;
using System.Collections.Generic;
using System.IO;

public class JpegDecoder
{
    private readonly JpegParser _parser;

    private static readonly int[] ZigZag = new int[]
    {
        0, 1, 5, 6,14,15,27,28,
        2, 4, 7,13,16,26,29,42,
        3, 8,12,17,25,30,41,43,
        9,11,18,24,31,40,44,53,
       10,19,23,32,39,45,52,54,
       20,22,33,38,46,51,55,60,
       21,34,37,47,50,56,59,61,
       35,36,48,49,57,58,62,63
    };

    public JpegDecoder(JpegParser parser)
    {
        _parser = parser;
    }

    private struct CanonicalHuff
    {
        public int[] FirstCode;  // 1..16
        public int[] CodeCount;  // 1..16
        public int[] SymbolOffset; // 1..16
    }

    private CanonicalHuff BuildCanonical(JpegHuffmanTable ht)
    {
        var first = new int[17];
        var count = new int[17];
        var off = new int[17];
        int sum = 0;
        for (int L = 1; L <= 16; L++)
        {
            count[L] = ht.CodeLengths[L - 1];
        }
        int code = 0;
        for (int L = 1; L <= 16; L++)
        {
            first[L] = code;
            off[L] = sum;
            code = (code + count[L]) << 1;
            sum += count[L];
        }
        return new CanonicalHuff { FirstCode = first, CodeCount = count, SymbolOffset = off };
    }

    private int ExtendSign(int v, int t)
    {
        int vt = 1 << (t - 1);
        if (v < vt)
            v -= (1 << t) - 1;
        return v;
    }

    private int DecodeSymbol(BitReader br, CanonicalHuff ch, JpegHuffmanTable ht)
    {
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            code = (code << 1) | br.GetBit();
            int fc = ch.FirstCode[len];
            int cnt = ch.CodeCount[len];
            if (cnt == 0) continue;
            int rel = code - fc;
            if (rel >= 0 && rel < cnt)
            {
                int idx = ch.SymbolOffset[len] + rel;
                return ht.Symbols[idx];
            }
        }
        throw new Exception("霍夫曼码未匹配");
    }

    public byte[] DecodeToRGB(string inputPath)
    {
        T.Assert(_parser.Scans.Count > 0, "未找到扫描数据");
        T.Assert(_parser.FrameComponents.Count > 0, "未找到SOF0组件");
        if (_parser.MaxH != 1 || _parser.MaxV != 1)
        {
            throw new NotSupportedException("当前实现仅支持4:4:4采样。如需支持4:2:0/4:2:2，请确认后我再继续实现上采样和MCU映射。");
        }

        int width = _parser.Width;
        int height = _parser.Height;
        var Y = new int[width * height];
        var Cb = new int[width * height];
        var Cr = new int[width * height];

        var compIndexById = new Dictionary<byte, int>();
        for (int i = 0; i < _parser.FrameComponents.Count; i++)
            compIndexById[_parser.FrameComponents[i].id] = i;

        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
        var scan = _parser.Scans[0]; // 基线假设：单扫描
        fs.Position = scan.DataOffset;
        var br = new BitReader(fs);

        // 构建每个使用表的canonical
        var dcCanon = new Dictionary<byte, CanonicalHuff>();
        var acCanon = new Dictionary<byte, CanonicalHuff>();
        foreach (var c in scan.Components)
        {
            var dc = _parser.HuffmanTables[(0, c.dcTableId)];
            var ac = _parser.HuffmanTables[(1, c.acTableId)];
            dcCanon[c.dcTableId] = BuildCanonical(dc);
            acCanon[c.acTableId] = BuildCanonical(ac);
        }

        // 每个分量的反量化表（按自然顺序）
        var dequants = new Dictionary<byte, ushort[]>();
        foreach (var f in _parser.FrameComponents)
        {
            ushort[] dq = new ushort[64];
            var qt = _parser.QuantTables[f.quantId];
            // qt.Values按读取顺序（JPEG标准为ZigZag），映射到自然顺序
            for (int j = 0; j < 64; j++) dq[ZigZag[j]] = qt.Values[j];
            dequants[f.id] = dq;
        }

        int mcuWidth = 8;
        int mcuHeight = 8;
        int mcusX = (width + mcuWidth - 1) / mcuWidth;
        int mcusY = (height + mcuHeight - 1) / mcuHeight;

        int[] prevDC = new int[256];
        Array.Clear(prevDC, 0, prevDC.Length);
        int mcusProcessed = 0;

        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                foreach (var sc in scan.Components)
                {
                    byte cid = sc.channelId;
                    var f = _parser.FrameComponents[compIndexById[cid]];

                    // 4:4:4: 每个分量一个8x8
                    short[] block = new short[64];

                    // DC
                    var dcTable = _parser.HuffmanTables[(0, sc.dcTableId)];
                    int ssss = DecodeSymbol(br, dcCanon[sc.dcTableId], dcTable);
                    int dcDiff = (ssss == 0) ? 0 : ExtendSign(br.GetBits(ssss), ssss);
                    int dc = prevDC[cid] + dcDiff;
                    prevDC[cid] = dc;
                    block[0] = (short)dc;

                    // AC
                    var acTable = _parser.HuffmanTables[(1, sc.acTableId)];
                    int k = 1;
                    while (k < 64)
                    {
                        int rs = DecodeSymbol(br, acCanon[sc.acTableId], acTable);
                        int r = rs >> 4;
                        int s = rs & 0x0F;
                        if (s == 0)
                        {
                            if (r == 0) // EOB
                                break;
                            k += r;
                            continue;
                        }
                        k += r;
                        int val = ExtendSign(br.GetBits(s), s);
                        block[ZigZag[k]] = (short)val;
                        k++;
                    }

                    // 反量化（自然顺序）
                    var dq = dequants[cid];
                    for (int i = 0; i < 64; i++)
                        block[i] = (short)(block[i] * dq[i]);

                    // IDCT
                    int[] pix = new int[64];
                    Idct.IDCT8x8(block, 0, pix, 0);

                    // 放置到图像
                    int baseX = mx * 8;
                    int baseY = my * 8;
                    for (int yy = 0; yy < 8; yy++)
                    {
                        int py = baseY + yy;
                        if (py >= height) break;
                        for (int xx = 0; xx < 8; xx++)
                        {
                            int px = baseX + xx;
                            if (px >= width) break;
                            int dst = py * width + px;
                            int v = pix[yy * 8 + xx];
                            if (cid == 1) Y[dst] = v;
                            else if (cid == 2) Cb[dst] = v;
                            else if (cid == 3) Cr[dst] = v;
                        }
                    }
                }

                mcusProcessed++;
                if (_parser.RestartInterval > 0 && (mcusProcessed % _parser.RestartInterval) == 0)
                {
                    Array.Clear(prevDC, 0, prevDC.Length);
                    br.ResetBits();
                }
            }
        }

        // YCbCr -> RGB (BT.601)
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int y = Y[i];
            int cb = Cb[i] - 128;
            int cr = Cr[i] - 128;
            int R = (int)Math.Round(y + 1.402 * cr);
            int G = (int)Math.Round(y - 0.344136 * cb - 0.714136 * cr);
            int B = (int)Math.Round(y + 1.772 * cb);
            if (R < 0) R = 0; if (R > 255) R = 255;
            if (G < 0) G = 0; if (G > 255) G = 255;
            if (B < 0) B = 0; if (B > 255) B = 255;
            rgb[i * 3 + 0] = (byte)B;
            rgb[i * 3 + 1] = (byte)G;
            rgb[i * 3 + 2] = (byte)R;
        }

        return rgb;
    }
}