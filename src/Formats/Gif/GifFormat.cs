using System;
using System.IO;
using PictureSharp.Core;

namespace PictureSharp.Formats.Gif
{
    public sealed class GifFormat : IImageFormat
    {
        public string Name => "GIF";
        public string[] Extensions => new[] { ".gif" };
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[3];
            if (s.Read(b) != b.Length) return false;
            return b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F';
        }
    }
}
