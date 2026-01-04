using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace SharpImageConverter;

public class JpegDecoder
{
    private byte[] _data;
    private int _pos;
    private FrameHeader _frame;
    private readonly List<QuantizationTable> _qtables = new List<QuantizationTable>();
    private readonly List<HuffmanTable> _htables = new List<HuffmanTable>();
    private int _restartInterval;
    private int _warningCount;

    public int Width => _frame != null ? _frame.Width : 0;
    public int Height => _frame != null ? _frame.Height : 0;
    public int ExifOrientation { get; private set; } = 1;

    public byte[] DecodeToRGB(Stream stream)
    {
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            _data = ms.ToArray();
        }

        _pos = 0;
        _frame = null;
        _qtables.Clear();
        _htables.Clear();
        _restartInterval = 0;
        _warningCount = 0;
        ExifOrientation = 1;

        if (_data.Length < 2 || _data[_pos++] != 0xFF || _data[_pos++] != JpegMarkers.SOI)
        {
            throw new Exception("Not a valid JPEG file (missing SOI)");
        }

        ParseHeaders();

        while (true)
        {
            if (_pos >= _data.Length - 1) break;

            while (_pos < _data.Length && _data[_pos] != 0xFF)
            {
                _pos++;
            }

            if (_pos >= _data.Length) break;

            if (_pos + 1 >= _data.Length) break;
            byte marker = _data[_pos + 1];

            if (marker == JpegMarkers.EOI)
            {
                break;
            }
            else if (marker == JpegMarkers.SOS)
            {
                _pos += 2;
                ProcessScan();
            }
            else if (JpegMarkers.IsSOF(marker) || marker == JpegMarkers.DHT || marker == JpegMarkers.DQT || marker == JpegMarkers.DRI)
            {
                _pos += 2;
                ParseMarker(marker);
            }
            else if (marker >= JpegMarkers.APP0 && marker <= JpegMarkers.APP15)
            {
                _pos += 2;
                ParseMarker(marker);
            }
            else if (marker == JpegMarkers.COM)
            {
                _pos += 2;
                ParseMarker(marker);
            }
            else if (marker == JpegMarkers.DNL)
            {
                _pos += 2;
                ParseMarker(marker);
            }
            else
            {
                if (_warningCount < 10)
                {
                    Console.WriteLine($"Warning: Unexpected marker {marker:X2} between scans at {_pos}");
                }
                else if (_warningCount == 10)
                {
                    Console.WriteLine("Warning: Too many unexpected markers, suppressing...");
                }
                _warningCount++;
                _pos += 2;
            }
        }

        return PerformIDCTAndOutput();
    }

    private void ParseHeaders()
    {
        while (_pos < _data.Length)
        {
            if (_data[_pos] != 0xFF)
            {
                _pos++;
                continue;
            }

            if (_pos + 1 >= _data.Length) return;
            byte marker = _data[_pos + 1];

            if (marker == JpegMarkers.SOS || marker == JpegMarkers.EOI)
            {
                return;
            }

            _pos += 2;
            ParseMarker(marker);
        }
    }

    private void ParseMarker(byte marker)
    {
        if (marker == 0x00) return;

        if (_pos + 1 >= _data.Length) return;
        int length = (_data[_pos] << 8) | _data[_pos + 1];
        int endPos = _pos + length;
        if (endPos > _data.Length) endPos = _data.Length;

        switch (marker)
        {
            case JpegMarkers.SOF0:
            case JpegMarkers.SOF2:
                ParseSOF(length, marker == JpegMarkers.SOF2);
                break;
            case JpegMarkers.DQT:
                ParseDQT(length);
                break;
            case JpegMarkers.DHT:
                ParseDHT(length);
                break;
            case JpegMarkers.DRI:
                if (_pos + 3 < _data.Length)
                {
                    _restartInterval = (_data[_pos + 2] << 8) | _data[_pos + 3];
                    Console.WriteLine($"Restart Interval: {_restartInterval}");
                }
                break;
            case JpegMarkers.APP1:
                {
                    int contentLen = length - 2;
                    int p = _pos + 2;
                    if (contentLen > 0 && p + contentLen <= _data.Length)
                    {
                        byte[] buf = new byte[contentLen];
                        Buffer.BlockCopy(_data, p, buf, 0, contentLen);
                        TryParseExifOrientation(buf);
                    }
                    break;
                }
            case JpegMarkers.APP0:
            case JpegMarkers.APP2:
            case JpegMarkers.APP3:
            case JpegMarkers.APP4:
            case JpegMarkers.APP5:
            case JpegMarkers.APP6:
            case JpegMarkers.APP7:
            case JpegMarkers.APP8:
            case JpegMarkers.APP9:
            case JpegMarkers.APP10:
            case JpegMarkers.APP11:
            case JpegMarkers.APP12:
            case JpegMarkers.APP13:
            case JpegMarkers.APP14:
            case JpegMarkers.APP15:
            case JpegMarkers.COM:
                break;
            default:
                Console.WriteLine($"Skipping marker {marker:X2} length {length}");
                break;
        }

        _pos = endPos;
    }

    private void ParseSOF(int length, bool isProgressive)
    {
        Console.WriteLine($"Parsing SOF{(isProgressive ? "2 (Progressive)" : "0 (Baseline)")}");
        int p = _pos + 2;
        if (p + 6 > _data.Length) throw new Exception("SOF truncated");

        FrameHeader frame = new FrameHeader();
        frame.IsProgressive = isProgressive;
        frame.Precision = _data[p++];
        frame.Height = (_data[p++] << 8) | _data[p++];
        frame.Width = (_data[p++] << 8) | _data[p++];
        frame.ComponentsCount = _data[p++];
        frame.Components = new Component[frame.ComponentsCount];

        int maxH = 0, maxV = 0;

        for (int i = 0; i < frame.ComponentsCount; i++)
        {
            if (p + 3 > _data.Length) throw new Exception("SOF components truncated");
            var comp = new Component();
            comp.Id = _data[p++];
            int hv = _data[p++];
            comp.HFactor = hv >> 4;
            comp.VFactor = hv & 0xF;
            comp.QuantTableId = _data[p++];
            frame.Components[i] = comp;

            if (comp.HFactor > maxH) maxH = comp.HFactor;
            if (comp.VFactor > maxV) maxV = comp.VFactor;
        }

        frame.McuWidth = maxH * 8;
        frame.McuHeight = maxV * 8;
        frame.McuCols = (frame.Width + frame.McuWidth - 1) / frame.McuWidth;
        frame.McuRows = (frame.Height + frame.McuHeight - 1) / frame.McuHeight;

        Console.WriteLine($"Image: {frame.Width}x{frame.Height}, Components: {frame.ComponentsCount}, MCU: {frame.McuCols}x{frame.McuRows}");

        foreach (var comp in frame.Components)
        {
            comp.WidthInBlocks = frame.McuCols * comp.HFactor;
            comp.HeightInBlocks = frame.McuRows * comp.VFactor;
            comp.Width = comp.WidthInBlocks * 8;
            comp.Height = comp.HeightInBlocks * 8;

            int totalBlocks = comp.WidthInBlocks * comp.HeightInBlocks;
            comp.Coeffs = new int[totalBlocks][];
            for (int b = 0; b < totalBlocks; b++)
            {
                comp.Coeffs[b] = new int[64];
            }
            Console.WriteLine($"Component {comp.Id}: {comp.WidthInBlocks}x{comp.HeightInBlocks} blocks allocated.");
        }

        _frame = frame;
    }

    private void ParseDQT(int length)
    {
        int p = _pos + 2;
        int end = _pos + length;
        if (end > _data.Length) end = _data.Length;

        while (p < end)
        {
            if (p >= _data.Length) break;
            int info = _data[p++];
            int id = info & 0xF;
            int precision = info >> 4;
            int[] t = new int[64];

            for (int i = 0; i < 64; i++)
            {
                if (p >= _data.Length) throw new Exception("DQT truncated");
                int val;
                if (precision == 0) val = _data[p++];
                else
                {
                    if (p + 1 >= _data.Length) throw new Exception("DQT truncated");
                    val = (_data[p++] << 8) | _data[p++];
                }

                t[JpegUtils.ZigZag[i]] = val;
            }

            var qt = _qtables.FirstOrDefault(x => x.Id == id);
            if (qt == null)
            {
                qt = new QuantizationTable { Id = id };
                _qtables.Add(qt);
            }
            qt.Precision = precision;
            qt.Table = t;
            Console.WriteLine($"DQT Id: {id}, Precision: {precision}");
        }
    }

    private void ParseDHT(int length)
    {
        int p = _pos + 2;
        int end = _pos + length;
        if (end > _data.Length) end = _data.Length;

        while (p < end)
        {
            if (p + 17 > end)
            {
                Console.WriteLine("Warning: DHT truncated or padding bytes?");
                break;
            }

            int info = _data[p++];
            int tc = info >> 4;
            int id = info & 0xF;

            byte[] counts = new byte[16];
            int total = 0;
            for (int i = 0; i < 16; i++)
            {
                if (p >= end) break;
                counts[i] = _data[p++];
                total += counts[i];
            }

            byte[] symbols = new byte[total];
            for (int i = 0; i < total; i++)
            {
                if (p >= end)
                {
                    Console.WriteLine("Warning: DHT truncated reading symbols");
                    return;
                }
                symbols[i] = _data[p++];
            }

            var ht = _htables.FirstOrDefault(x => x.Class == tc && x.Id == id);
            if (ht == null)
            {
                ht = new HuffmanTable { Class = tc, Id = id };
                _htables.Add(ht);
            }
            ht.Counts = counts;
            ht.Symbols = symbols;

            if (!GenerateHuffmanTables(ht))
            {
                Console.WriteLine("Error: Failed to generate Huffman table (overflow or invalid). Skipping rest of DHT.");
                return;
            }
            Console.WriteLine($"DHT Class: {tc}, Id: {id}, Total Symbols: {total}");
        }
    }

    private bool GenerateHuffmanTables(HuffmanTable ht)
    {
        int p = 0;
        int[] huffsize = new int[257];
        int[] huffcode = new int[257];

        for (int i = 1; i <= 16; i++)
        {
            for (int j = 1; j <= ht.Counts[i - 1]; j++)
            {
                if (p >= 256)
                {
                    Console.WriteLine($"Error: Huffman table overflow. p={p}, i={i}");
                    return false;
                }
                huffsize[p++] = i;
            }
        }
        huffsize[p] = 0;

        int code = 0;
        int si = huffsize[0];
        p = 0;
        while (huffsize[p] != 0)
        {
            while (huffsize[p] == si)
            {
                huffcode[p++] = code;
                code++;
            }
            code <<= 1;
            si++;
        }

        int jIdx = 0;
        for (int i = 0; i < 17; i++) ht.MaxCode[i] = -1;

        for (int i = 1; i <= 16; i++)
        {
            if (ht.Counts[i - 1] == 0)
            {
                ht.MaxCode[i] = -1;
            }
            else
            {
                ht.ValPtr[i] = jIdx;
                ht.MinCode[i] = huffcode[jIdx];
                ht.MaxCode[i] = huffcode[jIdx + ht.Counts[i - 1] - 1];
                jIdx += ht.Counts[i - 1];
            }
        }
        return true;
    }

    private void ProcessScan()
    {
        if (_frame == null) throw new Exception("Frame not parsed before scan");

        if (_pos + 1 >= _data.Length) throw new Exception("SOS length truncated");
        int length = (_data[_pos] << 8) | _data[_pos + 1];
        int p = _pos + 2;

        ScanHeader scan = new ScanHeader();
        if (p >= _data.Length) throw new Exception("SOS truncated");
        scan.ComponentsCount = _data[p++];
        scan.Components = new ScanComponent[scan.ComponentsCount];

        for (int i = 0; i < scan.ComponentsCount; i++)
        {
            if (p + 1 >= _data.Length) throw new Exception("SOS components truncated");
            var sc = new ScanComponent();
            sc.ComponentId = _data[p++];
            int tableInfo = _data[p++];
            sc.DcTableId = tableInfo >> 4;
            sc.AcTableId = tableInfo & 0xF;
            scan.Components[i] = sc;
        }

        if (p + 3 > _data.Length) throw new Exception("SOS spectral selection truncated");
        scan.StartSpectralSelection = _data[p++];
        scan.EndSpectralSelection = _data[p++];
        int approx = _data[p++];
        scan.SuccessiveApproximationBitHigh = approx >> 4;
        scan.SuccessiveApproximationBitLow = approx & 0xF;

        _pos += length;

        Console.WriteLine($"Scan: Ss={scan.StartSpectralSelection}, Se={scan.EndSpectralSelection}, Ah={scan.SuccessiveApproximationBitHigh}, Al={scan.SuccessiveApproximationBitLow}, Comps={scan.ComponentsCount}");

        var reader = new JpegBitReader(_data);
        reader.SetPosition(_pos);

        try
        {
            if (_frame.IsProgressive)
            {
                DecodeProgressiveScan(scan, reader);
            }
            else
            {
                DecodeBaselineScan(scan, reader);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Scan decoding interrupted: {ex.Message}");
        }

        if (reader.HitMarker)
        {
            _pos = reader.BytePosition - 1;
        }
        else
        {
            _pos = reader.BytePosition;

            while (_pos < _data.Length && _data[_pos] != 0xFF)
            {
                _pos++;
            }
        }

        if (_pos + 1 < _data.Length && _data[_pos] == 0xFF && _data[_pos + 1] == 0x00)
        {
            Console.WriteLine("Warning: Landed on FF 00 after scan. Skipping...");
            _pos += 2;
            while (_pos < _data.Length && _data[_pos] != 0xFF) _pos++;
        }
    }

    private void DecodeProgressiveScan(ScanHeader scan, JpegBitReader reader)
    {
        int Ss = scan.StartSpectralSelection;
        int Se = scan.EndSpectralSelection;
        int Ah = scan.SuccessiveApproximationBitHigh;
        int Al = scan.SuccessiveApproximationBitLow;
        int compsInScan = scan.ComponentsCount;

        int restartsLeft = _restartInterval;
        int eobRun = 0;

        foreach (var c in _frame.Components) c.DcPred = 0;

        if (compsInScan > 1)
        {
            for (int mcuY = 0; mcuY < _frame.McuRows; mcuY++)
            {
                for (int mcuX = 0; mcuX < _frame.McuCols; mcuX++)
                {
                    CheckRestart(ref restartsLeft, reader);

                    foreach (var sc in scan.Components)
                    {
                        var comp = _frame.Components.First(c => c.Id == sc.ComponentId);
                        int baseX = mcuX * comp.HFactor;
                        int baseY = mcuY * comp.VFactor;

                        for (int v = 0; v < comp.VFactor; v++)
                        {
                            for (int h = 0; h < comp.HFactor; h++)
                            {
                                int blockIndex = (baseY + v) * comp.WidthInBlocks + (baseX + h);
                                int[] block = comp.Coeffs[blockIndex];
                                DecodeDCProgressive(reader, block, sc.DcTableId, Ah, Al, ref comp.DcPred);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            var sc = scan.Components[0];
            var comp = _frame.Components.First(c => c.Id == sc.ComponentId);
            for (int blockY = 0; blockY < comp.HeightInBlocks; blockY++)
            {
                for (int blockX = 0; blockX < comp.WidthInBlocks; blockX++)
                {
                    CheckRestart(ref restartsLeft, reader);
                    
                    int blockIndex = blockY * comp.WidthInBlocks + blockX;
                    int[] block = comp.Coeffs[blockIndex];
                    if (Ss == 0)
                    {
                        DecodeDCProgressive(reader, block, sc.DcTableId, Ah, Al, ref comp.DcPred);
                    }
                    else
                    {
                        DecodeACProgressive(reader, block, sc.AcTableId, Ss, Se, Ah, Al, ref eobRun);
                    }
                }
            }
        }
    }

    private void DecodeDCProgressive(JpegBitReader reader, int[] block, int dcTableId, int Ah, int Al, ref int dcPred)
    {
        if (Ah == 0)
        {
            var ht = _htables.First(t => t.Class == 0 && t.Id == dcTableId);
            int s = DecodeHuffman(ht, reader);
            if (s < 0) throw new Exception("Huffman decode error (DC)");

            int diff = Receive(s, reader);
            diff = Extend(diff, s);
            dcPred += diff;
            block[0] = dcPred << Al;
        }
        else
        {
            int bit = reader.ReadBit();
            if (bit == -1) throw new Exception("Bit read error (DC refinement)");
            if (bit == 1)
            {
                int delta = 1 << Al;
                if (block[0] >= 0) block[0] += delta;
                else block[0] -= delta;
            }
        }
    }

    private void DecodeACProgressive(JpegBitReader reader, int[] block, int acTableId, int Ss, int Se, int Ah, int Al, ref int eobRun)
    {
        if (Ah == 0)
        {
            if (eobRun > 0)
            {
                eobRun--;
                return;
            }

            var ht = _htables.First(t => t.Class == 1 && t.Id == acTableId);

            for (int k = Ss; k <= Se; k++)
            {
                int s = DecodeHuffman(ht, reader);
                if (s < 0) throw new Exception("Huffman decode error (AC)");

                int r = s >> 4;
                int n = s & 0xF;

                if (n != 0)
                {
                    k += r;
                    int val = Receive(n, reader);
                    val = Extend(val, n);
                    if (k <= 63)
                        block[JpegUtils.ZigZag[k]] = val << Al;
                }
                else
                {
                    if (r != 15)
                    {
                        eobRun = (1 << r) + Receive(r, reader) - 1;
                        break;
                    }
                    k += 15;
                }
            }
        }
        else
        {
            int k = Ss;
            if (eobRun > 0)
            {
                while (k <= Se)
                {
                    int idx = JpegUtils.ZigZag[k];
                    if (block[idx] != 0) RefineNonZero(reader, block, idx, Al);
                    k++;
                }
                eobRun--;
                return;
            }

            var ht = _htables.First(t => t.Class == 1 && t.Id == acTableId);

            while (k <= Se)
            {
                int s = DecodeHuffman(ht, reader);
                if (s < 0) throw new Exception("Huffman decode error (AC Refinement)");

                int r = s >> 4;
                int n = s & 0xF;

                if (n != 0)
                {
                    int zerosToSkip = r;
                    while (k <= Se)
                    {
                        int idx = JpegUtils.ZigZag[k];
                        if (block[idx] != 0)
                        {
                            RefineNonZero(reader, block, idx, Al);
                        }
                        else
                        {
                            if (zerosToSkip == 0) break;
                            zerosToSkip--;
                        }
                        k++;
                    }

                    if (k > Se) break;

                    int val = 1;
                    int sign = reader.ReadBit();
                    if (sign == 0) val = -1;

                    block[JpegUtils.ZigZag[k]] = val << Al;
                    k++;
                }
                else
                {
                    if (r != 15)
                    {
                        eobRun = (1 << r) + Receive(r, reader) - 1;
                        while (k <= Se)
                        {
                            int idx = JpegUtils.ZigZag[k];
                            if (block[idx] != 0) RefineNonZero(reader, block, idx, Al);
                            k++;
                        }
                        break;
                    }
                    else
                    {
                        int zerosToSkip = 16;
                           while (k <= Se && zerosToSkip > 0)
                           {
                               int idx = JpegUtils.ZigZag[k];
                               if (block[idx] != 0)
                               {
                                   RefineNonZero(reader, block, idx, Al);
                               }
                               else
                               {
                                   zerosToSkip--;
                               }
                               k++;
                           }
                    }
                }
            }
        }
    }

    private void RefineNonZero(JpegBitReader reader, int[] block, int idx, int Al)
    {
        int bit = reader.ReadBit();
        if (bit == 1)
        {
            if (block[idx] > 0) block[idx] += (1 << Al);
            else block[idx] -= (1 << Al);
        }
    }

    private void DecodeBaselineScan(ScanHeader scan, JpegBitReader reader)
    {
        int restartsLeft = _restartInterval;
        foreach (var c in _frame.Components) c.DcPred = 0;

        for (int mcuY = 0; mcuY < _frame.McuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < _frame.McuCols; mcuX++)
            {
                CheckRestart(ref restartsLeft, reader);

                foreach (var sc in scan.Components)
                {
                    var comp = _frame.Components.First(c => c.Id == sc.ComponentId);
                    int baseX = mcuX * comp.HFactor;
                    int baseY = mcuY * comp.VFactor;

                    for (int v = 0; v < comp.VFactor; v++)
                    {
                        for (int h = 0; h < comp.HFactor; h++)
                        {
                            int blockIndex = (baseY + v) * comp.WidthInBlocks + (baseX + h);
                            int[] block = comp.Coeffs[blockIndex];

                            DecodeDCProgressive(reader, block, sc.DcTableId, 0, 0, ref comp.DcPred);

                            int dummyEob = 0;
                            DecodeACProgressive(reader, block, sc.AcTableId, 1, 63, 0, 0, ref dummyEob);
                        }
                    }
                }
            }
        }
    }

    private int Receive(int n, JpegBitReader reader)
    {
        if (n == 0) return 0;
        return reader.ReadBits(n);
    }

    private byte[] PerformIDCTAndOutput()
    {
        if (_frame == null) throw new Exception("Frame not initialized");

        Console.WriteLine("Performing IDCT and Output...");

        int width = _frame.Width;
        int height = _frame.Height;
        byte[] rgb = new byte[width * height * 3];

        Component compY = _frame.Components[0];
        Component compCb = _frame.Components.Length > 1 ? _frame.Components[1] : null;
        Component compCr = _frame.Components.Length > 2 ? _frame.Components[2] : null;

        int mcuW = _frame.McuWidth;
        int mcuH = _frame.McuHeight;

        byte[][] yBuffer = new byte[compY.HFactor * compY.VFactor][];
        for (int i = 0; i < yBuffer.Length; i++) yBuffer[i] = new byte[64];

        byte[][] cbBuffer = null;
        if (compCb != null)
        {
            cbBuffer = new byte[compCb.HFactor * compCb.VFactor][];
            for (int i = 0; i < cbBuffer.Length; i++) cbBuffer[i] = new byte[64];
        }

        byte[][] crBuffer = null;
        if (compCr != null)
        {
            crBuffer = new byte[compCr.HFactor * compCr.VFactor][];
            for (int i = 0; i < crBuffer.Length; i++) crBuffer[i] = new byte[64];
        }

        for (int mcuY = 0; mcuY < _frame.McuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < _frame.McuCols; mcuX++)
            {
                int yBlockBaseX = mcuX * compY.HFactor;
                int yBlockBaseY = mcuY * compY.VFactor;

                for (int v = 0; v < compY.VFactor; v++)
                {
                    for (int h = 0; h < compY.HFactor; h++)
                    {
                        int blockIdx = (yBlockBaseY + v) * compY.WidthInBlocks + (yBlockBaseX + h);
                        int[] coeffs = compY.Coeffs[blockIdx];
                        int qId = compY.QuantTableId;
                        var qt = _qtables.First(q => q.Id == qId);

                        int[] dequantized = new int[64];
                        for (int i = 0; i < 64; i++) dequantized[i] = coeffs[i] * qt.Table[i];

                        JpegIDCT.BlockIDCT(dequantized, yBuffer[v * compY.HFactor + h]);
                    }
                }

                if (compCb != null)
                {
                    int cbBlockBaseX = mcuX * compCb.HFactor;
                    int cbBlockBaseY = mcuY * compCb.VFactor;

                    for (int v = 0; v < compCb.VFactor; v++)
                    {
                        for (int h = 0; h < compCb.HFactor; h++)
                        {
                            int cbIdx = (cbBlockBaseY + v) * compCb.WidthInBlocks + (cbBlockBaseX + h);
                            var qtCb = _qtables.First(q => q.Id == compCb.QuantTableId);
                            int[] deqCb = new int[64];
                            for (int i = 0; i < 64; i++) deqCb[i] = compCb.Coeffs[cbIdx][i] * qtCb.Table[i];
                            JpegIDCT.BlockIDCT(deqCb, cbBuffer[v * compCb.HFactor + h]);
                        }
                    }
                }

                if (compCr != null)
                {
                    int crBlockBaseX = mcuX * compCr.HFactor;
                    int crBlockBaseY = mcuY * compCr.VFactor;

                    for (int v = 0; v < compCr.VFactor; v++)
                    {
                        for (int h = 0; h < compCr.HFactor; h++)
                        {
                            int crIdx = (crBlockBaseY + v) * compCr.WidthInBlocks + (crBlockBaseX + h);
                            var qtCr = _qtables.First(q => q.Id == compCr.QuantTableId);
                            int[] deqCr = new int[64];
                            for (int i = 0; i < 64; i++) deqCr[i] = compCr.Coeffs[crIdx][i] * qtCr.Table[i];
                            JpegIDCT.BlockIDCT(deqCr, crBuffer[v * compCr.HFactor + h]);
                        }
                    }
                }

                int pixelBaseX = mcuX * mcuW;
                int pixelBaseY = mcuY * mcuH;

                for (int py = 0; py < mcuH; py++)
                {
                    for (int px = 0; px < mcuW; px++)
                    {
                        int globalX = pixelBaseX + px;
                        int globalY = pixelBaseY + py;

                        if (globalX >= width || globalY >= height) continue;

                        int yBlockX = px / 8;
                        int yBlockY = py / 8;
                        int yBlockIdx = yBlockY * compY.HFactor + yBlockX;
                        int yInnerX = px % 8;
                        int yInnerY = py % 8;

                        byte Y = yBuffer[yBlockIdx][yInnerY * 8 + yInnerX];

                        byte Cb = 128;
                        byte Cr = 128;

                        if (compCb != null)
                        {
                            int cbX = (px * compCb.HFactor) / compY.HFactor;
                            int cbY = (py * compCb.VFactor) / compY.VFactor;

                            int cbBlockX = cbX / 8;
                            int cbBlockY = cbY / 8;
                            int cbInnerX = cbX % 8;
                            int cbInnerY = cbY % 8;

                            int cbBlockIdx = cbBlockY * compCb.HFactor + cbBlockX;

                            if (cbBlockIdx < cbBuffer.Length)
                            {
                                Cb = cbBuffer[cbBlockIdx][cbInnerY * 8 + cbInnerX];
                            }
                        }

                        if (compCr != null)
                        {
                            int crX = (px * compCr.HFactor) / compY.HFactor;
                            int crY = (py * compCr.VFactor) / compY.VFactor;

                            int crBlockX = crX / 8;
                            int crBlockY = crY / 8;
                            int crInnerX = crX % 8;
                            int crInnerY = crY % 8;

                            int crBlockIdx = crBlockY * compCr.HFactor + crBlockX;

                            if (crBlockIdx < crBuffer.Length)
                            {
                                Cr = crBuffer[crBlockIdx][crInnerY * 8 + crInnerX];
                            }
                        }

                        double cCb = Cb - 128;
                        double cCr = Cr - 128;

                        int r = (int)(Y + 1.402 * cCr);
                        int g = (int)(Y - 0.344136 * cCb - 0.714136 * cCr);
                        int b = (int)(Y + 1.772 * cCb);

                        int idx = (globalY * width + globalX) * 3;
                        rgb[idx] = (byte)JpegUtils.Clamp(r);
                        rgb[idx + 1] = (byte)JpegUtils.Clamp(g);
                        rgb[idx + 2] = (byte)JpegUtils.Clamp(b);
                    }
                }
            }
        }

        return rgb;
    }

    private int DecodeHuffman(HuffmanTable ht, JpegBitReader reader)
    {
        int code = reader.ReadBit();
        int i = 1;
        if (code == -1) return -1;

        while (code > ht.MaxCode[i])
        {
            int bit = reader.ReadBit();
            if (bit == -1) return -1;
            code = (code << 1) | bit;
            i++;
            if (i > 16) return -1;
        }

        int j = ht.ValPtr[i];
        int j2 = j + code - ht.MinCode[i];
        return ht.Symbols[j2];
    }

    private int Extend(int v, int t)
    {
        int vt = 1 << (t - 1);
        if (v < vt)
        {
            vt = (-1) << t;
            return v + vt + 1;
        }
        return v;
    }

    private void CheckRestart(ref int restartsLeft, JpegBitReader reader)
    {
        if (_restartInterval == 0) return;

        restartsLeft--;
        if (restartsLeft == 0)
        {
            reader.AlignToByte();
            if (!reader.ConsumeRestartMarker())
            {
                Console.WriteLine("Warning: Expected restart marker but didn't find one.");
            }
            restartsLeft = _restartInterval;

            foreach (var c in _frame.Components) c.DcPred = 0;
        }
    }

    private void TryParseExifOrientation(byte[] app1)
    {
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
        if (magic != 42) return;
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
            if (tag == 0x0112)
            {
                int orientation = 1;
                if (type == 3 && count >= 1)
                {
                    orientation = littleEndian ? (app1[valueOffset] | (app1[valueOffset + 1] << 8)) : ((app1[valueOffset] << 8) | app1[valueOffset + 1]);
                }
                else
                {
                    uint valPtr = ReadU32(valueOffset);
                    int p = tiffBase + (int)valPtr;
                    if (p >= 0 && p + 2 <= app1.Length)
                        orientation = ReadU16(p);
                }
                if (orientation >= 1 && orientation <= 8)
                    ExifOrientation = orientation;
                return;
            }
        }
    }
}

class QuantizationTable
{
    public int Id { get; set; }
    public int Precision { get; set; }
    public int[] Table { get; set; } = new int[64];
}

class HuffmanTable
{
    public int Class { get; set; }
    public int Id { get; set; }
    public byte[] Counts { get; set; } = new byte[16];
    public byte[] Symbols { get; set; }
    public int[] MaxCode { get; set; } = new int[17];
    public int[] MinCode { get; set; } = new int[17];
    public int[] ValPtr { get; set; } = new int[17];
}

class Component
{
    public int Id { get; set; }
    public int HFactor { get; set; }
    public int VFactor { get; set; }
    public int QuantTableId { get; set; }
    public int DcTableId { get; set; }
    public int AcTableId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WidthInBlocks { get; set; }
    public int HeightInBlocks { get; set; }
    public int[][] Coeffs { get; set; }
    public int DcPred;
}

class FrameHeader
{
    public int Precision { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }
    public int ComponentsCount { get; set; }
    public Component[] Components { get; set; }
    public bool IsProgressive { get; set; }
    public int McuWidth { get; set; }
    public int McuHeight { get; set; }
    public int McuCols { get; set; }
    public int McuRows { get; set; }
}

class ScanHeader
{
    public int ComponentsCount { get; set; }
    public ScanComponent[] Components { get; set; }
    public int StartSpectralSelection { get; set; }
    public int EndSpectralSelection { get; set; }
    public int SuccessiveApproximationBitHigh { get; set; }
    public int SuccessiveApproximationBitLow { get; set; }
}

class ScanComponent
{
    public int ComponentId { get; set; }
    public int DcTableId { get; set; }
    public int AcTableId { get; set; }
}

static class JpegMarkers
{
    public const byte SOI = 0xD8;
    public const byte EOI = 0xD9;
    public const byte SOS = 0xDA;
    public const byte DQT = 0xDB;
    public const byte DNL = 0xDC;
    public const byte DRI = 0xDD;
    public const byte DHP = 0xDE;
    public const byte EXP = 0xDF;

    public const byte APP0 = 0xE0;
    public const byte APP1 = 0xE1;
    public const byte APP2 = 0xE2;
    public const byte APP3 = 0xE3;
    public const byte APP4 = 0xE4;
    public const byte APP5 = 0xE5;
    public const byte APP6 = 0xE6;
    public const byte APP7 = 0xE7;
    public const byte APP8 = 0xE8;
    public const byte APP9 = 0xE9;
    public const byte APP10 = 0xEA;
    public const byte APP11 = 0xEB;
    public const byte APP12 = 0xEC;
    public const byte APP13 = 0xED;
    public const byte APP14 = 0xEE;
    public const byte APP15 = 0xEF;

    public const byte JPG0 = 0xF0;
    public const byte JPG13 = 0xFD;
    public const byte COM = 0xFE;
    public const byte TEM = 0x01;

    public const byte SOF0 = 0xC0;
    public const byte SOF1 = 0xC1;
    public const byte SOF2 = 0xC2;
    public const byte SOF3 = 0xC3;

    public const byte SOF5 = 0xC5;
    public const byte SOF6 = 0xC6;
    public const byte SOF7 = 0xC7;

    public const byte SOF9 = 0xC9;
    public const byte SOF10 = 0xCA;
    public const byte SOF11 = 0xCB;

    public const byte SOF13 = 0xCD;
    public const byte SOF14 = 0xCE;
    public const byte SOF15 = 0xCF;

    public const byte DHT = 0xC4;
    public const byte DAC = 0xCC;

    public const byte RST0 = 0xD0;
    public const byte RST1 = 0xD1;
    public const byte RST2 = 0xD2;
    public const byte RST3 = 0xD3;
    public const byte RST4 = 0xD4;
    public const byte RST5 = 0xD5;
    public const byte RST6 = 0xD6;
    public const byte RST7 = 0xD7;

    public static bool IsRST(byte marker) => marker >= RST0 && marker <= RST7;
    public static bool IsSOF(byte marker) => marker == SOF0 || marker == SOF1 || marker == SOF2 || marker == SOF3 ||
                                             marker == SOF9 || marker == SOF10 || marker == SOF11 ||
                                             marker == SOF5 || marker == SOF6 || marker == SOF7 ||
                                             marker == SOF13 || marker == SOF14 || marker == SOF15;
}

static class JpegUtils
{
    public static readonly int[] ZigZag = new int[64]
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    public static int Clamp(int val)
    {
        if (val < 0) return 0;
        if (val > 255) return 255;
        return val;
    }
}

static class JpegIDCT
{
    private static readonly float[] C = new float[8];

    static JpegIDCT()
    {
        for (int i = 0; i < 8; i++)
        {
            C[i] = (i == 0) ? (float)(1.0 / Math.Sqrt(2.0)) : 1.0f;
        }
    }

    public static void BlockIDCT(int[] block, byte[] dest)
    {
        float[] temp = new float[64];

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    sum += C[u] * block[y * 8 + u] * (float)Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
                temp[y * 8 + x] = sum * 0.5f;
            }
        }

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                float sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    sum += C[v] * temp[v * 8 + x] * (float)Math.Cos((2 * y + 1) * v * Math.PI / 16.0);
                }
                sum *= 0.5f;
                int val = (int)(sum + 128.5f);
                if (val < 0) val = 0;
                if (val > 255) val = 255;
                dest[y * 8 + x] = (byte)val;
            }
        }
    }
}

class JpegBitReader
{
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos;
    private int _currentByte;
    private bool _hitMarker;
    private byte _marker;

    public JpegBitReader(byte[] data)
    {
        _data = data;
        _bytePos = 0;
        _bitPos = 0;
        _currentByte = 0;
        _hitMarker = false;
        _marker = 0;
    }

    public int BytePosition => _bytePos;

    public void ResetBits()
    {
        _bitPos = 0;
    }

    public void SetPosition(int pos)
    {
        _bytePos = pos;
        _bitPos = 0;
    }

    public int ReadBit()
    {
        if (_bitPos == 0)
        {
            NextByte();
            if (_hitMarker) return -1;
        }

        int bit = (_currentByte >> (--_bitPos)) & 1;
        return bit;
    }

    public int ReadBits(int n)
    {
        int result = 0;
        for (int i = 0; i < n; i++)
        {
            int bit = ReadBit();
            if (bit == -1) return -1;
            result = (result << 1) | bit;
        }
        return result;
    }

    private void NextByte()
    {
        if (_bytePos >= _data.Length)
        {
            _currentByte = 0xFF;
            _bitPos = 8;
            return;
        }

        int b = _data[_bytePos++];

        if (b == 0xFF)
        {
            if (_bytePos >= _data.Length)
            {
                _currentByte = 0xFF;
                _bitPos = 8;
                return;
            }

            int b2 = _data[_bytePos];
            if (b2 == 0x00)
            {
                _bytePos++;
                _currentByte = 0xFF;
            }
            else
            {
                _hitMarker = true;
                _marker = (byte)b2;
                _currentByte = 0;
                _bitPos = 0;
                return;
            }
        }
        else
        {
            _currentByte = b;
        }

        _bitPos = 8;
    }

    public int PeekByte()
    {
        if (_bytePos >= _data.Length) return -1;
        return _data[_bytePos];
    }

    public void AlignToByte()
    {
        _bitPos = 0;
    }

    public bool HitMarker => _hitMarker;
    public byte Marker => _marker;

    public bool ConsumeRestartMarker()
    {
        if (!_hitMarker)
        {
            if (_bitPos != 0 && _bitPos != 8)
            {
            }

            NextByte();
        }

        if (_hitMarker && JpegMarkers.IsRST(_marker))
        {
            _hitMarker = false;
            _marker = 0;
            _bytePos++;
            _bitPos = 0;
            return true;
        }
        return false;
    }
}
