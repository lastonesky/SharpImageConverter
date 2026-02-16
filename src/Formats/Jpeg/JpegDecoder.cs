using SharpImageConverter.Core;
using SharpImageConverter.Metadata;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
namespace SharpImageConverter.Formats.Jpeg;

public static class StaticJpegDecoder
{
    public static JpegImage Decode(ReadOnlySpan<byte> data)
    {
        var parser = new Parser(data);
        return parser.Decode();
    }

    public static async Task<StreamingDecodeResult> DecodeFromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        await using var input = new JpegStreamInput(stream);
        var parser = new StreamingParser(input);
        return await parser.DecodeAsync(cancellationToken).ConfigureAwait(false);
    }

    public readonly record struct StreamingDecodeResult(JpegImage Image, byte[]? ExifRaw, int ExifOrientation);

    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> data;
        private int offset;

        private readonly QuantizationTable[] quantTables = new QuantizationTable[4];
        private readonly HuffmanDecodingTable[] dcTables = new HuffmanDecodingTable[4];
        private readonly HuffmanDecodingTable[] acTables = new HuffmanDecodingTable[4];

        private FrameHeader frame;
        private bool hasFrame;
        private bool isProgressive;
        private ushort restartInterval;
        private ComponentState[] components = Array.Empty<ComponentState>();
        private int queuedMarker = -1;
        private bool hasAdobe;
        private byte adobeTransform;

        public Parser(ReadOnlySpan<byte> data)
        {
            this.data = data;
            offset = 0;

            for (int i = 0; i < 4; i++)
            {
                quantTables[i] = new QuantizationTable();
                dcTables[i] = new HuffmanDecodingTable();
                acTables[i] = new HuffmanDecodingTable();
            }
        }

        public JpegImage Decode()
        {
            ReadMarkerExpected(JpegMarker.SOI);

            while (offset < data.Length)
            {
                JpegMarker marker = ReadMarker();
                if (marker == JpegMarker.EOI)
                {
                    break;
                }

                switch (marker)
                {
                    case JpegMarker.APP0:
                        ParseApp0();
                        break;
                    case JpegMarker.APP1:
                        ParseApp1();
                        break;
                    case JpegMarker.APP14:
                        ParseApp14();
                        break;
                    case JpegMarker.COM:
                        SkipSegment();
                        break;
                    case JpegMarker.DQT:
                        ParseDqt();
                        break;
                    case JpegMarker.DHT:
                        ParseDht();
                        break;
                    case JpegMarker.DRI:
                        ParseDri();
                        break;
                    case JpegMarker.SOF0:
                    case JpegMarker.SOF2:
                        ParseSof(marker);
                        break;
                    case JpegMarker.SOS:
                        ParseSosAndDecodeScan();
                        break;
                    default:
                        if (marker is >= JpegMarker.RST0 and <= JpegMarker.RST7)
                        {
                            ThrowHelper.ThrowInvalidData("Unexpected restart marker outside entropy-coded data.");
                        }

                        SkipSegment();
                        break;
                }
            }

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

            return ReconstructImage();
        }

        private void ParseApp0()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            if (segmentLength < 5)
            {
                return;
            }

            if (segment[0] == (byte)'J' &&
                segment[1] == (byte)'F' &&
                segment[2] == (byte)'I' &&
                segment[3] == (byte)'F' &&
                segment[4] == 0)
            {
                return;
            }
        }

        private void ParseApp1()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            if (segmentLength < 6)
            {
                return;
            }

            if (segment[0] == (byte)'E' &&
                segment[1] == (byte)'x' &&
                segment[2] == (byte)'i' &&
                segment[3] == (byte)'f' &&
                segment[4] == 0 &&
                segment[5] == 0)
            {
                return;
            }
        }

        private void ParseApp14()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            if (segmentLength < 12)
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

        private readonly JpegImage ReconstructImage()
        {
            int width = frame.Width;
            int height = frame.Height;
            int bitsPerSample = frame.Precision;
            int maxH = frame.MaxH;
            int maxV = frame.MaxV;

            int fullWidth = frame.McuX * maxH * 8;
            int fullHeight = frame.McuY * maxV * 8;
            JpegColorSpace colorSpace = DetermineColorSpace(components.Length, hasAdobe, adobeTransform);
            JpegPixelFormat pixelFormat = PixelFormatFromColorSpace(colorSpace);
            int[] componentOrder = BuildComponentOrder(components, colorSpace);
            int channelCount = componentOrder.Length;

            byte[][] planes = new byte[components.Length][];
            int[] planeStrides = new int[components.Length];
            int[] planeWidths = new int[components.Length];
            int[] planeHeights = new int[components.Length];

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

                c.DecodeSpatial(plane.AsSpan(0, w * h), w, h, w, quantTables[c.QuantTableId].Table);
            }

            byte[] output = new byte[checked(width * height * channelCount)];
            InterleaveComponents(planes, planeStrides, planeWidths, planeHeights, fullWidth, fullHeight, width, height, componentOrder, output);

            for (int i = 0; i < planes.Length; i++)
            {
                ArrayPool<byte>.Shared.Return(planes[i]);
            }

            var colorInfo = new JpegColorInfo(colorSpace, hasAdobe, adobeTransform, null);
            return new JpegImage(width, height, pixelFormat, bitsPerSample, colorInfo, output);
        }

        private void ParseSosAndDecodeScan()
        {
            if (!hasFrame)
            {
                ThrowHelper.ThrowInvalidData("SOS before SOF.");
            }

            ReadOnlySpan<byte> segment = ReadSegment(out int segmentStart, out int segmentLength);
            int count = segment[0];
            if (count <= 0 || count > 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid SOS component count.");
            }

            if (segmentLength < 1 + (2 * count) + 3)
            {
                ThrowHelper.ThrowInvalidData("Invalid SOS length.");
            }

            var scanComponents = new ScanComponent[count];
            int p = 1;
            for (int i = 0; i < count; i++)
            {
                byte cs = segment[p++];
                byte tdta = segment[p++];
                byte td = (byte)(tdta >> 4);
                byte ta = (byte)(tdta & 0x0F);
                scanComponents[i] = new ScanComponent(cs, td, ta);
            }

            byte ss = segment[p++];
            byte se = segment[p++];
            byte ahal = segment[p++];
            byte ah = (byte)(ahal >> 4);
            byte al = (byte)(ahal & 0x0F);

            if (!isProgressive)
            {
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

            int entropyStart = segmentStart + segmentLength;
            ReadOnlySpan<byte> entropyData = data[entropyStart..];
            var scan = new ScanHeader(scanComponents, ss, se, ah, al);

            var reader = new JpegBitReader(entropyData);
            DecodeScan(scan, ref reader);

            reader.AlignToByte();
            _ = reader.PeekBits(1);
            if (reader.HasPendingMarker)
            {
                queuedMarker = reader.PendingMarker;
                reader.ClearPendingMarker();
            }

            offset = entropyStart + reader.BytesConsumed;
        }

        /// <summary>
        /// 解码一次扫描（Scan），处理熵编码数据、重启标记以及渐进 JPEG 的 EOB 运行。
        /// </summary>
        /// <param name="scan">扫描头，包含参与的分量及 SS/SE/Ah/Al 参数</param>
        /// <param name="reader">位流读取器，封装了 JPEG 字节填充与标记处理</param>
        private void DecodeScan(in ScanHeader scan, ref JpegBitReader reader)
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

        private readonly void DecodeProgressiveBlock(ref JpegBitReader reader, ComponentState comp, Span<short> block, int ss, int se, int ah, int al, ref int eobRun)
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

        private readonly ComponentState FindComponent(byte id)
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

        /// <summary>
        /// 解析 SOF 帧头（SOF0 基线 / SOF2 渐进），并初始化帧尺寸、
        /// 采样因子、MCU 网格以及各分量的块几何信息。
        /// </summary>
        /// <param name="marker">SOF 标记类型（决定是否为渐进 JPEG）</param>
        private void ParseSof(JpegMarker marker)
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);

            if (segmentLength < 6)
            {
                ThrowHelper.ThrowInvalidData("Invalid SOF length.");
            }

            byte precision = segment[0];
            if (precision != 8)
            {
                ThrowHelper.ThrowNotSupported("Only 8-bit JPEG is supported.");
            }

            ushort height = ReadU16(segment.Slice(1));
            ushort width = ReadU16(segment.Slice(3));
            byte count = segment[5];
            if (count <= 0 || count > 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid component count.");
            }

            if (segmentLength < 6 + (3 * count))
            {
                ThrowHelper.ThrowInvalidData("Invalid SOF length.");
            }

            var comps = new ComponentState[count];
            int p = 6;
            int maxH = 0;
            int maxV = 0;
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

        private void ParseDqt()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            int p = 0;
            ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

            while (p < segmentLength)
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
                    if (p + 64 > segmentLength) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                    for (int i = 0; i < 64; i++)
                    {
                        quantTables[tq].Table[zigzag[i]] = segment[p++];
                    }
                }
                else if (pq == 1)
                {
                    if (p + 128 > segmentLength) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                    for (int i = 0; i < 64; i++)
                    {
                        quantTables[tq].Table[zigzag[i]] = ReadU16(segment.Slice(p + (i * 2)));
                    }

                    p += 128;
                }
                else
                {
                    ThrowHelper.ThrowInvalidData("Invalid DQT precision.");
                }
            }
        }

        private void ParseDht()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            int p = 0;

            while (p < segmentLength)
            {
                byte tcTh = segment[p++];
                int tc = tcTh >> 4;
                int th = tcTh & 0x0F;
                if (th >= 4) ThrowHelper.ThrowInvalidData("Invalid DHT table id.");
                if (tc is not (0 or 1)) ThrowHelper.ThrowInvalidData("Invalid DHT class.");

                if (p + 16 > segmentLength) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
                ReadOnlySpan<byte> bits = segment.Slice(p, 16);
                p += 16;

                int total = 0;
                for (int i = 0; i < 16; i++) total += bits[i];
                if (p + total > segmentLength) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
                ReadOnlySpan<byte> values = segment.Slice(p, total);
                p += total;

                if (tc == 0)
                {
                    dcTables[th].Build(bits, values);
                }
                else
                {
                    acTables[th].Build(bits, values);
                }
            }
        }

        private void ParseDri()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            if (segmentLength != 2)
            {
                ThrowHelper.ThrowInvalidData("Invalid DRI length.");
            }

            restartInterval = ReadU16(segment);
        }

        private void SkipSegment()
        {
            _ = ReadSegment(out _, out _);
        }

        private ReadOnlySpan<byte> ReadSegment(out int segmentStart, out int segmentLength)
        {
            if (offset + 2 > data.Length)
            {
                ThrowHelper.ThrowInvalidData("Unexpected end of file.");
            }

            ushort length = ReadU16(data.Slice(offset, 2));
            offset += 2;

            if (length < 2)
            {
                ThrowHelper.ThrowInvalidData("Invalid segment length.");
            }

            segmentStart = offset;
            segmentLength = length - 2;

            if (offset + segmentLength > data.Length)
            {
                ThrowHelper.ThrowInvalidData("Truncated segment.");
            }

            ReadOnlySpan<byte> seg = data.Slice(offset, segmentLength);
            offset += segmentLength;
            return seg;
        }

        private void ReadMarkerExpected(JpegMarker expected)
        {
            JpegMarker m = ReadMarker();
            if (m != expected)
            {
                ThrowHelper.ThrowInvalidData("Invalid JPEG header.");
            }
        }

        private JpegMarker ReadMarker()
        {
            if (queuedMarker >= 0)
            {
                byte m = (byte)queuedMarker;
                queuedMarker = -1;
                return (JpegMarker)m;
            }

            while (offset < data.Length && data[offset] != 0xFF)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                ThrowHelper.ThrowInvalidData("Unexpected end of file.");
            }

            while (offset < data.Length && data[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                ThrowHelper.ThrowInvalidData("Unexpected end of file.");
            }

            byte marker = data[offset++];
            return (JpegMarker)marker;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadU16(ReadOnlySpan<byte> s) => (ushort)((s[0] << 8) | s[1]);
    }

    private static JpegColorSpace DetermineColorSpace(int componentCount, bool hasAdobe, byte adobeTransform)
    {
        if (componentCount == 1)
        {
            return JpegColorSpace.Gray;
        }

        if (componentCount == 3)
        {
            if (hasAdobe)
            {
                if (adobeTransform == 0)
                {
                    return JpegColorSpace.Rgb;
                }

                if (adobeTransform == 1)
                {
                    return JpegColorSpace.YCbCr;
                }
            }

            return JpegColorSpace.YCbCr;
        }

        if (componentCount == 4)
        {
            if (hasAdobe)
            {
                if (adobeTransform == 0)
                {
                    return JpegColorSpace.Cmyk;
                }

                if (adobeTransform == 2)
                {
                    return JpegColorSpace.Ycck;
                }
            }

            return JpegColorSpace.Unknown4;
        }

        ThrowHelper.ThrowNotSupported("Unsupported number of components.");
        return JpegColorSpace.Unknown4;
    }

    private static JpegPixelFormat PixelFormatFromColorSpace(JpegColorSpace colorSpace)
    {
        return colorSpace switch
        {
            JpegColorSpace.Gray => JpegPixelFormat.Gray8,
            JpegColorSpace.Rgb => JpegPixelFormat.Rgb24,
            JpegColorSpace.YCbCr => JpegPixelFormat.YCbCr24,
            JpegColorSpace.Cmyk => JpegPixelFormat.Cmyk32,
            JpegColorSpace.Ycck => JpegPixelFormat.Ycck32,
            JpegColorSpace.Unknown4 => JpegPixelFormat.Unknown32,
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace))
        };
    }

    private static int[] BuildComponentOrder(ComponentState[] components, JpegColorSpace colorSpace)
    {
        return colorSpace switch
        {
            JpegColorSpace.Gray => BuildComponentOrder(components, [1]),
            JpegColorSpace.Rgb => BuildComponentOrder(components, [1, 2, 3]),
            JpegColorSpace.YCbCr => BuildComponentOrder(components, [1, 2, 3]),
            JpegColorSpace.Cmyk => BuildComponentOrder(components, [1, 2, 3, 4]),
            JpegColorSpace.Ycck => BuildComponentOrder(components, [1, 2, 3, 4]),
            JpegColorSpace.Unknown4 => BuildSequentialOrder(components.Length),
            _ => BuildSequentialOrder(components.Length)
        };
    }

    private static int[] BuildComponentOrder(ComponentState[] components, ReadOnlySpan<byte> expectedIds)
    {
        int[] order = new int[expectedIds.Length];
        for (int i = 0; i < expectedIds.Length; i++)
        {
            int index = FindComponentIndex(components, expectedIds[i]);
            if (index < 0)
            {
                return BuildSequentialOrder(components.Length);
            }

            order[i] = index;
        }

        return order;
    }

    private static int[] BuildSequentialOrder(int count)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        return order;
    }

    private static int FindComponentIndex(ComponentState[] components, byte id)
    {
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static void InterleaveComponents(
        byte[][] planes,
        int[] planeStrides,
        int[] planeWidths,
        int[] planeHeights,
        int fullWidth,
        int fullHeight,
        int width,
        int height,
        int[] componentOrder,
        byte[] output)
    {
        int channels = componentOrder.Length;
        int outStride = width * channels;
        int mapLength = width * channels;
        int[] xMap = ArrayPool<int>.Shared.Rent(mapLength);
        try
        {
            for (int c = 0; c < channels; c++)
            {
                int planeIndex = componentOrder[c];
                int planeW = planeWidths[planeIndex];
                int baseIndex = c * width;
                for (int x = 0; x < width; x++)
                {
                    xMap[baseIndex + x] = (x * planeW) / fullWidth;
                }
            }

            for (int y = 0; y < height; y++)
            {
                int rowOut = y * outStride;
                for (int c = 0; c < channels; c++)
                {
                    int planeIndex = componentOrder[c];
                    int planeH = planeHeights[planeIndex];
                    int py = (y * planeH) / fullHeight;
                    int srcRow = py * planeStrides[planeIndex];
                    byte[] plane = planes[planeIndex];
                    int mapBase = c * width;
                    int outIndex = rowOut + c;
                    for (int x = 0; x < width; x++)
                    {
                        output[outIndex] = plane[srcRow + xMap[mapBase + x]];
                        outIndex += channels;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xMap);
        }
    }

    private sealed class StreamingParser
    {
        private readonly JpegStreamInput input;
        private readonly QuantizationTable[] quantTables = new QuantizationTable[4];
        private readonly HuffmanDecodingTable[] dcTables = new HuffmanDecodingTable[4];
        private readonly HuffmanDecodingTable[] acTables = new HuffmanDecodingTable[4];

        private FrameHeader frame;
        private bool hasFrame;
        private bool isProgressive;
        private ushort restartInterval;
        private ComponentState[] components = [];
        private int queuedMarker = -1;
        private byte[]? exifRaw;
        private int exifOrientation = 1;
        private bool hasAdobe;
        private byte adobeTransform;

        public StreamingParser(JpegStreamInput input)
        {
            this.input = input;
            for (int i = 0; i < 4; i++)
            {
                quantTables[i] = new QuantizationTable();
                dcTables[i] = new HuffmanDecodingTable();
                acTables[i] = new HuffmanDecodingTable();
            }
        }

        public async Task<StreamingDecodeResult> DecodeAsync(CancellationToken cancellationToken)
        {
            await ReadMarkerExpectedAsync(JpegMarker.SOI, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                JpegMarker marker = await ReadMarkerAsync(cancellationToken).ConfigureAwait(false);
                if (marker == JpegMarker.EOI)
                {
                    break;
                }

                switch (marker)
                {
                    case JpegMarker.APP0:
                        await ParseApp0Async(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.APP1:
                        await ParseApp1Async(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.APP14:
                        await ParseApp14Async(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.COM:
                        await SkipSegmentAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.DQT:
                        await ParseDqtAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.DHT:
                        await ParseDhtAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.DRI:
                        await ParseDriAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.SOF0:
                    case JpegMarker.SOF2:
                        await ParseSofAsync(marker, cancellationToken).ConfigureAwait(false);
                        break;
                    case JpegMarker.SOS:
                        await ParseSosAndDecodeScanAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        if (marker is >= JpegMarker.RST0 and <= JpegMarker.RST7)
                        {
                            ThrowHelper.ThrowInvalidData("Unexpected restart marker outside entropy-coded data.");
                        }

                        await SkipSegmentAsync(cancellationToken).ConfigureAwait(false);
                        break;
                }
            }

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

            var img = ReconstructImage();
            return new StreamingDecodeResult(img, exifRaw, exifOrientation);
        }

        private async Task ParseApp0Async(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (segment.Length < 5)
                {
                    return;
                }

                ReadOnlySpan<byte> span = segment.Span;
                if (span[0] == (byte)'J' &&
                    span[1] == (byte)'F' &&
                    span[2] == (byte)'I' &&
                    span[3] == (byte)'F' &&
                    span[4] == 0)
                {
                    return;
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        private async Task ParseApp1Async(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (segment.Length < 6)
                {
                    return;
                }

                ReadOnlySpan<byte> span = segment.Span;
                if (span[0] == (byte)'E' &&
                    span[1] == (byte)'x' &&
                    span[2] == (byte)'i' &&
                    span[3] == (byte)'f' &&
                    span[4] == 0 &&
                    span[5] == 0)
                {
                    exifRaw ??= span.ToArray();
                    if (exifOrientation == 1)
                    {
                        int orientation = ParseExifOrientation(span.Slice(6));
                        if (orientation >= 1 && orientation <= 8)
                        {
                            exifOrientation = orientation;
                        }
                    }
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        private async Task ParseApp14Async(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (segment.Length < 12)
                {
                    return;
                }

                ReadOnlySpan<byte> span = segment.Span;
                if (span[0] == (byte)'A' &&
                    span[1] == (byte)'d' &&
                    span[2] == (byte)'o' &&
                    span[3] == (byte)'b' &&
                    span[4] == (byte)'e')
                {
                    hasAdobe = true;
                    adobeTransform = span[11];
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        private JpegImage ReconstructImage()
        {
            int width = frame.Width;
            int height = frame.Height;
            int bitsPerSample = frame.Precision;
            int maxH = frame.MaxH;
            int maxV = frame.MaxV;

            int fullWidth = frame.McuX * maxH * 8;
            int fullHeight = frame.McuY * maxV * 8;
            JpegColorSpace colorSpace = DetermineColorSpace(components.Length, hasAdobe, adobeTransform);
            JpegPixelFormat pixelFormat = PixelFormatFromColorSpace(colorSpace);
            int[] componentOrder = BuildComponentOrder(components, colorSpace);
            int channelCount = componentOrder.Length;

            byte[][] planes = new byte[components.Length][];
            int[] planeStrides = new int[components.Length];
            int[] planeWidths = new int[components.Length];
            int[] planeHeights = new int[components.Length];

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

                c.DecodeSpatial(plane.AsSpan(0, w * h), w, h, w, quantTables[c.QuantTableId].Table);
            }

            byte[] output = new byte[checked(width * height * channelCount)];
            InterleaveComponents(planes, planeStrides, planeWidths, planeHeights, fullWidth, fullHeight, width, height, componentOrder, output);

            for (int i = 0; i < planes.Length; i++)
            {
                ArrayPool<byte>.Shared.Return(planes[i]);
            }

            var colorInfo = new JpegColorInfo(colorSpace, hasAdobe, adobeTransform, null);
            return new JpegImage(width, height, pixelFormat, bitsPerSample, colorInfo, output);
        }

        private async Task ParseSosAndDecodeScanAsync(CancellationToken cancellationToken)
        {
            if (!hasFrame)
            {
                ThrowHelper.ThrowInvalidData("SOS before SOF.");
            }

            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ReadOnlySpan<byte> span = segment.Span;
                int count = span[0];
                if (count <= 0 || count > 4)
                {
                    ThrowHelper.ThrowInvalidData("Invalid SOS component count.");
                }

                if (segment.Length < 1 + (2 * count) + 3)
                {
                    ThrowHelper.ThrowInvalidData("Invalid SOS length.");
                }

                var scanComponents = new ScanComponent[count];
                int p = 1;
                for (int i = 0; i < count; i++)
                {
                    byte cs = span[p++];
                    byte tdta = span[p++];
                    byte td = (byte)(tdta >> 4);
                    byte ta = (byte)(tdta & 0x0F);
                    scanComponents[i] = new ScanComponent(cs, td, ta);
                }

                byte ss = span[p++];
                byte se = span[p++];
                byte ahal = span[p++];
                byte ah = (byte)(ahal >> 4);
                byte al = (byte)(ahal & 0x0F);

                if (!isProgressive)
                {
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

                var scan = new ScanHeader(scanComponents, ss, se, ah, al);
                var reader = new JpegStreamBitReader(input, cancellationToken);
                DecodeScan(scan, ref reader);

                reader.AlignToByte();
                _ = reader.PeekBits(1);
                if (reader.HasPendingMarker)
                {
                    queuedMarker = reader.PendingMarker;
                    reader.ClearPendingMarker();
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        private void DecodeScan(in ScanHeader scan, ref JpegStreamBitReader reader)
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

        private void ProcessRestart(ref JpegStreamBitReader reader, ref int expectedRst, ScanComponent[] scanComponents, ref int eobRun)
        {
            reader.AlignToByte();
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

        private void DecodeBlock(ref JpegStreamBitReader reader, ComponentState comp, in ScanHeader scan, int bx, int by, ref int eobRun)
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

        private void DecodeBaselineBlock(ref JpegStreamBitReader reader, ComponentState comp, Span<short> block)
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

        private void DecodeProgressiveBlock(ref JpegStreamBitReader reader, ComponentState comp, Span<short> block, int ss, int se, int ah, int al, ref int eobRun)
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

        private async Task ParseSofAsync(JpegMarker marker, CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (segment.Length < 6)
                {
                    ThrowHelper.ThrowInvalidData("Invalid SOF length.");
                }

                ReadOnlySpan<byte> span = segment.Span;
                byte precision = span[0];
                if (precision != 8)
                {
                    ThrowHelper.ThrowNotSupported("Only 8-bit JPEG is supported.");
                }

                ushort height = ReadU16(span.Slice(1));
                ushort width = ReadU16(span.Slice(3));
                byte count = span[5];
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
                for (int i = 0; i < count; i++)
                {
                    byte id = span[p++];
                    byte hv = span[p++];
                    byte tq = span[p++];
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
            finally
            {
                segment.Dispose();
            }
        }

        private async Task ParseDqtAsync(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ReadOnlySpan<byte> span = segment.Span;
                int p = 0;
                ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

                while (p < segment.Length)
                {
                    byte pqTq = span[p++];
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
                            quantTables[tq].Table[zigzag[i]] = span[p++];
                        }
                    }
                    else if (pq == 1)
                    {
                        if (p + 128 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                        for (int i = 0; i < 64; i++)
                        {
                            quantTables[tq].Table[zigzag[i]] = ReadU16(span[(p + (i * 2))..]);
                        }

                        p += 128;
                    }
                    else
                    {
                        ThrowHelper.ThrowInvalidData("Invalid DQT precision.");
                    }
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        private async Task ParseDhtAsync(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ReadOnlySpan<byte> span = segment.Span;
                int p = 0;

                while (p < segment.Length)
                {
                    byte tcTh = span[p++];
                    int tc = tcTh >> 4;
                    int th = tcTh & 0x0F;
                    if (th >= 4) ThrowHelper.ThrowInvalidData("Invalid DHT table id.");
                    if (tc is not (0 or 1)) ThrowHelper.ThrowInvalidData("Invalid DHT class.");

                    if (p + 16 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
                    ReadOnlySpan<byte> bits = span.Slice(p, 16);
                    p += 16;

                    int total = 0;
                    for (int i = 0; i < 16; i++) total += bits[i];
                    if (p + total > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
                    ReadOnlySpan<byte> values = span.Slice(p, total);
                    p += total;

                    if (tc == 0)
                    {
                        dcTables[th].Build(bits, values);
                    }
                    else
                    {
                        acTables[th].Build(bits, values);
                    }
                }
            }
            finally
            {
                segment.Dispose();
            }
        }

        private async Task ParseDriAsync(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (segment.Length != 2)
                {
                    ThrowHelper.ThrowInvalidData("Invalid DRI length.");
                }

                restartInterval = ReadU16(segment.Span);
            }
            finally
            {
                segment.Dispose();
            }
        }

        private async Task SkipSegmentAsync(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            segment.Dispose();
        }

        private async Task<SegmentBuffer> ReadSegmentAsync(CancellationToken cancellationToken)
        {
            ushort length = await ReadU16Async(cancellationToken).ConfigureAwait(false);
            if (length < 2)
            {
                ThrowHelper.ThrowInvalidData("Invalid segment length.");
            }

            int segmentLength = length - 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(segmentLength);
            if (segmentLength > 0)
            {
                await input.ReadExactAsync(buffer.AsMemory(0, segmentLength), cancellationToken).ConfigureAwait(false);
            }

            return new SegmentBuffer(buffer, segmentLength);
        }

        private async Task ReadMarkerExpectedAsync(JpegMarker expected, CancellationToken cancellationToken)
        {
            JpegMarker m = await ReadMarkerAsync(cancellationToken).ConfigureAwait(false);
            if (m != expected)
            {
                ThrowHelper.ThrowInvalidData("Invalid JPEG header.");
            }
        }

        private async Task<JpegMarker> ReadMarkerAsync(CancellationToken cancellationToken)
        {
            if (queuedMarker >= 0)
            {
                byte m = (byte)queuedMarker;
                queuedMarker = -1;
                return (JpegMarker)m;
            }

            byte b;
            do
            {
                b = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            } while (b != 0xFF);

            do
            {
                b = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            } while (b == 0xFF);

            return (JpegMarker)b;
        }

        private async Task<ushort> ReadU16Async(CancellationToken cancellationToken)
        {
            byte hi = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            byte lo = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            return (ushort)((hi << 8) | lo);
        }

        private static ushort ReadU16(ReadOnlySpan<byte> s) => (ushort)((s[0] << 8) | s[1]);

        private static int ParseExifOrientation(ReadOnlySpan<byte> tiff)
        {
            if (tiff.Length < 8) return 1;
            bool little = tiff[0] == 0x49 && tiff[1] == 0x49;
            bool big = tiff[0] == 0x4D && tiff[1] == 0x4D;
            if (!little && !big) return 1;

            if (ReadU16(tiff, little, 2) != 42) return 1;
            uint ifd0 = ReadU32(tiff, little, 4);
            if (ifd0 + 2 > tiff.Length) return 1;

            int offset = (int)ifd0;
            ushort count = ReadU16(tiff, little, offset);
            offset += 2;
            for (int i = 0; i < count; i++)
            {
                int entry = offset + (i * 12);
                if (entry + 12 > tiff.Length) break;
                ushort tag = ReadU16(tiff, little, entry);
                if (tag != 0x0112) continue;
                ushort type = ReadU16(tiff, little, entry + 2);
                uint num = ReadU32(tiff, little, entry + 4);
                if (num != 1) return 1;
                if (type == 3)
                {
                    ushort val = ReadU16(tiff, little, entry + 8);
                    return val;
                }
                if (type == 4)
                {
                    uint val = ReadU32(tiff, little, entry + 8);
                    return (int)val;
                }
                return 1;
            }

            return 1;
        }

        private static ushort ReadU16(ReadOnlySpan<byte> tiff, bool little, int offset)
        {
            if (offset + 2 > tiff.Length) return 0;
            return little
                ? (ushort)(tiff[offset] | (tiff[offset + 1] << 8))
                : (ushort)((tiff[offset] << 8) | tiff[offset + 1]);
        }

        private static uint ReadU32(ReadOnlySpan<byte> tiff, bool little, int offset)
        {
            if (offset + 4 > tiff.Length) return 0;
            return little
                ? (uint)(tiff[offset] | (tiff[offset + 1] << 8) | (tiff[offset + 2] << 16) | (tiff[offset + 3] << 24))
                : (uint)((tiff[offset] << 24) | (tiff[offset + 1] << 16) | (tiff[offset + 2] << 8) | tiff[offset + 3]);
        }

        private readonly struct SegmentBuffer(byte[] buffer, int length) : IDisposable
        {
            private readonly byte[] buffer = buffer;
            public readonly int Length = length;

            public ReadOnlySpan<byte> Span => buffer.AsSpan(0, Length);

            public void Dispose()
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private readonly record struct FrameHeader(int Width, int Height, byte Precision, int MaxH, int MaxV, int McuX, int McuY);

    private sealed class QuantizationTable
    {
        public ushort[] Table { get; } = new ushort[64];
    }

    private readonly record struct ScanHeader(ScanComponent[] Components, byte Ss, byte Se, byte Ah, byte Al);

    private readonly record struct ScanComponent(byte ComponentId, byte DcTableId, byte AcTableId);

    private sealed class ComponentState(byte id, byte h, byte v, byte quantTableId)
    {
        private short[]? coefficientBuffer;
        private int coefficientLength;

        public byte Id { get; } = id;
        public byte H { get; } = h;
        public byte V { get; } = v;
        public byte QuantTableId { get; } = quantTableId;

        public byte DcTableId { get; private set; }
        public byte AcTableId { get; private set; }
        public int DcPredictor { get; set; }

        public int BlocksX { get; private set; }
        public int BlocksY { get; private set; }

        public bool HasCoefficients { get; set; }

        public void AssignTables(byte dcId, byte acId)
        {
            DcTableId = dcId;
            AcTableId = acId;
        }

        public void SetGeometry(int mcuX, int mcuY)
        {
            BlocksX = mcuX * H;
            BlocksY = mcuY * V;
        }

        public void ResetPredictors()
        {
            DcPredictor = 0;
        }

        public void EnsureCoefficientBuffer(bool clear)
        {
            int blocks = BlocksX * BlocksY;
            coefficientLength = blocks * 64;
            coefficientBuffer = ArrayPool<short>.Shared.Rent(coefficientLength);
            if (clear)
            {
                Array.Clear(coefficientBuffer, 0, coefficientLength);
            }
        }

        public Span<short> GetBlockSpan(int blockIndex)
        {
            if (coefficientBuffer is null)
            {
                ThrowHelper.ThrowInvalidData("Coefficient buffer not allocated.");
            }

            int start = blockIndex * 64;
            return coefficientBuffer.AsSpan(start, 64);
        }

        /// <summary>
        /// 将当前分量的频域系数通过 IDCT 与反量化还原为空间域像素块，
        /// 并写入目标平面缓冲区，随后归还系数缓冲。
        /// </summary>
        /// <param name="plane">输出平面缓冲区</param>
        /// <param name="planeWidth">平面宽度（像素）</param>
        /// <param name="planeHeight">平面高度（像素）</param>
        /// <param name="stride">平面行跨度（字节）</param>
        /// <param name="quant">对应的量化表</param>
        public void DecodeSpatial(Span<byte> plane, int planeWidth, int planeHeight, int stride, ushort[] quant)
        {
            if (coefficientBuffer is null)
            {
                ThrowHelper.ThrowInvalidData("Missing coefficients.");
            }

            int blocksX = BlocksX;
            int blocksY = BlocksY;

            for (int by = 0; by < blocksY; by++)
            {
                int py = by * 8;
                if (py >= planeHeight) break;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    int px = bx * 8;
                    if (px >= planeWidth) break;

                    Span<byte> dest = plane[((py * stride) + px)..];
                    ReadOnlySpan<short> block = coefficientBuffer.AsSpan(((by * blocksX) + bx) * 64, 64);
                    //FloatingPointIDCT.Transform(block, quant, dest, stride);
                    FastIDCT.Transform(block, quant, dest, stride);
                }
            }

            short[] buffer = coefficientBuffer!;
            ArrayPool<short>.Shared.Return(buffer);
            coefficientBuffer = null;
            coefficientLength = 0;
        }
    }
}

public sealed class JpegDecoder
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ExifOrientation { get; private set; } = 1;
    public bool UseFloatingPointIdct { get; set; }
    public Image<Rgb24> Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return DecodeAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<Image<Rgb24>> DecodeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var result = await StaticJpegDecoder.DecodeFromStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        return BuildImageFromJpeg(result.Image, result.ExifOrientation, result.ExifRaw);
    }

    public Image<Rgb24> Decode(ReadOnlySpan<byte> data)
    {
        int orientation = TryReadExifOrientation(data, out var exifRaw);
        var img = StaticJpegDecoder.Decode(data);
        return BuildImageFromJpeg(img, orientation, exifRaw);
    }

    private Image<Rgb24> BuildImageFromJpeg(JpegImage img, int orientation, byte[]? exifRaw)
    {
        var metadata = new ImageMetadata();
        ExifOrientation = orientation;
        if (exifRaw != null) metadata.ExifRaw = exifRaw;
        metadata.Orientation = ExifOrientation;

        Width = img.Width;
        Height = img.Height;

        byte[] rgb;
        if (img.PixelFormat == JpegPixelFormat.Rgb24)
        {
            if (ExifOrientation != 1)
            {
                var t = ApplyExifOrientation(img.PixelDataArray, Width, Height, ExifOrientation);
                rgb = t.pixels;
                Width = t.width;
                Height = t.height;
                ExifOrientation = 1;
                metadata.Orientation = 1;
            }
            else
            {
                rgb = img.PixelDataArray;
            }
        }
        else
        {
            rgb = img.PixelFormat switch
            {
                JpegPixelFormat.Gray8 => ExpandGray(img.PixelData, Width, Height),
                JpegPixelFormat.Rgba32 => DropAlpha(img.PixelData, Width, Height),
                JpegPixelFormat.YCbCr24 => DropAlpha(img.Rgba32, Width, Height),
                JpegPixelFormat.Cmyk32 => DropAlpha(img.Rgba32, Width, Height),
                JpegPixelFormat.Ycck32 => DropAlpha(img.Rgba32, Width, Height),
                JpegPixelFormat.Unknown32 => DropAlpha(img.Rgba32, Width, Height),
                _ => throw new InvalidOperationException("Unsupported pixel format.")
            };
            if (ExifOrientation != 1)
            {
                var t = ApplyExifOrientation(rgb, Width, Height, ExifOrientation);
                rgb = t.pixels;
                Width = t.width;
                Height = t.height;
                ExifOrientation = 1;
                metadata.Orientation = 1;
            }
        }
        return new Image<Rgb24>(Width, Height, rgb, metadata);
    }
    private static (byte[] pixels, int width, int height) ApplyExifOrientation(byte[] src, int width, int height, int orientation)
    {
        int newW;
        int newH;
        switch (orientation)
        {
            case 1:
                return (src, width, height);
            case 2:
            case 3:
            case 4:
                newW = width; newH = height; break;
            case 5:
            case 6:
            case 7:
            case 8:
                newW = height; newH = width; break;
            default:
                return (src, width, height);
        }

        byte[] dst = new byte[newW * newH * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx, dy;
                switch (orientation)
                {
                    case 2:
                        dx = (width - 1 - x); dy = y; break;
                    case 3:
                        dx = (width - 1 - x); dy = (height - 1 - y); break;
                    case 4:
                        dx = x; dy = (height - 1 - y); break;
                    case 5:
                        dx = y; dy = x; break;
                    case 6:
                        dx = (height - 1 - y); dy = x; break;
                    case 7:
                        dx = (height - 1 - y); dy = (width - 1 - x); break;
                    case 8:
                        dx = y; dy = (width - 1 - x); break;
                    default:
                        dx = x; dy = y; break;
                }
                int srcIdx = (y * width + x) * 3;
                int dstIdx = (dy * newW + dx) * 3;
                dst[dstIdx + 0] = src[srcIdx + 0];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
            }
        }
        return (dst, newW, newH);
    }
    private static byte[] ExpandGray(ReadOnlySpan<byte> src, int width, int height)
    {
        int count = checked(width * height);
        var dst = new byte[checked(count * 3)];
        for (int i = 0; i < count; i++)
        {
            byte v = src[i];
            int o = i * 3;
            dst[o + 0] = v;
            dst[o + 1] = v;
            dst[o + 2] = v;
        }
        return dst;
    }

    private static byte[] DropAlpha(ReadOnlySpan<byte> src, int width, int height)
    {
        int count = checked(width * height);
        var dst = new byte[checked(count * 3)];
        int si = 0;
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            dst[o + 0] = src[si + 0];
            dst[o + 1] = src[si + 1];
            dst[o + 2] = src[si + 2];
            si += 4;
        }
        return dst;
    }

    private static int TryReadExifOrientation(ReadOnlySpan<byte> data, out byte[]? exifRaw)
    {
        exifRaw = null;
        int i = 0;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            int marker = data[i + 1];
            i += 2;

            if (marker == 0xD8 || marker == 0x01)
            {
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA)
            {
                break;
            }

            if (i + 2 > data.Length) break;
            int length = (data[i] << 8) | data[i + 1];
            if (length < 2 || i + length > data.Length) break;

            if (marker == 0xE1 && length >= 8)
            {
                var segment = data.Slice(i + 2, length - 2);
                if (segment.Length >= 6 &&
                    segment[0] == (byte)'E' &&
                    segment[1] == (byte)'x' &&
                    segment[2] == (byte)'i' &&
                    segment[3] == (byte)'f' &&
                    segment[4] == 0 &&
                    segment[5] == 0)
                {
                    exifRaw = segment.ToArray();
                    int orientation = ParseExifOrientation(segment.Slice(6));
                    if (orientation >= 1 && orientation <= 8) return orientation;
                }
            }

            i += length;
        }
        return 1;
    }

    private static int ParseExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        bool little = tiff[0] == 0x49 && tiff[1] == 0x49;
        bool big = tiff[0] == 0x4D && tiff[1] == 0x4D;
        if (!little && !big) return 1;

        if (ReadU16(tiff, little, 2) != 42) return 1;
        uint ifd0 = ReadU32(tiff, little, 4);
        if (ifd0 + 2 > tiff.Length) return 1;

        int offset = (int)ifd0;
        ushort count = ReadU16(tiff, little, offset);
        offset += 2;
        for (int i = 0; i < count; i++)
        {
            int entry = offset + (i * 12);
            if (entry + 12 > tiff.Length) break;
            ushort tag = ReadU16(tiff, little, entry);
            if (tag != 0x0112) continue;
            ushort type = ReadU16(tiff, little, entry + 2);
            uint num = ReadU32(tiff, little, entry + 4);
            if (num != 1) return 1;
            if (type == 3)
            {
                ushort val = ReadU16(tiff, little, entry + 8);
                return val;
            }
            if (type == 4)
            {
                uint val = ReadU32(tiff, little, entry + 8);
                return (int)val;
            }
            return 1;
        }

        return 1;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> tiff, bool little, int offset)
    {
        if (offset + 2 > tiff.Length) return 0;
        return little
            ? (ushort)(tiff[offset] | (tiff[offset + 1] << 8))
            : (ushort)((tiff[offset] << 8) | tiff[offset + 1]);
    }

    private static uint ReadU32(ReadOnlySpan<byte> tiff, bool little, int offset)
    {
        if (offset + 4 > tiff.Length) return 0;
        return little
            ? (uint)(tiff[offset] | (tiff[offset + 1] << 8) | (tiff[offset + 2] << 16) | (tiff[offset + 3] << 24))
            : (uint)((tiff[offset] << 24) | (tiff[offset + 1] << 16) | (tiff[offset + 2] << 8) | tiff[offset + 3]);
    }
}
