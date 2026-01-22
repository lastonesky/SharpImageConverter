using Xunit;
using System.IO;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Formats;

namespace SharpImageConverter.Tests
{
    public class SniffingTests
    {
        [Fact]
        public void Jpeg_IsMatch_By_Header()
        {
            var fmt = new JpegFormat();
            using var ms = new MemoryStream(new byte[] { 0xFF, 0xD8, 0x00, 0x00 });
            Assert.True(fmt.IsMatch(ms));
        }

        [Fact]
        public void Png_IsMatch_By_Header()
        {
            var fmt = new PngFormat();
            using var ms = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 });
            Assert.True(fmt.IsMatch(ms));
        }

        [Fact]
        public void Bmp_IsMatch_By_Header()
        {
            var fmt = new BmpFormat();
            using var ms = new MemoryStream(new byte[] { (byte)'B', (byte)'M', 0x00, 0x00 });
            Assert.True(fmt.IsMatch(ms));
        }

        [Fact]
        public void Random_Header_Is_Not_Match()
        {
            using var ms = new MemoryStream(new byte[] { 0x00, 0x11, 0x22, 0x33 });
            Assert.False(new JpegFormat().IsMatch(ms));
            ms.Position = 0;
            Assert.False(new PngFormat().IsMatch(ms));
            ms.Position = 0;
            Assert.False(new BmpFormat().IsMatch(ms));
        }
    }
}
