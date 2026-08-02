using SharpImageConverter.Metadata;
using System.Buffers;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// 与数据源无关的 JPEG 帧解码状态机：解析 DQT/DHT/DRI/SOF/SOS，执行熵解码（基线 + 渐进式），
/// 并重建图像。内存解码（<c>Parser</c>）与流式解码（<c>StreamingParser</c>）共享此状态，
/// 二者只负责提供 marker/segment 与熵数据来源。
/// </summary>
internal sealed class JpegFrameState
{
    private readonly QuantizationTable[] quantTables = new QuantizationTable[4];
    private readonly HuffmanDecodingTable[] dcTables = new HuffmanDecodingTable[4];
    private readonly HuffmanDecodingTable[] acTables = new HuffmanDecodingTable[4];
    private readonly bool[] quantTableDefined = new bool[4];
    private readonly bool[] dcTableDefined = new bool[4];
    private readonly bool[] acTableDefined = new bool[4];

    private FrameHeader frame;
    private bool hasFrame;
    private bool isProgressive;
    private ushort restartInterval;
    private ComponentState[] components = Array.Empty<ComponentState>();
    private bool hasJfif;
    private bool hasAdobe;
    private byte adobeTransform;
    private IccProfileCollector? iccCollector;
    private byte[]? exifRaw;
    private int exifOrientation = 1;
    private readonly bool useFloatingPointIdct;

    /// <summary>原始 EXIF APP1 数据（仅第一个）。</summary>
    public byte[]? ExifRaw => exifRaw;

    /// <summary>EXIF Orientation 标签（1-8，缺省 1）。</summary>
    public int ExifOrientation => exifOrientation;

    public JpegFrameState(bool useFloatingPointIdct)
    {
        this.useFloatingPointIdct = useFloatingPointIdct;
        for (int i = 0; i < 4; i++)
        {
            quantTables[i] = new QuantizationTable();
            dcTables[i] = new HuffmanDecodingTable();
            acTables[i] = new HuffmanDecodingTable();
        }
    }

    // ---------- 段解析 ----------

    public void ParseApp0(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 5)
        {
            return;
        }

        if (segment[0] == (byte)'J' &&
            segment[1] == (byte)'F' &&
            segment[2] == (byte)'I' &&
            segment[3] == (byte)'F' &&
            segment[4] == 0)
        {
            hasJfif = true;
        }
    }

    public void ParseApp1(ReadOnlySpan<byte> segment)
    {
        JpegDecoder.TryStoreExifApp1(segment, ref exifRaw, ref exifOrientation);
    }

    public void ParseApp14(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 12)
        {
            return;
        }

        if (segment[0] == (byte)'A' &&
            segment[1] == (byte)'d' &&
            segment[2] == (byte)'o' &&
            segment[3] == (byte)'b' &&
            segment[4] == (byte)'e')
        {
            hasAdobe = true;
            adobeTransform = segment[11];
        }
    }

    public void ParseApp2(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 14)
        {
            return;
        }

        iccCollector ??= new IccProfileCollector();
        iccCollector.Add(segment);
    }

    public void ParseDqt(ReadOnlySpan<byte> segment)
    {
        int p = 0;
        ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

        while (p < segment.Length)
        {
            byte pqTq = segment[p++];
            int pq = pqTq >> 4;
            int tq = pqTq & 0x0F;
            if (tq >= 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid DQT table id.");
            }

            if (pq == 0)
            {
                if (p + 64 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                for (int i = 0; i < 64; i++)
                {
                    quantTables[tq].Table[zigzag[i]] = segment[p++];
                }

                quantTableDefined[tq] = true;
            }
            else if (pq == 1)
            {
                if (p + 128 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                for (int i = 0; i < 64; i++)
                {
                    quantTables[tq].Table[zigzag[i]] = (ushort)((segment[p + (i * 2)] << 8) | segment[p + (i * 2) + 1]);
                }

                p += 128;
                quantTableDefined[tq] = true;
            }
            else
            {
                ThrowHelper.ThrowInvalidData("Invalid DQT precision.");
            }
        }
    }

    public void ParseDht(ReadOnlySpan<byte> segment)
    {
        int p = 0;

        while (p < segment.Length)
        {
            byte tcTh = segment[p++];
            int tc = tcTh >> 4;
            int th = tcTh & 0x0F;
            if (th >= 4) ThrowHelper.ThrowInvalidData("Invalid DHT table id.");
            if (tc is not (0 or 1)) ThrowHelper.ThrowInvalidData("Invalid DHT class.");

            if (p + 16 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
            ReadOnlySpan<byte> bits = segment.Slice(p, 16);
            p += 16;

            int total = 0;
            for (int i = 0; i < 16; i++) total += bits[i];
            if (p + total > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
            ReadOnlySpan<byte> values = segment.Slice(p, total);
            p += total;

            if (tc == 0)
            {
                dcTables[th].Build(bits, values);
                dcTableDefined[th] = true;
            }
            else
            {
                acTables[th].Build(bits, values);
                acTableDefined[th] = true;
            }
        }
    }

    public void ParseDri(ReadOnlySpan<byte> segment)
    {
        if (segment.Length != 2)
        {
            ThrowHelper.ThrowInvalidData("Invalid DRI length.");
        }

        restartInterval = (ushort)((segment[0] << 8) | segment[1]);
    }

    public void ParseSof(JpegMarker marker, ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 6)
        {
            ThrowHelper.ThrowInvalidData("Invalid SOF length.");
        }

        byte precision = segment[0];
        if (precision != 8)
        {
            ThrowHelper.ThrowNotSupported("Only 8-bit JPEG is supported.");
        }

        if (hasFrame)
        {
            ThrowHelper.ThrowInvalidData("Multiple SOF markers are not supported.");
        }

        ushort height = (ushort)((segment[1] << 8) | segment[2]);
        ushort width = (ushort)((segment[3] << 8) | segment[4]);
        byte count = segment[5];
        if (count <= 0 || count > 4)
        {
            ThrowHelper.ThrowInvalidData("Invalid component count.");
        }

        if (segment.Length < 6 + (3 * count))
        {
            ThrowHelper.ThrowInvalidData("Invalid SOF length.");
        }

        var comps = new ComponentState[count];
        int p = 6;
        int maxH = 0;
        int maxV = 0;
        Span<byte> seenComponentIds = stackalloc byte[256];
        for (int i = 0; i < count; i++)
        {
            byte id = segment[p++];
            byte hv = segment[p++];
            byte tq = segment[p++];
            int h = hv >> 4;
            int v = hv & 0x0F;
            if (h <= 0 || v <= 0 || h > 4 || v > 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid sampling factor.");
            }

            if (tq >= 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid quant table id.");
            }

            if (seenComponentIds[id] != 0)
            {
                ThrowHelper.ThrowInvalidData("Duplicated component id in SOF.");
            }

            seenComponentIds[id] = 1;

            if (h > maxH) maxH = h;
            if (v > maxV) maxV = v;

            comps[i] = new ComponentState(id, (byte)h, (byte)v, tq);
        }

        int mcuX = (width + (8 * maxH) - 1) / (8 * maxH);
        int mcuY = (height + (8 * maxV) - 1) / (8 * maxV);

        for (int i = 0; i < comps.Length; i++)
        {
            comps[i].SetGeometry(mcuX, mcuY);
        }

        frame = new FrameHeader(width, height, precision, maxH, maxV, mcuX, mcuY);
        hasFrame = true;
        isProgressive = marker == JpegMarker.SOF2;
        components = comps;
    }

    /// <summary>
    /// 解析 SOS 段头并做完整参数校验，返回扫描描述。熵编码数据由调用方通过
    /// <see cref="DecodeScan"/> 提供（内存模式为数据切片，流模式为继续读取输入）。
    /// </summary>
    public ScanHeader ParseSosHeader(ReadOnlySpan<byte> segment)
    {
        if (!hasFrame)
        {
            ThrowHelper.ThrowInvalidData("SOS before SOF.");
        }

        if (segment.Length < 1)
        {
            ThrowHelper.ThrowInvalidData("Invalid SOS length.");
        }

        int count = segment[0];
        if (count <= 0 || count > 4)
        {
            ThrowHelper.ThrowInvalidData("Invalid SOS component count.");
        }

        if (count > components.Length || segment.Length < 1 + (2 * count) + 3)
        {
            ThrowHelper.ThrowInvalidData("Invalid SOS length.");
        }

        Span<byte> seenScanComponentIds = stackalloc byte[256];
        var scanComponents = new ScanComponent[count];
        int p = 1;
        for (int i = 0; i < count; i++)
        {
            byte cs = segment[p++];
            byte tdta = segment[p++];
            byte td = (byte)(tdta >> 4);
            byte ta = (byte)(tdta & 0x0F);
            if (td >= 4 || ta >= 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid Huffman table selector.");
            }

            if (JpegDecoder.FindComponentIndex(components, cs) < 0)
            {
                ThrowHelper.ThrowInvalidData("Unknown component id in SOS.");
            }

            if (seenScanComponentIds[cs] != 0)
            {
                ThrowHelper.ThrowInvalidData("Duplicated component id in SOS.");
            }

            seenScanComponentIds[cs] = 1;
            scanComponents[i] = new ScanComponent(cs, td, ta);
        }

        byte ss = segment[p++];
        byte se = segment[p++];
        byte ahal = segment[p++];
        byte ah = (byte)(ahal >> 4);
        byte al = (byte)(ahal & 0x0F);

        if (!isProgressive)
        {
            if (count != components.Length)
            {
                ThrowHelper.ThrowInvalidData("Baseline scan must include all components.");
            }

            if (ss != 0 || se != 63 || ah != 0 || al != 0)
            {
                ThrowHelper.ThrowInvalidData("Invalid baseline SOS parameters.");
            }
        }
        else
        {
            if (se > 63 || ss > se || ah > 13 || al > 13)
            {
                ThrowHelper.ThrowInvalidData("Invalid progressive SOS parameters.");
            }
        }

        return new ScanHeader(scanComponents, ss, se, ah, al);
    }

    // ---------- 熵解码 ----------

    public void DecodeScan(in ScanHeader scan, ref JpegBitReader reader)
    {
        bool interleaved = scan.Components.Length > 1;

        var scanComponents = new ComponentState[scan.Components.Length];
        for (int i = 0; i < scan.Components.Length; i++)
        {
            ScanComponent sc = scan.Components[i];
            ComponentState comp = FindComponent(sc.ComponentId);
            if (!comp.HasCoefficients)
            {
                comp.EnsureCoefficientBuffer(isProgressive);
            }

            if (!quantTableDefined[comp.QuantTableId])
            {
                ThrowHelper.ThrowInvalidData("Missing quantization table for component.");
            }

            if (!dcTableDefined[sc.DcTableId])
            {
                ThrowHelper.ThrowInvalidData("Missing DC Huffman table.");
            }

            if (scan.Ss != 0 && !acTableDefined[sc.AcTableId])
            {
                ThrowHelper.ThrowInvalidData("Missing AC Huffman table.");
            }

            comp.AssignTables(sc.DcTableId, sc.AcTableId);
            scanComponents[i] = comp;
        }

        int expectedRst = (int)JpegMarker.RST0;
        int unitsUntilRestart = restartInterval;
        int eobRun = 0;

        if (interleaved)
        {
            for (int my = 0; my < frame.McuY; my++)
            {
                for (int mx = 0; mx < frame.McuX; mx++)
                {
                    for (int ci = 0; ci < scanComponents.Length; ci++)
                    {
                        ComponentState comp = scanComponents[ci];
                        for (int v = 0; v < comp.V; v++)
                        {
                            for (int h = 0; h < comp.H; h++)
                            {
                                int bx = (mx * comp.H) + h;
                                int by = (my * comp.V) + v;
                                try
                                {
                                    DecodeBlock(ref reader, comp, scan, bx, by, ref eobRun);
                                }
                                catch (InvalidDataException ex)
                                {
                                    ThrowHelper.ThrowInvalidData($"Scan decode failed (mx={mx}, my={my}, componentId={comp.Id}, bx={bx}, by={by}, bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}). {ex.Message}");
                                }
                            }
                        }
                    }

                    if (restartInterval != 0 && --unitsUntilRestart == 0)
                    {
                        ProcessRestart(ref reader, ref expectedRst, scan.Components, ref eobRun);
                        unitsUntilRestart = restartInterval;
                    }
                }
            }
        }
        else
        {
            ComponentState comp = scanComponents[0];
            int compWidth = (frame.Width * comp.H + frame.MaxH - 1) / frame.MaxH;
            int compHeight = (frame.Height * comp.V + frame.MaxV - 1) / frame.MaxV;
            int blocksX = (compWidth + 7) / 8;
            int blocksY = (compHeight + 7) / 8;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    try
                    {
                        DecodeBlock(ref reader, comp, scan, bx, by, ref eobRun);
                    }
                    catch (InvalidDataException ex)
                    {
                        ThrowHelper.ThrowInvalidData($"Scan decode failed (componentId={comp.Id}, bx={bx}, by={by}, bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}). {ex.Message}");
                    }

                    if (restartInterval != 0 && --unitsUntilRestart == 0)
                    {
                        ProcessRestart(ref reader, ref expectedRst, scan.Components, ref eobRun);
                        unitsUntilRestart = restartInterval;
                    }
                }
            }
        }
    }

    private void ProcessRestart(ref JpegBitReader reader, ref int expectedRst, ScanComponent[] scanComponents, ref int eobRun)
    {
        reader.AlignToByte();
        reader.DiscardBufferedBits();
        _ = reader.PeekBits(1);
        if (!reader.HasPendingMarker)
        {
            ThrowHelper.ThrowInvalidData("Missing restart marker.");
        }

        int marker = reader.PendingMarker;
        reader.ClearPendingMarker();
        if (marker != expectedRst)
        {
            ThrowHelper.ThrowInvalidData("Unexpected restart marker.");
        }

        expectedRst++;
        if (expectedRst > (int)JpegMarker.RST7)
        {
            expectedRst = (int)JpegMarker.RST0;
        }

        for (int i = 0; i < scanComponents.Length; i++)
        {
            FindComponent(scanComponents[i].ComponentId).ResetPredictors();
        }

        eobRun = 0;
        reader.Reset();
    }

    private void DecodeBlock(ref JpegBitReader reader, ComponentState comp, in ScanHeader scan, int bx, int by, ref int eobRun)
    {
        int blockIndex = (by * comp.BlocksX) + bx;
        Span<short> block = comp.GetBlockSpan(blockIndex);

        if (!isProgressive)
        {
            block.Clear();
            DecodeBaselineBlock(ref reader, comp, block);
            comp.HasCoefficients = true;
            return;
        }

        DecodeProgressiveBlock(ref reader, comp, block, scan.Ss, scan.Se, scan.Ah, scan.Al, ref eobRun);
        comp.HasCoefficients = true;
    }

    private void DecodeBaselineBlock(ref JpegBitReader reader, ComponentState comp, Span<short> block)
    {
        int s = dcTables[comp.DcTableId].Decode(ref reader);
        int diff = reader.ReceiveAndExtend(s);
        comp.DcPredictor += diff;
        block[0] = (short)comp.DcPredictor;

        int k = 1;
        ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;
        while (k < 64)
        {
            int rs = acTables[comp.AcTableId].Decode(ref reader);
            int r = rs >> 4;
            s = rs & 0x0F;

            if (s == 0)
            {
                if (r == 15)
                {
                    k += 16;
                    continue;
                }

                break;
            }

            k += r;
            if (k >= 64)
            {
                ThrowHelper.ThrowInvalidData("Bad AC coefficients.");
            }

            int coef = reader.ReceiveAndExtend(s);
            block[zigzag[k]] = (short)coef;
            k++;
        }
    }

    private void DecodeProgressiveBlock(ref JpegBitReader reader, ComponentState comp, Span<short> block, int ss, int se, int ah, int al, ref int eobRun)
    {
        ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

        if (ss == 0)
        {
            if (ah == 0)
            {
                int t = dcTables[comp.DcTableId].Decode(ref reader);
                int diff = reader.ReceiveAndExtend(t);
                comp.DcPredictor += diff;
                block[0] = (short)(comp.DcPredictor << al);
            }
            else
            {
                if (reader.ReadBits(1) != 0)
                {
                    int delta = 1 << al;
                    block[0] = (short)(block[0] >= 0 ? block[0] + delta : block[0] - delta);
                }
            }

            return;
        }

        if (ah == 0)
        {
            int k = ss;
            if (eobRun > 0)
            {
                eobRun--;
                return;
            }

            while (k <= se)
            {
                int rs = acTables[comp.AcTableId].Decode(ref reader);
                int r = rs >> 4;
                int s = rs & 0x0F;

                if (s == 0)
                {
                    if (r < 15)
                    {
                        int extra = (int)reader.ReadBits(r);
                        eobRun = ((1 << r) - 1) + extra;
                        return;
                    }

                    k += 16;
                    continue;
                }

                k += r;
                if (k > se)
                {
                    ThrowHelper.ThrowInvalidData("Bad progressive AC.");
                }

                int coef = reader.ReceiveAndExtend(s) << al;
                block[zigzag[k]] = (short)coef;
                k++;
            }

            return;
        }

        int bit = 1 << al;
        if (eobRun > 0)
        {
            for (int k = ss; k <= se; k++)
            {
                int idx = zigzag[k];
                if (block[idx] != 0)
                {
                    if (reader.ReadBits(1) != 0)
                    {
                        block[idx] = (short)(block[idx] >= 0 ? block[idx] + bit : block[idx] - bit);
                    }
                }
            }

            eobRun--;
            return;
        }

        int kk = ss;
        while (kk <= se)
        {
            int rs = acTables[comp.AcTableId].Decode(ref reader);
            int r = rs >> 4;
            int s = rs & 0x0F;

            if (s == 0)
            {
                if (r < 15)
                {
                    int extra = (int)reader.ReadBits(r);
                    eobRun = ((1 << r) - 1) + extra;

                    for (int k = kk; k <= se; k++)
                    {
                        int idx = zigzag[k];
                        if (block[idx] != 0)
                        {
                            if (reader.ReadBits(1) != 0)
                            {
                                block[idx] = (short)(block[idx] >= 0 ? block[idx] + bit : block[idx] - bit);
                            }
                        }
                    }

                    return;
                }

                int zeroCount = 16;
                while (kk <= se && zeroCount > 0)
                {
                    int idx = zigzag[kk];
                    if (block[idx] != 0)
                    {
                        if (reader.ReadBits(1) != 0)
                        {
                            block[idx] = (short)(block[idx] >= 0 ? block[idx] + bit : block[idx] - bit);
                        }
                    }
                    else
                    {
                        zeroCount--;
                    }

                    kk++;
                }

                continue;
            }

            if (s != 1)
            {
                ThrowHelper.ThrowInvalidData("Bad progressive refinement.");
            }

            int newCoef = reader.ReadBits(1) != 0 ? bit : -bit;
            int zc = r;
            while (kk <= se)
            {
                int idx = zigzag[kk];
                if (block[idx] != 0)
                {
                    if (reader.ReadBits(1) != 0)
                    {
                        block[idx] = (short)(block[idx] >= 0 ? block[idx] + bit : block[idx] - bit);
                    }
                }
                else
                {
                    if (zc == 0)
                    {
                        block[idx] = (short)newCoef;
                        kk++;
                        break;
                    }

                    zc--;
                }

                kk++;
            }
        }
    }

    private ComponentState FindComponent(byte id)
    {
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i].Id == id)
            {
                return components[i];
            }
        }

        ThrowHelper.ThrowInvalidData("Unknown component id.");
        return null!;
    }

    // ---------- 收尾 ----------

    public void ValidateScansComplete()
    {
        if (!hasFrame)
        {
            ThrowHelper.ThrowInvalidData("Missing SOF marker.");
        }

        for (int i = 0; i < components.Length; i++)
        {
            if (!components[i].HasCoefficients)
            {
                ThrowHelper.ThrowInvalidData("Missing scan data.");
            }
        }
    }

    public JpegImage ReconstructImage()
    {
        int width = frame.Width;
        int height = frame.Height;
        int bitsPerSample = frame.Precision;
        int maxH = frame.MaxH;
        int maxV = frame.MaxV;

        int fullWidth = frame.McuX * maxH * 8;
        int fullHeight = frame.McuY * maxV * 8;
        JpegColorSpace colorSpace = JpegDecoder.DetermineColorSpace(components, hasJfif, hasAdobe, adobeTransform);
        JpegPixelFormat pixelFormat = JpegDecoder.PixelFormatFromColorSpace(colorSpace);
        int[] componentOrder = JpegDecoder.BuildComponentOrder(components, colorSpace);
        int channelCount = componentOrder.Length;

        byte[][] planes = new byte[components.Length][];
        int[] planeStrides = new int[components.Length];
        int[] planeWidths = new int[components.Length];
        int[] planeHeights = new int[components.Length];
        byte[]? output = null;

        try
        {
            bool handled = false;

            if (colorSpace == JpegColorSpace.Gray && components.Length == 1)
            {
                output = new byte[checked(width * height)];
                handled = JpegDecoder.TryReconstructGrayDirect(frame, components[0], quantTables, useFloatingPointIdct, output);
                if (!handled)
                {
                    output = null;
                }
            }

            if (!handled && colorSpace == JpegColorSpace.Rgb && channelCount == 3)
            {
                output = new byte[checked(width * height * 3)];
                handled = JpegDecoder.TryReconstructRgbDirect(frame, components, quantTables, componentOrder, useFloatingPointIdct, output);
                if (!handled)
                {
                    output = null;
                }
            }

            if (!handled && colorSpace == JpegColorSpace.YCbCr && !useFloatingPointIdct && Sse2.IsSupported)
            {
                output = new byte[checked(width * height * channelCount)];
                if (JpegDecoder.TryDecodeInterleavedYCbCrSimd(components, output, width, height, fullWidth, fullHeight, componentOrder, quantTables, useFloatingPointIdct, frame))
                {
                    byte[]? iccProfileSimd = iccCollector?.GetProfile();
                    var colorInfoSimd = new JpegColorInfo(colorSpace, hasAdobe, adobeTransform, iccProfileSimd);
                    // SIMD path produces RGB24
                    var jpegImg = new JpegImage(width, height, JpegPixelFormat.Rgb24, bitsPerSample, colorInfoSimd, output);
                    jpegImg.Metadata.ExifRaw = exifRaw;
                    jpegImg.Metadata.Orientation = exifOrientation;
                    jpegImg.Metadata.IccProfile = iccProfileSimd;
                    return jpegImg;
                }
            }

            if (!handled)
            {
                for (int i = 0; i < components.Length; i++)
                {
                    ComponentState c = components[i];
                    int w = frame.McuX * c.H * 8;
                    int h = frame.McuY * c.V * 8;

                    planeWidths[i] = w;
                    planeHeights[i] = h;
                    planeStrides[i] = w;

                    byte[] plane = ArrayPool<byte>.Shared.Rent(w * h);
                    planes[i] = plane;

                    c.DecodeSpatial(plane.AsSpan(0, w * h), w, h, w, quantTables[c.QuantTableId].Table, useFloatingPointIdct);
                }

                output ??= new byte[checked(width * height * channelCount)];
                JpegDecoder.InterleaveComponents(planes, planeStrides, planeWidths, planeHeights, fullWidth, fullHeight, width, height, componentOrder, output);
            }
        }
        finally
        {
            for (int i = 0; i < planes.Length; i++)
            {
                byte[]? plane = planes[i];
                if (plane is not null)
                {
                    ArrayPool<byte>.Shared.Return(plane);
                }
            }
        }

        byte[]? iccProfile = iccCollector?.GetProfile();
        var colorInfo = new JpegColorInfo(colorSpace, hasAdobe, adobeTransform, iccProfile);
        var result = new JpegImage(width, height, pixelFormat, bitsPerSample, colorInfo, output!);
        result.Metadata.ExifRaw = exifRaw;
        result.Metadata.Orientation = exifOrientation;
        result.Metadata.IccProfile = iccProfile;
        return result;
    }

    public void DisposeResources()
    {
        for (int i = 0; i < components.Length; i++)
        {
            components[i].FreeCoefficientBuffer();
        }
        iccCollector?.Dispose();
        iccCollector = null;
    }
}
