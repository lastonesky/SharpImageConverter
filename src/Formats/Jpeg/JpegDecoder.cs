using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter;

/// <summary>
/// JPEG 解码器，用于将 JPEG 图像数据解码为 RGB 图像。
/// </summary>
public class JpegDecoder
{
    private const int ColorShift = 16;
    private const int ColorHalf = 1 << (ColorShift - 1);
    private static readonly int[] CbToB = BuildColorTable(116130);
    private static readonly int[] CbToG = BuildColorTable(-22554);
    private static readonly int[] CrToR = BuildColorTable(91881);
    private static readonly int[] CrToG = BuildColorTable(-46802);

    private Stream _stream;
    private FrameHeader _frame;
    private readonly List<JpegQuantTable> _qtables = [];
    private readonly List<HuffmanDecodingTable> _htables = [];
    private readonly JpegQuantTable[] _qtablesById = new JpegQuantTable[4];
    private readonly HuffmanDecodingTable[,] _htablesByClassAndId = new HuffmanDecodingTable[2, 4];
    private readonly Component[] _componentsById = new Component[256];
    private int _restartInterval;

    public int Width => _frame != null ? _frame.Width : 0;
    public int Height => _frame != null ? _frame.Height : 0;
    
    /// <summary>
    /// 获取当前 JPEG 图像的 EXIF 方向值（1 为默认方向）。
    /// </summary>
    public int ExifOrientation { get; private set; } = 1;

    /// <summary>
    /// 将传入的 JPEG 流解码为 RGB 图像。
    /// </summary>
    /// <param name="stream">包含 JPEG 图像数据的输入流。</param>
    /// <returns>RGB 图像。</returns>
    public Image<Rgb24> Decode(Stream stream)
    {
        _stream = stream;
        
        _frame = null;
        _qtables.Clear();
        _htables.Clear();
        Array.Clear(_qtablesById, 0, _qtablesById.Length);
        Array.Clear(_htablesByClassAndId, 0, _htablesByClassAndId.Length);
        Array.Clear(_componentsById, 0, _componentsById.Length);
        _restartInterval = 0;
        ExifOrientation = 1;

        // Check SOI
        int b1 = _stream.ReadByte();
        int b2 = _stream.ReadByte();
        if (b1 != 0xFF || b2 != JpegMarkers.SOI)
        {
             throw new InvalidDataException("Not a valid JPEG file (missing SOI)");
        }

        ParseHeaders();

        while (_stream.Position < _stream.Length)
        {
            // Scan for next marker
            int b = _stream.ReadByte();
            if (b == -1) break;
            
            if (b != 0xFF)
            {
                // Skip garbage between segments
                continue;
            }

            int markerInt = _stream.ReadByte();
            while (markerInt == 0xFF) 
            {
                markerInt = _stream.ReadByte();
            }
            
            if (markerInt == -1) break;
            
            byte marker = (byte)markerInt;

            if (marker == JpegMarkers.EOI)
            {
                break;
            }
            else if (marker == JpegMarkers.SOS)
            {
                ProcessScan();
            }
            else if (JpegMarkers.IsSOF(marker) || marker == JpegMarkers.DHT || marker == JpegMarkers.DQT || marker == JpegMarkers.DRI)
            {
                ParseMarker(marker);
            }
            else if (marker >= JpegMarkers.APP0 && marker <= JpegMarkers.APP15)
            {
                ParseMarker(marker);
            }
            else if (marker == JpegMarkers.COM)
            {
                ParseMarker(marker);
            }
            else if (marker == JpegMarkers.DNL)
            {
                ParseMarker(marker);
            }
            else if (marker == 0x00)
            {
                // FF 00 is just a byte in stream, but here we are looking for markers. 
                // If we are here, we are outside of SOS scan, so FF 00 shouldn't happen usually unless it's garbage.
            }
            else
            {
                 // Unknown marker, read length and skip
                 ParseMarker(marker);
            }
        }

        return PerformIDCTAndOutput();
    }

    private byte ReadByte()
    {
        int b = _stream.ReadByte();
        if (b == -1) throw new InvalidDataException("Unexpected end of stream");
        return (byte)b;
    }

    private ushort ReadUShort()
    {
        int b1 = _stream.ReadByte();
        int b2 = _stream.ReadByte();
        if (b1 == -1 || b2 == -1) throw new InvalidDataException("Unexpected end of stream");
        return (ushort)((b1 << 8) | b2);
    }
    
    private void ParseHeaders()
    {
        long initialPos = _stream.Position;
        while (_stream.Position < _stream.Length)
        {
            int b = _stream.ReadByte();
            if (b == -1) break;
            if (b != 0xFF) continue;
            
            int markerInt = _stream.ReadByte();
            while (markerInt == 0xFF) markerInt = _stream.ReadByte();
            if (markerInt == -1) break;
            
            byte marker = (byte)markerInt;

            if (marker == JpegMarkers.SOS || marker == JpegMarkers.EOI)
            {
                // Rewind 2 bytes so main loop can process it
                _stream.Seek(-2, SeekOrigin.Current);
                return;
            }

            ParseMarker(marker);
        }
    }

    private void ParseMarker(byte marker)
    {
        if (marker == 0x00) return;

        ushort length = ReadUShort();
        long endPos = _stream.Position + length - 2;

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
                if (length >= 4)
                {
                    _restartInterval = ReadUShort();
                }
                break;
            case JpegMarkers.APP1:
                {
                    int contentLen = length - 2;
                    if (contentLen > 0)
                    {
                        byte[] buf = new byte[contentLen];
                        _stream.ReadExactly(buf, 0, contentLen);
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
                // Skip
                break;
            default:
                // Skip unknown
                break;
        }

        _stream.Position = endPos;
    }

    private void ParseSOF(int length, bool isProgressive)
    {
        FrameHeader frame = new()
        {
            IsProgressive = isProgressive,
            Precision = ReadByte(),
            Height = ReadUShort(),
            Width = ReadUShort(),
            ComponentsCount = ReadByte()
        };

        if (frame.Precision != 8)
        {
            throw new NotSupportedException("Only 8-bit JPEG precision is supported.");
        }

        if (frame.ComponentsCount < 1 || frame.ComponentsCount > 3)
        {
            throw new NotSupportedException("Only JPEG images with 1 to 3 components are supported.");
        }

        frame.Components = new Component[frame.ComponentsCount];

        int maxH = 0, maxV = 0;

        for (int i = 0; i < frame.ComponentsCount; i++)
        {
            var comp = new Component();
            comp.Id = ReadByte();
            int hv = ReadByte();
            comp.HFactor = hv >> 4;
            comp.VFactor = hv & 0xF;
            comp.QuantTableId = ReadByte();
            frame.Components[i] = comp;
            if (comp.Id >= 0 && comp.Id < _componentsById.Length)
            {
                _componentsById[comp.Id] = comp;
            }

            if (comp.HFactor > maxH) maxH = comp.HFactor;
            if (comp.VFactor > maxV) maxV = comp.VFactor;
        }

        frame.McuWidth = maxH * 8;
        frame.McuHeight = maxV * 8;
        frame.McuCols = (frame.Width + frame.McuWidth - 1) / frame.McuWidth;
        frame.McuRows = (frame.Height + frame.McuHeight - 1) / frame.McuHeight;

        foreach (var comp in frame.Components)
        {
            comp.WidthInBlocks = frame.McuCols * comp.HFactor;
            comp.HeightInBlocks = frame.McuRows * comp.VFactor;
            comp.Width = comp.WidthInBlocks * 8;
            comp.Height = comp.HeightInBlocks * 8;

            int totalBlocks = comp.WidthInBlocks * comp.HeightInBlocks;
            comp.Coeffs = new int[totalBlocks * 64];
        }

        _frame = frame;
    }

    private void ParseDQT(int length)
    {
        long end = _stream.Position + length - 2;

        while (_stream.Position < end)
        {
            int info = ReadByte();
            int id = info & 0xF;
            int precision = info >> 4;
            ushort[] t = new ushort[64];

            for (int i = 0; i < 64; i++)
            {
                if (precision == 0) t[JpegUtils.ZigZag[i]] = ReadByte();
                else t[JpegUtils.ZigZag[i]] = ReadUShort();
            }

            JpegQuantTable qt = null;
            for (int qi = 0; qi < _qtables.Count; qi++)
            {
                if (_qtables[qi].Id == id)
                {
                    qt = _qtables[qi];
                    break;
                }
            }
            if (qt == null)
            {
                qt = new JpegQuantTable((byte)id, (byte)precision, t);
                _qtables.Add(qt);
            }
            // If already exists, we might want to update it, but JpegQuantTable is immutable-ish (Values is array).
            // JpegQuantTable has Values prop which is array.
            Array.Copy(t, qt.Values, 64);
            
            if (id >= 0 && id < _qtablesById.Length)
            {
                _qtablesById[id] = qt;
            }
        }
    }

    private void ParseDHT(int length)
    {
        long end = _stream.Position + length - 2;

        while (_stream.Position < end)
        {
            int info = ReadByte();
            int tc = info >> 4;
            int id = info & 0xF;

            byte[] counts = new byte[16];
            int total = 0;
            for (int i = 0; i < 16; i++)
            {
                counts[i] = ReadByte();
                total += counts[i];
            }

            byte[] symbols = new byte[total];
            _stream.ReadExactly(symbols, 0, total);

            JpegHuffmanTable rawHt = new JpegHuffmanTable((byte)tc, (byte)id, counts, symbols);
            HuffmanDecodingTable ht = new HuffmanDecodingTable(rawHt);
            
            // Check if exists
            bool found = false;
            for (int i = 0; i < _htables.Count; i++)
            {
                if (_htables[i].Table.TableClass == tc && _htables[i].Table.TableId == id)
                {
                    _htables[i] = ht;
                    found = true;
                    break;
                }
            }
            if (!found) _htables.Add(ht);

            if (tc >= 0 && tc < _htablesByClassAndId.GetLength(0) && id >= 0 && id < _htablesByClassAndId.GetLength(1))
            {
                _htablesByClassAndId[tc, id] = ht;
            }
        }
    }

    private void ProcessScan()
    {
        if (_frame == null) throw new InvalidOperationException("Frame not parsed before scan");

        int length = ReadUShort();
        
        ScanHeader scan = new ScanHeader();
        scan.ComponentsCount = ReadByte();
        scan.Components = new ScanComponent[scan.ComponentsCount];

        for (int i = 0; i < scan.ComponentsCount; i++)
        {
            var sc = new ScanComponent
            {
                ComponentId = ReadByte()
            };
            int tableInfo = ReadByte();
            sc.DcTableId = tableInfo >> 4;
            sc.AcTableId = tableInfo & 0xF;
            scan.Components[i] = sc;
        }

        scan.StartSpectralSelection = ReadByte();
        scan.EndSpectralSelection = ReadByte();
        int approx = ReadByte();
        scan.SuccessiveApproximationBitHigh = approx >> 4;
        scan.SuccessiveApproximationBitLow = approx & 0xF;

        var reader = new JpegBitReader(_stream);
        // Position is already correct

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
        catch (InvalidOperationException)
        {
            // Rethrow critical errors
            throw;
        }
        catch (Exception ex)
        {
             // Log or suppress? Jpeg decoding often has some corruption at end.
             // We'll suppress generic errors during scan but maybe throw on critical ones.
        }

        if (reader.HitMarker)
        {
             // If we hit a marker, we need to ensure stream position is correct.
             // JpegBitReader handles buffering.
             // But JpegBitReader.ConsumeRestartMarker consumes bytes.
             // If we stopped due to a marker, we should be at that marker.
             // JpegBitReader.NextByte consumes the marker byte if it sees one.
             // Actually JpegBitReader doesn't support seeking back easily if it over-read.
             // But our JpegBitReader is synchronized with Stream position mostly.
             // If HitMarker is true, it means we saw FF xx. 
             // We need to back up so the main loop can see the marker.
             // The JpegBitReader logic says: if b == 0xFF and b2 != 0x00, HitMarker = true, Marker = b2.
             // It consumed FF and b2.
             // So we are AFTER the marker.
             // Main loop expects to read FF then xx.
             // So we should seek back 2 bytes.
             if (reader.Marker != 0)
             {
                 _stream.Seek(-2, SeekOrigin.Current);
             }
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
                        var comp = sc.ComponentId >= 0 && sc.ComponentId < _componentsById.Length ? _componentsById[sc.ComponentId] : null;
                        if (comp == null) throw new InvalidDataException("Component not found in frame");
                        int baseX = mcuX * comp.HFactor;
                        int baseY = mcuY * comp.VFactor;

                        for (int v = 0; v < comp.VFactor; v++)
                        {
                            for (int h = 0; h < comp.HFactor; h++)
                            {
                                int blockIndex = (baseY + v) * comp.WidthInBlocks + (baseX + h);
                                DecodeDCProgressive(reader, comp.Coeffs, blockIndex * 64, sc.DcTableId, Ah, Al, ref comp.DcPred);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            var sc = scan.Components[0];
            var comp = sc.ComponentId >= 0 && sc.ComponentId < _componentsById.Length ? _componentsById[sc.ComponentId] : null;
            if (comp == null) throw new InvalidDataException("Component not found in frame");
            for (int blockY = 0; blockY < comp.HeightInBlocks; blockY++)
            {
                for (int blockX = 0; blockX < comp.WidthInBlocks; blockX++)
                {
                    CheckRestart(ref restartsLeft, reader);
                    
                    int blockIndex = blockY * comp.WidthInBlocks + blockX;
                    if (Ss == 0)
                    {
                        DecodeDCProgressive(reader, comp.Coeffs, blockIndex * 64, sc.DcTableId, Ah, Al, ref comp.DcPred);
                    }
                    else
                    {
                        DecodeACProgressive(reader, comp.Coeffs, blockIndex * 64, sc.AcTableId, Ss, Se, Ah, Al, ref eobRun);
                    }
                }
            }
        }
    }

    private void DecodeDCProgressive(JpegBitReader reader, int[] coeffs, int offset, int dcTableId, int Ah, int Al, ref int dcPred)
    {
        if (Ah == 0)
        {
            HuffmanDecodingTable ht = null;
            if (dcTableId >= 0 && dcTableId < _htablesByClassAndId.GetLength(1))
            {
                ht = _htablesByClassAndId[0, dcTableId];
            }
            if (ht == null) throw new InvalidDataException("Missing DC Huffman table");
            int s = DecodeHuffman(ht, reader);
            if (s < 0) throw new InvalidDataException("Huffman decode error (DC)");

            int diff = Receive(s, reader);
            diff = Extend(diff, s);
            dcPred += diff;
            coeffs[offset + 0] = dcPred << Al;
        }
        else
        {
            int bit = reader.ReadBit();
            if (bit == -1) throw new InvalidDataException("Bit read error (DC refinement)");
            if (bit == 1)
            {
                int delta = 1 << Al;
                if (coeffs[offset + 0] >= 0) coeffs[offset + 0] += delta;
                else coeffs[offset + 0] -= delta;
            }
        }
    }

    private void DecodeACProgressive(JpegBitReader reader, int[] coeffs, int offset, int acTableId, int Ss, int Se, int Ah, int Al, ref int eobRun)
    {
        if (Ah == 0)
        {
            if (eobRun > 0)
            {
                eobRun--;
                return;
            }

            HuffmanDecodingTable ht = null;
            if (acTableId >= 0 && acTableId < _htablesByClassAndId.GetLength(1))
            {
                ht = _htablesByClassAndId[1, acTableId];
            }
            if (ht == null) throw new InvalidDataException("Missing AC Huffman table");

            for (int k = Ss; k <= Se; k++)
            {
                int s = DecodeHuffman(ht, reader);
                if (s < 0) throw new InvalidDataException("Huffman decode error (AC)");

                int r = s >> 4;
                int n = s & 0xF;

                if (n != 0)
                {
                    k += r;
                    int val = Receive(n, reader);
                    val = Extend(val, n);
                    coeffs[offset + JpegUtils.ZigZag[k]] = val << Al;
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
                    if (coeffs[offset + idx] != 0) RefineNonZero(reader, coeffs, offset + idx, Al);
                    k++;
                }
                eobRun--;
                return;
            }

            HuffmanDecodingTable ht = null;
            if (acTableId >= 0 && acTableId < _htablesByClassAndId.GetLength(1))
            {
                ht = _htablesByClassAndId[1, acTableId];
            }
            if (ht == null) throw new InvalidDataException("Missing AC Huffman table");

            while (k <= Se)
            {
                int s = DecodeHuffman(ht, reader);
                if (s < 0) throw new InvalidDataException("Huffman decode error (AC Refinement)");

                int r = s >> 4;
                int n = s & 0xF;

                if (n != 0)
                {
                    int zerosToSkip = r;
                    while (k <= Se)
                    {
                        int idx = JpegUtils.ZigZag[k];
                        if (coeffs[offset + idx] != 0)
                        {
                            RefineNonZero(reader, coeffs, offset + idx, Al);
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

                    coeffs[offset + JpegUtils.ZigZag[k]] = val << Al;
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
                            if (coeffs[offset + idx] != 0) RefineNonZero(reader, coeffs, offset + idx, Al);
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
                               if (coeffs[offset + idx] != 0)
                               {
                                RefineNonZero(reader, coeffs, offset + idx, Al);
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

    private static void RefineNonZero(JpegBitReader reader, int[] coeffs, int idx, int Al)
    {
        int bit = reader.ReadBit();
        if (bit == 1)
        {
            if (coeffs[idx] > 0) coeffs[idx] += (1 << Al);
            else coeffs[idx] -= (1 << Al);
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
                    var comp = sc.ComponentId >= 0 && sc.ComponentId < _componentsById.Length ? _componentsById[sc.ComponentId] : null;
                    if (comp == null) throw new InvalidDataException("Component not found in frame");
                    int baseX = mcuX * comp.HFactor;
                    int baseY = mcuY * comp.VFactor;

                    for (int v = 0; v < comp.VFactor; v++)
                    {
                        for (int h = 0; h < comp.HFactor; h++)
                        {
                            int blockIndex = (baseY + v) * comp.WidthInBlocks + (baseX + h);
                            DecodeDCProgressive(reader, comp.Coeffs, blockIndex * 64, sc.DcTableId, 0, 0, ref comp.DcPred);

                            int dummyEob = 0;
                            DecodeACProgressive(reader, comp.Coeffs, blockIndex * 64, sc.AcTableId, 1, 63, 0, 0, ref dummyEob);
                        }
                    }
                }
            }
        }
    }

    private static int Receive(int n, JpegBitReader reader)
    {
        if (n == 0) return 0;
        return reader.ReadBits(n);
    }

    private Image<Rgb24> PerformIDCTAndOutput()
    {
        if (_frame == null) throw new InvalidOperationException("Frame not initialized");

        int width = _frame.Width;
        int height = _frame.Height;
        byte[] rgb = new byte[width * height * 3];

        Component compY = _frame.Components[0];
        Component compCb = _frame.Components.Length > 1 ? _frame.Components[1] : null;
        Component compCr = _frame.Components.Length > 2 ? _frame.Components[2] : null;

        int mcuW = _frame.McuWidth;
        int mcuH = _frame.McuHeight;

        bool is420 = compCb != null && compCr != null &&
                     compY.HFactor == 2 && compY.VFactor == 2 &&
                     compCb.HFactor == 1 && compCb.VFactor == 1 &&
                     compCr.HFactor == 1 && compCr.VFactor == 1;

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

        int[] dequantY = new int[64];
        int[] dequantCb = compCb != null ? new int[64] : null;
        int[] dequantCr = compCr != null ? new int[64] : null;

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
                        int qId = compY.QuantTableId;
                        JpegQuantTable qt = null;
                        if (qId >= 0 && qId < _qtablesById.Length)
                        {
                            qt = _qtablesById[qId];
                        }
                        if (qt == null) throw new InvalidDataException("Quantization table not found for Y component");

                        for (int i = 0; i < 64; i++)
                            dequantY[i] = compY.Coeffs[blockIdx * 64 + i] * qt.Values[i];

                        JpegIDCT.BlockIDCT(dequantY, yBuffer[v * compY.HFactor + h]);
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
                            JpegQuantTable qtCb = null;
                            int cbQId = compCb.QuantTableId;
                            if (cbQId >= 0 && cbQId < _qtablesById.Length)
                            {
                                qtCb = _qtablesById[cbQId];
                            }
                            if (qtCb == null) throw new InvalidDataException("Quantization table not found for Cb component");
                            for (int i = 0; i < 64; i++)
                                dequantCb[i] = compCb.Coeffs[cbIdx * 64 + i] * qtCb.Values[i];
                            JpegIDCT.BlockIDCT(dequantCb, cbBuffer[v * compCb.HFactor + h]);
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
                            JpegQuantTable qtCr = null;
                            int crQId = compCr.QuantTableId;
                            if (crQId >= 0 && crQId < _qtablesById.Length)
                            {
                                qtCr = _qtablesById[crQId];
                            }
                            if (qtCr == null) throw new InvalidDataException("Quantization table not found for Cr component");
                            for (int i = 0; i < 64; i++)
                                dequantCr[i] = compCr.Coeffs[crIdx * 64 + i] * qtCr.Values[i];
                            JpegIDCT.BlockIDCT(dequantCr, crBuffer[v * compCr.HFactor + h]);
                        }
                    }
                }

                int pixelBaseX = mcuX * mcuW;
                int pixelBaseY = mcuY * mcuH;

                if (is420 && cbBuffer != null && crBuffer != null)
                {
                    byte[] cbBlock = cbBuffer[0];
                    byte[] crBlock = crBuffer[0];

                    for (int py = 0; py < mcuH; py++)
                    {
                        int globalY = pixelBaseY + py;
                        if (globalY >= height) break;

                        int yBlockY = py >> 3;
                        int yInnerY = py & 7;
                        int cbY = py >> 1;

                        for (int px = 0; px < mcuW; px++)
                        {
                            int globalX = pixelBaseX + px;
                            if (globalX >= width) break;

                            int yBlockX = px >> 3;
                            int yBlockIdx = yBlockY * compY.HFactor + yBlockX;
                            int yInnerX = px & 7;

                            byte Y = yBuffer[yBlockIdx][yInnerY * 8 + yInnerX];

                            int cbX = px >> 1;
                            int cbIdx = cbY * 8 + cbX;

                            byte Cb = cbBlock[cbIdx];
                            byte Cr = crBlock[cbIdx];

                            int yScaled = Y << ColorShift;
                            int r = (yScaled + CrToR[Cr] + ColorHalf) >> ColorShift;
                            int g = (yScaled + CbToG[Cb] + CrToG[Cr] + ColorHalf) >> ColorShift;
                            int b = (yScaled + CbToB[Cb] + ColorHalf) >> ColorShift;

                            int idx = (globalY * width + globalX) * 3;
                            rgb[idx] = (byte)JpegUtils.Clamp(r);
                            rgb[idx + 1] = (byte)JpegUtils.Clamp(g);
                            rgb[idx + 2] = (byte)JpegUtils.Clamp(b);
                        }
                    }
                }
                else
                {
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

                            if (compCb != null && cbBuffer != null)
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

                            if (compCr != null && crBuffer != null)
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

                            int yScaled = Y << ColorShift;
                            int r = (yScaled + CrToR[Cr] + ColorHalf) >> ColorShift;
                            int g = (yScaled + CbToG[Cb] + CrToG[Cr] + ColorHalf) >> ColorShift;
                            int b = (yScaled + CbToB[Cb] + ColorHalf) >> ColorShift;

                            int idx = (globalY * width + globalX) * 3;
                            rgb[idx] = (byte)JpegUtils.Clamp(r);
                            rgb[idx + 1] = (byte)JpegUtils.Clamp(g);
                            rgb[idx + 2] = (byte)JpegUtils.Clamp(b);
                        }
                    }
                }
            }
        }
        return new Image<Rgb24>(width, height, rgb);
    }

    private static int[] BuildColorTable(int k)
    {
        var t = new int[256];
        for (int i = 0; i < 256; i++)
        {
            t[i] = (i - 128) * k;
        }
        return t;
    }

    private int DecodeHuffman(HuffmanDecodingTable ht, JpegBitReader reader)
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
        return ht.Table.Symbols[j2];
    }

    private static int Extend(int v, int t)
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
                // Warning
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
