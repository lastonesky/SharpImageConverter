using System;
using System.IO;
using Core;

namespace Formats
{
    public sealed class JpegFormat : IImageFormat
    {
        public string Name => "JPEG";
        public string[] Extensions => new[] { ".jpg", ".jpeg" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            s.Read(b);
            return b[0] == 0xFF && b[1] == 0xD8;
        }
    }

    public sealed class PngFormat : IImageFormat
    {
        public string Name => "PNG";
        public string[] Extensions => new[] { ".png" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            s.Read(b);
            return b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
        }
    }

    public sealed class BmpFormat : IImageFormat
    {
        public string Name => "BMP";
        public string[] Extensions => new[] { ".bmp" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            s.Read(b);
            return b[0] == (byte)'B' && b[1] == (byte)'M';
        }
    }
}
