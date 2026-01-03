using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{
    public sealed class BmpFormat : IImageFormat
    {
        public string Name => "BMP";
        public string[] Extensions => new[] { ".bmp" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            if (s.Read(b) != b.Length) return false;
            return b[0] == (byte)'B' && b[1] == (byte)'M';
        }
    }
}
