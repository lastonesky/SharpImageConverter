
using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{

    public sealed class JpegFormat : IImageFormat
    {
        public string Name => "JPEG";
        public string[] Extensions => [".jpg", ".jpeg"];
        public bool IsMatch(Stream s)
        {
            long origin = 0;
            bool restorePosition = s.CanSeek;
            if (restorePosition)
            {
                origin = s.Position;
            }

            Span<byte> b = stackalloc byte[2];
            try
            {
                if (s.Read(b) != b.Length) return false;
                return b[0] == 0xFF && b[1] == 0xD8;
            }
            finally
            {
                if (restorePosition)
                {
                    s.Position = origin;
                }
            }
        }
    }
}
