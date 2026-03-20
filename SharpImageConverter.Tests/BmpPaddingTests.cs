using Xunit;
using System;
using System.Buffers.Binary;
using System.IO;
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
            var rgb = BmpReader.Read(ms, out int width, out int height);

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
    }
}
