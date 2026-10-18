using System;
using System.Buffers.Binary;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Bmp
{
    /// <summary>
    /// BMP 逐行源：支持 8-bit 调色板、24-bit、32-bit 的未压缩 BMP。
    /// </summary>
    /// <remarks>
    /// BMP 行天然独立，读取时按文件中的行序 seek，不会把整幅像素读进内存。
    /// 32-bit BMP 的 alpha 常被忽略，这里按不透明处理（与现有普通转换的 RGB24 路径一致）。
    /// </remarks>
    internal sealed class BmpRowSource : IRowImageSource
    {
        private readonly FileStream _stream;
        private readonly int _rowStride;
        private readonly int _bpp;
        private readonly bool _bottomUp;
        private readonly byte[] _rowBuffer;
        private readonly byte[]? _palette;
        private readonly long _pixelOffset;
        private int _nextRow;

        public BmpRowSource(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

            Span<byte> fileHeader = stackalloc byte[14];
            _stream.ReadExactly(fileHeader);
            if (fileHeader[0] != 'B' || fileHeader[1] != 'M') throw new InvalidDataException("Not a BMP file");

            uint pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader[10..14]);
            Span<byte> dib = stackalloc byte[40];
            _stream.ReadExactly(dib);
            uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
            if (dibSize < 40) throw new NotSupportedException("BMP DIB header is too small");

            int rawWidth = BinaryPrimitives.ReadInt32LittleEndian(dib[4..8]);
            int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..12]);
            int bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..16]);
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..20]);
            uint clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..36]);

            if (rawWidth <= 0 || rawHeight == 0) throw new InvalidDataException("Invalid BMP dimensions");
            if (compression != 0) throw new NotSupportedException("Only uncompressed BMP is supported for streaming");
            if (bpp is not (8 or 24 or 32)) throw new NotSupportedException($"Streaming BMP does not support {bpp}-bit pixels");

            Width = rawWidth;
            Height = Math.Abs(rawHeight);
            _bottomUp = rawHeight > 0;
            _bpp = bpp;
            _pixelOffset = pixelOffset;
            Channels = 3;
            HasAlpha = false;

            int rowBits = Width * bpp;
            _rowStride = ((rowBits + 31) >> 5) << 2;
            _rowBuffer = new byte[_rowStride];

            if (bpp == 8)
            {
                int entries = clrUsed == 0 ? 256 : (int)clrUsed;
                entries = Math.Clamp(entries, 1, 256);
                var palette = new byte[entries * 3];
                Span<byte> bgra = stackalloc byte[4];
                for (int i = 0; i < entries; i++)
                {
                    _stream.ReadExactly(bgra);
                    palette[i * 3] = bgra[2];
                    palette[i * 3 + 1] = bgra[1];
                    palette[i * 3 + 2] = bgra[0];
                }
                _palette = palette;
            }
        }

        internal static bool IsSupported(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> head = stackalloc byte[34];
                if (fs.ReadAtLeast(head, 34, throwOnEndOfStream: false) < 34) return false;
                if (head[0] != 'B' || head[1] != 'M') return false;
                int bpp = BinaryPrimitives.ReadUInt16LittleEndian(head[28..30]);
                uint compression = BinaryPrimitives.ReadUInt32LittleEndian(head[30..34]);
                return bpp is 8 or 24 or 32 && compression == 0;
            }
            catch
            {
                return false;
            }
        }

        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public bool HasAlpha { get; }

        public bool ReadRow(Span<byte> row)
        {
            if (_nextRow >= Height) return false;
            if (row.Length < Width * 3) throw new ArgumentException("Row buffer too small", nameof(row));

            int fileRow = _bottomUp ? Height - 1 - _nextRow : _nextRow;
            _stream.Seek(_pixelOffset + (long)fileRow * _rowStride, SeekOrigin.Begin);
            _stream.ReadExactly(_rowBuffer);

            switch (_bpp)
            {
                case 24:
                    for (int x = 0, s = 0, d = 0; x < Width; x++, s += 3, d += 3)
                    {
                        row[d] = _rowBuffer[s + 2];
                        row[d + 1] = _rowBuffer[s + 1];
                        row[d + 2] = _rowBuffer[s];
                    }
                    break;
                case 32:
                    for (int x = 0, s = 0, d = 0; x < Width; x++, s += 4, d += 3)
                    {
                        row[d] = _rowBuffer[s + 2];
                        row[d + 1] = _rowBuffer[s + 1];
                        row[d + 2] = _rowBuffer[s];
                    }
                    break;
                default:
                {
                    byte[] palette = _palette!;
                    int colors = palette.Length / 3;
                    for (int x = 0; x < Width; x++)
                    {
                        int index = _rowBuffer[x];
                        if (index >= colors) index = 0;
                        row[x * 3] = palette[index * 3];
                        row[x * 3 + 1] = palette[index * 3 + 1];
                        row[x * 3 + 2] = palette[index * 3 + 2];
                    }
                    break;
                }
            }

            _nextRow++;
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>
    /// BMP 逐行汇：写 24-bit BMP，按文件要求的自底向上行序 seek 写入。
    /// </summary>
    internal sealed class BmpRowSink : IRowImageSink
    {
        private readonly FileStream _stream;
        private readonly int _width;
        private readonly int _height;
        private readonly int _rowStride;
        private readonly long _pixelOffset;
        private readonly byte[] _rowBuffer;
        private readonly bool _gray;
        private int _nextRow;
        private bool _completed;

        public BmpRowSink(string path, int width, int height, bool gray = false)
        {
            _gray = gray;
            _width = width;
            _height = height;
            _rowStride = (((width * 24) + 31) >> 5) << 2;
            _pixelOffset = 14 + 40;
            _rowBuffer = new byte[_rowStride];

            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            Span<byte> header = stackalloc byte[54];
            header[0] = (byte)'B';
            header[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], (uint)(_pixelOffset + (long)_rowStride * height));
            BinaryPrimitives.WriteUInt32LittleEndian(header[10..14], (uint)_pixelOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..18], 40);
            BinaryPrimitives.WriteInt32LittleEndian(header[18..22], width);
            // 负高度 = top-down：流式行按顺序写出，不需要按行 seek。
            BinaryPrimitives.WriteInt32LittleEndian(header[22..26], -height);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..28], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..30], 24);
            BinaryPrimitives.WriteUInt32LittleEndian(header[34..38], (uint)(_rowStride * height));
            _stream.Write(header);
            _stream.SetLength(_pixelOffset + (long)_rowStride * height);
        }

        public void WriteRow(ReadOnlySpan<byte> row, int channels)
        {
            if (_nextRow >= _height) throw new InvalidOperationException("Too many rows");
            if (row.Length < _width * channels) throw new ArgumentException("Row buffer too small", nameof(row));

            Array.Clear(_rowBuffer);
            for (int x = 0; x < _width; x++)
            {
                int s = x * channels;
                int d = x * 3;
                if (_gray)
                {
                    byte luma = (byte)((row[s] * 77 + row[s + 1] * 150 + row[s + 2] * 29) >> 8);
                    _rowBuffer[d] = luma;
                    _rowBuffer[d + 1] = luma;
                    _rowBuffer[d + 2] = luma;
                }
                else
                {
                    _rowBuffer[d] = row[s + 2];
                    _rowBuffer[d + 1] = row[s + 1];
                    _rowBuffer[d + 2] = row[s];
                }
            }

            _stream.Write(_rowBuffer);
            _nextRow++;
        }

        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            _stream.Flush();
        }

        public void Dispose()
        {
            if (!_completed) Complete();
            _stream.Dispose();
        }
    }
}
