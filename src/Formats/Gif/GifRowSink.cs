using System;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Gif;

namespace SharpImageConverter.Formats.Gif
{
    /// <summary>
    /// GIF 单帧汇：GIF 编码必须对整帧建调色板，因此这里只做到“单帧缓冲”，
    /// 不是逐行编码；相比普通路径省掉的是「源图整幅 + 目标帧」两份同时驻留。
    /// </summary>
    internal sealed class GifRowSink : IRowImageSink
    {
        private readonly string _path;
        private readonly int _width;
        private readonly int _height;
        private readonly int _channels;
        private readonly bool _gray;
        private readonly byte[] _frame;
        private int _nextRow;
        private bool _completed;

        internal GifRowSink(string path, int width, int height, int sourceChannels, bool gray = false)
        {
            _path = path;
            _width = width;
            _height = height;
            _gray = gray;
            _channels = sourceChannels == 4 ? 4 : 3;
            _frame = new byte[width * height * _channels];
        }

        public void WriteRow(ReadOnlySpan<byte> row, int channels)
        {
            if (_nextRow >= _height) throw new InvalidOperationException("Too many rows");
            if (channels != _channels) throw new ArgumentException("Channel count mismatch", nameof(channels));
            int dest = _nextRow * _width * _channels;
            if (_gray)
            {
                for (int x = 0; x < _width; x++)
                {
                    int s = x * _channels;
                    int d = dest + x * _channels;
                    byte luma = (byte)((row[s] * 77 + row[s + 1] * 150 + row[s + 2] * 29) >> 8);
                    _frame[d] = luma;
                    _frame[d + 1] = luma;
                    _frame[d + 2] = luma;
                    if (_channels == 4) _frame[d + 3] = row[s + 3];
                }
            }
            else
            {
                row[..(_width * _channels)].CopyTo(_frame.AsSpan(dest));
            }
            _nextRow++;
        }

        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (_channels == 4)
            {
                new GifEncoderAdapterRgba().EncodeRgba32(_path, new Image<Rgba32>(_width, _height, _frame));
            }
            else
            {
                new GifEncoderAdapter().EncodeRgb24(_path, new Image<Rgb24>(_width, _height, _frame));
            }
        }

        public void Dispose()
        {
            if (!_completed) Complete();
        }
    }
}
