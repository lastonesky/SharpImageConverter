using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Jpeg
{
    /// <summary>
    /// JPEG 行级源：解析一遍系数（基线/渐进式均可），之后按 MCU 行做 IDCT/上采样，
    /// 只保留一个 MCU 行高的 RGB 窄带。
    /// </summary>
    /// <remarks>
    /// 仍然需要整幅**量化系数**（约 3 B/px 的 native 缓冲）和压缩文件字节；
    /// 省掉的是整幅 RGB 输出（3 B/px）以及一次性 Interleave 的峰值。
    /// </remarks>
    internal sealed class JpegRowSource : IRowImageSource
    {
        private readonly JpegFrameState? _state;
        private readonly IRowImageSource _rows;

        private JpegRowSource(string path, bool useFloatingPointIdct)
        {
            byte[] data = File.ReadAllBytes(path);
            JpegFrameState headerState = JpegDecoder.ParseHeadersForRows(data, out ScanHeader scan, out int entropyOffset);
            if (headerState.ExifOrientation != 1)
            {
                headerState.DisposeResources();
                throw new NotSupportedException("JPEG 行级解码暂不支持 EXIF Orientation≠1；请提高内存预算或使用 --no-memory-guard");
            }

            if (CanUseFused(headerState, scan))
            {
                ComponentState[] components = headerState.PrepareBaselineRowScan(scan);
                bool is420 = components[0].H == 2 && components[0].V == 2;
                _rows = new JpegBaselineFusedRowSource(headerState, data, entropyOffset, scan, components, is420);
                _state = null;
            }
            else
            {
                headerState.DisposeResources();
                _state = JpegDecoder.ParseForTranscode(data, out _);
                if (_state.ExifOrientation != 1)
                {
                    _state.DisposeResources();
                    throw new NotSupportedException("JPEG 行级解码暂不支持 EXIF Orientation≠1；请提高内存预算或使用 --no-memory-guard");
                }
                _rows = _state.CreateRowSource();
            }
        }

        private static bool CanUseFused(JpegFrameState state, in ScanHeader scan)
        {
            ComponentState[] components = state.Components;
            if (state.IsProgressive || components.Length != 3 || scan.Components.Length != 3) return false;
            if (state.ColorSpace != JpegColorSpace.YCbCr) return false;
            for (int i = 0; i < 3; i++)
            {
                if (scan.Components[i].ComponentId != components[i].Id) return false;
            }
            bool is420 = components[0].H == 2 && components[0].V == 2
                && components[1].H == 1 && components[1].V == 1
                && components[2].H == 1 && components[2].V == 1;
            bool is444 = components[0].H == 1 && components[0].V == 1
                && components[1].H == 1 && components[1].V == 1
                && components[2].H == 1 && components[2].V == 1;
            return is420 || is444;
        }

        internal static bool IsSupported(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> head = stackalloc byte[3];
                return fs.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
                    && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
            }
            catch
            {
                return false;
            }
        }

        internal static JpegRowSource Open(string path, bool useFloatingPointIdct = false)
            => new(path, useFloatingPointIdct);

        public int Width => _rows.Width;
        public int Height => _rows.Height;
        public int Channels => _rows.Channels;
        public bool HasAlpha => false;

        public bool ReadRow(Span<byte> row) => _rows.ReadRow(row);

        public void Dispose()
        {
            _rows.Dispose();
            _state?.DisposeResources();
        }
    }
}
