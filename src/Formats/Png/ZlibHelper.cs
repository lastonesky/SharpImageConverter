using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using SharpImageConverter.Formats.Png;

namespace SharpImageConverter.Formats.Png;

public static class ZlibHelper
{
    public static byte[] Decompress(byte[] data)
    {
        return Decompress(data, 0, data.Length);
    }

    public static byte[] Decompress(byte[] data, int offset, int count)
    {
        return Decompress(data, offset, count, 0);
    }

    public static byte[] Decompress(byte[] data, int offset, int count, int expectedDecompressedSize)
    {
        if (data == null || count < 6)
            throw new ArgumentException("Invalid Zlib data");

        byte cmf = data[offset];
        byte flg = data[offset + 1];
        if ((cmf & 0x0F) != 8)
            throw new NotSupportedException("Only Deflate compression is supported");
        if ((flg & 0x20) != 0)
            throw new NotSupportedException("Preset dictionary is not supported");
        if ((((cmf << 8) | flg) % 31) != 0)
            throw new InvalidDataException("Invalid Zlib header check");

        int end = offset + count;
        uint expectedAdler = (uint)((data[end - 4] << 24) | (data[end - 3] << 16) | (data[end - 2] << 8) | data[end - 1]);

        using var ms = new MemoryStream(data, offset + 2, count - 6, false);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = expectedDecompressedSize > 0 ? new MemoryStream(expectedDecompressedSize) : new MemoryStream();

        uint adler = 1u;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                int read = ds.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                outMs.Write(buffer, 0, read);
                adler = Adler32.Update(adler, buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (expectedAdler != adler)
            throw new InvalidDataException($"Adler32 Checksum failed. Expected {expectedAdler:X8}, got {adler:X8}");

        return outMs.ToArray();
    }

    public static byte[] Compress(byte[] data)
    {
        using var outMs = new PooledMemoryStream(data.Length + 64);
        CompressRaw(s => s.Write(data, 0, data.Length), outMs);
        ArraySegment<byte> segment = outMs.GetBuffer();
        var result = new byte[segment.Count];
        Buffer.BlockCopy(segment.Array, segment.Offset, result, 0, segment.Count);
        return result;
    }

    public static void CompressRaw(Action<Stream> writeRaw, Stream output)
    {
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        var ds = new DeflateStream(output, CompressionLevel.Optimal, true);
        var ads = new Adler32Stream(ds);
        try
        {
            writeRaw(ads);
        }
        finally
        {
            ads.Dispose();
        }
        uint adler = ads.Adler;
        output.WriteByte((byte)((adler >> 24) & 0xFF));
        output.WriteByte((byte)((adler >> 16) & 0xFF));
        output.WriteByte((byte)((adler >> 8) & 0xFF));
        output.WriteByte((byte)(adler & 0xFF));
    }

    private sealed class Adler32Stream : Stream
    {
        private readonly Stream _baseStream;
        private uint _adler;

        public Adler32Stream(Stream baseStream)
        {
            _baseStream = baseStream;
            _adler = 1u;
        }

        public uint Adler => _adler;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _baseStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            _baseStream.Write(buffer, offset, count);
            _adler = Adler32.Update(_adler, buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _baseStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
