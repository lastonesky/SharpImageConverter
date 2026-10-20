using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpImageConverter.Compression;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using Xunit;

namespace SharpImageConverter.Tests
{
    [Collection(JpegSimdStateCollection.Name)]
    /// <summary>optimize 相关测试的共享夹具（临时文件、构造样本、手工拼容器）。</summary>
    public partial class OptimizeTests
    {
        private static string NewTemp(string ext) => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
















        // ───────────────────────── 跨格式「转换即最优」─────────────────────────

        /// <summary>
        /// 构造接近真实照片的 RGB 像素（平滑渐变 + 噪声），便于比较不同格式的压缩率。
        /// </summary>
        private static byte[] BuildPhotoLikeRgb(int w, int h, int seed)
        {
            var rgb = new byte[w * h * 3];
            var rng = new Random(seed);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    int noise = rng.Next(-8, 8);
                    rgb[o] = (byte)Math.Clamp(128 + 90 * Math.Sin(x * 0.05) + noise, 0, 255);
                    rgb[o + 1] = (byte)Math.Clamp(120 + 80 * Math.Cos(y * 0.06) + noise, 0, 255);
                    rgb[o + 2] = (byte)Math.Clamp(140 + 70 * Math.Sin((x + y) * 0.03) + noise, 0, 255);
                }
            }
            return rgb;
        }

        private static void Cleanup(params string[] paths)
        {
            foreach (var f in paths)
            {
                if (File.Exists(f)) File.Delete(f);
            }
        }













        private static void WriteJpegWithMetadata(string path, int w, int h, byte[] rgb, int orientation)
        {
            using var ms = new MemoryStream();
            JpegEncoder.Encode(new Image<Rgb24>(w, h, rgb), ms,
                new JpegEncoderOptions(95, subsample420: true, keepMetadata: false, enableDiagnostics: false));
            byte[] jpeg = ms.ToArray();

            // Exif\0\0 + 最小 TIFF（1 个 Orientation 标签）
            var exif = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
            exif.AddRange([0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]); // II + 偏移 8
            exif.AddRange([0x01, 0x00]);                                     // 1 个 IFD 项
            exif.AddRange([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00]); // Orientation, SHORT, count=1
            exif.Add((byte)orientation); exif.Add(0); exif.Add(0); exif.Add(0);
            exif.AddRange([0, 0, 0, 0]);                                     // next IFD = 0

            using var fs = File.Create(path);
            fs.WriteByte(0xFF); fs.WriteByte(0xD8);

            byte[] app1 = BuildJpegSegment(0xE1, exif.ToArray());
            fs.Write(app1);
            byte[] com = BuildJpegSegment(0xFE, "hello"u8.ToArray());
            fs.Write(com);
            fs.Write(jpeg, 2, jpeg.Length - 2);
        }

        private static byte[] BuildJpegSegment(byte marker, byte[] payload)
        {
            int len = payload.Length + 2;
            var seg = new byte[4 + payload.Length];
            seg[0] = 0xFF; seg[1] = marker;
            seg[2] = (byte)(len >> 8); seg[3] = (byte)len;
            Buffer.BlockCopy(payload, 0, seg, 4, payload.Length);
            return seg;
        }

        private static bool ContainsMarker(byte[] jpeg, byte marker)
        {
            for (int i = 0; i + 1 < jpeg.Length; i++)
            {
                if (jpeg[i] == 0xFF && jpeg[i + 1] == marker) return true;
            }
            return false;
        }





        private static void Write16BitPng(string path)
        {
            using var fs = File.Create(path);
            fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 1);
            ihdr[8] = 16;   // bit depth
            ihdr[9] = 2;    // RGB
            WritePngChunk(fs, "IHDR", ihdr);

            // 1x1 RGB16：filter 0 + (0x1234, 0x5678, 0x9ABC)
            byte[] raw = [0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC];
            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw);
            }
            WritePngChunk(fs, "IDAT", compressed.ToArray());
            WritePngChunk(fs, "IEND", []);
        }

        private static void InsertChunkAfterIhdr(string path, string type, byte[] data)
        {
            byte[] png = File.ReadAllBytes(path);
            int ihdrLength = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4));
            int ihdrEnd = 8 + 4 + 4 + ihdrLength + 4;
            using var fs = File.Create(path);
            fs.Write(png, 0, ihdrEnd);
            WritePngChunk(fs, type, data);
            fs.Write(png, ihdrEnd, png.Length - ihdrEnd);
        }

        private static void WritePngChunk(Stream stream, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            stream.Write(len);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);

            var crcInput = new byte[typeBytes.Length + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
            Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(crcInput));
            stream.Write(crc);
        }

        private static void WriteAnimatedWebpHeader(string path)
        {
            using var body = new MemoryStream();
            body.Write("WEBP"u8);

            Span<byte> size = stackalloc byte[4];
            var vp8x = new byte[10];
            vp8x[0] = 0x02; // animation flag
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)vp8x.Length);
            body.Write("VP8X"u8);
            body.Write(size);
            body.Write(vp8x);

            var anim = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)anim.Length);
            body.Write("ANIM"u8);
            body.Write(size);
            body.Write(anim);

            using var fs = File.Create(path);
            fs.Write("RIFF"u8);
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            fs.Write(size);
            body.Position = 0;
            body.CopyTo(fs);
        }






















    }
}
