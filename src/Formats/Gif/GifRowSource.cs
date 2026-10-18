using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif
{
    /// <summary>
    /// GIF 逐行源（帧级）：解码首帧到一块 RGBA 画布，之后按行输出，写完即丢。
    /// </summary>
    /// <remarks>
    /// 普通静态转换只需要首帧；动画的逐帧流式编码不在本类范围内。
    /// 与一次性解码相比，它避免的是“所有帧同时驻留”，而不是单帧画布的 4 B/px。
    /// </remarks>
    internal sealed class GifRowSource : IRowImageSource
    {
        private readonly byte[] _rgba;
        private readonly bool _hasAlpha;
        private int _nextRow;

        public GifRowSource(string path)
        {
            var decoder = new GifDecoder();
            var frame = decoder.DecodeRgba32(path);
            Width = frame.Width;
            Height = frame.Height;
            _rgba = frame.Buffer;
            _hasAlpha = false;
            for (int i = 3; i < _rgba.Length; i += 4)
            {
                if (_rgba[i] != 255)
                {
                    _hasAlpha = true;
                    break;
                }
            }
            Channels = _hasAlpha ? 4 : 3;
        }

        internal static bool IsSupported(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> head = stackalloc byte[6];
                if (fs.ReadAtLeast(head, 6, throwOnEndOfStream: false) < 6) return false;
                return head[0] == 'G' && head[1] == 'I' && head[2] == 'F';
            }
            catch
            {
                return false;
            }
        }

        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public bool HasAlpha => _hasAlpha;

        public bool ReadRow(Span<byte> row)
        {
            if (_nextRow >= Height) return false;
            if (row.Length < Width * Channels) throw new ArgumentException("Row buffer too small", nameof(row));

            if (Channels == 4)
            {
                _rgba.AsSpan(_nextRow * Width * 4, Width * 4).CopyTo(row);
            }
            else
            {
                for (int x = 0, s = _nextRow * Width * 4, d = 0; x < Width; x++, s += 4, d += 3)
                {
                    row[d] = _rgba[s];
                    row[d + 1] = _rgba[s + 1];
                    row[d + 2] = _rgba[s + 2];
                }
            }
            _nextRow++;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
