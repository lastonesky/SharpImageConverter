using Xunit;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Bmp;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class BmpPaddingTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        [Fact]
        public void Bmp_RowPadding_Roundtrip_Exact()
        {
            for (int w = 1; w <= 5; w++)
            {
                int h = 3;
                var img = TestImageFactory.CreateChecker(w, h, (10, 20, 30), (200, 210, 220));
                string path = NewTemp(".bmp");
                Image.Save(img, path);
                var loaded = Image.Load(path);
                Assert.Equal(w, loaded.Width);
                Assert.Equal(h, loaded.Height);
                BufferAssert.EqualExact(img.Buffer, loaded.Buffer);
                File.Delete(path);
            }
        }

        [Fact]
        public void Bmp_8bit_Grayscale_CanBeReadAsRgb24()
        {
            int w = 4, h = 3;
            var gray = new byte[w * h];
            for (int i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(i * 17);
            }
            string path = NewTemp(".bmp");
            BmpWriter.Write8(path, w, h, gray);
            var loaded = Image.Load(path);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
            for (int i = 0; i < gray.Length; i++)
            {
                int o = i * 3;
                Assert.Equal(gray[i], loaded.Buffer[o + 0]);
                Assert.Equal(gray[i], loaded.Buffer[o + 1]);
                Assert.Equal(gray[i], loaded.Buffer[o + 2]);
            }
            File.Delete(path);
        }

        [Fact]
        public void Bmp_32Bitfields_NonDefaultMask_Decode_Correct()
        {
            byte[] bmp = new byte[70];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), bmp.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), 66);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 3);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), 4);

            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(54, 4), 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(58, 4), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(62, 4), 0x00FF0000);

            bmp[66] = 0x11;
            bmp[67] = 0x22;
            bmp[68] = 0x33;
            bmp[69] = 0x00;

            using var ms = new MemoryStream(bmp);
            var rgb = BmpReader.Read(ms, out int width, out int height, out _, out _);

            Assert.Equal(1, width);
            Assert.Equal(1, height);
            Assert.Equal((byte)0x11, rgb[0]);
            Assert.Equal((byte)0x22, rgb[1]);
            Assert.Equal((byte)0x33, rgb[2]);
        }

        [Fact]
        public void BmpWriter_Throws_On_Invalid_Buffer_Length()
        {
            Assert.Throws<ArgumentException>(() => BmpWriter.Write8(Stream.Null, 2, 2, new byte[3]));
            Assert.Throws<ArgumentException>(() => BmpWriter.Write24(Stream.Null, 2, 2, new byte[11]));
        }

        [Fact]
        public void Bmp_16Bit_Rgb555_Decode_Correct()
        {
            // Construct a 2x2 16-bit RGB555 BMP
            // RGB555: bits 14-10 = R, 9-5 = G, 4-0 = B
            int w = 2, h = 2;
            int rowStride = ((w * 16 + 31) / 32) * 4; // = 4
            int pixelDataSize = rowStride * h;
            int dataOffset = 66; // header(14) + info(40) + gap for alignment
            int fileSize = dataOffset + pixelDataSize;

            byte[] bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), dataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), w);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), h);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 16);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 0); // BI_RGB

            // Pixel data (bottom-up: first row in file = last image row):
            // Pixel (0,0): R=31,G=0,B=0  -> 0x7C00
            // Pixel (1,0): R=0,G=31,B=0  -> 0x03E0
            // Pixel (0,1): R=0,G=0,B=31  -> 0x001F
            // Pixel (1,1): R=31,G=31,B=31 -> 0x7FFF
            // File row 0 (image row 1): pixels (0,1), (1,1)
            // File row 1 (image row 0): pixels (0,0), (1,0)
            ushort[] pixels = [0x001F, 0x7FFF, 0x7C00, 0x03E0]; 
            MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(bmp.AsSpan(dataOffset));

            using var ms = new MemoryStream(bmp);
            var rgb = BmpReader.Read(ms, out int width, out int height, out _, out _);

            Assert.Equal(w, width);
            Assert.Equal(h, height);
            // Image row 0: pixel (0,0)=0x7C00 (R=31,G=0,B=0)
            Assert.Equal((byte)255, rgb[0]); // R: 31→(31<<3)|(31>>2)=255
            Assert.Equal(0, rgb[1]);          // G
            Assert.Equal(0, rgb[2]);          // B
            // Image row 0: pixel (1,0)=0x03E0 (R=0,G=31,B=0)
            Assert.Equal(0, rgb[3]);
            Assert.Equal((byte)255, rgb[4]);
            Assert.Equal(0, rgb[5]);
            // Image row 1: pixel (0,1)=0x001F (R=0,G=0,B=31)
            Assert.Equal(0, rgb[6]);
            Assert.Equal(0, rgb[7]);
            Assert.Equal((byte)255, rgb[8]);
            // Image row 1: pixel (1,1)=0x7FFF (R=31,G=31,B=31) => white
            Assert.Equal((byte)255, rgb[9]);
            Assert.Equal((byte)255, rgb[10]);
            Assert.Equal((byte)255, rgb[11]);
        }

        [Fact]
        public void Bmp_16Bit_Bitfields_Decode_Correct()
        {
            // 16-bit BI_BITFIELDS with RGB565 masks (R=0xF800, G=0x07E0, B=0x001F)
            int w = 1, h = 1;
            int rowStride = 2; // (1*16+31)/32*4 = 2... actually ((16+31)/32)*4 = 4
            rowStride = ((w * 16 + 31) / 32) * 4; // rowStride = 4
            int dataOffset = 66;
            int fileSize = dataOffset + rowStride;

            byte[] bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), dataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), w);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), h);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 16);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 3); // BI_BITFIELDS
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), 4); // imageSize = 4

            // Masks at offset 54 (after 40-byte header + 14 byte file header)
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(54, 4), 0xF800); // R
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(58, 4), 0x07E0); // G
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(62, 4), 0x001F); // B

            // Pixel: RGB565 R=31,G=63,B=31 => 0xFFFF (white)
            bmp[66] = 0xFF;
            bmp[67] = 0xFF;
            bmp[68] = 0x00;
            bmp[69] = 0x00;

            using var ms = new MemoryStream(bmp);
            var rgb = BmpReader.Read(ms, out int width, out int height, out _, out _);

            Assert.Equal(1, width);
            Assert.Equal(1, height);
            Assert.Equal((byte)255, rgb[0]);
            Assert.Equal((byte)255, rgb[1]);
            Assert.Equal((byte)255, rgb[2]);
        }

        [Fact]
        public void Bmp_AlphaBitfields_Decode_AsRgb24()
        {
            // BI_ALPHABITFIELDS (compression=6) should be treated same as BI_BITFIELDS (3)
            int w = 1, h = 1;
            int rowStride = ((w * 32 + 31) / 32) * 4; // = 4
            int fileSize = 70;

            byte[] bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), 66);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), w);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), h);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 6); // BI_ALPHABITFIELDS
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), 4);

            // Standard RGBA masks (R, G, B, A)
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(54, 4), 0x00FF0000); // R
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(58, 4), 0x0000FF00); // G
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(62, 4), 0x000000FF); // B

            // Pixel: A=0x80, B=0x11, G=0x22, R=0x33 (little-endian: B G R A)
            bmp[66] = 0x11;
            bmp[67] = 0x22;
            bmp[68] = 0x33;
            bmp[69] = 0x80;

            using var ms = new MemoryStream(bmp);
            var rgb = BmpReader.Read(ms, out int width, out int height, out _, out _);

            Assert.Equal(1, width);
            Assert.Equal(1, height);
            // Should decode as RGB24 (alpha discarded)
            Assert.Equal((byte)0x33, rgb[0]); // R
            Assert.Equal((byte)0x22, rgb[1]); // G
            Assert.Equal((byte)0x11, rgb[2]); // B
        }

        [Fact]
        public void Bmp_32Bit_Rgb_Decode_Large_Random_Correct()
        {
            // Create a random 32x32 32-bit BMP, read back, verify all pixels
            int w = 32, h = 32;
            int rowStride = ((w * 32 + 31) / 32) * 4; // = 128
            int dataOffset = 54; // no palette, no extra header
            int pixelDataSize = rowStride * h;
            int fileSize = dataOffset + pixelDataSize;

            byte[] bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), dataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), w);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), h);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 0); // BI_RGB
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), pixelDataSize);

            // Fill with deterministic random pixel data
            // Bottom-up: file row y = image row (h-1-y)
            var rng = new Random(42);
            // Pre-generate all pixel values [image_y][x] = (B, G, R, A)
            var pixels = new (byte B, byte G, byte R, byte A)[h, w];
            for (int imgY = 0; imgY < h; imgY++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[imgY, x] = (
                        (byte)rng.Next(256),
                        (byte)rng.Next(256),
                        (byte)rng.Next(256),
                        (byte)rng.Next(256));
                }
            }
            // Write to file in bottom-up order
            for (int fileY = 0; fileY < h; fileY++)
            {
                int imgY = h - 1 - fileY;
                int rowOffset = dataOffset + fileY * rowStride;
                for (int x = 0; x < w; x++)
                {
                    int o = rowOffset + x * 4;
                    bmp[o + 0] = pixels[imgY, x].B;
                    bmp[o + 1] = pixels[imgY, x].G;
                    bmp[o + 2] = pixels[imgY, x].R;
                    bmp[o + 3] = pixels[imgY, x].A;
                }
            }

            using var ms = new MemoryStream(bmp);
            var rgb = BmpReader.Read(ms, out int width, out int height, out _, out _);

            Assert.Equal(w, width);
            Assert.Equal(h, height);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var px = pixels[y, x];
                    int o = (y * w + x) * 3;
                    Assert.Equal(px.R, rgb[o + 0]);
                    Assert.Equal(px.G, rgb[o + 1]);
                    Assert.Equal(px.B, rgb[o + 2]);
                }
            }
        }

        [Fact]
        public void Bmp_Dpi_Roundtrip()
        {
            int w = 4, h = 4;
            var img = TestImageFactory.CreateChecker(w, h, (10, 20, 30), (200, 210, 220));
            // Set custom DPI via metadata
            img.Metadata.HorizontalDpi = 300.0;
            img.Metadata.VerticalDpi = 150.0;

            string path = NewTemp(".bmp");
            Image.Save(img, path);
            var loaded = Image.Load(path);

            // DPI should be preserved (within rounding tolerance for pelsPerMeter conversion)
            Assert.Equal(300.0, loaded.Metadata.HorizontalDpi, 1);
            Assert.Equal(150.0, loaded.Metadata.VerticalDpi, 1);
            BufferAssert.EqualExact(img.Buffer, loaded.Buffer);

            File.Delete(path);
        }

        [Fact]
        public void Bmp_Dpi_Default_When_Unset()
        {
            // BMP file with no DPI (pelsPerMeter = 0) should decode to DPI = 0
            // 24-bit 1x1: rowStride=4, so total file size = 54 + 4 = 70
            int fileSize = 70;
            int dataOffset = 66; // 14 + 40 + 12 padding (to make data at 66)
            byte[] bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), dataOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 24);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), 4);
            // pelsPerMeterX/Y at offsets 38/42 remain 0 (default)
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(46, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(50, 4), 0);
            // Pixel data at offset 66
            bmp[66] = 0x11; bmp[67] = 0x22; bmp[68] = 0x33;
            bmp[69] = 0x00; // padding

            using var ms = new MemoryStream(bmp);
            var rgb = BmpReader.Read(ms, out int width, out int height, out double xDpi, out double yDpi);

            Assert.Equal(1, width);
            Assert.Equal(1, height);
            Assert.Equal(0.0, xDpi);
            Assert.Equal(0.0, yDpi);
            Assert.Equal((byte)0x33, rgb[0]);
            Assert.Equal((byte)0x22, rgb[1]);
            Assert.Equal((byte)0x11, rgb[2]);
        }
    }
}
