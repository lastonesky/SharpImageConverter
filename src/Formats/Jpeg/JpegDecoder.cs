using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Jpeg;

public static partial class JpegDecoder
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

    /// <summary>
    /// 内存解码适配器：直接对完整字节切片执行 marker/segment 解析与熵解码（零拷贝）。
    /// 所有跨数据源共享的解析与重建逻辑都在 <see cref="JpegFrameState"/> 中。
    /// </summary>
    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> data;
        private int offset;
        private int queuedMarker = -1;
        private readonly JpegFrameState state;

        public Parser(ReadOnlySpan<byte> data, bool useFloatingPointIdct)
        {
            this.data = data;
            offset = 0;
            state = new JpegFrameState(useFloatingPointIdct);
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
                            state.ParseApp0(ReadSegmentSpan());
                            break;
                        case JpegMarker.APP1:
                            state.ParseApp1(ReadSegmentSpan());
                            break;
                        case JpegMarker.APP2:
                            state.ParseApp2(ReadSegmentSpan());
                            break;
                        case JpegMarker.APP14:
                            state.ParseApp14(ReadSegmentSpan());
                            break;
                        case JpegMarker.COM:
                            SkipSegment();
                            break;
                        case JpegMarker.DQT:
                            state.ParseDqt(ReadSegmentSpan());
                            break;
                        case JpegMarker.DHT:
                            state.ParseDht(ReadSegmentSpan());
                            break;
                        case JpegMarker.DRI:
                            state.ParseDri(ReadSegmentSpan());
                            break;
                        case JpegMarker.SOF0:
                        case JpegMarker.SOF2:
                            state.ParseSof(marker, ReadSegmentSpan());
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

                state.ValidateScansComplete();
                return state.ReconstructImage();
            }
            finally
            {
                state.DisposeResources();
            }
        }

        private void ParseSosAndDecodeScan()
        {
            ReadOnlySpan<byte> segment = ReadSegment(out int segmentStart, out int segmentLength);
            ScanHeader scan = state.ParseSosHeader(segment);

            int entropyStart = segmentStart + segmentLength;
            ReadOnlySpan<byte> entropyData = data[entropyStart..];

            var reader = new JpegBitReader(entropyData);
            state.DecodeScan(scan, ref reader);

            reader.AlignToByte();
            reader.DiscardBufferedBits();
            _ = reader.PeekBits(1);
            if (reader.HasPendingMarker)
            {
                queuedMarker = reader.PendingMarker;
                reader.ClearPendingMarker();
            }

            offset = entropyStart + reader.BytesConsumed;
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

        private ReadOnlySpan<byte> ReadSegmentSpan() => ReadSegment(out _, out _);

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

    /// <summary>
    /// 流式解码适配器：通过 <see cref="JpegStreamInput"/> 按需读取 marker/segment 与熵数据。
    /// 共享解析与重建逻辑同样位于 <see cref="JpegFrameState"/>。
    /// </summary>
    private sealed class StreamingParser
    {
        private readonly JpegStreamInput input;
        private int queuedMarker = -1;
        private readonly JpegFrameState state;

        public StreamingParser(JpegStreamInput input, bool useFloatingPointIdct)
        {
            this.input = input;
            state = new JpegFrameState(useFloatingPointIdct);
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
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseApp0(segment.Span);
                            }
                            break;
                        case JpegMarker.APP1:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseApp1(segment.Span);
                            }
                            break;
                        case JpegMarker.APP2:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseApp2(segment.Span);
                            }
                            break;
                        case JpegMarker.APP14:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseApp14(segment.Span);
                            }
                            break;
                        case JpegMarker.COM:
                            await SkipSegmentAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        case JpegMarker.DQT:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseDqt(segment.Span);
                            }
                            break;
                        case JpegMarker.DHT:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseDht(segment.Span);
                            }
                            break;
                        case JpegMarker.DRI:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseDri(segment.Span);
                            }
                            break;
                        case JpegMarker.SOF0:
                        case JpegMarker.SOF2:
                            using (SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false))
                            {
                                state.ParseSof(marker, segment.Span);
                            }
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

                state.ValidateScansComplete();
                var img = state.ReconstructImage();
                return new StreamingDecodeResult(img, state.ExifRaw, state.ExifOrientation);
            }
            finally
            {
                state.DisposeResources();
            }
        }

        private async Task ParseSosAndDecodeScanAsync(CancellationToken cancellationToken)
        {
            using SegmentBuffer segment = await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            ScanHeader scan = state.ParseSosHeader(segment.Span);

            var reader = new JpegBitReader(input, cancellationToken);
            state.DecodeScan(scan, ref reader);

            reader.AlignToByte();
            reader.DiscardBufferedBits();
            _ = reader.PeekBits(1);
            if (reader.HasPendingMarker)
            {
                queuedMarker = reader.PendingMarker;
                reader.ClearPendingMarker();
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
            try
            {
                await input.ReadExactAsync(buffer.AsMemory(0, contentLen), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 读取失败（如截断流）时归还租用缓冲，避免泄漏。
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            return new SegmentBuffer(buffer, contentLen);
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
}
