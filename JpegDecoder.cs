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

    private static CanonicalHuff BuildCanonical(JpegHuffmanTable ht)
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

    private static FastHuff BuildFast(JpegHuffmanTable ht, CanonicalHuff ch)
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

    private static int ExtendSign(int v, int t)
    {
        int vt = 1 << (t - 1);
        if (v < vt)
            v -= (1 << t) - 1;
        return v;
    }

    private static int DecodeSymbolSlow(BitReader br, CanonicalHuff ch, JpegHuffmanTable ht)
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

    private static int DecodeSymbol(BitReader br, CanonicalHuff ch, FastHuff fh, JpegHuffmanTable ht)
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
        // 慢速回退：若位流已结束但仍有残留位导致未匹配，优雅视为扫描结束
        try
        {
            return DecodeSymbolSlow(br, ch, ht);
        }
        catch (EndOfStreamException)
        {
            throw;
        }
        catch (Exception)
        {
            if (br.IsEOF) throw new EndOfStreamException("扫描结束");
            throw;
        }
    }

    /// <summary>
    /// 解码入口：根据帧类型路由到基线或渐进式解码
    /// </summary>
    public byte[] DecodeToRGB(string inputPath)
    {
        if (_parser.IsProgressive)
            return DecodeProgressiveToRGB(inputPath);
        else
            return DecodeBaselineToRGB(inputPath);
    }

    /// <summary>
    /// 基线JPEG解码为RGB（单扫描，霍夫曼+反量化+IDCT）
    /// </summary>
    public byte[] DecodeBaselineToRGB(string inputPath)
    {
        T.Assert(_parser.Scans.Count > 0, "未找到扫描数据");
        T.Assert(_parser.FrameComponents.Count > 0, "未找到SOF0组件");

        int width = _parser.Width;
        int height = _parser.Height;
        var compIndexById = new Dictionary<byte, int>();
        for (int i = 0; i < _parser.FrameComponents.Count; i++)
            compIndexById[_parser.FrameComponents[i].id] = i;

        int mcuWidth = 8 * _parser.MaxH;
        int mcuHeight = 8 * _parser.MaxV;
        int mcusX = (width + mcuWidth - 1) / mcuWidth;
        int mcusY = (height + mcuHeight - 1) / mcuHeight;

        var subPlanes = new Dictionary<byte, (int w, int h, int[] data)>();
        foreach (var (id, h, v, _) in _parser.FrameComponents)
        {
            int wComp = mcusX * h * 8;
            int hComp = mcusY * v * 8;
            subPlanes[id] = (wComp, hComp, new int[wComp * hComp]);
        }

        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
        var scan = _parser.Scans[0];
        fs.Position = scan.DataOffset;
        var br = new BitReader(fs);

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

        var dequants = new Dictionary<byte, ushort[]>();
        foreach (var (id, h, v, quantId) in _parser.FrameComponents)
        {
            ushort[] dq = new ushort[64];
            var qt = _parser.QuantTables[quantId];
            for (int j = 0; j < 64; j++) dq[j] = qt.Values[ZigZag[j]];
            dequants[id] = dq;
        }

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
                    var (wComp, hComp, plane) = subPlanes[cid];

                    int baseXSub = mx * (8 * f.h);
                    int baseYSub = my * (8 * f.v);

                    var dcTable = _parser.HuffmanTables[(0, sc.dcTableId)];
                    var acTable = _parser.HuffmanTables[(1, sc.acTableId)];
                    var dcCanonRef = dcCanon[sc.dcTableId];
                    var acCanonRef = acCanon[sc.acTableId];
                    var dq = dequants[cid];

                    var block = System.Buffers.ArrayPool<short>.Shared.Rent(64);
                    var pix = System.Buffers.ArrayPool<int>.Shared.Rent(64);

                    for (int vy = 0; vy < f.v; vy++)
                    {
                        for (int hx = 0; hx < f.h; hx++)
                        {
                            Array.Clear(block, 0, 64);

                            int ssss = DecodeSymbol(br, dcCanonRef, dcFast[sc.dcTableId], dcTable);
                            int dcDiff = (ssss == 0) ? 0 : ExtendSign(br.GetBits(ssss), ssss);
                            int dc = prevDC[cid] + dcDiff;
                            prevDC[cid] = dc;
                            block[0] = (short)dc;

                            int k = 1;
                            while (k < 64)
                            {
                                int rs = DecodeSymbol(br, acCanonRef, acFast[sc.acTableId], acTable);
                                int r = rs >> 4;
                                int s = rs & 0x0F;
                                if (s == 0)
                                {
                                    if (r == 0) break;
                                    if (r == 15) { k += 16; continue; }
                                    k += r; continue;
                                }
                                k += r;
                                if (k >= 64) break;
                                int val = ExtendSign(br.GetBits(s), s);
                                block[UnZigZag[k]] = (short)val;
                                k++;
                            }

                            for (int i = 0; i < 64; i++)
                                block[i] = (short)(block[i] * dq[i]);

                            bool dcOnly = true;
                            for (int i = 1; i < 64; i++) { if (block[i] != 0) { dcOnly = false; break; } }

                            if (dcOnly)
                            {
                                int val = (block[0] / 8) + 128;
                                if (val < 0) val = 0; if (val > 255) val = 255;
                                for (int i = 0; i < 64; i++) pix[i] = val;
                            }
                            else
                            {
                                Idct.IDCT8x8Fast(block, 0, pix, 0);
                            }

                            int blockBaseX = baseXSub + hx * 8;
                            int blockBaseY = baseYSub + vy * 8;
                            for (int yy = 0; yy < 8; yy++)
                            {
                                int py = blockBaseY + yy; if (py >= hComp) break;
                                for (int xx = 0; xx < 8; xx++)
                                {
                                    int px = blockBaseX + xx; if (px >= wComp) break;
                                    plane[py * wComp + px] = pix[yy * 8 + xx];
                                }
                            }
                        }
                    }
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

        return ComposeRGBFromPlanes(width, height, subPlanes);
    }

    /// <summary>
    /// 渐进式JPEG解码为RGB（完整支持DC/AC初始扫描及细化扫描）
    /// </summary>
    public byte[] DecodeProgressiveToRGB(string inputPath)
    {
        T.Assert(_parser.Scans.Count > 0, "未找到扫描数据");
        T.Assert(_parser.FrameComponents.Count > 0, "未找到SOF2组件");

        int width = _parser.Width;
        int height = _parser.Height;

        int mcuWidth = 8 * _parser.MaxH;
        int mcuHeight = 8 * _parser.MaxV;
        int mcusX = (width + mcuWidth - 1) / mcuWidth;
        int mcusY = (height + mcuHeight - 1) / mcuHeight;

        // 分量子平面与系数缓冲
        var subPlanes = new Dictionary<byte, (int w, int h, int[] data)>();
        var coeffs = new Dictionary<byte, (int wBlocks, int hBlocks, short[] data)>();
        var dequants = new Dictionary<byte, ushort[]>();
        var compIndexById = new Dictionary<byte, int>();
        for (int i = 0; i < _parser.FrameComponents.Count; i++) compIndexById[_parser.FrameComponents[i].id] = i;
        foreach (var (id, h, v, quantId) in _parser.FrameComponents)
        {
            int wComp = mcusX * h * 8;
            int hComp = mcusY * v * 8;
            subPlanes[id] = (wComp, hComp, new int[wComp * hComp]);
            int wBlocks = wComp / 8;
            int hBlocks = hComp / 8;
            coeffs[id] = (wBlocks, hBlocks, new short[wBlocks * hBlocks * 64]);
            ushort[] dq = new ushort[64];
            var qt = _parser.QuantTables[quantId];
            for (int j = 0; j < 64; j++) dq[j] = qt.Values[ZigZag[j]];
            dequants[id] = dq;
        }

        int[] prevDC = new int[256];
        Array.Clear(prevDC, 0, prevDC.Length);
        int mcusProcessed = 0;

        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);

        foreach (var scan in _parser.Scans)
        {
            fs.Position = scan.DataOffset;
            var br = new BitReader(fs);

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

            if (scan.Ss == 0) // DC 扫描
            {
                bool endScan = false;
                // 非交错 DC 扫描 (Single Component)
                if (scan.NbChannels == 1)
                {
                    var sc = scan.Components[0];
                    byte cid = sc.channelId;
                    var (wBlocks, hBlocks, cbuf) = coeffs[cid];
                    
                    for (int by = 0; by < hBlocks; by++)
                    {
                        for (int bx = 0; bx < wBlocks; bx++)
                        {
                            int bIndex = (by * wBlocks + bx) * 64;
                            if (br.IsEOF) { endScan = true; break; }
                            try
                            {
                                int ssss = DecodeSymbol(br, dcCanon[sc.dcTableId], dcFast[sc.dcTableId], _parser.HuffmanTables[(0, sc.dcTableId)]);
                                if (scan.Ah == 0)
                                {
                                    int dcDiff = (ssss == 0) ? 0 : ExtendSign(br.GetBits(ssss), ssss);
                                    int dc = prevDC[cid] + dcDiff;
                                    prevDC[cid] = dc;
                                    cbuf[bIndex + 0] = (short)(dc << scan.Al);
                                }
                                else
                                {
                                    int bit = br.GetBit();
                                    if (bit != 0)
                                        cbuf[bIndex + 0] += (short)(1 << scan.Al);
                                }
                            }
                            catch (EndOfStreamException) { endScan = true; break; }
                            catch (Exception) { endScan = true; break; }

                            mcusProcessed++;
                            if (_parser.RestartInterval > 0 && (mcusProcessed % _parser.RestartInterval) == 0)
                            {
                                Array.Clear(prevDC, 0, prevDC.Length);
                                br.ResetBits();
                            }
                        }
                        if (endScan) break;
                    }
                }
                // 交错 DC 扫描 (Multi Component)
                else
                {
                    int mcuPerScanX = mcusX;
                    int mcuPerScanY = mcusY;
                    for (int my = 0; my < mcuPerScanY; my++)
                    {
                        for (int mx = 0; mx < mcuPerScanX; mx++)
                        {
                            foreach (var sc in scan.Components)
                            {
                                byte cid = sc.channelId;
                                var f = _parser.FrameComponents[compIndexById[cid]];
                                var (wBlocks, hBlocks, cbuf) = coeffs[cid];

                                int baseXB = mx * f.h;
                                int baseYB = my * f.v;

                                for (int vy = 0; vy < f.v; vy++)
                                {
                                    for (int hx = 0; hx < f.h; hx++)
                                    {
                                        int bx = baseXB + hx;
                                        int by = baseYB + vy;
                                        if (bx >= wBlocks || by >= hBlocks) continue;
                                        int bIndex = (by * wBlocks + bx) * 64;
                                        if (br.IsEOF) { endScan = true; break; }
                                        try
                                        {
                                            int ssss = DecodeSymbol(br, dcCanon[sc.dcTableId], dcFast[sc.dcTableId], _parser.HuffmanTables[(0, sc.dcTableId)]);
                                            if (scan.Ah == 0)
                                            {
                                                int dcDiff = (ssss == 0) ? 0 : ExtendSign(br.GetBits(ssss), ssss);
                                                int dc = prevDC[cid] + dcDiff;
                                                prevDC[cid] = dc;
                                                cbuf[bIndex + 0] = (short)(dc << scan.Al);
                                            }
                                            else
                                            {
                                                int bit = br.GetBit();
                                                if (bit != 0)
                                                    cbuf[bIndex + 0] += (short)(1 << scan.Al);
                                            }
                                        }
                                        catch (EndOfStreamException)
                                        {
                                            endScan = true;
                                            break;
                                        }
                                        catch (Exception)
                                        {
                                            endScan = true;
                                            break;
                                        }
                                    }
                                    if (endScan) break;
                                }
                                if (endScan) break;
                            }

                            mcusProcessed++;
                            if (_parser.RestartInterval > 0 && (mcusProcessed % _parser.RestartInterval) == 0)
                            {
                                Array.Clear(prevDC, 0, prevDC.Length);
                                br.ResetBits();
                            }
                            if (endScan) break;
                        }
                        if (endScan) break;
                    }
                }
            }
            else // AC 扫描（通常单分量）
            {
                T.Assert(scan.NbChannels == 1, "当前实现仅支持单分量AC扫描");
                var sc = scan.Components[0];
                byte cid = sc.channelId;
                var (wBlocks, hBlocks, cbuf) = coeffs[cid];
                int Ss = scan.Ss;
                int Se = scan.Se;
                bool endScan = false;

                int eob_run = 0;
                for (int by = 0; by < hBlocks; by++)
                {
                    for (int bx = 0; bx < wBlocks; bx++)
                    {
                        int bIndex = (by * wBlocks + bx) * 64;
                        if (scan.Ah == 0)
                        {
                            int k = Ss;
                            if (eob_run > 0)
                            {
                                eob_run--;
                                k = Se + 1;
                            }

                            while (k <= Se)
                            {
                                if (br.IsEOF) { endScan = true; break; }
                                try
                                {
                                    int rs = DecodeSymbol(br, acCanon[sc.acTableId], acFast[sc.acTableId], _parser.HuffmanTables[(1, sc.acTableId)]);
                                    int r = rs >> 4;
                                    int s = rs & 0x0F;
                                    if (s == 0)
                                    {
                                        if (r == 15) { k += 16; continue; } // ZRL
                                        else 
                                        {
                                            eob_run = 1 << r;
                                            if (r > 0) eob_run += br.GetBits(r);
                                            eob_run--;
                                            break; // EOB
                                        }
                                    }
                                    k += r;
                                    if (k > Se) break;
                                    int val = ExtendSign(br.GetBits(s), s);
                                    cbuf[bIndex + UnZigZag[k]] = (short)(val << scan.Al);
                                    k++;
                                }
                                catch (EndOfStreamException)
                                {
                                    endScan = true;
                                    break;
                                }
                                catch (Exception)
                                {
                                    endScan = true;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            int k = Ss;
                            bool forceZero = eob_run > 0;
                            if (forceZero) eob_run--;
                            int zerosToSkip = 0;
                            bool hasPendingVal = false;
                            short pendingNewVal = 0;

                            while (k <= Se)
                            {
                                if (br.IsEOF) { endScan = true; break; }
                                int idx = UnZigZag[k];
                                
                                if (cbuf[bIndex + idx] != 0)
                                {
                                    if (!forceZero)
                                    {
                                        try
                                        {
                                            int bit = br.GetBit();
                                            if (bit != 0)
                                            {
                                                if (cbuf[bIndex + idx] > 0) cbuf[bIndex + idx] += (short)(1 << scan.Al);
                                                else cbuf[bIndex + idx] -= (short)(1 << scan.Al);
                                            }
                                        }
                                        catch (EndOfStreamException) { endScan = true; break; }
                                    }
                                }
                                else
                                {
                                    if (zerosToSkip > 0)
                                    {
                                        zerosToSkip--;
                                    }
                                    else if (hasPendingVal)
                                    {
                                        cbuf[bIndex + idx] = pendingNewVal;
                                        hasPendingVal = false;
                                        pendingNewVal = 0;
                                    }
                                    else
                                    {
                                        if (forceZero) { k++; continue; }
                                        try
                                        {
                                            int rs = DecodeSymbol(br, acCanon[sc.acTableId], acFast[sc.acTableId], _parser.HuffmanTables[(1, sc.acTableId)]);
                                            int r = rs >> 4;
                                            int s = rs & 0x0F;
                                            if (s == 0)
                                            {
                                                if (r == 15) zerosToSkip = 15; // ZRL
                                                else
                                                {
                                                    eob_run = 1 << r;
                                                    if (r > 0) eob_run += br.GetBits(r);
                                                    eob_run--;
                                                    forceZero = true;
                                                }
                                            }
                                            else
                                            {
                                                zerosToSkip = r;
                                                int sign = br.GetBit();
                                                pendingNewVal = (short)((1 << scan.Al) * (sign != 0 ? 1 : -1));
                                                hasPendingVal = true;
                                                
                                                if (zerosToSkip == 0)
                                                {
                                                    cbuf[bIndex + idx] = pendingNewVal;
                                                    hasPendingVal = false;
                                                    pendingNewVal = 0;
                                                }
                                                else
                                                {
                                                    zerosToSkip--;
                                                }
                                            }
                                        }
                                        catch (EndOfStreamException) { endScan = true; break; }
                                        catch (Exception) { endScan = true; break; }
                                    }
                                }
                                k++;
                            }
                        }
                        mcusProcessed++;
                        if (_parser.RestartInterval > 0 && (mcusProcessed % _parser.RestartInterval) == 0)
                        {
                            Array.Clear(prevDC, 0, prevDC.Length);
                            br.ResetBits();
                        }
                        if (endScan) break;
                    }
                    if (endScan) break;
                }
            }
        }

        // 完整系数 -> 反量化 -> IDCT -> 填充子平面
        foreach (var (id, h, v, _) in _parser.FrameComponents)
        {
            var (wComp, hComp, plane) = subPlanes[id];
            var (wBlocks, hBlocks, cbuf) = coeffs[id];
            var dq = dequants[id];
            var pix = System.Buffers.ArrayPool<int>.Shared.Rent(64);
            var blk = System.Buffers.ArrayPool<short>.Shared.Rent(64);
            for (int by = 0; by < hBlocks; by++)
            {
                for (int bx = 0; bx < wBlocks; bx++)
                {
                    int bIndex = (by * wBlocks + bx) * 64;
                    for (int i = 0; i < 64; i++) blk[i] = (short)(cbuf[bIndex + i] * dq[i]);
                    Idct.IDCT8x8Fast(blk, 0, pix, 0);
                    int baseX = bx * 8;
                    int baseY = by * 8;
                    for (int yy = 0; yy < 8; yy++)
                    {
                        int py = baseY + yy; if (py >= hComp) break;
                        for (int xx = 0; xx < 8; xx++)
                        {
                            int px = baseX + xx; if (px >= wComp) break;
                            plane[py * wComp + px] = pix[yy * 8 + xx];
                        }
                    }
                }
            }
            System.Buffers.ArrayPool<int>.Shared.Return(pix);
            System.Buffers.ArrayPool<short>.Shared.Return(blk);
        }

        return ComposeRGBFromPlanes(width, height, subPlanes);
    }

    /// <summary>
    /// 将分量子平面组合为最终RGB像素
    /// </summary>
    private byte[] ComposeRGBFromPlanes(int width, int height, Dictionary<byte, (int w, int h, int[] data)> subPlanes)
    {
        subPlanes.TryGetValue(1, out var yPlane);
        subPlanes.TryGetValue(2, out var cbPlane);
        subPlanes.TryGetValue(3, out var crPlane);

        int yH = 1, yV = 1;
        int cbH = 1, cbV = 1;
        int crH = 1, crV = 1;

        foreach (var f in _parser.FrameComponents)
        {
            if (f.id == 1) { yH = f.h; yV = f.v; }
            else if (f.id == 2) { cbH = f.h; cbV = f.v; }
            else if (f.id == 3) { crH = f.h; crV = f.v; }
        }

        int maxH = Math.Max(1, _parser.MaxH);
        int maxV = Math.Max(1, _parser.MaxV);

        int sxY = maxH / Math.Max(1, yH);
        int syY = maxV / Math.Max(1, yV);
        int sxCb = maxH / Math.Max(1, cbH);
        int syCb = maxV / Math.Max(1, cbV);
        int sxCr = maxH / Math.Max(1, crH);
        int syCr = maxV / Math.Max(1, crV);

        byte[] rgb = new byte[width * height * 3];
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            int yY = y / syY;
            int yCb = y / syCb;
            int yCr = y / syCr;

            for (int x = 0; x < width; x++)
            {
                int xY = x / sxY;
                int xCb = x / sxCb;
                int xCr = x / sxCr;

                int Y = (yPlane.data != null) ? yPlane.data[yY * yPlane.w + xY] : 0;
                int Cb = (cbPlane.data != null) ? cbPlane.data[yCb * cbPlane.w + xCb] : 128;
                int Cr = (crPlane.data != null) ? crPlane.data[yCr * crPlane.w + xCr] : 128;

                int R = Y + CrToR[Cr];
                int G = Y - (CbToG[Cb] + CrToG[Cr]);
                int B = Y + CbToB[Cb];
                if (R < 0) R = 0; if (R > 255) R = 255;
                if (G < 0) G = 0; if (G > 255) G = 255;
                if (B < 0) B = 0; if (B > 255) B = 255;

                rgb[dst + 0] = (byte)R;
                rgb[dst + 1] = (byte)G;
                rgb[dst + 2] = (byte)B;
                dst += 3;
            }
        }
        return rgb;
    }
}
