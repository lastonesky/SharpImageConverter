
using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{

    public sealed class JpegFormat : IImageFormat
    {
        public string Name => "JPEG";
        public string[] Extensions => new[] { ".jpg", ".jpeg" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            if (s.Read(b) != b.Length) return false;
            return b[0] == 0xFF && b[1] == 0xD8;
        }
    }
}