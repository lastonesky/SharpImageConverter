using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Metadata;

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
    private int _adobeColorTransform = -1;
    private ImageMetadata _metadata = new ImageMetadata();
    private byte[][]? _iccChunks;
    private int _iccChunkCount;
    private int _iccChunksReceived;
    private bool _huffmanRecoveryAttempted;

    public int Width => _frame != null ? _frame.Width : 0;
    public int Height => _frame != null ? _frame.Height : 0;
    
    /// <summary>
    /// 获取当前 JPEG 图像的 EXIF 方向值（1 为默认方向）。
    /// </summary>
    public int ExifOrientation { get; private set; } = 1;

    internal bool EnableHuffmanFastPath { get; set; } = true;

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
        _adobeColorTransform = -1;
        _metadata = new ImageMetadata();
        _iccChunks = null;
        _iccChunkCount = 0;
        _iccChunksReceived = 0;
        _huffmanRecoveryAttempted = false;
        ExifOrientation = 1;

        // Check SOI
        int b1 = _stream.ReadByte();
        int b2 = _stream.ReadByte();
        if (b1 != 0xFF || b2 != JpegMarkers.SOI)
        {
             throw new JpegHeaderException("Not a valid JPEG file (missing SOI)");
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

        FinalizeIccProfile();
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
        while (_stream.Position < _stream.Length)
        {
            int prefix;
            do
            {
                prefix = _stream.ReadByte();
                if (prefix == -1) return;
            } while (prefix != 0xFF);

            int markerInt;
            do
            {
                markerInt = _stream.ReadByte();
                if (markerInt == -1) return;
            } while (markerInt == 0xFF);

            byte marker = (byte)markerInt;
            if (marker == 0x00) continue;

            if (marker == JpegMarkers.SOS || marker == JpegMarkers.EOI)
            {
                _stream.Seek(-2, SeekOrigin.Current);
                return;
            }

            if (marker == JpegMarkers.SOI) continue;
            if (marker >= JpegMarkers.RST0 && marker <= JpegMarkers.RST7) continue;
            if (marker == JpegMarkers.TEM) continue;

            ParseMarker(marker);
        }
    }

    private void ParseMarker(byte marker)
    {
        if (marker == 0x00) return;

        ushort length = ReadUShort();
        int contentLen = length >= 2 ? length - 2 : 0;

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
                if (contentLen >= 2)
                {
                    _restartInterval = ReadUShort();
                    contentLen -= 2;
                }
                if (contentLen > 0)
                {
                    _stream.Seek(contentLen, SeekOrigin.Current);
                }
                break;

            case JpegMarkers.APP1:
                if (contentLen > 0)
                {
                    byte[] buf = new byte[contentLen];
                    _stream.ReadExactly(buf, 0, contentLen);
                    TryParseExif(buf);
                }
                break;

            case JpegMarkers.APP2:
                if (contentLen > 0)
                {
                    byte[] buf = new byte[contentLen];
                    _stream.ReadExactly(buf, 0, contentLen);
                    TryParseIccProfileSegment(buf);
                }
                break;

            case JpegMarkers.APP14:
                if (contentLen > 0)
                {
                    byte[] buf = new byte[contentLen];
                    _stream.ReadExactly(buf, 0, contentLen);
                    if (buf.Length >= 12 &&
                        buf[0] == (byte)'A' && buf[1] == (byte)'d' && buf[2] == (byte)'o' && buf[3] == (byte)'b' && buf[4] == (byte)'e')
                    {
                        _adobeColorTransform = buf[11];
                    }
                }
                break;

            case JpegMarkers.APP0:
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
                if (contentLen > 0)
                {
                    _stream.Seek(contentLen, SeekOrigin.Current);
                }
                break;

            case JpegMarkers.APP15:
            case JpegMarkers.COM:
                if (contentLen > 0)
                {
                    _stream.Seek(contentLen, SeekOrigin.Current);
                }
                break;

            default:
                if (contentLen > 0)
                {
                    _stream.Seek(contentLen, SeekOrigin.Current);
                }
                break;
        }
    }

    private void TryParseIccProfileSegment(byte[] app2)
    {
        if (app2.Length < 14) return;
        if (app2[0] != (byte)'I' || app2[1] != (byte)'C' || app2[2] != (byte)'C' || app2[3] != (byte)'_' ||
            app2[4] != (byte)'P' || app2[5] != (byte)'R' || app2[6] != (byte)'O' || app2[7] != (byte)'F' ||
            app2[8] != (byte)'I' || app2[9] != (byte)'L' || app2[10] != (byte)'E' || app2[11] != 0x00)
        {
            return;
        }

        int seqNo = app2[12];
        int count = app2[13];
        if (seqNo <= 0 || count <= 0) return;

        if (_iccChunks == null || _iccChunkCount != count)
        {
            _iccChunkCount = count;
            _iccChunks = new byte[count][];
            _iccChunksReceived = 0;
        }

        int idx = seqNo - 1;
        if (idx < 0 || idx >= _iccChunks!.Length) return;
        if (_iccChunks[idx] != null) return;

        int payloadLen = app2.Length - 14;
        if (payloadLen <= 0) return;
        byte[] payload = new byte[payloadLen];
        Buffer.BlockCopy(app2, 14, payload, 0, payloadLen);
        _iccChunks[idx] = payload;
        _iccChunksReceived++;
    }

    private void FinalizeIccProfile()
    {
        if (_iccChunks == null || _iccChunkCount <= 0) return;
        if (_iccChunksReceived != _iccChunkCount) return;

        int total = 0;
        for (int i = 0; i < _iccChunks.Length; i++)
        {
            if (_iccChunks[i] == null) return;
            total += _iccChunks[i].Length;
        }

        byte[] profile = new byte[total];
        int offset = 0;
        for (int i = 0; i < _iccChunks.Length; i++)
        {
            byte[] chunk = _iccChunks[i];
            Buffer.BlockCopy(chunk, 0, profile, offset, chunk.Length);
            offset += chunk.Length;
        }

        _metadata.IccProfile = profile;
        _metadata.IccProfileKind = DetectIccProfileKind(profile);
    }

    private static IccProfileKind DetectIccProfileKind(byte[] profile)
    {
        static bool ContainsAscii(byte[] haystack, string needle)
        {
            if (haystack.Length == 0 || string.IsNullOrEmpty(needle)) return false;
            byte[] n = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i <= haystack.Length - n.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < n.Length; j++)
                {
                    if (haystack[i + j] != n[j]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        if (ContainsAscii(profile, "sRGB")) return IccProfileKind.SRgb;
        if (ContainsAscii(profile, "Display P3") || ContainsAscii(profile, "DisplayP3")) return IccProfileKind.DisplayP3;
        if (ContainsAscii(profile, "Adobe RGB") || ContainsAscii(profile, "AdobeRGB")) return IccProfileKind.AdobeRgb;
        if (ContainsAscii(profile, "ProPhoto")) return IccProfileKind.ProPhotoRgb;
        return IccProfileKind.Unknown;
    }

    private void TryParseExif(byte[] app1)
    {
        if (app1.Length < 8) return;
        if (!(app1[0] == (byte)'E' && app1[1] == (byte)'x' && app1[2] == (byte)'i' && app1[3] == (byte)'f' && app1[4] == 0 && app1[5] == 0))
            return;

        _metadata.ExifRaw = app1;
        _metadata.Exif ??= new ExifMetadata();

        int tiffBase = 6;
        if (app1.Length < tiffBase + 8) return;
        bool littleEndian;
        if (app1[tiffBase + 0] == (byte)'I' && app1[tiffBase + 1] == (byte)'I') littleEndian = true;
        else if (app1[tiffBase + 0] == (byte)'M' && app1[tiffBase + 1] == (byte)'M') littleEndian = false;
        else return;

        ushort ReadU16(int offset)
        {
            if (offset < 0 || offset + 2 > app1.Length) return 0;
            return littleEndian ? (ushort)(app1[offset] | (app1[offset + 1] << 8)) : (ushort)((app1[offset] << 8) | app1[offset + 1]);
        }

        uint ReadU32(int offset)
        {
            if (offset < 0 || offset + 4 > app1.Length) return 0;
            return littleEndian
                ? (uint)(app1[offset] | (app1[offset + 1] << 8) | (app1[offset + 2] << 16) | (app1[offset + 3] << 24))
                : (uint)((app1[offset] << 24) | (app1[offset + 1] << 16) | (app1[offset + 2] << 8) | app1[offset + 3]);
        }

        int TypeSize(ushort type) => type switch
        {
            1 => 1,
            2 => 1,
            3 => 2,
            4 => 4,
            5 => 8,
            7 => 1,
            9 => 4,
            10 => 8,
            _ => 0
        };

        int ValueOffset(int entryOffset, ushort type, uint count)
        {
            int size = TypeSize(type);
            long bytes = (long)size * count;
            if (bytes <= 4) return entryOffset + 8;
            return tiffBase + (int)ReadU32(entryOffset + 8);
        }

        string? ReadAscii(int offset, uint count)
        {
            if (offset < 0 || offset >= app1.Length) return null;
            int len = (int)Math.Min(count, (uint)(app1.Length - offset));
            int end = 0;
            for (int i = 0; i < len; i++)
            {
                if (app1[offset + i] == 0) { end = i; break; }
                end = i + 1;
            }
            if (end <= 0) return null;
            return System.Text.Encoding.ASCII.GetString(app1, offset, end);
        }

        double? ReadRationalAsDouble(int offset)
        {
            uint num = ReadU32(offset);
            uint den = ReadU32(offset + 4);
            if (den == 0) return null;
            return (double)num / den;
        }

        void ParseExifSubIfd(int ifdOffset)
        {
            if (ifdOffset < 0 || ifdOffset + 2 > app1.Length) return;
            ushort numEntries = ReadU16(ifdOffset);
            int entryBase = ifdOffset + 2;
            for (int i = 0; i < numEntries; i++)
            {
                int e = entryBase + i * 12;
                if (e + 12 > app1.Length) break;
                ushort tag = ReadU16(e + 0);
                ushort type = ReadU16(e + 2);
                uint count = ReadU32(e + 4);
                int vOff = ValueOffset(e, type, count);
                if (tag == 0x9003 && type == 2)
                {
                    _metadata.Exif!.DateTimeOriginal = ReadAscii(vOff, count);
                }
            }
        }

        void ParseGpsIfd(int ifdOffset)
        {
            if (ifdOffset < 0 || ifdOffset + 2 > app1.Length) return;
            ushort numEntries = ReadU16(ifdOffset);
            int entryBase = ifdOffset + 2;
            string? latRef = null;
            string? lonRef = null;
            double? lat = null;
            double? lon = null;
            for (int i = 0; i < numEntries; i++)
            {
                int e = entryBase + i * 12;
                if (e + 12 > app1.Length) break;
                ushort tag = ReadU16(e + 0);
                ushort type = ReadU16(e + 2);
                uint count = ReadU32(e + 4);
                int vOff = ValueOffset(e, type, count);
                switch (tag)
                {
                    case 0x0001:
                        if (type == 2) latRef = ReadAscii(vOff, count);
                        break;
                    case 0x0003:
                        if (type == 2) lonRef = ReadAscii(vOff, count);
                        break;
                    case 0x0002:
                        if (type == 5 && count == 3)
                        {
                            double? d = ReadRationalAsDouble(vOff + 0);
                            double? m = ReadRationalAsDouble(vOff + 8);
                            double? s = ReadRationalAsDouble(vOff + 16);
                            if (d.HasValue && m.HasValue && s.HasValue) lat = d.Value + m.Value / 60d + s.Value / 3600d;
                        }
                        break;
                    case 0x0004:
                        if (type == 5 && count == 3)
                        {
                            double? d = ReadRationalAsDouble(vOff + 0);
                            double? m = ReadRationalAsDouble(vOff + 8);
                            double? s = ReadRationalAsDouble(vOff + 16);
                            if (d.HasValue && m.HasValue && s.HasValue) lon = d.Value + m.Value / 60d + s.Value / 3600d;
                        }
                        break;
                }
            }

            if (lat.HasValue)
            {
                if (string.Equals(latRef, "S", StringComparison.OrdinalIgnoreCase)) lat = -lat.Value;
                _metadata.Exif!.GpsLatitude = lat;
            }
            if (lon.HasValue)
            {
                if (string.Equals(lonRef, "W", StringComparison.OrdinalIgnoreCase)) lon = -lon.Value;
                _metadata.Exif!.GpsLongitude = lon;
            }
        }

        ushort magic = ReadU16(tiffBase + 2);
        if (magic != 42) return;
        uint ifd0Offset = ReadU32(tiffBase + 4);
        int ifd0 = tiffBase + (int)ifd0Offset;
        if (ifd0 < 0 || ifd0 + 2 > app1.Length) return;

        ushort numEntries0 = ReadU16(ifd0);
        int entryBase0 = ifd0 + 2;
        int? exifIfdOffset = null;
        int? gpsIfdOffset = null;
        for (int i = 0; i < numEntries0; i++)
        {
            int e = entryBase0 + i * 12;
            if (e + 12 > app1.Length) break;
            ushort tag = ReadU16(e + 0);
            ushort type = ReadU16(e + 2);
            uint count = ReadU32(e + 4);
            int vOff = ValueOffset(e, type, count);
            switch (tag)
            {
                case 0x010F:
                    if (type == 2) _metadata.Exif.Make = ReadAscii(vOff, count);
                    break;
                case 0x0110:
                    if (type == 2) _metadata.Exif.Model = ReadAscii(vOff, count);
                    break;
                case 0x0132:
                    if (type == 2) _metadata.Exif.DateTime = ReadAscii(vOff, count);
                    break;
                case 0x0112:
                    if (type == 3 && count >= 1)
                    {
                        int o = ReadU16(vOff);
                        if (o >= 1 && o <= 8)
                        {
                            ExifOrientation = o;
                            _metadata.Orientation = o;
                        }
                    }
                    break;
                case 0x8769:
                    if (type == 4 && count == 1) exifIfdOffset = tiffBase + (int)ReadU32(vOff);
                    break;
                case 0x8825:
                    if (type == 4 && count == 1) gpsIfdOffset = tiffBase + (int)ReadU32(vOff);
                    break;
            }
        }

        if (exifIfdOffset.HasValue) ParseExifSubIfd(exifIfdOffset.Value);
        if (gpsIfdOffset.HasValue) ParseGpsIfd(gpsIfdOffset.Value);
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

        if (frame.ComponentsCount < 1 || frame.ComponentsCount > 4)
        {
            throw new NotSupportedException("Only JPEG images with 1 to 4 components are supported.");
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
        if (_frame == null) throw new JpegHeaderException("Frame not parsed before scan");

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
        bool missingTables = false;
        bool needsDc = scan.StartSpectralSelection == 0 && scan.SuccessiveApproximationBitHigh == 0;
        bool needsAc = scan.StartSpectralSelection != 0;
        for (int i = 0; i < scan.ComponentsCount; i++)
        {
            if (needsDc && GetHuffmanTable(0, scan.Components[i].DcTableId) == null)
            {
                missingTables = true;
            }
            if (needsAc && GetHuffmanTable(1, scan.Components[i].AcTableId) == null)
            {
                missingTables = true;
            }
        }

        if (missingTables)
        {
            int b = reader.ReadBit();
            if (b == -1 && reader.HitMarker)
            {
                if (reader.Marker != 0) _stream.Seek(-2, SeekOrigin.Current);
                return;
            }
            reader.AlignToByte();
            DrainToNextMarker();
            return;
        }

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
            if (!reader.HitMarker)
            {
                string info = $"components={scan.ComponentsCount} Ss={scan.StartSpectralSelection} Se={scan.EndSpectralSelection} Ah={scan.SuccessiveApproximationBitHigh} Al={scan.SuccessiveApproximationBitLow}";
                for (int i = 0; i < scan.ComponentsCount; i++)
                {
                    var c = scan.Components[i];
                    info += $" [cid={c.ComponentId} dc={c.DcTableId} ac={c.AcTableId}]";
                }
                throw new JpegScanException($"JPEG scan decode failed ({info}).", ex);
            }
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
        else
        {
            reader.AlignToByte();
            DrainToNextMarker();
        }
    }

    private void DrainToNextMarker()
    {
        while (_stream.Position < _stream.Length)
        {
            int b = _stream.ReadByte();
            if (b == -1) return;
            if (b != 0xFF) continue;

            int markerInt = _stream.ReadByte();
            if (markerInt == -1) return;
            while (markerInt == 0xFF)
            {
                markerInt = _stream.ReadByte();
                if (markerInt == -1) return;
            }

            if (markerInt == 0x00) continue;
            _stream.Seek(-2, SeekOrigin.Current);
            return;
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

        bool needsDc = Ss == 0 && Ah == 0;
        bool needsAc = Ss != 0;

        HuffmanDecodingTable[] dcTables = null;
        HuffmanDecodingTable[] acTables = null;

        if (compsInScan > 0)
        {
            if (needsDc)
            {
                dcTables = new HuffmanDecodingTable[compsInScan];
                for (int i = 0; i < compsInScan; i++)
                {
                    var sc = scan.Components[i];
                    var ht = GetHuffmanTable(0, sc.DcTableId);
                    if (ht == null) throw new JpegHeaderException("Missing DC Huffman table");
                    dcTables[i] = ht;
                }
            }

            if (needsAc)
            {
                acTables = new HuffmanDecodingTable[compsInScan];
                for (int i = 0; i < compsInScan; i++)
                {
                    var sc = scan.Components[i];
                    var ht = GetHuffmanTable(1, sc.AcTableId);
                    if (ht == null) throw new JpegHeaderException("Missing AC Huffman table");
                    acTables[i] = ht;
                }
            }
        }

        if (compsInScan > 1)
        {
            for (int mcuY = 0; mcuY < _frame.McuRows; mcuY++)
            {
                for (int mcuX = 0; mcuX < _frame.McuCols; mcuX++)
                {
                    CheckRestart(ref restartsLeft, reader);

                    for (int ci = 0; ci < compsInScan; ci++)
                    {
                        var sc = scan.Components[ci];
                        var comp = sc.ComponentId >= 0 && sc.ComponentId < _componentsById.Length ? _componentsById[sc.ComponentId] : null;
                        if (comp == null) throw new JpegScanException("Component not found in frame");
                        int baseX = mcuX * comp.HFactor;
                        int baseY = mcuY * comp.VFactor;
                        var dcTable = dcTables != null ? dcTables[ci] : null;

                        for (int v = 0; v < comp.VFactor; v++)
                        {
                            for (int h = 0; h < comp.HFactor; h++)
                            {
                                int blockIndex = (baseY + v) * comp.WidthInBlocks + (baseX + h);
                                DecodeDCProgressive(reader, comp.Coeffs, blockIndex * 64, dcTable, Ah, Al, ref comp.DcPred);
                            }
                        }
                    }
                }
            }
        }
        else if (compsInScan == 1)
        {
            var sc = scan.Components[0];
            var comp = sc.ComponentId >= 0 && sc.ComponentId < _componentsById.Length ? _componentsById[sc.ComponentId] : null;
            if (comp == null) throw new JpegScanException("Component not found in frame");
            var dcTable = dcTables != null ? dcTables[0] : null;
            var acTable = acTables != null ? acTables[0] : null;

            for (int blockY = 0; blockY < comp.HeightInBlocks; blockY++)
            {
                for (int blockX = 0; blockX < comp.WidthInBlocks; blockX++)
                {
                    CheckRestart(ref restartsLeft, reader);

                    int blockIndex = blockY * comp.WidthInBlocks + blockX;
                    if (Ss == 0)
                    {
                        DecodeDCProgressive(reader, comp.Coeffs, blockIndex * 64, dcTable, Ah, Al, ref comp.DcPred);
                    }
                    else
                    {
                        DecodeACProgressive(reader, comp.Coeffs, blockIndex * 64, acTable, Ss, Se, Ah, Al, ref eobRun);
                    }
                }
            }
        }
    }

    private void DecodeDCProgressive(JpegBitReader reader, int[] coeffs, int offset, HuffmanDecodingTable dcTable, int Ah, int Al, ref int dcPred)
    {
        if (Ah == 0)
        {
            if (dcTable == null) throw new JpegHeaderException("Missing DC Huffman table");
            int s = DecodeHuffman(dcTable, reader);
            if (s < 0) throw new JpegScanException("Huffman decode error (DC)");

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

    private void DecodeACProgressive(JpegBitReader reader, int[] coeffs, int offset, HuffmanDecodingTable acTable, int Ss, int Se, int Ah, int Al, ref int eobRun)
    {
        if (acTable == null) throw new JpegHeaderException("Missing AC Huffman table");

        if (Ah == 0)
        {
            if (eobRun > 0)
            {
                eobRun--;
                return;
            }

            for (int k = Ss; k <= Se; k++)
            {
                int s = DecodeHuffman(acTable, reader);
                if (s < 0) throw new JpegScanException("Huffman decode error (AC)");

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

            while (k <= Se)
            {
                int s = DecodeHuffman(acTable, reader);
                if (s < 0) throw new JpegScanException("Huffman decode error (AC Refinement)");

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

        int compsInScan = scan.ComponentsCount;
        var dcTables = new HuffmanDecodingTable[compsInScan];
        var acTables = new HuffmanDecodingTable[compsInScan];

        for (int i = 0; i < compsInScan; i++)
        {
            var sc = scan.Components[i];
            var dcTable = GetHuffmanTable(0, sc.DcTableId);
            if (dcTable == null) throw new JpegHeaderException("Missing DC Huffman table");
            dcTables[i] = dcTable;

            var acTable = GetHuffmanTable(1, sc.AcTableId);
            if (acTable == null) throw new JpegHeaderException("Missing AC Huffman table");
            acTables[i] = acTable;
        }

        for (int mcuY = 0; mcuY < _frame.McuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < _frame.McuCols; mcuX++)
            {
                CheckRestart(ref restartsLeft, reader);

                for (int ci = 0; ci < compsInScan; ci++)
                {
                    var sc = scan.Components[ci];
                    var comp = sc.ComponentId >= 0 && sc.ComponentId < _componentsById.Length ? _componentsById[sc.ComponentId] : null;
                    if (comp == null) throw new JpegScanException("Component not found in frame");
                    int baseX = mcuX * comp.HFactor;
                    int baseY = mcuY * comp.VFactor;

                    var dcTable = dcTables[ci];
                    var acTable = acTables[ci];

                    for (int v = 0; v < comp.VFactor; v++)
                    {
                        for (int h = 0; h < comp.HFactor; h++)
                        {
                            int blockIndex = (baseY + v) * comp.WidthInBlocks + (baseX + h);
                            DecodeDCProgressive(reader, comp.Coeffs, blockIndex * 64, dcTable, 0, 0, ref comp.DcPred);

                            int dummyEob = 0;
                            DecodeACProgressive(reader, comp.Coeffs, blockIndex * 64, acTable, 1, 63, 0, 0, ref dummyEob);
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
        Component compK = _frame.Components.Length > 3 ? _frame.Components[3] : null;

        int mcuW = _frame.McuWidth;
        int mcuH = _frame.McuHeight;

        bool isGrayscale = compCb == null && compCr == null && compK == null;
        bool is420 = compK == null && compCb != null && compCr != null &&
                     compY.HFactor == 2 && compY.VFactor == 2 &&
                     compCb.HFactor == 1 && compCb.VFactor == 1 &&
                     compCr.HFactor == 1 && compCr.VFactor == 1;

        int[] yBlockIndexPerPx = null;
        int[] yInnerXPerPx = null;
        int[] cbXPerPx = null;
        if (is420)
        {
            yBlockIndexPerPx = new int[mcuW];
            yInnerXPerPx = new int[mcuW];
            cbXPerPx = new int[mcuW];
            for (int px = 0; px < mcuW; px++)
            {
                yBlockIndexPerPx[px] = px >> 3;
                yInnerXPerPx[px] = px & 7;
                cbXPerPx[px] = px >> 1;
            }
        }

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

        byte[][] kBuffer = null;
        if (compK != null)
        {
            kBuffer = new byte[compK.HFactor * compK.VFactor][];
            for (int i = 0; i < kBuffer.Length; i++) kBuffer[i] = new byte[64];
        }

        int[] dequantY = new int[64];
        int[] dequantCb = compCb != null ? new int[64] : null;
        int[] dequantCr = compCr != null ? new int[64] : null;
        int[] dequantK = compK != null ? new int[64] : null;

        for (int mcuY = 0; mcuY < _frame.McuRows; mcuY++)
        {
            for (int mcuX = 0; mcuX < _frame.McuCols; mcuX++)
            {
                // Y Component
                {
                    int blockBaseX = mcuX * compY.HFactor;
                    int blockBaseY = mcuY * compY.VFactor;
                    int blocksPerRow = compY.WidthInBlocks;
                    JpegQuantTable qtY = _qtablesById[compY.QuantTableId];
                    if (qtY == null) throw new JpegHeaderException("Quantization table not found for Y");
                    ushort[] qtYValues = qtY.Values;
                    for (int v = 0; v < compY.VFactor; v++)
                    {
                        for (int h = 0; h < compY.HFactor; h++)
                        {
                            int blockIdx = (blockBaseY + v) * blocksPerRow + (blockBaseX + h);
                            int coeffBase = blockIdx * 64;
                            int[] src = compY.Coeffs;
                            for (int i = 0; i < 64; i++) dequantY[i] = src[coeffBase + i] * qtYValues[i];
                            JpegIDCT.BlockIDCT(dequantY, yBuffer[v * compY.HFactor + h]);
                        }
                    }
                }

                // Cb Component
                if (compCb != null)
                {
                    int blockBaseX = mcuX * compCb.HFactor;
                    int blockBaseY = mcuY * compCb.VFactor;
                    int blocksPerRow = compCb.WidthInBlocks;
                    JpegQuantTable qtCb = _qtablesById[compCb.QuantTableId];
                    if (qtCb == null) throw new JpegHeaderException("Quantization table not found for Cb");
                    ushort[] qtCbValues = qtCb.Values;
                    int[] cbCoeffs = compCb.Coeffs;
                    for (int v = 0; v < compCb.VFactor; v++)
                    {
                        for (int h = 0; h < compCb.HFactor; h++)
                        {
                            int blockIdx = (blockBaseY + v) * blocksPerRow + (blockBaseX + h);
                            int coeffBase = blockIdx * 64;
                            for (int i = 0; i < 64; i++) dequantCb[i] = cbCoeffs[coeffBase + i] * qtCbValues[i];
                            JpegIDCT.BlockIDCT(dequantCb, cbBuffer[v * compCb.HFactor + h]);
                        }
                    }
                }

                // Cr Component
                if (compCr != null)
                {
                    int blockBaseX = mcuX * compCr.HFactor;
                    int blockBaseY = mcuY * compCr.VFactor;
                    int blocksPerRow = compCr.WidthInBlocks;
                    JpegQuantTable qtCr = _qtablesById[compCr.QuantTableId];
                    if (qtCr == null) throw new JpegHeaderException("Quantization table not found for Cr");
                    ushort[] qtCrValues = qtCr.Values;
                    int[] crCoeffs = compCr.Coeffs;
                    for (int v = 0; v < compCr.VFactor; v++)
                    {
                        for (int h = 0; h < compCr.HFactor; h++)
                        {
                            int blockIdx = (blockBaseY + v) * blocksPerRow + (blockBaseX + h);
                            int coeffBase = blockIdx * 64;
                            for (int i = 0; i < 64; i++) dequantCr[i] = crCoeffs[coeffBase + i] * qtCrValues[i];
                            JpegIDCT.BlockIDCT(dequantCr, crBuffer[v * compCr.HFactor + h]);
                        }
                    }
                }

                // K Component
                if (compK != null)
                {
                    int blockBaseX = mcuX * compK.HFactor;
                    int blockBaseY = mcuY * compK.VFactor;
                    int blocksPerRow = compK.WidthInBlocks;
                    JpegQuantTable qtK = _qtablesById[compK.QuantTableId];
                    if (qtK == null) throw new JpegHeaderException("Quantization table not found for K");
                    ushort[] qtKValues = qtK.Values;
                    int[] kCoeffs = compK.Coeffs;
                    for (int v = 0; v < compK.VFactor; v++)
                    {
                        for (int h = 0; h < compK.HFactor; h++)
                        {
                            int blockIdx = (blockBaseY + v) * blocksPerRow + (blockBaseX + h);
                            int coeffBase = blockIdx * 64;
                            for (int i = 0; i < 64; i++) dequantK[i] = kCoeffs[coeffBase + i] * qtKValues[i];
                            JpegIDCT.BlockIDCT(dequantK, kBuffer[v * compK.HFactor + h]);
                        }
                    }
                }

                int pixelBaseX = mcuX * mcuW;
                int pixelBaseY = mcuY * mcuH;

                if (isGrayscale)
                {
                    for (int py = 0; py < mcuH; py++)
                    {
                        int globalY = pixelBaseY + py;
                        if (globalY >= height) break;

                        int yBlockY = py >> 3;
                        int yInnerY = py & 7;
                        int yBlockRow = yBlockY * compY.HFactor;
                        int maxX = width - pixelBaseX;
                        if (maxX <= 0) break;
                        if (maxX > mcuW) maxX = mcuW;

                        int rowBase = (globalY * width + pixelBaseX) * 3;
                        int idx = rowBase;

                        for (int px = 0; px < maxX; px++)
                        {
                            int yBlockX = px >> 3;
                            int yBlockIdx = yBlockRow + yBlockX;
                            int yInnerX = px & 7;

                            byte Y = yBuffer[yBlockIdx][yInnerY * 8 + yInnerX];

                            rgb[idx] = Y;
                            rgb[idx + 1] = Y;
                            rgb[idx + 2] = Y;

                            idx += 3;
                        }
                    }
                }
                else if (is420 && cbBuffer != null && crBuffer != null)
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

                        int maxX = width - pixelBaseX;
                        if (maxX <= 0) break;
                        if (maxX > mcuW) maxX = mcuW;

                        int rowBase = (globalY * width + pixelBaseX) * 3;
                        int idx = rowBase;
                        int yBlockRowBase = yBlockY * compY.HFactor;

                        for (int px = 0; px < maxX; px++)
                        {
                            int yBlockIdx = yBlockRowBase + yBlockIndexPerPx[px];
                            int yInnerX = yInnerXPerPx[px];

                            byte Y = yBuffer[yBlockIdx][yInnerY * 8 + yInnerX];

                            int cbX = cbXPerPx[px];
                            int cbIdx = cbY * 8 + cbX;
                            byte Cb = cbBlock[cbIdx];
                            byte Cr = crBlock[cbIdx];

                            int yScaled = Y << ColorShift;
                            int r = (yScaled + CrToR[Cr] + ColorHalf) >> ColorShift;
                            int g = (yScaled + CbToG[Cb] + CrToG[Cr] + ColorHalf) >> ColorShift;
                            int b = (yScaled + CbToB[Cb] + ColorHalf) >> ColorShift;

                            rgb[idx] = (byte)JpegUtils.Clamp(r);
                            rgb[idx + 1] = (byte)JpegUtils.Clamp(g);
                            rgb[idx + 2] = (byte)JpegUtils.Clamp(b);
                            idx += 3;
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

                            int yBlockX = px >> 3;
                            int yBlockY = py >> 3;
                            int yBlockIdx = yBlockY * compY.HFactor + yBlockX;
                            int yInnerX = px & 7;
                            int yInnerY = py & 7;

                            byte val0 = yBuffer[yBlockIdx][yInnerY * 8 + yInnerX];
                            byte val1 = 128;
                            byte val2 = 128;
                            byte val3 = 255;

                            if (compCb != null && cbBuffer != null)
                            {
                                int cbX = (px * compCb.HFactor) / compY.HFactor;
                                int cbY = (py * compCb.VFactor) / compY.VFactor;
                                int cbBlockIdx = (cbY / 8) * compCb.HFactor + (cbX / 8);
                                if (cbBlockIdx < cbBuffer.Length)
                                    val1 = cbBuffer[cbBlockIdx][(cbY % 8) * 8 + (cbX % 8)];
                            }

                            if (compCr != null && crBuffer != null)
                            {
                                int crX = (px * compCr.HFactor) / compY.HFactor;
                                int crY = (py * compCr.VFactor) / compY.VFactor;
                                int crBlockIdx = (crY / 8) * compCr.HFactor + (crX / 8);
                                if (crBlockIdx < crBuffer.Length)
                                    val2 = crBuffer[crBlockIdx][(crY % 8) * 8 + (crX % 8)];
                            }

                            if (compK != null && kBuffer != null)
                            {
                                int kX = (px * compK.HFactor) / compY.HFactor;
                                int kY = (py * compK.VFactor) / compY.VFactor;
                                int kBlockIdx = (kY / 8) * compK.HFactor + (kX / 8);
                                if (kBlockIdx < kBuffer.Length)
                                    val3 = kBuffer[kBlockIdx][(kY % 8) * 8 + (kX % 8)];
                            }

                            int r, g, b;

                            if (compK != null)
                            {
                                // 4 Components
                                if (_adobeColorTransform == 2) // YCCK
                                {
                                    // Y = val0, Cb = val1, Cr = val2, K = val3
                                    int yScaled = val0 << ColorShift;
                                    r = (yScaled + CrToR[val2] + ColorHalf) >> ColorShift;
                                    g = (yScaled + CbToG[val1] + CrToG[val2] + ColorHalf) >> ColorShift;
                                    b = (yScaled + CbToB[val1] + ColorHalf) >> ColorShift;

                                    r = JpegUtils.Clamp(r) * val3 / 255;
                                    g = JpegUtils.Clamp(g) * val3 / 255;
                                    b = JpegUtils.Clamp(b) * val3 / 255;
                                }
                                else // CMYK (Inverted)
                                {
                                    // C = val0, M = val1, Y = val2, K = val3
                                    // Inverted CMYK: 255 = White (0 ink?), No.
                                    // Photoshop CMYK is 0=White (inverted). So 255 is Black.
                                    // Actually, if it's inverted CMYK, then C,M,Y,K are 0..255.
                                    // Formula:
                                    // R = (C * K) / 255
                                    // G = (M * K) / 255
                                    // B = (Y * K) / 255
                                    // Assuming inputs are "Inverted CMY" and "Inverted K".
                                    
                                    r = val0 * val3 / 255;
                                    g = val1 * val3 / 255;
                                    b = val2 * val3 / 255;
                                }
                            }
                            else
                            {
                                // 1 or 3 Components (Y/RGB or YCbCr)
                                int yScaled = val0 << ColorShift;
                                r = (yScaled + CrToR[val2] + ColorHalf) >> ColorShift;
                                g = (yScaled + CbToG[val1] + CrToG[val2] + ColorHalf) >> ColorShift;
                                b = (yScaled + CbToB[val1] + ColorHalf) >> ColorShift;
                            }

                            int idx = (globalY * width + globalX) * 3;
                            rgb[idx] = (byte)JpegUtils.Clamp(r);
                            rgb[idx + 1] = (byte)JpegUtils.Clamp(g);
                            rgb[idx + 2] = (byte)JpegUtils.Clamp(b);
                        }
                    }
                }
            }
        }
        return new Image<Rgb24>(width, height, rgb, _metadata);
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
        if (EnableHuffmanFastPath)
        {
            int first8 = reader.PeekBits(8);
            if (first8 != -1)
            {
                int len = ht.FastBits[first8];
                if (len != 0)
                {
                    reader.SkipBits(len);
                    return ht.FastSymbols[first8];
                }
            }
        }

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

    private HuffmanDecodingTable? GetHuffmanTable(int tableClass, int tableId)
    {
        if ((uint)tableClass >= (uint)_htablesByClassAndId.GetLength(0)) return null;
        if ((uint)tableId >= (uint)_htablesByClassAndId.GetLength(1)) return null;

        var ht = _htablesByClassAndId[tableClass, tableId];
        if (ht != null) return ht;

        if (!_huffmanRecoveryAttempted && _stream != null && _stream.CanSeek)
        {
            _huffmanRecoveryAttempted = true;
            TryRecoverHuffmanTables(_stream.Position);
            ht = _htablesByClassAndId[tableClass, tableId];
            if (ht != null) return ht;
        }

        for (int i = 0; i < _htables.Count; i++)
        {
            var t = _htables[i];
            if (t.Table.TableClass == tableClass && t.Table.TableId == tableId)
            {
                _htablesByClassAndId[tableClass, tableId] = t;
                return t;
            }
        }
        return null;
    }

    private void TryRecoverHuffmanTables(long stopPos)
    {
        long cur = _stream.Position;
        try
        {
            if (stopPos < 2) stopPos = 2;
            _stream.Seek(2, SeekOrigin.Begin);
            while (_stream.Position < stopPos && _stream.Position < _stream.Length)
            {
                int prefix;
                do
                {
                    prefix = _stream.ReadByte();
                    if (prefix == -1) return;
                } while (prefix != 0xFF);

                int markerInt;
                do
                {
                    markerInt = _stream.ReadByte();
                    if (markerInt == -1) return;
                } while (markerInt == 0xFF);

                byte marker = (byte)markerInt;
                if (marker == 0x00) continue;
                if (marker == JpegMarkers.SOI) continue;
                if (marker >= JpegMarkers.RST0 && marker <= JpegMarkers.RST7) continue;
                if (marker == JpegMarkers.TEM) continue;
                if (marker == JpegMarkers.SOS || marker == JpegMarkers.EOI) break;

                ushort length = ReadUShort();
                if (marker == JpegMarkers.DHT)
                {
                    ParseDHT(length);
                }
                else
                {
                    int contentLen = length >= 2 ? length - 2 : 0;
                    if (contentLen > 0) _stream.Seek(contentLen, SeekOrigin.Current);
                }
            }
        }
        finally
        {
            _stream.Position = cur;
        }
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
