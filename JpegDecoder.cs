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

    // 颜色转换查表，减少每像素乘法
    private static readonly int[] CrToR = BuildCrToR();
    private static readonly int[] CbToB = BuildCbToB();
    private static readonly int[] CrToG = BuildCrToG();
    private static readonly int[] CbToG = BuildCbToG();

    private static int[] BuildCrToR()
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++) t[i] = (359 * (i - 128)) >> 8; // 1.402*256≈359
        return t;
    }
    private static int[] BuildCbToB()
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++) t[i] = (454 * (i - 128)) >> 8; // 1.772*256≈454
        return t;
    }
    private static int[] BuildCrToG()
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++) t[i] = (183 * (i - 128)) >> 8; // 0.714136*256≈183
        return t;
    }
    private static int[] BuildCbToG()
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++) t[i] = (88 * (i - 128)) >> 8; // 0.344136*256≈88
        return t;
    }

    // 快速霍夫曼：派生表，包含最大 12 位前缀查找（常用）
    private class FastHuff
    {
        public int[] Lookup = new int[1 << 12]; // 值: (len<<8) | symbol, 若为 -1 则需慢速路径
        public int MaxFastBits = 12;
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

    private FastHuff BuildFast(JpegHuffmanTable ht, CanonicalHuff ch)
    {
        var fh = new FastHuff();
        for (int i = 0; i < fh.Lookup.Length; i++) fh.Lookup[i] = -1;
        for (int len = 1; len <= Math.Min(12, 16); len++)
        {
            int fc = ch.FirstCode[len];
            int cnt = ch.CodeCount[len];
            if (cnt == 0) continue;
            for (int r = 0; r < cnt; r++)
            {
                int code = fc + r;
                int idx = ch.SymbolOffset[len] + r;
                int sym = ht.Symbols[idx];
                // 扩展到 12 位前缀空间
                int fill = fh.MaxFastBits - len;
                int baseCode = code << fill;
                int end = baseCode | ((1 << fill) - 1);
                for (int x = baseCode; x <= end; x++)
                {
                    fh.Lookup[x] = (len << 8) | sym;
                }
            }
        }
        return fh;
    }

    private int ExtendSign(int v, int t)
    {
        int vt = 1 << (t - 1);
        if (v < vt)
            v -= (1 << t) - 1;
        return v;
    }

    private int DecodeSymbolSlow(BitReader br, CanonicalHuff ch, JpegHuffmanTable ht)
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

    private int DecodeSymbol(BitReader br, CanonicalHuff ch, FastHuff fh, JpegHuffmanTable ht)
    {
        // 快速路径：预取 12 位
        if (br.EnsureBits(fh.MaxFastBits))
        {
            int peek = br.PeekBits(fh.MaxFastBits);
            int v = fh.Lookup[peek];
            if (v >= 0)
            {
                int len = (v >> 8) & 0xFF;
                int sym = v & 0xFF;
                br.DropBits(len);
                return sym;
            }
        }
        // 慢速回退
        return DecodeSymbolSlow(br, ch, ht);
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
        var dcFast = new Dictionary<byte, FastHuff>();
        var acFast = new Dictionary<byte, FastHuff>();
        foreach (var c in scan.Components)
        {
            var dc = _parser.HuffmanTables[(0, c.dcTableId)];
            var ac = _parser.HuffmanTables[(1, c.acTableId)];
            dcCanon[c.dcTableId] = BuildCanonical(dc);
            acCanon[c.acTableId] = BuildCanonical(ac);
            dcFast[c.dcTableId] = BuildFast(dc, dcCanon[c.dcTableId]);
            acFast[c.acTableId] = BuildFast(ac, acCanon[c.acTableId]);
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
                    // 使用 ArrayPool 复用块缓冲，降低分配与 GC 压力
                    var block = System.Buffers.ArrayPool<short>.Shared.Rent(64);
                    var pix = System.Buffers.ArrayPool<int>.Shared.Rent(64);

                    for (int vy = 0; vy < f.v; vy++)
                    {
                        for (int hx = 0; hx < f.h; hx++)
                        {
                            Array.Clear(block, 0, 64);

                            // DC
                            int ssss = DecodeSymbol(br, dcCanonRef, dcFast[sc.dcTableId], dcTable);
                            int dcDiff = (ssss == 0) ? 0 : ExtendSign(br.GetBits(ssss), ssss);
                            int dc = prevDC[cid] + dcDiff;
                            prevDC[cid] = dc;
                            block[0] = (short)dc;

                            // AC
                            int k = 1;
                            while (k < 64)
                            {
                                int rs = DecodeSymbol(br, acCanonRef, acFast[sc.acTableId], acTable);
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
                    // 归还缓冲（组件级作用域）
                    System.Buffers.ArrayPool<short>.Shared.Return(block);
                    System.Buffers.ArrayPool<int>.Shared.Return(pix);
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

        // 最近邻上采样到全分辨率（4:4:4 专用快速路径 + 通用路径）
        var swUpsample = Stopwatch.StartNew();
        var Y_full = new int[width * height];
        var Cb_full = new int[width * height];
        var Cr_full = new int[width * height];

        bool is444 = _parser.MaxH == 1 && _parser.MaxV == 1;
        if (is444)
        {
            foreach (var f in _parser.FrameComponents)
            {
                if (f.h != 1 || f.v != 1) { is444 = false; break; }
            }
        }

        if (is444)
        {
            // 直接行拷贝 plane 的前 width 元素（顶部 height 行）
            foreach (var f in _parser.FrameComponents)
            {
                var (wComp, hComp, plane) = subPlanes[f.id];
                for (int y = 0; y < height; y++)
                {
                    int srcRowBase = y * wComp;
                    int dstRowBase = y * width;
                    int len = width;
                    // 手动拷贝以避免 int[] 到 int[] 的 BlockCopy 开销非对齐问题
                    for (int x = 0; x < len; x++)
                    {
                        int val = plane[srcRowBase + x];
                        int dst = dstRowBase + x;
                        if (f.id == 1) Y_full[dst] = val;
                        else if (f.id == 2) Cb_full[dst] = val;
                        else if (f.id == 3) Cr_full[dst] = val;
                    }
                }
            }
        }
        else
        {
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
        }
        swUpsample.Stop();

        // YCbCr -> RGB (BT.601) — 使用整数近似提升性能
        var swColor = Stopwatch.StartNew();
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            int y = Y_full[i];
            int cbv = Cb_full[i];
            int crv = Cr_full[i];
            int R = y + CrToR[crv];
            int G = y - (CbToG[cbv] + CrToG[crv]);
            int B = y + CbToB[cbv];
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