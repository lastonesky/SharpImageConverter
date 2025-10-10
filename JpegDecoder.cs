using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

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

    // 逆映射：ZigZag 索引 -> 自然序索引
    private static readonly int[] UnZigZag = BuildInverse(ZigZag);

    private static int[] BuildInverse(int[] map)
    {
        var inv = new int[64];
        for (int i = 0; i < 64; i++) inv[map[i]] = i;
        return inv;
    }

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

        int width = _parser.Width;
        int height = _parser.Height;
        // 子采样分量的子平面（按各自采样分辨率），稍后上采样到全分辨率
        var compIndexById = new Dictionary<byte, int>();
        for (int i = 0; i < _parser.FrameComponents.Count; i++)
            compIndexById[_parser.FrameComponents[i].id] = i;

        // MCU 尺寸取决于最大采样因子
        int mcuWidth = 8 * _parser.MaxH;
        int mcuHeight = 8 * _parser.MaxV;
        int mcusX = (width + mcuWidth - 1) / mcuWidth;
        int mcusY = (height + mcuHeight - 1) / mcuHeight;

        // 为每个分量分配子平面
        var subPlanes = new Dictionary<byte, (int w, int h, int[] data)>();
        foreach (var f in _parser.FrameComponents)
        {
            int wComp = mcusX * f.h * 8;
            int hComp = mcusY * f.v * 8;
            subPlanes[f.id] = (wComp, hComp, new int[wComp * hComp]);
        }

        // 上方已建立 compIndexById

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
            for (int j = 0; j < 64; j++) dq[j] = qt.Values[ZigZag[j]];
            dequants[f.id] = dq;
        }

        // MCU 尺寸与数量已在上方计算

        int[] prevDC = new int[256];
        Array.Clear(prevDC, 0, prevDC.Length);
        int mcusProcessed = 0;

        // 性能计时
        var swEntropy = Stopwatch.StartNew();
        long idctTicks = 0;

        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                foreach (var sc in scan.Components)
                {
                    byte cid = sc.channelId;
                    var f = _parser.FrameComponents[compIndexById[cid]];
                    var (wComp, hComp, plane) = subPlanes[cid];

                    // 每个分量在一个 MCU 中有 h×v 个 8×8 块
                    int baseXSub = mx * (8 * f.h);
                    int baseYSub = my * (8 * f.v);

                    // 预取该分量的表与反量化，减少字典查找
                    var dcTable = _parser.HuffmanTables[(0, sc.dcTableId)];
                    var acTable = _parser.HuffmanTables[(1, sc.acTableId)];
                    var dcCanonRef = dcCanon[sc.dcTableId];
                    var acCanonRef = acCanon[sc.acTableId];
                    var dq = dequants[cid];

                    // 复用块与像素缓冲，避免频繁分配
                    short[] block = new short[64];
                    int[] pix = new int[64];

                    for (int vy = 0; vy < f.v; vy++)
                    {
                        for (int hx = 0; hx < f.h; hx++)
                        {
                            Array.Clear(block, 0, 64);

                            // DC
                            int ssss = DecodeSymbol(br, dcCanonRef, dcTable);
                            int dcDiff = (ssss == 0) ? 0 : ExtendSign(br.GetBits(ssss), ssss);
                            int dc = prevDC[cid] + dcDiff;
                            prevDC[cid] = dc;
                            block[0] = (short)dc;

                            // AC
                            int k = 1;
                            while (k < 64)
                            {
                                int rs = DecodeSymbol(br, acCanonRef, acTable);
                                int r = rs >> 4;
                                int s = rs & 0x0F;
                                if (s == 0)
                                {
                                    if (r == 0) // EOB
                                        break;
                                    if (r == 15) // ZRL: 跳过 16 个零系数
                                    {
                                        k += 16;
                                        continue;
                                    }
                                    k += r;
                                    continue;
                                }
                                k += r;
                                if (k >= 64) break; // 防越界
                                int val = ExtendSign(br.GetBits(s), s);
                                block[UnZigZag[k]] = (short)val;
                                k++;
                            }

                            // 反量化（自然顺序）
                            for (int i = 0; i < 64; i++)
                                block[i] = (short)(block[i] * dq[i]);

                            // IDCT
                            bool dcOnly = true;
                            for (int i = 1; i < 64; i++)
                            {
                                if (block[i] != 0) { dcOnly = false; break; }
                            }

                            if (dcOnly)
                            {
                                int val = (block[0] / 8) + 128;
                                if (val < 0) val = 0;
                                if (val > 255) val = 255;
                                for (int i = 0; i < 64; i++) pix[i] = val;
                            }
                            else
                            {
                                var swId = Stopwatch.StartNew();
                                Idct.IDCT8x8Fast(block, 0, pix, 0);
                                swId.Stop();
                                idctTicks += swId.ElapsedTicks;
                            }

                            // 放置到分量子平面
                            int blockBaseX = baseXSub + hx * 8;
                            int blockBaseY = baseYSub + vy * 8;
                            for (int yy = 0; yy < 8; yy++)
                            {
                                int py = blockBaseY + yy;
                                if (py >= hComp) break;
                                for (int xx = 0; xx < 8; xx++)
                                {
                                    int px = blockBaseX + xx;
                                    if (px >= wComp) break;
                                    int dst = py * wComp + px;
                                    plane[dst] = pix[yy * 8 + xx];
                                }
                            }
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

        swEntropy.Stop();

        // 最近邻上采样到全分辨率
        var swUpsample = Stopwatch.StartNew();
        var Y_full = new int[width * height];
        var Cb_full = new int[width * height];
        var Cr_full = new int[width * height];

        foreach (var f in _parser.FrameComponents)
        {
            var (wComp, hComp, plane) = subPlanes[f.id];
            int sx = _parser.MaxH / Math.Max(1, (int)f.h);
            int sy = _parser.MaxV / Math.Max(1, (int)f.v);

            for (int ySub = 0; ySub < hComp; ySub++)
            {
                int yFullBase = ySub * sy;
                for (int xSub = 0; xSub < wComp; xSub++)
                {
                    int xFullBase = xSub * sx;
                    int val = plane[ySub * wComp + xSub];
                    for (int dy = 0; dy < sy; dy++)
                    {
                        int py = yFullBase + dy;
                        if (py >= height) break;
                        for (int dx = 0; dx < sx; dx++)
                        {
                            int px = xFullBase + dx;
                            if (px >= width) break;
                            int dst = py * width + px;
                            if (f.id == 1) Y_full[dst] = val;
                            else if (f.id == 2) Cb_full[dst] = val;
                            else if (f.id == 3) Cr_full[dst] = val;
                        }
                    }
                }
            }
        }
        swUpsample.Stop();

        // YCbCr -> RGB (BT.601) — 使用整数近似提升性能
        var swColor = Stopwatch.StartNew();
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int y = Y_full[i];
            int cb = Cb_full[i] - 128;
            int cr = Cr_full[i] - 128;
            // 近似系数 *256：R = y + 1.402*cr → 359, G = y - 0.344136*cb - 0.714136*cr → -88, -183, B = y + 1.772*cb → 454
            int R = y + ((359 * cr) >> 8);
            int G = y - ((88 * cb + 183 * cr) >> 8);
            int B = y + ((454 * cb) >> 8);
            if (R < 0) R = 0; if (R > 255) R = 255;
            if (G < 0) G = 0; if (G > 255) G = 255;
            if (B < 0) B = 0; if (B > 255) B = 255;
            rgb[i * 3 + 0] = (byte)B;
            rgb[i * 3 + 1] = (byte)G;
            rgb[i * 3 + 2] = (byte)R;
        }
        swColor.Stop();

        // 打印分阶段耗时（ms）
        double toMs(long ticks) => (ticks * 1000.0) / Stopwatch.Frequency;
        Console.WriteLine($"⏱️ 熵解码+反量化耗时: {swEntropy.ElapsedMilliseconds} ms");
        Console.WriteLine($"⏱️ IDCT耗时: {toMs(idctTicks):F1} ms");
        Console.WriteLine($"⏱️ 上采样耗时: {swUpsample.ElapsedMilliseconds} ms");
        Console.WriteLine($"⏱️ 颜色转换耗时: {swColor.ElapsedMilliseconds} ms");

        return rgb;
    }
}