using System.IO;
using Formats;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    public class SniffingTests
    {
        public static void Jpeg_IsMatch_By_Header()
        {
            var fmt = new JpegFormat();
            using var ms = new MemoryStream(new byte[] { 0xFF, 0xD8, 0x00, 0x00 });
            Assert.IsTrue(fmt.IsMatch(ms));
        }

        public static void Png_IsMatch_By_Header()
        {
            var fmt = new PngFormat();
            using var ms = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 });
            Assert.IsTrue(fmt.IsMatch(ms));
        }

        public static void Bmp_IsMatch_By_Header()
        {
            var fmt = new BmpFormat();
            using var ms = new MemoryStream(new byte[] { (byte)'B', (byte)'M', 0x00, 0x00 });
            Assert.IsTrue(fmt.IsMatch(ms));
        }

        public static void Random_Header_Is_Not_Match()
        {
            using var ms = new MemoryStream(new byte[] { 0x00, 0x11, 0x22, 0x33 });
            Assert.IsFalse(new JpegFormat().IsMatch(ms));
            ms.Position = 0;
            Assert.IsFalse(new PngFormat().IsMatch(ms));
            ms.Position = 0;
            Assert.IsFalse(new BmpFormat().IsMatch(ms));
        }
    }
}
