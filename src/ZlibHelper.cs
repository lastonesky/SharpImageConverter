using System;
using System.IO;
using System.IO.Compression;

namespace PictureSharp;

public static class ZlibHelper
{
    // Decompress a Zlib stream (CMF+FLG ... Data ... Adler32)
    public static byte[] Decompress(byte[] data)
    {
        if (data == null || data.Length < 6)
            throw new ArgumentException("Invalid Zlib data");

        // Validate CMF and FLG
        byte cmf = data[0];
        byte flg = data[1];
        
        if ((cmf & 0x0F) != 8) // Compression method must be 8 (Deflate)
            throw new NotSupportedException("Only Deflate compression is supported");

        if (((cmf * 256 + flg) % 31) != 0)
            throw new InvalidDataException("Invalid Zlib header check");

        // DeflateStream expects raw deflate data (without zlib header/footer)
        // We skip first 2 bytes (CMF, FLG) and last 4 bytes (Adler32)
        using (var ms = new MemoryStream(data, 2, data.Length - 6))
        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            ds.CopyTo(outMs);
            byte[] decompressed = outMs.ToArray();

            // Verify Adler32
            // Note: In a production environment, we should verify Adler32. 
            // For now we implement it to ensure data integrity.
            uint expectedAdler = (uint)((data[data.Length - 4] << 24) | (data[data.Length - 3] << 16) | (data[data.Length - 2] << 8) | data[data.Length - 1]);
            uint actualAdler = Adler32.Compute(decompressed, 0, decompressed.Length);
            
            if (expectedAdler != actualAdler)
                throw new InvalidDataException($"Adler32 Checksum failed. Expected {expectedAdler:X8}, got {actualAdler:X8}");

            return decompressed;
        }
    }

    // Compress data to Zlib format
    public static byte[] Compress(byte[] data)
    {
        using (var outMs = new MemoryStream())
        {
            // Zlib Header
            // CMF: 0x78 (Deflate, 32K window)
            // FLG: 0xDA (Default compression) -> 0x78DA % 31 == 0
            // We'll use 0x78 0x9C (Default compression level)
            // 0x78 = 0111 1000 (CM = 8, CINFO = 7 -> 32K window)
            // 0x9C = 1001 1100 (FLEVEL = 2 -> Default, FDICT = 0, FCHECK = ?)
            // 0x789C = 30876. 30876 % 31 = 0. OK.
            
            outMs.WriteByte(0x78);
            outMs.WriteByte(0x9C);

            using (var ds = new DeflateStream(outMs, CompressionLevel.Optimal, true))
            {
                ds.Write(data, 0, data.Length);
            }

            // Adler32
            uint adler = Adler32.Compute(data, 0, data.Length);
            outMs.WriteByte((byte)((adler >> 24) & 0xFF));
            outMs.WriteByte((byte)((adler >> 16) & 0xFF));
            outMs.WriteByte((byte)((adler >> 8) & 0xFF));
            outMs.WriteByte((byte)(adler & 0xFF));

            return outMs.ToArray();
        }
    }
}
