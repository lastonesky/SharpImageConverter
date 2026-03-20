using SharpImageConverter.Core;
using SharpImageConverter.Metadata;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Jpeg;

public static class JpegDecoder
{
    public static JpegImage Decode(ReadOnlySpan<byte> data, bool useFloatingPointIdct = false)
    {
        var parser = new Parser(data, useFloatingPointIdct);
        return parser.Decode();
    }

    public static JpegImage Decode(Stream stream, bool useFloatingPointIdct = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = ReadAllBytesFromStreamPooled(stream);
        return Decode(data, useFloatingPointIdct);
    }

    public static async Task<StreamingDecodeResult> DecodeFromStreamAsync(Stream stream, CancellationToken cancellationToken = default, bool useFloatingPointIdct = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using var input = new JpegStreamInput(stream);
        var parser = new StreamingParser(input, useFloatingPointIdct);
        return await parser.DecodeAsync(cancellationToken).ConfigureAwait(false);
    }

    public readonly record struct StreamingDecodeResult(JpegImage Image, byte[]? ExifRaw, int ExifOrientation);

    private static byte[] ReadAllBytesFromStreamPooled(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining < 0) throw new InvalidDataException("Invalid stream position.");
            if (remaining > int.MaxValue) throw new InvalidDataException("Stream too large.");
            int length = (int)remaining;
            byte[] data = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(data, read, length - read);
                if (n == 0) throw new InvalidDataException("Unexpected EOF");
                read += n;
            }
            return data;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        int total = 0;
        try
        {
            while (true)
            {
                if (total == rented.Length)
                {
                    int newSize = checked(rented.Length << 1);
                    byte[] enlarged = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(rented, 0, enlarged, 0, total);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = enlarged;
                }

                int read = stream.Read(rented, total, rented.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }

            byte[] data = new byte[total];
            if (total != 0)
            {
                Buffer.BlockCopy(rented, 0, data, 0, total);
            }
            return data;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> data;
        private int offset;

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
        private int queuedMarker = -1;
        private bool hasJfif;
        private bool hasAdobe;
        private byte adobeTransform;
        private IccProfileCollector? iccCollector;
        private byte[]? exifRaw;
        private int exifOrientation = 1;
        private readonly bool useFloatingPointIdct;

        public Parser(ReadOnlySpan<byte> data, bool useFloatingPointIdct)
        {
            this.data = data;
            offset = 0;
            this.useFloatingPointIdct = useFloatingPointIdct;

            for (int i = 0; i < 4; i++)
            {
                quantTables[i] = new QuantizationTable();
                dcTables[i] = new HuffmanDecodingTable();
                acTables[i] = new HuffmanDecodingTable();
            }
        }

        public JpegImage Decode()
        {
            try
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
                        case JpegMarker.APP2:
                            ParseApp2();
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
            finally
            {
                iccCollector?.Dispose();
            }
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
                hasJfif = true;
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
                exifRaw = segment.ToArray();
                exifOrientation = ParseExifOrientation(segment);
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

        private void ParseApp2()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out _, out int segmentLength);
            if (segmentLength < 14)
            {
                return;
            }

            iccCollector ??= new IccProfileCollector();
            iccCollector.Add(segment);
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
            JpegColorSpace colorSpace = DetermineColorSpace(components, hasJfif, hasAdobe, adobeTransform);
            JpegPixelFormat pixelFormat = PixelFormatFromColorSpace(colorSpace);
            int[] componentOrder = BuildComponentOrder(components, colorSpace);
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
                    ComponentState c = components[0];
                    int w = frame.McuX * c.H * 8;
                    int h = frame.McuY * c.V * 8;
                    if (w >= width && h >= height)
                    {
                        output = new byte[checked(width * height)];
                        if (w == width && h == height)
                        {
                            c.DecodeSpatial(output.AsSpan(0, w * h), w, h, w, quantTables[c.QuantTableId].Table, useFloatingPointIdct);
                        }
                        else
                        {
                            byte[] plane = ArrayPool<byte>.Shared.Rent(w * h);
                            try
                            {
                                c.DecodeSpatial(plane.AsSpan(0, w * h), w, h, w, quantTables[c.QuantTableId].Table, useFloatingPointIdct);
                                for (int y = 0; y < height; y++)
                                {
                                    Buffer.BlockCopy(plane, y * w, output, y * width, width);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(plane);
                            }
                        }
                        handled = true;
                    }
                }

                if (!handled && colorSpace == JpegColorSpace.Rgb && channelCount == 3)
                {
                    bool fullRes = true;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int compIndex = componentOrder[channel];
                        ComponentState c = components[compIndex];
                        if (c.H != maxH || c.V != maxV)
                        {
                            fullRes = false;
                            break;
                        }
                    }

                    if (fullRes)
                    {
                        output = new byte[checked(width * height * 3)];
                        for (int channel = 0; channel < 3; channel++)
                        {
                            int compIndex = componentOrder[channel];
                            ComponentState c = components[compIndex];
                            int w = frame.McuX * c.H * 8;
                            int h = frame.McuY * c.V * 8;
                            if (w < width || h < height)
                            {
                                fullRes = false;
                                break;
                            }

                            byte[] plane = ArrayPool<byte>.Shared.Rent(w * h);
                            try
                            {
                                c.DecodeSpatial(plane.AsSpan(0, w * h), w, h, w, quantTables[c.QuantTableId].Table, useFloatingPointIdct);
                                for (int y = 0; y < height; y++)
                                {
                                    int srcRow = y * w;
                                    int dstRow = y * width * 3 + channel;
                                    for (int x = 0; x < width; x++)
                                    {
                                        output[dstRow + x * 3] = plane[srcRow + x];
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(plane);
                            }
                        }
                    }

                    if (fullRes)
                    {
                        handled = true;
                    }
                    else
                    {
                        output = null;
                    }
                }

                if (!handled && colorSpace == JpegColorSpace.YCbCr && !useFloatingPointIdct && Sse2.IsSupported)
                {
                    output = new byte[checked(width * height * channelCount)];
                    if (TryDecodeInterleavedYCbCrSimd(components, output, width, height, fullWidth, fullHeight, componentOrder, quantTables, useFloatingPointIdct, frame))
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
                    InterleaveComponents(planes, planeStrides, planeWidths, planeHeights, fullWidth, fullHeight, width, height, componentOrder, output);
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

        private void ParseSosAndDecodeScan()
        {
            if (!hasFrame)
            {
                ThrowHelper.ThrowInvalidData("SOS before SOF.");
            }

            ReadOnlySpan<byte> segment = ReadSegment(out int segmentStart, out int segmentLength);
            if (segmentLength < 1)
            {
                ThrowHelper.ThrowInvalidData("Invalid SOS length.");
            }

            int count = segment[0];
            if (count <= 0 || count > 4)
            {
                ThrowHelper.ThrowInvalidData("Invalid SOS component count.");
            }

            if (count > components.Length || segmentLength < 1 + (2 * count) + 3)
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

                if (FindComponentIndex(components, cs) < 0)
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

                    quantTableDefined[tq] = true;
                }
                else if (pq == 1)
                {
                    if (p + 128 > segmentLength) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                    for (int i = 0; i < 64; i++)
                    {
                        quantTables[tq].Table[zigzag[i]] = ReadU16(segment.Slice(p + (i * 2)));
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
                    dcTableDefined[th] = true;
                }
                else
                {
                    acTables[th].Build(bits, values);
                    acTableDefined[th] = true;
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

        private static ushort ReadU16(ReadOnlySpan<byte> s) => (ushort)((s[0] << 8) | s[1]);
    }

    private sealed class StreamingParser
    {
        private readonly JpegStreamInput input;
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
        private int queuedMarker = -1;
        private byte[]? exifRaw;
        private int exifOrientation = 1;
        private bool hasJfif;
        private bool hasAdobe;
        private byte adobeTransform;
        private IccProfileCollector? iccCollector;
        private readonly bool useFloatingPointIdct;

        public StreamingParser(JpegStreamInput input, bool useFloatingPointIdct)
        {
            this.input = input;
            this.useFloatingPointIdct = useFloatingPointIdct;
            for (int i = 0; i < 4; i++)
            {
                quantTables[i] = new QuantizationTable();
                dcTables[i] = new HuffmanDecodingTable();
                acTables[i] = new HuffmanDecodingTable();
            }
        }

        public async Task<StreamingDecodeResult> DecodeAsync(CancellationToken cancellationToken)
        {
            try
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
                        case JpegMarker.APP2:
                            await ParseApp2Async(cancellationToken).ConfigureAwait(false);
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
            finally
            {
                iccCollector?.Dispose();
            }
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
                    hasJfif = true;
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
                        int orientation = ParseExifOrientation(span);
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

        private async Task ParseApp2Async(CancellationToken cancellationToken)
        {
            SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (segment.Length < 14)
                {
                    return;
                }

                iccCollector ??= new IccProfileCollector();
                iccCollector.Add(segment.Span);
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
            JpegColorSpace colorSpace = DetermineColorSpace(components, hasJfif, hasAdobe, adobeTransform);
            JpegPixelFormat pixelFormat = PixelFormatFromColorSpace(colorSpace);
            int[] componentOrder = BuildComponentOrder(components, colorSpace);
            int channelCount = componentOrder.Length;

            byte[][] planes = new byte[components.Length][];
            int[] planeStrides = new int[components.Length];
            int[] planeWidths = new int[components.Length];
            int[] planeHeights = new int[components.Length];
            byte[] output;

            try
            {
                if (colorSpace == JpegColorSpace.YCbCr && !useFloatingPointIdct && Sse2.IsSupported)
                {
                    output = new byte[checked(width * height * channelCount)];
                    if (TryDecodeInterleavedYCbCrSimd(components, output, width, height, fullWidth, fullHeight, componentOrder, quantTables, useFloatingPointIdct, frame))
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

                output = new byte[checked(width * height * channelCount)];
                InterleaveComponents(planes, planeStrides, planeWidths, planeHeights, fullWidth, fullHeight, width, height, componentOrder, output);
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
            var result = new JpegImage(width, height, pixelFormat, bitsPerSample, colorInfo, output);
            result.Metadata.ExifRaw = exifRaw;
            result.Metadata.Orientation = exifOrientation;
            result.Metadata.IccProfile = iccProfile;
            return result;
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
                if (segment.Length < 1)
                {
                    ThrowHelper.ThrowInvalidData("Invalid SOS length.");
                }

                int count = span[0];
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
                    byte cs = span[p++];
                    byte tdta = span[p++];
                    byte td = (byte)(tdta >> 4);
                    byte ta = (byte)(tdta & 0x0F);
                    if (td >= 4 || ta >= 4)
                    {
                        ThrowHelper.ThrowInvalidData("Invalid Huffman table selector.");
                    }

                    if (FindComponentIndex(components, cs) < 0)
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

                byte ss = span[p++];
                byte se = span[p++];
                byte ahal = span[p++];
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

        private async Task<JpegMarker> ReadMarkerAsync(CancellationToken cancellationToken)
        {
            if (queuedMarker >= 0)
            {
                byte m = (byte)queuedMarker;
                queuedMarker = -1;
                return (JpegMarker)m;
            }

            while (true)
            {
                byte b = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (b == 0xFF)
                {
                    break;
                }
            }

            while (true)
            {
                byte b = await input.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (b != 0xFF)
                {
                    return (JpegMarker)b;
                }
            }
        }

        private async Task ReadMarkerExpectedAsync(JpegMarker expected, CancellationToken cancellationToken)
        {
            JpegMarker m = await ReadMarkerAsync(cancellationToken).ConfigureAwait(false);
            if (m != expected)
            {
                ThrowHelper.ThrowInvalidData("Invalid JPEG header.");
            }
        }

        private async Task<SegmentBuffer> ReadSegmentAsync(CancellationToken cancellationToken)
        {
            ushort length = await input.ReadU16Async(cancellationToken).ConfigureAwait(false);
            if (length < 2)
            {
                ThrowHelper.ThrowInvalidData("Invalid segment length.");
            }

            int contentLen = length - 2;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(contentLen);
            await input.ReadExactAsync(buffer.AsMemory(0, contentLen), cancellationToken).ConfigureAwait(false);
            return new SegmentBuffer(buffer, contentLen);
        }

        private async Task SkipAsync(int count, CancellationToken cancellationToken)
        {
            await input.SkipAsync(count, cancellationToken).ConfigureAwait(false);
        }

        private async Task ParseDqtAsync(CancellationToken cancellationToken)
        {
            using SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            int p = 0;
            ReadOnlySpan<byte> zigzag = JpegConstants.ZigZag;

            while (p < segment.Length)
            {
                byte pqTq = segment.Span[p++];
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
                        quantTables[tq].Table[zigzag[i]] = segment.Span[p++];
                    }

                    quantTableDefined[tq] = true;
                }
                else if (pq == 1)
                {
                    if (p + 128 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DQT length.");
                    for (int i = 0; i < 64; i++)
                    {
                        quantTables[tq].Table[zigzag[i]] = (ushort)((segment.Span[p + (i * 2)] << 8) | segment.Span[p + (i * 2) + 1]);
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

        private async Task ParseDhtAsync(CancellationToken cancellationToken)
        {
            using SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            int p = 0;

            while (p < segment.Length)
            {
                byte tcTh = segment.Span[p++];
                int tc = tcTh >> 4;
                int th = tcTh & 0x0F;
                if (th >= 4) ThrowHelper.ThrowInvalidData("Invalid DHT table id.");
                if (tc is not (0 or 1)) ThrowHelper.ThrowInvalidData("Invalid DHT class.");

                if (p + 16 > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
                ReadOnlySpan<byte> bits = segment.Span.Slice(p, 16);
                p += 16;

                int total = 0;
                for (int i = 0; i < 16; i++) total += bits[i];
                if (p + total > segment.Length) ThrowHelper.ThrowInvalidData("Invalid DHT length.");
                ReadOnlySpan<byte> values = segment.Span.Slice(p, total);
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

        private async Task ParseDriAsync(CancellationToken cancellationToken)
        {
            using SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            if (segment.Length != 2)
            {
                ThrowHelper.ThrowInvalidData("Invalid DRI length.");
            }

            restartInterval = (ushort)((segment.Span[0] << 8) | segment.Span[1]);
        }

        private async Task ParseSofAsync(JpegMarker marker, CancellationToken cancellationToken)
        {
            using SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> span = segment.Span;

            if (segment.Length < 6)
            {
                ThrowHelper.ThrowInvalidData("Invalid SOF length.");
            }

            byte precision = span[0];
            if (precision != 8)
            {
                ThrowHelper.ThrowNotSupported("Only 8-bit JPEG is supported.");
            }

            ushort height = (ushort)((span[1] << 8) | span[2]);
            ushort width = (ushort)((span[3] << 8) | span[4]);
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
            Span<byte> seenComponentIds = stackalloc byte[256];
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

        private async Task SkipSegmentAsync(CancellationToken cancellationToken)
        {
            ushort length = await input.ReadU16Async(cancellationToken).ConfigureAwait(false);
            if (length < 2)
            {
                ThrowHelper.ThrowInvalidData("Invalid segment length.");
            }

            await input.SkipAsync(length - 2, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryDecodeInterleavedYCbCrSimd(ComponentState[] components, byte[] output, int width, int height, int fullWidth, int fullHeight, int[] componentOrder, QuantizationTable[] quantTables, bool useFloatingPointIdct, FrameHeader frame)
    {
        if (!Sse2.IsSupported || useFloatingPointIdct) return false;
        if (componentOrder.Length != 3) return false;
        int yIdx = componentOrder[0];
        int cbIdx = componentOrder[1];
        int crIdx = componentOrder[2];

        ComponentState yComp = components[yIdx];
        ComponentState cbComp = components[cbIdx];
        ComponentState crComp = components[crIdx];

        bool is444 = yComp.H == 1 && yComp.V == 1 && cbComp.H == 1 && cbComp.V == 1 && crComp.H == 1 && crComp.V == 1;
        bool is420 = yComp.H == 2 && yComp.V == 2 && cbComp.H == 1 && cbComp.V == 1 && crComp.H == 1 && crComp.V == 1;

        if (!is444 && !is420) return false;

        int mcuX = frame.McuX;
        int mcuY = frame.McuY;
        ushort[] yQuant = quantTables[yComp.QuantTableId].Table;
        ushort[] cbQuant = quantTables[cbComp.QuantTableId].Table;
        ushort[] crQuant = quantTables[crComp.QuantTableId].Table;

        for (int my = 0; my < mcuY; my++)
        {
            for (int mx = 0; mx < mcuX; mx++)
            {
                if (is444)
                {
                    DecodeMcu444Simd(mx, my, yComp, cbComp, crComp, yQuant, cbQuant, crQuant, output, width, height);
                }
                else if (is420)
                {
                    DecodeMcu420Simd(mx, my, yComp, cbComp, crComp, yQuant, cbQuant, crQuant, output, width, height);
                }
            }
        }

        return true;
    }

    private static void DecodeMcu444Simd(int mx, int my, ComponentState yComp, ComponentState cbComp, ComponentState crComp,
        ushort[] yQuant, ushort[] cbQuant, ushort[] crQuant, byte[] output, int width, int height)
    {
        int px = mx * 8;
        int py = my * 8;
        if (px >= width || py >= height) return;

        ReadOnlySpan<short> yBlock = yComp.GetBlockSpan(my * yComp.BlocksX + mx);
        ReadOnlySpan<short> cbBlock = cbComp.GetBlockSpan(my * cbComp.BlocksX + mx);
        ReadOnlySpan<short> crBlock = crComp.GetBlockSpan(my * crComp.BlocksX + mx);

        int stride = width * 3;
        if (px + 8 <= width && py + 8 <= height)
        {
            SimdJpegPipeline.TransformAndConvertYCbCr8x8(yBlock, yQuant, cbBlock, cbQuant, crBlock, crQuant, output.AsSpan(py * stride + px * 3), stride);
        }
    }

    private static void DecodeMcu420Simd(int mx, int my, ComponentState yComp, ComponentState cbComp, ComponentState crComp,
        ushort[] yQuant, ushort[] cbQuant, ushort[] crQuant, byte[] output, int width, int height)
    {
        int px = mx * 16;
        int py = my * 16;
        if (px >= width || py >= height) return;

        int blocksX = yComp.BlocksX;
        ReadOnlySpan<short> y0 = yComp.GetBlockSpan((my * 2 + 0) * blocksX + (mx * 2 + 0));
        ReadOnlySpan<short> y1 = yComp.GetBlockSpan((my * 2 + 0) * blocksX + (mx * 2 + 1));
        ReadOnlySpan<short> y2 = yComp.GetBlockSpan((my * 2 + 1) * blocksX + (mx * 2 + 0));
        ReadOnlySpan<short> y3 = yComp.GetBlockSpan((my * 2 + 1) * blocksX + (mx * 2 + 1));
        ReadOnlySpan<short> cb = cbComp.GetBlockSpan(my * cbComp.BlocksX + mx);
        ReadOnlySpan<short> cr = crComp.GetBlockSpan(my * crComp.BlocksX + mx);

        int stride = width * 3;
        if (px + 16 <= width && py + 16 <= height)
        {
            SimdJpegPipeline.TransformAndConvertYCbCr420(y0, y1, y2, y3, yQuant, cb, cbQuant, cr, crQuant, output.AsSpan(py * stride + px * 3), stride);
        }
    }

    private static JpegColorSpace DetermineColorSpace(ComponentState[] components, bool hasJfif, bool hasAdobe, byte adobeTransform)
    {
        int componentCount = components.Length;
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

            if (HasComponentIds(components, [(byte)'R', (byte)'G', (byte)'B']))
            {
                return JpegColorSpace.Rgb;
            }

            if (hasJfif || HasComponentIds(components, [1, 2, 3]))
            {
                return JpegColorSpace.YCbCr;
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

            if (HasComponentIds(components, [(byte)'C', (byte)'M', (byte)'Y', (byte)'K']))
            {
                return JpegColorSpace.Cmyk;
            }

            if (HasComponentIds(components, [1, 2, 3, 4]))
            {
                // Default 4-component is often YCbCrK (YCCK) in many JPEG libraries if no Adobe APP14
                return JpegColorSpace.Ycck;
            }

            return JpegColorSpace.Unknown4;
        }

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
            JpegColorSpace.Rgb => BuildComponentOrderWithFallback(components, [1, 2, 3], [(byte)'R', (byte)'G', (byte)'B']),
            JpegColorSpace.YCbCr => BuildComponentOrder(components, [1, 2, 3]),
            JpegColorSpace.Cmyk => BuildComponentOrderWithFallback(components, [1, 2, 3, 4], [(byte)'C', (byte)'M', (byte)'Y', (byte)'K']),
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

    private static int[] BuildComponentOrderWithFallback(ComponentState[] components, ReadOnlySpan<byte> expectedIds, ReadOnlySpan<byte> fallbackIds)
    {
        int[] order = BuildComponentOrder(components, expectedIds);
        if (order.Length == expectedIds.Length)
        {
            return order;
        }

        int[] fallback = BuildComponentOrder(components, fallbackIds);
        if (fallback.Length == fallbackIds.Length)
        {
            return fallback;
        }

        return order;
    }

    private static bool IsSequential(ReadOnlySpan<int> order)
    {
        for (int i = 0; i < order.Length; i++)
        {
            if (order[i] != i)
            {
                return false;
            }
        }

        return true;
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

    private static bool HasComponentIds(ComponentState[] components, ReadOnlySpan<byte> expectedIds)
    {
        if (components.Length != expectedIds.Length)
        {
            return false;
        }

        for (int i = 0; i < expectedIds.Length; i++)
        {
            if (FindComponentIndex(components, expectedIds[i]) < 0)
            {
                return false;
            }
        }

        return true;
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

        bool directSample = true;
        for (int c = 0; c < channels; c++)
        {
            int planeIndex = componentOrder[c];
            if (planeWidths[planeIndex] != fullWidth || planeHeights[planeIndex] != fullHeight)
            {
                directSample = false;
                break;
            }
        }

        if (directSample)
        {
            for (int y = 0; y < height; y++)
            {
                int rowOut = y * outStride;
                for (int c = 0; c < channels; c++)
                {
                    int planeIndex = componentOrder[c];
                    int srcRow = y * planeStrides[planeIndex];
                    byte[] plane = planes[planeIndex];
                    int outIndex = rowOut + c;
                    for (int x = 0; x < width; x++)
                    {
                        output[outIndex] = plane[srcRow + x];
                        outIndex += channels;
                    }
                }
            }

            return;
        }

        int mapLength = width * channels;
        int[] x0Map = ArrayPool<int>.Shared.Rent(mapLength);
        int[] x1Map = ArrayPool<int>.Shared.Rent(mapLength);
        byte[] xWeightMap = ArrayPool<byte>.Shared.Rent(mapLength);
        try
        {
            for (int c = 0; c < channels; c++)
            {
                int planeIndex = componentOrder[c];
                int planeW = planeWidths[planeIndex];
                int baseIndex = c * width;
                for (int x = 0; x < width; x++)
                {
                    ComputeLinearSample(x, width, planeW, fullWidth, out int sx0, out int sx1, out byte xWeight);
                    x0Map[baseIndex + x] = sx0;
                    x1Map[baseIndex + x] = sx1;
                    xWeightMap[baseIndex + x] = xWeight;
                }
            }

            for (int y = 0; y < height; y++)
            {
                int rowOut = y * outStride;
                for (int c = 0; c < channels; c++)
                {
                    int planeIndex = componentOrder[c];
                    int planeH = planeHeights[planeIndex];
                    ComputeLinearSample(y, height, planeH, fullHeight, out int sy0, out int sy1, out byte yWeight);
                    int srcRow0 = sy0 * planeStrides[planeIndex];
                    int srcRow1 = sy1 * planeStrides[planeIndex];
                    byte[] plane = planes[planeIndex];
                    int mapBase = c * width;
                    int outIndex = rowOut + c;
                    for (int x = 0; x < width; x++)
                    {
                        int mapIndex = mapBase + x;
                        int sx0 = x0Map[mapIndex];
                        int sx1 = x1Map[mapIndex];
                        int wx = xWeightMap[mapIndex];
                        int top = ((plane[srcRow0 + sx0] * (256 - wx)) + (plane[srcRow0 + sx1] * wx) + 128) >> 8;
                        int bottom = ((plane[srcRow1 + sx0] * (256 - wx)) + (plane[srcRow1 + sx1] * wx) + 128) >> 8;
                        int value = ((top * (256 - yWeight)) + (bottom * yWeight) + 128) >> 8;
                        output[outIndex] = (byte)value;
                        outIndex += channels;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(x0Map);
            ArrayPool<int>.Shared.Return(x1Map);
            ArrayPool<byte>.Shared.Return(xWeightMap);
        }
    }

    private static void ComputeLinearSample(int dstIndex, int dstLength, int srcLength, int fullLength, out int index0, out int index1, out byte weight1)
    {
        if (srcLength <= 1 || fullLength <= 0 || dstLength <= 1)
        {
            index0 = 0;
            index1 = 0;
            weight1 = 0;
            return;
        }

        float srcPos = (((dstIndex + 0.5f) * srcLength) / fullLength) - 0.5f;
        int i0 = (int)MathF.Floor(srcPos);
        float frac = srcPos - i0;

        if (i0 < 0)
        {
            i0 = 0;
            frac = 0f;
        }
        else if (i0 >= srcLength - 1)
        {
            i0 = srcLength - 1;
            frac = 0f;
        }

        index0 = i0;
        index1 = i0 < srcLength - 1 ? i0 + 1 : i0;
        int w = (int)(frac * 256f + 0.5f);
        if (w < 0) w = 0;
        if (w > 255) w = 255;
        weight1 = (byte)w;
    }

    private static void ComputeLinearSample(int index, int length, int fullLength, out int i0, out int i1, out byte w)
    {
        float srcPos = ((index + 0.5f) * fullLength) / length - 0.5f;
        i0 = (int)MathF.Floor(srcPos);
        float frac = srcPos - i0;
        if (i0 < 0) { i0 = 0; frac = 0; }
        if (i0 >= fullLength - 1) { i0 = fullLength - 1; frac = 0; }
        i1 = (i0 < fullLength - 1) ? i0 + 1 : i0;
        w = (byte)(frac * 256 + 0.5f);
    }

    private static int ParseExifOrientation(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) return 1;
        if (data[0] != (byte)'E' || data[1] != (byte)'x' || data[2] != (byte)'i' || data[3] != (byte)'f' || data[4] != 0 || data[5] != 0)
            return 1;

        int tiffBase = 6;
        bool littleEndian;
        if (data[tiffBase + 0] == (byte)'I' && data[tiffBase + 1] == (byte)'I') littleEndian = true;
        else if (data[tiffBase + 0] == (byte)'M' && data[tiffBase + 1] == (byte)'M') littleEndian = false;
        else return 1;

        if (ReadU16(data, tiffBase + 2, littleEndian) != 42) return 1;
        uint ifdOffset = ReadU32(data, tiffBase + 4, littleEndian);
        if (ifdOffset == 0) return 1;

        int p = tiffBase + (int)ifdOffset;
        ushort entryCount = ReadU16(data, p, littleEndian);
        p += 2;

        for (int i = 0; i < entryCount; i++)
        {
            ushort tag = ReadU16(data, p, littleEndian);
            if (tag == 0x0112) // Orientation
            {
                ushort type = ReadU16(data, p + 2, littleEndian);
                uint count = ReadU32(data, p + 4, littleEndian);
                if (type == 3 && count == 1) // SHORT
                {
                    return ReadU16(data, p + 8, littleEndian);
                }
            }

            p += 12;
        }

        return 1;

        static ushort ReadU16(ReadOnlySpan<byte> s, int offset, bool le)
        {
            if (offset < 0 || offset + 2 > s.Length) return 0;
            return le ? (ushort)(s[offset] | (s[offset + 1] << 8)) : (ushort)((s[offset] << 8) | s[offset + 1]);
        }

        static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool le)
        {
            if (offset < 0 || offset + 4 > s.Length) return 0;
            return le
                ? (uint)(s[offset] | (s[offset + 1] << 8) | (s[offset + 2] << 16) | (s[offset + 3] << 24))
                : (uint)((s[offset] << 24) | (s[offset + 1] << 16) | (s[offset + 2] << 8) | s[offset + 3]);
        }
    }

    private sealed class QuantizationTable
    {
        public ushort[] Table { get; } = new ushort[64];
    }

    private readonly record struct FrameHeader(int Width, int Height, int Precision, int MaxH, int MaxV, int McuX, int McuY);

    private sealed class ComponentState
    {
        public byte Id { get; }
        public byte H { get; }
        public byte V { get; }
        public byte QuantTableId { get; }
        public int BlocksX { get; private set; }
        public int BlocksY { get; private set; }
        public short[]? Coefficients { get; private set; }
        public bool HasCoefficients { get; set; }
        public int DcPredictor { get; set; }
        public byte DcTableId { get; private set; }
        public byte AcTableId { get; private set; }

        public ComponentState(byte id, byte h, byte v, byte quantTableId)
        {
            Id = id;
            H = h;
            V = v;
            QuantTableId = quantTableId;
        }

        public void SetGeometry(int mcuX, int mcuY)
        {
            BlocksX = mcuX * H;
            BlocksY = mcuY * V;
        }

        public void EnsureCoefficientBuffer(bool isProgressive)
        {
            if (Coefficients == null)
            {
                Coefficients = new short[BlocksX * BlocksY * 64];
            }
        }

        public void AssignTables(byte dc, byte ac)
        {
            DcTableId = dc;
            AcTableId = ac;
        }

        public void ResetPredictors()
        {
            DcPredictor = 0;
        }

        public Span<short> GetBlockSpan(int blockIndex)
        {
            return Coefficients.AsSpan(blockIndex * 64, 64);
        }

        public void DecodeSpatial(Span<byte> output, int width, int height, int stride, ushort[] quantTable, bool useFloatingPointIdct)
        {
            if (Coefficients == null) return;

            for (int by = 0; by < BlocksY; by++)
            {
                int basePy = by * 8;
                if (basePy >= height) break;

                for (int bx = 0; bx < BlocksX; bx++)
                {
                    int basePx = bx * 8;
                    if (basePx >= width) break;

                    int blockIdx = by * BlocksX + bx;
                    Span<short> block = GetBlockSpan(blockIdx);

                    int rowOffset = basePy * stride;
                    Span<byte> dest = output.Slice(rowOffset + basePx);

                    if (useFloatingPointIdct)
                    {
                        FloatingPointIDCT.Transform(block, quantTable, dest, stride);
                    }
                    else
                    {
                        FastIDCT.Transform(block, quantTable, dest, stride);
                    }
                }
            }
        }
    }

    private sealed class IccProfileCollector : IDisposable
    {
        private readonly List<(byte[] Buffer, int Length)> chunks = new();
        public void Add(ReadOnlySpan<byte> segment)
        {
            if (segment.Length < 14) return;
            if (!segment.StartsWith("ICC_PROFILE"u8)) return;
            int len = segment.Length - 14;
            if (len <= 0) return;
            byte[] chunk = ArrayPool<byte>.Shared.Rent(len);
            segment.Slice(14).CopyTo(chunk);
            chunks.Add((chunk, len));
        }
        public byte[]? GetProfile()
        {
            if (chunks.Count == 0) return null;
            int totalLen = 0;
            foreach (var chunk in chunks) totalLen += chunk.Length;
            byte[] profile = new byte[totalLen];
            int offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.Buffer.AsSpan(0, chunk.Length).CopyTo(profile.AsSpan(offset));
                offset += chunk.Length;
            }
            Dispose();
            return profile;
        }

        public void Dispose()
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(chunks[i].Buffer);
            }
            chunks.Clear();
        }
    }

    private readonly record struct ScanHeader(ScanComponent[] Components, byte Ss, byte Se, byte Ah, byte Al);
    private readonly record struct ScanComponent(byte ComponentId, byte DcTableId, byte AcTableId);

    private sealed class SegmentBuffer : IDisposable
    {
        private readonly byte[] buffer;
        public int Length { get; }
        public ReadOnlySpan<byte> Span => buffer.AsSpan(0, Length);
        public SegmentBuffer(byte[] buffer, int length) { this.buffer = buffer; Length = length; }
        public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);
    }
}
