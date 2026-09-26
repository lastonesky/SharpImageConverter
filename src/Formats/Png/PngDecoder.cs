using System;
using System.Collections.Generic;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using System.Text;
using SharpImageConverter.Core;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Png;

/// <summary>
/// PNG 解码器，读取 PNG 文件并输出 RGB24 或 RGBA32 像素数据。
/// </summary>
public class PngDecoder
{
    /// <summary>
    /// 图像宽度（像素）
    /// </summary>
    public int Width { get; private set; }
    /// <summary>
    /// 图像高度（像素）
    /// </summary>
    public int Height { get; private set; }
    /// <summary>
    /// 位深（1、2、4、8 或 16）
    /// </summary>
    public byte BitDepth { get; private set; }
    /// <summary>
    /// 颜色类型（0 灰度、2 真彩、3 调色板、4 灰度+Alpha、6 真彩+Alpha）
    /// </summary>
    public byte ColorType { get; private set; }
    /// <summary>
    /// 压缩方法（PNG 规范固定为 0）
    /// </summary>
    public byte CompressionMethod { get; private set; }
    /// <summary>
    /// 滤波方法（PNG 规范固定为 0）
    /// </summary>
    public byte FilterMethod { get; private set; }
    /// <summary>
    /// 隔行方式（0：非隔行，1：Adam7）
    /// </summary>
    public byte InterlaceMethod { get; private set; }

    public bool ValidateChunkCrc { get; set; }

    private byte[]? _palette;
    private byte[]? _transparency;
    private ImageMetadata _metadata = new ImageMetadata();
    public ImageMetadata Metadata => _metadata;
    private const uint TypeIHDR = 0x49484452;
    private const uint TypePLTE = 0x504C5445;
    private const uint TypeIDAT = 0x49444154;
    private const uint TypeTRNS = 0x74524E53;
    private const uint TypeIEND = 0x49454E44;
    private const uint TypeEXIF = 0x65584966;
    private const uint TypeICCP = 0x69434350;
    private const uint TypeSRGB = 0x73524742;
    private static readonly int[] Adam7StartX = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] Adam7StartY = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] Adam7StepX = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] Adam7StepY = [8, 8, 8, 4, 4, 2, 2];

    /// <summary>
    /// 解码 PNG 文件为 RGB24 像素数据
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>按 RGB 顺序排列的字节数组（长度为 Width*Height*3）</returns>
    public byte[] DecodeToRGB(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return DecodeToRGB(fs);
    }

    /// <summary>
    /// 解码 PNG 流为 RGB24 像素数据
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>按 RGB 顺序排列的字节数组（长度为 Width*Height*3）</returns>
    public byte[] DecodeToRGB(Stream stream)
    {
        _palette = null;
        _transparency = null;
        _metadata = new ImageMetadata();
        byte[] sig = new byte[8];
        ReadExact(stream, sig, 0, 8);
        if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");

        byte[] lenBytes = new byte[4];
        byte[] typeBytes = new byte[4];
        byte[] crcBytes = new byte[4];
        while (true)
        {
            if (!TryReadExact(stream, lenBytes, 0, 4)) break;
            uint length = ReadBigEndianUint32(lenBytes, 0);
            if (length > int.MaxValue) throw new InvalidDataException("Chunk too large");
            ReadExact(stream, typeBytes, 0, 4);
            uint type = ReadBigEndianUint32(typeBytes, 0);

            if (type == TypeIDAT)
            {
                int idatLength = (int)length;
                int expectedDecompressedSize = GetExpectedDecompressedSize();
                byte[] decompressed = ArrayPool<byte>.Shared.Rent(expectedDecompressedSize);
                try
                {
                    using var idatStream = new IdatStream(stream, idatLength, ValidateChunkCrc);
                    ZlibHelper.DecompressTo(idatStream, decompressed.AsSpan(0, expectedDecompressedSize));
                    return ProcessImage(decompressed.AsSpan(0, expectedDecompressedSize));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(decompressed);
                }
            }

            byte[] data;
            byte[]? rented = null;
            int dataLength = (int)length;
            if (dataLength > 0)
            {
                if (type == TypeIHDR || type == TypePLTE || type == TypeTRNS || type == TypeEXIF || type == TypeICCP || type == TypeSRGB)
                {
                    data = new byte[dataLength];
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent(dataLength);
                    data = rented;
                }
                ReadExact(stream, data, 0, dataLength);
            }
            else
            {
                data = Array.Empty<byte>();
            }

            ReadExact(stream, crcBytes, 0, 4);
            if (ValidateChunkCrc)
            {
                uint fileCrc = ReadBigEndianUint32(crcBytes, 0);
                uint calcCrc = Crc32.Compute(typeBytes);
                if (dataLength > 0) calcCrc = Crc32.Update(calcCrc, data, 0, dataLength);
                if (calcCrc != fileCrc)
                {
                    string typeName = Encoding.ASCII.GetString(typeBytes);
                    throw new InvalidDataException($"CRC mismatch in chunk {typeName}. Expected {fileCrc:X8}, got {calcCrc:X8}");
                }
            }

            switch (type)
            {
                case TypeIHDR:
                    ParseIHDR(data);
                    break;
                case TypePLTE:
                    _palette = data;
                    break;
                case TypeTRNS:
                    _transparency = data;
                    break;
                case TypeEXIF:
                    ParseExifChunk(data);
                    break;
                case TypeICCP:
                    ParseIccpChunk(data);
                    break;
                case TypeSRGB:
                    ParseSrgbChunk();
                    break;
                case TypeIEND:
                    break;
                default:
                    break;
            }
            if (type == TypeIEND) break;

            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        throw new InvalidDataException("Missing IDAT chunk.");
    }

    /// <summary>
    /// 解码 PNG 文件为 RGBA32 像素数据
    /// </summary>
    /// <param name="path">PNG 文件路径</param>
    /// <returns>按 RGBA 顺序排列的字节数组（长度为 Width*Height*4）</returns>
    public byte[] DecodeToRGBA(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return DecodeToRGBA(fs);
    }

    /// <summary>
    /// 解码 PNG 流为 RGBA32 像素数据
    /// </summary>
    /// <param name="stream">PNG 数据流</param>
    /// <returns>按 RGBA 顺序排列的字节数组（长度为 Width*Height*4）</returns>
    public byte[] DecodeToRGBA(Stream stream)
    {
        _palette = null;
        _transparency = null;
        _metadata = new ImageMetadata();
        byte[] sig = new byte[8];
        ReadExact(stream, sig, 0, 8);
        if (!IsPngSignature(sig)) throw new InvalidDataException("Not a PNG file");
        byte[] lenBytes = new byte[4];
        byte[] typeBytes = new byte[4];
        byte[] crcBytes = new byte[4];
        while (true)
        {
            if (!TryReadExact(stream, lenBytes, 0, 4)) break;
            uint length = ReadBigEndianUint32(lenBytes, 0);
            if (length > int.MaxValue) throw new InvalidDataException("Chunk too large");
            ReadExact(stream, typeBytes, 0, 4);
            uint type = ReadBigEndianUint32(typeBytes, 0);

            if (type == TypeIDAT)
            {
                int idatLength = (int)length;
                int expectedDecompressedSize = GetExpectedDecompressedSize();
                byte[] decompressed = ArrayPool<byte>.Shared.Rent(expectedDecompressedSize);
                try
                {
                    using var idatStream = new IdatStream(stream, idatLength, ValidateChunkCrc);
                    ZlibHelper.DecompressTo(idatStream, decompressed.AsSpan(0, expectedDecompressedSize));
                    return ProcessImageRgba(decompressed.AsSpan(0, expectedDecompressedSize));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(decompressed);
                }
            }
            byte[] data;
            byte[]? rented = null;
            int dataLength = (int)length;
            if (dataLength > 0)
            {
                if (type == TypeIHDR || type == TypePLTE || type == TypeTRNS || type == TypeEXIF || type == TypeICCP || type == TypeSRGB)
                {
                    data = new byte[dataLength];
                }
                else
                {
                    rented = ArrayPool<byte>.Shared.Rent(dataLength);
                    data = rented;
                }
                ReadExact(stream, data, 0, dataLength);
            }
            else
            {
                data = Array.Empty<byte>();
            }
            ReadExact(stream, crcBytes, 0, 4);
            if (ValidateChunkCrc)
            {
                uint fileCrc = ReadBigEndianUint32(crcBytes, 0);
                uint calcCrc = Crc32.Compute(typeBytes);
                if (dataLength > 0) calcCrc = Crc32.Update(calcCrc, data, 0, dataLength);
                if (calcCrc != fileCrc)
                {
                    string typeName = Encoding.ASCII.GetString(typeBytes);
                    throw new InvalidDataException($"CRC mismatch in chunk {typeName}. Expected {fileCrc:X8}, got {calcCrc:X8}");
                }
            }
            switch (type)
            {
                case TypeIHDR:
                    ParseIHDR(data);
                    break;
                case TypePLTE:
                    _palette = data;
                    break;
                case TypeTRNS:
                    _transparency = data;
                    break;
                case TypeEXIF:
                    ParseExifChunk(data);
                    break;
                case TypeICCP:
                    ParseIccpChunk(data);
                    break;
                case TypeSRGB:
                    ParseSrgbChunk();
                    break;
                case TypeIEND:
                    break;
                default:
                    break;
            }
            if (type == TypeIEND) break;
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        throw new InvalidDataException("Missing IDAT chunk.");
    }

    private static bool TryReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, offset + read, count - read);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new InvalidDataException("Unexpected EOF");
            }
            read += n;
        }
        return true;
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        if (!TryReadExact(stream, buffer, offset, count))
            throw new InvalidDataException("Unexpected EOF");
    }

    private sealed class IdatStream : Stream
    {
        private static readonly byte[] IdatType = [(byte)'I', (byte)'D', (byte)'A', (byte)'T'];
        private static readonly byte[] IendType = [(byte)'I', (byte)'E', (byte)'N', (byte)'D'];

        private readonly Stream _stream;
        private readonly bool _validateCrc;
        private readonly byte[] _header = new byte[8];
        private readonly byte[] _crcBytes = new byte[4];
        private int _remaining;
        private uint _crc;
        private bool _done;

        public IdatStream(Stream stream, int firstChunkLength, bool validateCrc)
        {
            _stream = stream;
            _validateCrc = validateCrc;
            _remaining = firstChunkLength;
            if (_validateCrc) _crc = Crc32.Compute(IdatType);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_done) return 0;
            if (_remaining == 0)
            {
                MoveNextChunk();
                if (_done) return 0;
            }

            int toRead = count;
            if (toRead > _remaining) toRead = _remaining;
            int read = _stream.Read(buffer, offset, toRead);
            if (read == 0) throw new InvalidDataException("Unexpected EOF");
            if (_validateCrc) _crc = Crc32.Update(_crc, buffer, offset, read);
            _remaining -= read;
            if (_remaining == 0)
            {
                MoveNextChunk();
            }
            return read;
        }

        private void MoveNextChunk()
        {
            while (true)
            {
                ReadExact(_stream, _crcBytes, 0, 4);
                if (_validateCrc)
                {
                    uint fileCrc = ReadBigEndianUint32(_crcBytes, 0);
                    if (_crc != fileCrc) throw new InvalidDataException("CRC mismatch in IDAT chunk.");
                }

                ReadExact(_stream, _header, 0, 8);
                uint length = ReadBigEndianUint32(_header, 0);
                if (length > int.MaxValue) throw new InvalidDataException("Chunk too large");
                uint type = ReadBigEndianUint32(_header, 4);

                if (type == TypeIDAT)
                {
                    if (_validateCrc) _crc = Crc32.Compute(IdatType);
                    int len = (int)length;
                    if (len == 0)
                    {
                        ReadExact(_stream, _crcBytes, 0, 4);
                        if (_validateCrc)
                        {
                            uint fileCrc = ReadBigEndianUint32(_crcBytes, 0);
                            if (_crc != fileCrc) throw new InvalidDataException("CRC mismatch in IDAT chunk.");
                        }
                        continue;
                    }
                    _remaining = len;
                    return;
                }

                if (type == TypeIEND)
                {
                    uint crc = _validateCrc ? Crc32.Compute(IendType) : 0;
                    int len = (int)length;
                    if (len > 0)
                    {
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
                        try
                        {
                            int remaining = len;
                            while (remaining > 0)
                            {
                                int chunk = remaining > buffer.Length ? buffer.Length : remaining;
                                ReadExact(_stream, buffer, 0, chunk);
                                if (_validateCrc) crc = Crc32.Update(crc, buffer, 0, chunk);
                                remaining -= chunk;
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
                    ReadExact(_stream, _crcBytes, 0, 4);
                    if (_validateCrc)
                    {
                        uint fileCrc = ReadBigEndianUint32(_crcBytes, 0);
                        if (crc != fileCrc) throw new InvalidDataException("CRC mismatch in IEND chunk.");
                    }
                    _done = true;
                    _remaining = 0;
                    return;
                }

                throw new InvalidDataException("Unexpected chunk after IDAT.");
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private void ParseExifChunk(byte[] data)
    {
        if (data.Length == 0) return;
        if (data.Length >= 6 &&
            data[0] == (byte)'E' && data[1] == (byte)'x' && data[2] == (byte)'i' && data[3] == (byte)'f' &&
            data[4] == 0 && data[5] == 0)
        {
            _metadata.ExifRaw = data;
            return;
        }
        var exif = new byte[data.Length + 6];
        exif[0] = (byte)'E';
        exif[1] = (byte)'x';
        exif[2] = (byte)'i';
        exif[3] = (byte)'f';
        exif[4] = 0;
        exif[5] = 0;
        Buffer.BlockCopy(data, 0, exif, 6, data.Length);
        _metadata.ExifRaw = exif;
    }

    private void ParseIccpChunk(byte[] data)
    {
        int nullIdx = Array.IndexOf(data, (byte)0);
        if (nullIdx < 0 || nullIdx + 2 > data.Length) return;
        byte compression = data[nullIdx + 1];
        if (compression != 0) return;
        int payloadOffset = nullIdx + 2;
        int payloadLen = data.Length - payloadOffset;
        if (payloadLen <= 0) return;
        byte[] profile = ZlibHelper.Decompress(data, payloadOffset, payloadLen);
        if (profile.Length == 0) return;
        _metadata.IccProfile = profile;
        _metadata.IccProfileKind = IccProfileKind.Unknown;
    }

    private void ParseSrgbChunk()
    {
        if (_metadata.IccProfile == null)
        {
            _metadata.IccProfileKind = IccProfileKind.SRgb;
        }
    }

    private static bool IsPngSignature(byte[] sig)
    {
        return sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47 &&
               sig[4] == 0x0D && sig[5] == 0x0A && sig[6] == 0x1A && sig[7] == 0x0A;
    }

    private int GetExpectedDecompressedSize()
    {
        int bitsPerPixel = GetBitsPerPixel();
        if (Width <= 0 || Height <= 0) return 0;

        if (InterlaceMethod == 0)
        {
            long stride = ((long)Width * bitsPerPixel + 7) / 8;
            long total = (stride + 1) * Height;
            if ((ulong)total > int.MaxValue) return 0;
            return (int)total;
        }

        long sum = 0;
        int[] startX = Adam7StartX;
        int[] startY = Adam7StartY;
        int[] stepX = Adam7StepX;
        int[] stepY = Adam7StepY;
        for (int pass = 0; pass < 7; pass++)
        {
            int passW = (Width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int passH = (Height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (passW <= 0 || passH <= 0) continue;
            long stride = ((long)passW * bitsPerPixel + 7) / 8;
            sum += (stride + 1) * passH;
            if ((ulong)sum > int.MaxValue) return 0;
        }
        return (int)sum;
    }

    private void ParseIHDR(byte[] data)
    {
        Width = (int)ReadBigEndianUint32(data, 0);
        Height = (int)ReadBigEndianUint32(data, 4);
        BitDepth = data[8];
        ColorType = data[9];
        CompressionMethod = data[10];
        FilterMethod = data[11];
        InterlaceMethod = data[12];

        if (CompressionMethod != 0) throw new NotSupportedException("Unknown compression method");
        if (FilterMethod != 0) throw new NotSupportedException("Unknown filter method");
        if (InterlaceMethod > 1) throw new NotSupportedException("Unknown interlace method");
    }

    private byte[] ProcessImage(ReadOnlySpan<byte> rawData)
    {
        // Calculate bytes per pixel
        int bpp = GetBytesPerPixel();
        
        if (InterlaceMethod == 0)
        {
            return ProcessPass(rawData, Width, Height, bpp);
        }
        else
        {
            return ProcessInterlaced(rawData, bpp);
        }
    }

    private byte[] ProcessImageRgba(ReadOnlySpan<byte> rawData)
    {
        int bpp = GetBytesPerPixel();
        if (InterlaceMethod == 0)
        {
            return ProcessPassRgba(rawData, Width, Height, bpp);
        }
        else
        {
            return ProcessInterlacedRgba(rawData, bpp);
        }
    }

    private byte[] ProcessInterlaced(ReadOnlySpan<byte> rawData, int bpp)
    {
        // Adam7 passes
        // Pass 1: start (0,0), step (8,8)
        // Pass 2: start (4,0), step (8,8)
        // Pass 3: start (0,4), step (4,8)
        // Pass 4: start (2,0), step (4,4)
        // Pass 5: start (0,2), step (2,4)
        // Pass 6: start (1,0), step (2,2)
        // Pass 7: start (0,1), step (1,2)
        
        int[] startX = Adam7StartX;
        int[] startY = Adam7StartY;
        int[] stepX = Adam7StepX;
        int[] stepY = Adam7StepY;

        // Final image buffer (RGBA or RGB)
        // We will decode everything to RGB first for simplicity, 
        // but since we need to support transparency, we might need intermediate storage.
        // The output of DecodeToRGB is byte[] rgb (3 bytes per pixel) as per current BmpWriter.
        // However, if PNG has transparency, we should ideally handle it.
        // For now, let's target 24-bit RGB output to match existing Jpeg2Bmp capability.
        // Transparent pixels will be blended with white or black? Or just dropped?
        // Let's output RGB, composition with background if needed.
        
        byte[] finalImage = new byte[Width * Height * 3]; 
        int dataOffset = 0;

        for (int pass = 0; pass < 7; pass++)
        {
            int passW = (Width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int passH = (Height - startY[pass] + stepY[pass] - 1) / stepY[pass];

            if (passW == 0 || passH == 0) continue;

            // Calculate raw size for this pass
            int stride = (passW * GetBitsPerPixel() + 7) / 8;
            int passSize = (stride + 1) * passH; // +1 for filter byte

            ReadOnlySpan<byte> passData = rawData.Slice(dataOffset, passSize);
            dataOffset += passSize;

            byte[] decodedPass = Unfilter(passData, passW, passH, bpp, stride);

            // Scatter pixels to final image
            ExpandPassToImage(decodedPass, finalImage, pass, passW, passH, startX[pass], startY[pass], stepX[pass], stepY[pass]);
        }

        return finalImage;
    }

    private byte[] ProcessInterlacedRgba(ReadOnlySpan<byte> rawData, int bpp)
    {
        int[] startX = Adam7StartX;
        int[] startY = Adam7StartY;
        int[] stepX = Adam7StepX;
        int[] stepY = Adam7StepY;
        byte[] finalImage = new byte[Width * Height * 4];
        int dataOffset = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            int passW = (Width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            int passH = (Height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (passW == 0 || passH == 0) continue;
            int stride = (passW * GetBitsPerPixel() + 7) / 8;
            int passSize = (stride + 1) * passH;
            ReadOnlySpan<byte> passData = rawData.Slice(dataOffset, passSize);
            dataOffset += passSize;
            byte[] decodedPass = Unfilter(passData, passW, passH, bpp, stride);
            byte[] rgbaPass = (ColorType == 6 && BitDepth == 8) ? decodedPass : ConvertToRGBA(decodedPass, passW, passH);
            for (int y = 0; y < passH; y++)
            {
                for (int x = 0; x < passW; x++)
                {
                    int finalY = startY[pass] + y * stepY[pass];
                    int finalX = startX[pass] + x * stepX[pass];
                    int srcIdx = (y * passW + x) * 4;
                    int dstIdx = (finalY * Width + finalX) * 4;
                    finalImage[dstIdx + 0] = rgbaPass[srcIdx + 0];
                    finalImage[dstIdx + 1] = rgbaPass[srcIdx + 1];
                    finalImage[dstIdx + 2] = rgbaPass[srcIdx + 2];
                    finalImage[dstIdx + 3] = rgbaPass[srcIdx + 3];
                }
            }
        }
        return finalImage;
    }

    private byte[] ProcessPass(ReadOnlySpan<byte> rawData, int w, int h, int bpp)
    {
        int stride = (w * GetBitsPerPixel() + 7) / 8;

        if (ColorType == 2 && BitDepth == 8)
        {
            return UnfilterRgb8Direct(rawData, h, bpp, stride);
        }

        if (ColorType == 6 && BitDepth == 8)
        {
            return UnfilterRgba8ToRgbDirect(rawData, w, h, bpp, stride);
        }

        byte[] decoded = Unfilter(rawData, w, h, bpp, stride);

        if (ColorType == 2 && BitDepth == 8 && decoded.Length == w * h * 3)
        {
            return decoded;
        }

        return ConvertToRGB(decoded, w, h);
    }

    private byte[] ProcessPassRgba(ReadOnlySpan<byte> rawData, int w, int h, int bpp)
    {
        int stride = (w * GetBitsPerPixel() + 7) / 8;

        if (ColorType == 6 && BitDepth == 8)
        {
            return UnfilterRgba8Direct(rawData, h, bpp, stride);
        }

        if (ColorType == 2 && BitDepth == 8)
        {
            return UnfilterRgb8ToRgbaDirect(rawData, w, h, bpp, stride);
        }

        byte[] decoded = Unfilter(rawData, w, h, bpp, stride);

        if (ColorType == 6 && BitDepth == 8 && decoded.Length == w * h * 4)
        {
            return decoded;
        }

        return ConvertToRGBA(decoded, w, h);
    }

    private byte[] Unfilter(byte[] rawData, int w, int h, int bpp, int stride)
    {
        return Unfilter(rawData.AsSpan(), w, h, bpp, stride);
    }

    private byte[] Unfilter(ReadOnlySpan<byte> rawData, int w, int h, int bpp, int stride)
    {
        byte[] recon = new byte[stride * h];
        int reconIdx = 0;
        int rawIdx = 0;

        using var prevRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: true, padToMultiple: Vector<byte>.Count);
        using var curRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: false, padToMultiple: Vector<byte>.Count);

        var prev = prevRow;
        var cur = curRow;

        for (int y = 0; y < h; y++)
        {
            byte filterType = rawData[rawIdx++];
            ReadOnlySpan<byte> srcSpan = rawData.Slice(rawIdx, stride);
            rawIdx += stride;

            Span<byte> curSpan = cur.Span.Slice(0, stride);
            ReadOnlySpan<byte> prevSpan = prev.Span.Slice(0, stride);
            UnfilterScanline(filterType, srcSpan, curSpan, prevSpan, bpp);

            curSpan.CopyTo(recon.AsSpan(reconIdx, stride));
            reconIdx += stride;
            (cur, prev) = (prev, cur);
        }

        return recon;
    }

    private byte[] UnfilterRgb8Direct(ReadOnlySpan<byte> rawData, int h, int bpp, int stride)
    {
        byte[] output = new byte[stride * h];
        int rawIdx = 0;
        using var prevRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: true, padToMultiple: Vector<byte>.Count);
        using var curRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: false, padToMultiple: Vector<byte>.Count);

        var prev = prevRow;
        var cur = curRow;

        for (int y = 0; y < h; y++)
        {
            byte filterType = rawData[rawIdx++];
            ReadOnlySpan<byte> srcSpan = rawData.Slice(rawIdx, stride);
            rawIdx += stride;

            Span<byte> curSpan = cur.Span.Slice(0, stride);
            ReadOnlySpan<byte> prevSpan = prev.Span.Slice(0, stride);
            UnfilterScanline(filterType, srcSpan, curSpan, prevSpan, bpp);
            curSpan.CopyTo(output.AsSpan(y * stride, stride));
            (cur, prev) = (prev, cur);
        }

        return output;
    }

    private byte[] UnfilterRgba8Direct(ReadOnlySpan<byte> rawData, int h, int bpp, int stride)
    {
        byte[] output = new byte[stride * h];
        int rawIdx = 0;
        using var prevRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: true, padToMultiple: Vector<byte>.Count);
        using var curRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: false, padToMultiple: Vector<byte>.Count);

        var prev = prevRow;
        var cur = curRow;

        for (int y = 0; y < h; y++)
        {
            byte filterType = rawData[rawIdx++];
            ReadOnlySpan<byte> srcSpan = rawData.Slice(rawIdx, stride);
            rawIdx += stride;

            Span<byte> curSpan = cur.Span.Slice(0, stride);
            ReadOnlySpan<byte> prevSpan = prev.Span.Slice(0, stride);
            UnfilterScanline(filterType, srcSpan, curSpan, prevSpan, bpp);
            curSpan.CopyTo(output.AsSpan(y * stride, stride));
            (cur, prev) = (prev, cur);
        }

        return output;
    }

    private byte[] UnfilterRgba8ToRgbDirect(ReadOnlySpan<byte> rawData, int w, int h, int bpp, int stride)
    {
        byte[] output = new byte[w * h * 3];
        int rawIdx = 0;
        using var prevRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: true, padToMultiple: Vector<byte>.Count);
        using var curRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: false, padToMultiple: Vector<byte>.Count);

        var prev = prevRow;
        var cur = curRow;

        for (int y = 0; y < h; y++)
        {
            byte filterType = rawData[rawIdx++];
            ReadOnlySpan<byte> srcSpan = rawData.Slice(rawIdx, stride);
            rawIdx += stride;

            Span<byte> curSpan = cur.Span.Slice(0, stride);
            ReadOnlySpan<byte> prevSpan = prev.Span.Slice(0, stride);
            UnfilterScanline(filterType, srcSpan, curSpan, prevSpan, bpp);

            // RGBA -> RGB 走 SimdHelper 的 SSSE3 pshufb 版本（每批 4 像素：读 16 字节写 12 字节），
            // 替换原来逐像素的三次搬运标量循环
            SimdHelper.PackRgbaToRgb(curSpan.Slice(0, w * 4), output.AsSpan(y * w * 3, w * 3));
            (cur, prev) = (prev, cur);
        }

        return output;
    }

    private byte[] UnfilterRgb8ToRgbaDirect(ReadOnlySpan<byte> rawData, int w, int h, int bpp, int stride)
    {
        byte[] output = new byte[w * h * 4];
        int rawIdx = 0;
        using var prevRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: true, padToMultiple: Vector<byte>.Count);
        using var curRow = SimdHelper.AllocateAlignedBytes(stride, alignment: SimdHelper.DefaultAlignment, clear: false, padToMultiple: Vector<byte>.Count);

        var prev = prevRow;
        var cur = curRow;

        for (int y = 0; y < h; y++)
        {
            byte filterType = rawData[rawIdx++];
            ReadOnlySpan<byte> srcSpan = rawData.Slice(rawIdx, stride);
            rawIdx += stride;

            Span<byte> curSpan = cur.Span.Slice(0, stride);
            ReadOnlySpan<byte> prevSpan = prev.Span.Slice(0, stride);
            UnfilterScanline(filterType, srcSpan, curSpan, prevSpan, bpp);

            // RGB -> RGBA（alpha 置 255）同样复用 SimdHelper 的 SSSE3 版本
            SimdHelper.ExpandRgbToRgba(curSpan.Slice(0, w * 3), output.AsSpan(y * w * 4, w * 4));
            (cur, prev) = (prev, cur);
        }

        return output;
    }

    /// <summary>
    /// 对一行做反滤波：从 <paramref name="src"/>（原始行，不含滤波器字节）读，
    /// 把重建结果写入 <paramref name="dst"/>，<paramref name="prev"/> 是上一行的重建结果。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 按滤波器类型一次性分派到专用函数，而不是在逐字节循环里做 switch ——
    /// 真实 PNG（libpng/PIL 的自适应滤波）里 Paeth 通常占 90% 以上的行，
    /// 逐字节分支会同时破坏分支预测和指令缓存。
    /// </para>
    /// <para>
    /// 读 src、写 dst 而不是「先拷贝到 dst 再原地改」，省掉一遍整行拷贝。
    /// 这一点对 <see cref="UnfilterSub"/> 之外的所有滤波器都成立：左邻 a 取自 dst，
    /// 上邻 b、左上邻 c 取自 prev，三者都不是 src。
    /// </para>
    /// </remarks>
    private static void UnfilterScanline(byte filterType, ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> prev, int bpp)
    {
        switch (filterType)
        {
            case 0:
                src.CopyTo(dst);
                return;
            case 1:
                UnfilterSub(src, dst, bpp);
                return;
            case 2:
                UnfilterUp(src, prev, dst);
                return;
            case 3:
                UnfilterAverage(src, dst, prev, bpp);
                return;
            case 4:
                UnfilterPaeth(src, dst, prev, bpp);
                return;
            default:
                // 规范只定义 0..4。保持与旧实现一致的行为：无法识别时原样输出。
                src.CopyTo(dst);
                return;
        }
    }

    /// <summary>Sub：dst[i] = src[i] + dst[i-bpp]（左邻，首 bpp 字节的左邻为 0）。</summary>
    private static void UnfilterSub(ReadOnlySpan<byte> src, Span<byte> dst, int bpp)
    {
        int len = src.Length;
        if (len == 0) return;
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        int head = Math.Min(bpp, len);
        for (int i = 0; i < head; i++)
        {
            Unsafe.Add(ref d, i) = Unsafe.Add(ref s, i);
        }

        for (int i = bpp; i < len; i++)
        {
            Unsafe.Add(ref d, i) = (byte)(Unsafe.Add(ref s, i) + Unsafe.Add(ref d, i - bpp));
        }
    }

    /// <summary>Up：dst[i] = src[i] + prev[i]，逐字节回绕加法，可整块并行。</summary>
    private static void UnfilterUp(ReadOnlySpan<byte> src, ReadOnlySpan<byte> prev, Span<byte> dst)
    {
        int len = src.Length;
        if (len == 0) return;
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte p = ref MemoryMarshal.GetReference(prev);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        int i = 0;
        if (Sse2.IsSupported)
        {
            int limit = len - Vector128<byte>.Count;
            for (; i <= limit; i += Vector128<byte>.Count)
            {
                nuint o = (nuint)i;
                Vector128.StoreUnsafe(
                    Sse2.Add(Vector128.LoadUnsafe(ref s, o), Vector128.LoadUnsafe(ref p, o)),
                    ref d, o);
            }
        }
        else if (AdvSimd.IsSupported)
        {
            int limit = len - Vector128<byte>.Count;
            for (; i <= limit; i += Vector128<byte>.Count)
            {
                nuint o = (nuint)i;
                Vector128.StoreUnsafe(
                    AdvSimd.Add(Vector128.LoadUnsafe(ref s, o), Vector128.LoadUnsafe(ref p, o)),
                    ref d, o);
            }
        }
        else if (Vector.IsHardwareAccelerated && len >= Vector<byte>.Count)
        {
            int simd = Vector<byte>.Count;
            for (; i <= len - simd; i += simd)
            {
                nuint o = (nuint)i;
                Vector.StoreUnsafe(
                    Vector.Add(Vector.LoadUnsafe(ref s, o), Vector.LoadUnsafe(ref p, o)),
                    ref d, o);
            }
        }

        for (; i < len; i++)
        {
            Unsafe.Add(ref d, i) = (byte)(Unsafe.Add(ref s, i) + Unsafe.Add(ref p, i));
        }
    }

    /// <summary>Average：dst[i] = src[i] + ((dst[i-bpp] + prev[i]) &gt;&gt; 1)（截断，非四舍五入）。</summary>
    private static void UnfilterAverage(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> prev, int bpp)
    {
        int len = src.Length;
        if (len == 0) return;
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte p = ref MemoryMarshal.GetReference(prev);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        // 首 bpp 字节左邻 a = 0：(0 + prev[i]) / 2 = prev[i] >> 1
        int head = Math.Min(bpp, len);
        for (int i = 0; i < head; i++)
        {
            Unsafe.Add(ref d, i) = (byte)(Unsafe.Add(ref s, i) + (Unsafe.Add(ref p, i) >> 1));
        }

        for (int i = bpp; i < len; i++)
        {
            int a = Unsafe.Add(ref d, i - bpp);
            int b = Unsafe.Add(ref p, i);
            Unsafe.Add(ref d, i) = (byte)(Unsafe.Add(ref s, i) + ((a + b) >> 1));
        }
    }

    /// <summary>
    /// Paeth：dst[i] = src[i] + Paeth(a, b, c)，其中 a = dst[i-bpp]、b = prev[i]、c = prev[i-bpp]。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 预测量的选择按「代数化简后的三个差值」比较，不再先算 p = a+b-c 再各减一次。
    /// 展开后：<c>p-a = b-c</c>、<c>p-b = a-c</c>、<c>p-c = (b-c)+(a-c)</c>，
    /// 于是 p 本身根本不需要计算，且 pa 与 pb 可以并行求值，依赖链从「先 p 再三减」缩短一层。
    /// 值的范围：a,b,c ∈ [0,255] ⇒ pa,pb ∈ [-255,255]、pc ∈ [-510,510]，int 下不会溢出。
    /// </para>
    /// <para>
    /// 首 bpp 字节的 a 与 c 均为 0，此时 pa = b、pb = 0、pc = b。
    /// b &gt; 0 时 pb 严格最小 ⇒ 预测 b；b == 0 时三者全为 0，预测 a = 0 = b。
    /// 两种情形都等于 prev[i]，即<b>Paeth 的首 bpp 字节退化为 Up</b>，可以直接按 Up 处理。
    /// </para>
    /// <para>
    /// 这条循环是<b>真串行</b>：a 取自本行已重建的前一个字节，且 Paeth 的三路比较是非线性的，
    /// 因此既不能用「移位+加」的对数步前缀和，也不能靠 SIMD 跨像素并行。
    /// 唯一能并行的是 bpp 条互不相干的链（每个字节位置一条），交给乱序执行去发掘。
    /// </para>
    /// </remarks>
    private static void UnfilterPaeth(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> prev, int bpp)
    {
        int len = src.Length;
        if (len == 0) return;
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte p = ref MemoryMarshal.GetReference(prev);
        ref byte d = ref MemoryMarshal.GetReference(dst);

        int head = Math.Min(bpp, len);
        for (int i = 0; i < head; i++)
        {
            Unsafe.Add(ref d, i) = (byte)(Unsafe.Add(ref s, i) + Unsafe.Add(ref p, i));
        }

        for (int i = bpp; i < len; i++)
        {
            int a = Unsafe.Add(ref d, i - bpp);
            int b = Unsafe.Add(ref p, i);
            int c = Unsafe.Add(ref p, i - bpp);

            int pa = b - c;
            int pb = a - c;
            int pc = pa + pb;
            if (pa < 0) pa = -pa;
            if (pb < 0) pb = -pb;
            if (pc < 0) pc = -pc;

            int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
            Unsafe.Add(ref d, i) = (byte)(Unsafe.Add(ref s, i) + pred);
        }
    }


    private void ExpandPassToImage(byte[] decodedPass, byte[] finalImage, int pass, int w, int h, int sx, int sy, int dx, int dy)
    {
        // This is complex because we need to convert the partial pass (which might be packed differently depending on color type)
        // into the final RGB buffer.
        // It's easier if we convert the pass to RGB first, then scatter.

        if (ColorType == 2 && BitDepth == 8)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 3;
                    int dstIdx = (finalY * Width + finalX) * 3;

                    finalImage[dstIdx] = decodedPass[srcIdx];
                    finalImage[dstIdx + 1] = decodedPass[srcIdx + 1];
                    finalImage[dstIdx + 2] = decodedPass[srcIdx + 2];
                }
            }
            return;
        }

        if (ColorType == 2 && BitDepth == 16)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 6;
                    int dstIdx = (finalY * Width + finalX) * 3;

                    finalImage[dstIdx] = decodedPass[srcIdx];
                    finalImage[dstIdx + 1] = decodedPass[srcIdx + 2];
                    finalImage[dstIdx + 2] = decodedPass[srcIdx + 4];
                }
            }
            return;
        }

        if (ColorType == 6 && BitDepth == 8)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 4;
                    int dstIdx = (finalY * Width + finalX) * 3;

                    finalImage[dstIdx] = decodedPass[srcIdx];
                    finalImage[dstIdx + 1] = decodedPass[srcIdx + 1];
                    finalImage[dstIdx + 2] = decodedPass[srcIdx + 2];
                }
            }
            return;
        }

        if (ColorType == 6 && BitDepth == 16)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 8;
                    int dstIdx = (finalY * Width + finalX) * 3;

                    finalImage[dstIdx] = decodedPass[srcIdx];
                    finalImage[dstIdx + 1] = decodedPass[srcIdx + 2];
                    finalImage[dstIdx + 2] = decodedPass[srcIdx + 4];
                }
            }
            return;
        }

        if (ColorType == 0 && BitDepth == 8)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = y * w + x;
                    int dstIdx = (finalY * Width + finalX) * 3;
                    byte v = decodedPass[srcIdx];
                    finalImage[dstIdx] = v;
                    finalImage[dstIdx + 1] = v;
                    finalImage[dstIdx + 2] = v;
                }
            }
            return;
        }

        if (ColorType == 0 && BitDepth == 16)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 2;
                    int dstIdx = (finalY * Width + finalX) * 3;
                    byte v = decodedPass[srcIdx];
                    finalImage[dstIdx] = v;
                    finalImage[dstIdx + 1] = v;
                    finalImage[dstIdx + 2] = v;
                }
            }
            return;
        }

        if (ColorType == 4 && BitDepth == 8)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 2;
                    int dstIdx = (finalY * Width + finalX) * 3;
                    byte v = decodedPass[srcIdx];
                    finalImage[dstIdx] = v;
                    finalImage[dstIdx + 1] = v;
                    finalImage[dstIdx + 2] = v;
                }
            }
            return;
        }

        if (ColorType == 4 && BitDepth == 16)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = (y * w + x) * 4;
                    int dstIdx = (finalY * Width + finalX) * 3;
                    byte v = decodedPass[srcIdx];
                    finalImage[dstIdx] = v;
                    finalImage[dstIdx + 1] = v;
                    finalImage[dstIdx + 2] = v;
                }
            }
            return;
        }

        if (ColorType == 3 && BitDepth == 8)
        {
            byte[]? pal = _palette;
            int palLen = pal?.Length ?? 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int finalY = sy + y * dy;
                    int finalX = sx + x * dx;

                    int srcIdx = y * w + x;
                    int dstIdx = (finalY * Width + finalX) * 3;
                    int p = decodedPass[srcIdx] * 3;
                    if (p + 2 < palLen)
                    {
                        finalImage[dstIdx] = pal![p];
                        finalImage[dstIdx + 1] = pal[p + 1];
                        finalImage[dstIdx + 2] = pal[p + 2];
                    }
                }
            }
            return;
        }

        byte[] rgbPass = ConvertToRGB(decodedPass, w, h);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int finalY = sy + y * dy;
                int finalX = sx + x * dx;

                int srcIdx = (y * w + x) * 3;
                int dstIdx = (finalY * Width + finalX) * 3;

                finalImage[dstIdx] = rgbPass[srcIdx];
                finalImage[dstIdx + 1] = rgbPass[srcIdx + 1];
                finalImage[dstIdx + 2] = rgbPass[srcIdx + 2];
            }
        }
    }

    private unsafe byte[] ConvertToRGB(byte[] data, int w, int h)
    {
        if (BitDepth == 8)
        {
            if (ColorType == 0)
            {
                byte[] rgb0 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb0)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        byte v = *src++;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = v;
                    }
                }
                return rgb0;
            }

            if (ColorType == 2)
            {
                byte[] rgb2 = new byte[w * h * 3];
                Buffer.BlockCopy(data, 0, rgb2, 0, rgb2.Length);
                return rgb2;
            }

            if (ColorType == 4)
            {
                byte[] rgb4 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb4)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        byte v = *src;
                        src += 2;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = v;
                    }
                }
                return rgb4;
            }

            if (ColorType == 6)
            {
                byte[] rgb6 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb6)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        *dst++ = *src++;
                        *dst++ = *src++;
                        *dst++ = *src++;
                        src++;
                    }
                }
                return rgb6;
            }

            if (ColorType == 3)
            {
                byte[] rgb3 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb3)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    if (_palette != null)
                    {
                        fixed (byte* pPal = _palette)
                        {
                            int palLen = _palette.Length;
                            for (int i = 0; i < end; i++)
                            {
                                int index = *src++;
                                int p = index * 3;
                                if (p + 2 < palLen)
                                {
                                    *dst++ = pPal[p];
                                    *dst++ = pPal[p + 1];
                                    *dst++ = pPal[p + 2];
                                }
                                else
                                {
                                    dst += 3;
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < end; i++)
                        {
                            src++;
                            dst += 3;
                        }
                    }
                }
                return rgb3;
            }
        }

        if (BitDepth == 16)
        {
            if (ColorType == 0)
            {
                byte[] rgb016 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb016)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        byte v = *src;
                        src += 2;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = v;
                    }
                }
                return rgb016;
            }

            if (ColorType == 2)
            {
                byte[] rgb216 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb216)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        *dst++ = *src;
                        *dst++ = *(src + 2);
                        *dst++ = *(src + 4);
                        src += 6;
                    }
                }
                return rgb216;
            }

            if (ColorType == 6)
            {
                byte[] rgb616 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb616)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        *dst++ = *src;
                        *dst++ = *(src + 2);
                        *dst++ = *(src + 4);
                        src += 8;
                    }
                }
                return rgb616;
            }

            if (ColorType == 4)
            {
                byte[] rgb416 = new byte[w * h * 3];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgb416)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        byte v = *src;
                        src += 4;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = v;
                    }
                }
                return rgb416;
            }
        }

        byte[] rgb = new byte[w * h * 3];
        int dstIdx = 0;

        // Note: data is packed by scanlines (stride).
        // If BitDepth < 8, pixels are packed into bytes.
        int stride = (w * GetBitsPerPixel() + 7) / 8;

        for (int y = 0; y < h; y++)
        {
            int rowStart = y * stride;
            var row = data.AsSpan(rowStart, stride);
            int bitOffset = 0;

            for (int x = 0; x < w; x++)
            {
                byte r = 0, g = 0, b = 0;

                switch (ColorType)
                {
                    case 0: // Grayscale
                        {
                            int val = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                            // Scale to 8-bit
                            val = ScaleTo8Bit(val, BitDepth);
                            r = g = b = (byte)val;
                        }
                        break;
                    case 2: // Truecolor
                        {
                            if (BitDepth == 8)
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 1];
                                b = row[idx + 2];
                                bitOffset += 24;
                            }
                            else if (BitDepth == 16)
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 2];
                                b = row[idx + 4];
                                bitOffset += 48;
                            }
                        }
                        break;
                    case 3: // Indexed
                        {
                            int index = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                            if (_palette != null && index * 3 + 2 < _palette.Length)
                            {
                                int p = index * 3;
                                r = _palette[p];
                                g = _palette[p + 1];
                                b = _palette[p + 2];
                            }
                        }
                        break;
                    case 4: // Grayscale + Alpha
                        {
                            int val;
                            if (BitDepth == 8)
                            {
                                val = row[bitOffset / 8];
                                bitOffset += 16;
                            }
                            else // 16
                            {
                                val = row[bitOffset / 8];
                                bitOffset += 32;
                            }
                            r = g = b = (byte)val;
                        }
                        break;
                    case 6: // Truecolor + Alpha
                        {
                             if (BitDepth == 8)
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 1];
                                b = row[idx + 2];
                                bitOffset += 32;
                            }
                            else // 16
                            {
                                int idx = bitOffset / 8;
                                r = row[idx];
                                g = row[idx + 2];
                                b = row[idx + 4];
                                bitOffset += 64;
                            }
                        }
                        break;
                }

                rgb[dstIdx++] = r;
                rgb[dstIdx++] = g;
                rgb[dstIdx++] = b;
            }
        }

        return rgb;
    }

    private unsafe byte[] ConvertToRGBA(byte[] data, int w, int h)
    {
        if (BitDepth == 8)
        {
            if (ColorType == 0)
            {
                byte[] rgba0 = new byte[w * h * 4];
                int end = w * h;
                int ts = -1;
                if (_transparency != null && _transparency.Length >= 2)
                {
                    ts = (_transparency[0] << 8) | _transparency[1];
                }
                fixed (byte* pSrc = data, pDst = rgba0)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    if (ts >= 0)
                    {
                        for (int i = 0; i < end; i++)
                        {
                            byte v = *src++;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = (v == ts) ? (byte)0 : (byte)255;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < end; i++)
                        {
                            byte v = *src++;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = 255;
                        }
                    }
                }
                return rgba0;
            }

            if (ColorType == 2)
            {
                byte[] rgba2 = new byte[w * h * 4];
                int end = w * h;
                bool hasTrns = _transparency != null && _transparency.Length >= 6;
                byte tr = 0, tg = 0, tb = 0;
                if (hasTrns)
                {
                    tr = _transparency![1];
                    tg = _transparency![3];
                    tb = _transparency![5];
                }
                fixed (byte* pSrc = data, pDst = rgba2)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    if (hasTrns)
                    {
                        for (int i = 0; i < end; i++)
                        {
                            byte r = *src++;
                            byte g = *src++;
                            byte b = *src++;
                            *dst++ = r;
                            *dst++ = g;
                            *dst++ = b;
                            *dst++ = (r == tr && g == tg && b == tb) ? (byte)0 : (byte)255;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < end; i++)
                        {
                            *dst++ = *src++;
                            *dst++ = *src++;
                            *dst++ = *src++;
                            *dst++ = 255;
                        }
                    }
                }
                return rgba2;
            }

            if (ColorType == 3)
            {
                byte[] rgba3 = new byte[w * h * 4];
                int end = w * h;
                bool hasTrns = _transparency != null;
                fixed (byte* pSrc = data, pDst = rgba3)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    fixed (byte* pPal = _palette, pTrns = _transparency)
                    {
                        int palLen = _palette?.Length ?? 0;
                        int trnsLen = _transparency?.Length ?? 0;
                        if (hasTrns && pPal != null)
                        {
                            for (int i = 0; i < end; i++)
                            {
                                int index = *src++;
                                int p = index * 3;
                                if (p + 2 < palLen)
                                {
                                    *dst++ = pPal[p];
                                    *dst++ = pPal[p + 1];
                                    *dst++ = pPal[p + 2];
                                }
                                else
                                {
                                    *dst++ = 0; *dst++ = 0; *dst++ = 0;
                                }
                                *dst++ = index < trnsLen ? pTrns[index] : (byte)255;
                            }
                        }
                        else if (pPal != null)
                        {
                            for (int i = 0; i < end; i++)
                            {
                                int index = *src++;
                                int p = index * 3;
                                if (p + 2 < palLen)
                                {
                                    *dst++ = pPal[p];
                                    *dst++ = pPal[p + 1];
                                    *dst++ = pPal[p + 2];
                                }
                                else
                                {
                                    *dst++ = 0; *dst++ = 0; *dst++ = 0;
                                }
                                *dst++ = 255;
                            }
                        }
                    }
                }
                return rgba3;
            }

            if (ColorType == 4)
            {
                byte[] rgba4 = new byte[w * h * 4];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgba4)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        byte v = *src++;
                        byte a = *src++;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = a;
                    }
                }
                return rgba4;
            }

            if (ColorType == 6)
            {
                byte[] rgba6 = new byte[w * h * 4];
                Buffer.BlockCopy(data, 0, rgba6, 0, rgba6.Length);
                return rgba6;
            }
        }

        if (BitDepth == 16)
        {
            if (ColorType == 0)
            {
                byte[] rgba016 = new byte[w * h * 4];
                int end = w * h;
                int ts = -1;
                if (_transparency != null && _transparency.Length >= 2)
                {
                    int t = (_transparency[0] << 8) | _transparency[1];
                    ts = t >> 8;
                }
                fixed (byte* pSrc = data, pDst = rgba016)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    if (ts >= 0)
                    {
                        for (int i = 0; i < end; i++)
                        {
                            byte v = *src;
                            src += 2;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = (v == ts) ? (byte)0 : (byte)255;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < end; i++)
                        {
                            byte v = *src;
                            src += 2;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = v;
                            *dst++ = 255;
                        }
                    }
                }
                return rgba016;
            }

            if (ColorType == 2)
            {
                byte[] rgba216 = new byte[w * h * 4];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgba216)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        *dst++ = *src;
                        *dst++ = *(src + 2);
                        *dst++ = *(src + 4);
                        *dst++ = 255;
                        src += 6;
                    }
                }
                return rgba216;
            }

            if (ColorType == 6)
            {
                byte[] rgba616 = new byte[w * h * 4];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgba616)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        *dst++ = *src;
                        *dst++ = *(src + 2);
                        *dst++ = *(src + 4);
                        *dst++ = *(src + 6);
                        src += 8;
                    }
                }
                return rgba616;
            }

            if (ColorType == 4)
            {
                byte[] rgba416 = new byte[w * h * 4];
                int end = w * h;
                fixed (byte* pSrc = data, pDst = rgba416)
                {
                    byte* src = pSrc;
                    byte* dst = pDst;
                    for (int i = 0; i < end; i++)
                    {
                        byte v = *src;
                        byte a = *(src + 2);
                        src += 4;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = v;
                        *dst++ = a;
                    }
                }
                return rgba416;
            }
        }

        byte[] rgba = new byte[w * h * 4];
        int dstIdx = 0;
        int stride = (w * GetBitsPerPixel() + 7) / 8;
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * stride;
            var row = data.AsSpan(rowStart, stride);
            int bitOffset = 0;
            for (int x = 0; x < w; x++)
            {
                byte r = 0, g = 0, b = 0, a = 255;
                switch (ColorType)
                {
                    case 0:
                    {
                        int val = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                        val = ScaleTo8Bit(val, BitDepth);
                        r = g = b = (byte)val;
                        if (_transparency != null && _transparency.Length >= 2)
                        {
                            int t = (_transparency[0] << 8) | _transparency[1];
                            int ts = BitDepth == 16 ? (t >> 8) : t;
                            a = (val == ts) ? (byte)0 : (byte)255;
                        }
                    }
                    break;
                    case 2:
                    {
                        if (BitDepth == 8)
                        {
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 1];
                            b = row[idx + 2];
                            bitOffset += 24;
                            if (_transparency != null && _transparency.Length >= 6)
                            {
                                byte tr = _transparency[1];
                                byte tg = _transparency[3];
                                byte tb = _transparency[5];
                                a = (r == tr && g == tg && b == tb) ? (byte)0 : (byte)255;
                            }
                        }
                        else
                        {
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 2];
                            b = row[idx + 4];
                            bitOffset += 48;
                        }
                    }
                    break;
                    case 3:
                    {
                        int index = ReadBits(data, rowStart, ref bitOffset, BitDepth);
                        if (_palette != null && index * 3 + 2 < _palette.Length)
                        {
                            r = _palette[index * 3];
                            g = _palette[index * 3 + 1];
                            b = _palette[index * 3 + 2];
                        }
                        if (_transparency != null && index < _transparency.Length)
                        {
                            a = _transparency[index];
                        }
                    }
                    break;
                    case 4:
                    {
                        if (BitDepth == 8)
                        {
                            int idx = bitOffset / 8;
                            byte v = row[idx];
                            byte al = row[idx + 1];
                            bitOffset += 16;
                            r = g = b = v;
                            a = al;
                        }
                        else
                        {
                            int idx = bitOffset / 8;
                            byte v = row[idx];
                            byte al = row[idx + 2];
                            bitOffset += 32;
                            r = g = b = v;
                            a = al;
                        }
                    }
                    break;
                    case 6:
                    {
                        if (BitDepth == 8)
                        {
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 1];
                            b = row[idx + 2];
                            a = row[idx + 3];
                            bitOffset += 32;
                        }
                        else
                        {
                            int idx = bitOffset / 8;
                            r = row[idx];
                            g = row[idx + 2];
                            b = row[idx + 4];
                            a = row[idx + 6];
                            bitOffset += 64;
                        }
                    }
                    break;
                }
                rgba[dstIdx++] = r;
                rgba[dstIdx++] = g;
                rgba[dstIdx++] = b;
                rgba[dstIdx++] = a;
            }
        }
        return rgba;
    }

    private static int ReadBits(byte[] data, int rowStart, ref int bitOffset, int bits)
    {
        int byteIdx = rowStart + bitOffset / 8;
        int bitShift = 8 - (bitOffset % 8) - bits;
        int val = (data[byteIdx] >> bitShift) & ((1 << bits) - 1);
        bitOffset += bits;
        return val;
    }

    private static int ScaleTo8Bit(int val, int depth)
    {
        if (depth == 1) return val * 255;
        if (depth == 2) return val * 85;
        if (depth == 4) return val * 17;
        if (depth == 8) return val;
        if (depth == 16) return (val * 255 + 32895) >> 16;
        return val;
    }

    private int GetBitsPerPixel()
    {
        return ColorType switch
        {
            0 => BitDepth,
            2 => 3 * BitDepth,
            3 => BitDepth,
            4 => 2 * BitDepth,
            6 => 4 * BitDepth,
            _ => throw new NotSupportedException("Invalid color type"),
        };
    }

    // Helper for filtering
    private int GetBytesPerPixel()
    {
        return (GetBitsPerPixel() + 7) / 8;
    }

    private static uint ReadBigEndianUint32(byte[] buffer, int offset)
    {
        return (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }
}
