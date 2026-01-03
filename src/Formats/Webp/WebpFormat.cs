using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{
    public sealed class WebpFormat : IImageFormat
    {
        public string Name => "WebP";
        public string[] Extensions => new[] { ".webp" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[12];
            if (s.Read(b) != b.Length) return false;
            return b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
                && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
        }
    }
}
