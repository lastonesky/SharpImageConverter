using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{
    public sealed class PngFormat : IImageFormat
    {
        public string Name => "PNG";
        public string[] Extensions => new[] { ".png" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            if (s.Read(b) != b.Length) return false;
            return b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
        }
    }
}
