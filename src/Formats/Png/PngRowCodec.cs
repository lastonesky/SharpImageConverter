using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Png
{
    /// <summary>
    /// PNG 逐行源：支持非交错、8/16-bit 的灰度/RGB/调色板/带 alpha 图像。
    /// </summary>
    /// <remarks>
    /// zlib 流按扫描线解压，逐行反滤波后立即转换成 RGB/RGBA；不会保留整幅原始扫描线。
    /// Adam7 交错图需要乱序写行，当前版本不支持，由调用方回退到全量解码。
    /// </remarks>
    internal sealed class PngRowSource : IRowImageSource
    {
        private readonly FileStream _stream;
        private readonly PngDecoder.IdatStream _idat;
        private readonly ZLibStream _zlib;
        private readonly int _bitDepth;
        private readonly int _colorType;
        private readonly int _rawBpp;
        private readonly byte[] _filtered;
        private readonly byte[] _current;
        private readonly byte[] _previous;
        private readonly byte[]? _palette;
        private readonly byte[]? _paletteAlpha;
        private readonly int _transparentGray;
        private readonly int _transparentR;
        private readonly int _transparentG;
        private readonly int _transparentB;
        private readonly bool _hasTransparencyChunk;
        private int _nextRow;

        private PngRowSource(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            Span<byte> signature = stackalloc byte[8];
            _stream.ReadExactly(signature);
            if (signature[0] != 0x89 || signature[1] != 0x50 || signature[2] != 0x4E || signature[3] != 0x47)
                throw new InvalidDataException("Not a PNG file");

            Span<byte> header = stackalloc byte[8];
            int firstIdatLength = -1;
            int paletteEntries = 0;
            while (true)
            {
                _stream.ReadExactly(header);
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
                if (length < 0) throw new InvalidDataException("PNG chunk too large");

                if (type == 0x49484452) // IHDR
                {
                    Span<byte> ihdr = stackalloc byte[13];
                    _stream.ReadExactly(ihdr);
                    Width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr[..4]);
                    Height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..8]);
                    _bitDepth = ihdr[8];
                    _colorType = ihdr[9];
                    int interlace = ihdr[12];
                    if (Width <= 0 || Height <= 0) throw new InvalidDataException("Invalid PNG dimensions");
                    if (interlace != 0) throw new NotSupportedException("Interlaced PNG is not streamable");
                    if (_bitDepth is not (8 or 16)) throw new NotSupportedException("Only 8/16-bit PNG is streamable");
                    if (_colorType is not (0 or 2 or 3 or 4 or 6)) throw new NotSupportedException("Unsupported PNG color type");
                    _stream.Seek(4, SeekOrigin.Current); // CRC
                }
                else if (type == 0x504C5445) // PLTE
                {
                    _palette = new byte[length];
                    _stream.ReadExactly(_palette);
                    paletteEntries = length / 3;
                    _stream.Seek(4, SeekOrigin.Current);
                }
                else if (type == 0x74524E53) // tRNS
                {
                    byte[] trns = new byte[length];
                    _stream.ReadExactly(trns);
                    _hasTransparencyChunk = length > 0;
                    if (_colorType == 3)
                    {
                        _paletteAlpha = trns;
                    }
                    else if (_colorType == 0 && length >= 2)
                    {
                        _transparentGray = BinaryPrimitives.ReadUInt16BigEndian(trns);
                    }
                    else if (_colorType == 2 && length >= 6)
                    {
                        _transparentR = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(0, 2));
                        _transparentG = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2, 2));
                        _transparentB = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4, 2));
                    }
                    _stream.Seek(4, SeekOrigin.Current);
                }
                else if (type == 0x49444154) // IDAT
                {
                    firstIdatLength = length;
                    break;
                }
                else
                {
                    _stream.Seek((long)length + 4, SeekOrigin.Current);
                }
            }

            if (firstIdatLength < 0) throw new InvalidDataException("PNG has no IDAT");

            int sourceChannels = _colorType switch
            {
                0 => 1,
                2 => 3,
                3 => 1,
                4 => 2,
                _ => 4,
            };
            int bytesPerSample = _bitDepth / 8;
            _rawBpp = sourceChannels * bytesPerSample;
            int stride = Width * _rawBpp;
            _filtered = new byte[stride + 1];
            _current = new byte[stride];
            _previous = new byte[stride];

            bool baseAlpha = _colorType is 4 or 6;
            HasAlpha = baseAlpha || _hasTransparencyChunk;
            Channels = HasAlpha ? 4 : 3;
            if (_colorType == 3 && _palette == null) throw new InvalidDataException("PNG palette is missing");
            if (_colorType == 3 && paletteEntries == 0) throw new InvalidDataException("PNG palette is empty");

            _idat = new PngDecoder.IdatStream(_stream, firstIdatLength, validateCrc: false);
            _zlib = new ZLibStream(_idat, CompressionMode.Decompress, leaveOpen: false);
        }

        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public bool HasAlpha { get; }

        internal static bool IsSupported(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> head = stackalloc byte[33];
                if (fs.ReadAtLeast(head, 33, throwOnEndOfStream: false) < 33) return false;
                if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47) return false;
                if (head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R') return false;
                int bitDepth = head[24];
                int colorType = head[25];
                int interlace = head[28];
                return interlace == 0
                    && bitDepth is 8 or 16
                    && colorType is 0 or 2 or 3 or 4 or 6;
            }
            catch
            {
                return false;
            }
        }

        internal static PngRowSource Open(string path) => new(path);

        public bool ReadRow(Span<byte> row)
        {
            if (_nextRow >= Height) return false;
            if (row.Length < Width * Channels) throw new ArgumentException("Row buffer too small", nameof(row));

            ReadExactly(_zlib, _filtered);
            PngDecoder.UnfilterScanline(_filtered[0], _filtered.AsSpan(1), _current, _previous, _rawBpp);
            ConvertRow(row);
            _current.AsSpan().CopyTo(_previous);
            _nextRow++;
            return true;
        }

        private void ConvertRow(Span<byte> destination)
        {
            int sampleBytes = _bitDepth / 8;
            int sampleMask = _bitDepth == 16 ? 0xFF00 : 0xFFFF;
            int shift = _bitDepth == 16 ? 8 : 0;
            bool alpha = Channels == 4;

            switch (_colorType)
            {
                case 0:
                {
                    int t = _transparentGray;
                    for (int x = 0; x < Width; x++)
                    {
                        int raw = sampleBytes == 2
                            ? BinaryPrimitives.ReadUInt16BigEndian(_current.AsSpan(x * 2, 2))
                            : _current[x];
                        byte gray = (byte)((raw & sampleMask) >> shift);
                        int d = x * Channels;
                        destination[d] = gray;
                        destination[d + 1] = gray;
                        destination[d + 2] = gray;
                        if (alpha) destination[d + 3] = (byte)(raw == t ? 0 : 255);
                    }
                    break;
                }
                case 2:
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int s = x * _rawBpp;
                        int rRaw = sampleBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(_current.AsSpan(s, 2)) : _current[s];
                        int gRaw = sampleBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(_current.AsSpan(s + 2, 2)) : _current[s + 1];
                        int bRaw = sampleBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(_current.AsSpan(s + 4, 2)) : _current[s + 2];
                        int d = x * Channels;
                        destination[d] = (byte)((rRaw & sampleMask) >> shift);
                        destination[d + 1] = (byte)((gRaw & sampleMask) >> shift);
                        destination[d + 2] = (byte)((bRaw & sampleMask) >> shift);
                        if (alpha)
                        {
                            bool transparent = _hasTransparencyChunk
                                && rRaw == _transparentR && gRaw == _transparentG && bRaw == _transparentB;
                            destination[d + 3] = (byte)(transparent ? 0 : 255);
                        }
                    }
                    break;
                }
                case 3:
                {
                    byte[] palette = _palette!;
                    byte[]? paletteAlpha = _paletteAlpha;
                    int colors = palette.Length / 3;
                    for (int x = 0; x < Width; x++)
                    {
                        int index = _current[x];
                        if (index >= colors) index = 0;
                        int d = x * Channels;
                        destination[d] = palette[index * 3];
                        destination[d + 1] = palette[index * 3 + 1];
                        destination[d + 2] = palette[index * 3 + 2];
                        if (alpha)
                        {
                            destination[d + 3] = paletteAlpha != null && index < paletteAlpha.Length
                                ? paletteAlpha[index]
                                : (byte)255;
                        }
                    }
                    break;
                }
                case 4:
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int s = x * _rawBpp;
                        int grayRaw = sampleBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(_current.AsSpan(s, 2)) : _current[s];
                        int aRaw = sampleBytes == 2 ? BinaryPrimitives.ReadUInt16BigEndian(_current.AsSpan(s + 2, 2)) : _current[s + 1];
                        byte gray = (byte)((grayRaw & sampleMask) >> shift);
                        int d = x * Channels;
                        destination[d] = gray;
                        destination[d + 1] = gray;
                        destination[d + 2] = gray;
                        if (alpha) destination[d + 3] = (byte)((aRaw & sampleMask) >> shift);
                    }
                    break;
                }
                default: // 6
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int s = x * _rawBpp;
                        int d = x * Channels;
                        if (sampleBytes == 2)
                        {
                            destination[d] = _current[s];
                            destination[d + 1] = _current[s + 2];
                            destination[d + 2] = _current[s + 4];
                            if (alpha) destination[d + 3] = _current[s + 6];
                        }
                        else
                        {
                            destination[d] = _current[s];
                            destination[d + 1] = _current[s + 1];
                            destination[d + 2] = _current[s + 2];
                            if (alpha) destination[d + 3] = _current[s + 3];
                        }
                    }
                    break;
                }
            }
        }

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer[offset..]);
                if (read <= 0) throw new EndOfStreamException("Unexpected end of PNG data");
                offset += read;
            }
        }

        public void Dispose()
        {
            _zlib.Dispose();
            _idat.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// PNG 逐行汇：逐行 Up 滤波 + zlib 压缩，边压边写 IDAT 块。
    /// </summary>
    internal sealed class PngRowSink : IRowImageSink
    {
        private static readonly byte[] IhdrType = "IHDR"u8.ToArray();
        private static readonly byte[] IdatType = "IDAT"u8.ToArray();
        private static readonly byte[] IendType = "IEND"u8.ToArray();

        private readonly FileStream _stream;
        private readonly IdatChunkStream _idat;
        private readonly ZLibStream _zlib;
        private readonly int _width;
        private readonly int _height;
        private readonly int _sourceChannels;
        private readonly int _outputChannels;
        private readonly bool _gray;
        private readonly byte[] _previous;
        private readonly byte[] _current;
        private readonly byte[] _filtered;
        private int _nextRow;
        private bool _completed;

        internal PngRowSink(string path, int width, int height, int sourceChannels, bool hasAlpha, bool gray)
        {
            _width = width;
            _height = height;
            _sourceChannels = sourceChannels;
            _gray = gray;
            _outputChannels = gray ? 1 : hasAlpha ? 4 : 3;
            _previous = new byte[width * _outputChannels];
            _current = new byte[width * _outputChannels];
            _filtered = new byte[width * _outputChannels];

            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            _stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
            ihdr[8] = 8;
            ihdr[9] = _outputChannels == 1 ? (byte)0 : _outputChannels == 4 ? (byte)6 : (byte)2;
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WriteChunk(_stream, IhdrType, ihdr);

            _idat = new IdatChunkStream(_stream);
            _zlib = new ZLibStream(_idat, CompressionLevel.Optimal, leaveOpen: true);
        }

        public void WriteRow(ReadOnlySpan<byte> row, int channels)
        {
            if (_nextRow >= _height) throw new InvalidOperationException("Too many rows");
            if (channels != _sourceChannels) throw new ArgumentException("Channel count mismatch", nameof(channels));

            for (int x = 0; x < _width; x++)
            {
                int s = x * channels;
                byte r = row[s];
                byte g = row[s + 1];
                byte b = row[s + 2];
                if (_gray)
                {
                    _current[x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                }
                else if (_outputChannels == 4)
                {
                    int d = x * 4;
                    _current[d] = r;
                    _current[d + 1] = g;
                    _current[d + 2] = b;
                    _current[d + 3] = channels == 4 ? row[s + 3] : (byte)255;
                }
                else
                {
                    int d = x * 3;
                    _current[d] = r;
                    _current[d + 1] = g;
                    _current[d + 2] = b;
                }
            }

            for (int i = 0; i < _current.Length; i++)
            {
                _filtered[i] = (byte)(_current[i] - _previous[i]);
            }
            _zlib.WriteByte(2); // Up
            _zlib.Write(_filtered);
            _current.AsSpan().CopyTo(_previous);
            _nextRow++;
        }

        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            _zlib.Dispose();
            _idat.Complete();
            WriteChunk(_stream, IendType, ReadOnlySpan<byte>.Empty);
            _stream.Flush();
        }

        public void Dispose()
        {
            if (!_completed) Complete();
            _stream.Dispose();
        }

        private static void WriteChunk(Stream stream, byte[] type, ReadOnlySpan<byte> data)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
            stream.Write(buffer);
            stream.Write(type);
            if (data.Length > 0) stream.Write(data);
            uint crc = Crc32.Compute(type);
            if (data.Length > 0)
            {
                byte[] copy = data.ToArray();
                crc = Crc32.Update(crc, copy, 0, copy.Length);
            }
            BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
            stream.Write(buffer);
        }

        private sealed class IdatChunkStream(Stream output) : Stream
        {
            private readonly byte[] _buffer = new byte[64 * 1024];
            private int _count;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
                => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                while (buffer.Length > 0)
                {
                    int take = Math.Min(buffer.Length, _buffer.Length - _count);
                    buffer[..take].CopyTo(_buffer.AsSpan(_count));
                    _count += take;
                    buffer = buffer[take..];
                    if (_count == _buffer.Length) FlushChunk();
                }
            }

            private void FlushChunk()
            {
                if (_count == 0) return;
                WriteChunk(output, IdatType, _buffer.AsSpan(0, _count));
                _count = 0;
            }

            public void Complete()
            {
                FlushChunk();
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) Complete();
                base.Dispose(disposing);
            }
        }
    }
}
