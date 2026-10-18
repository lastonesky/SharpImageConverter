using System;
using System.Buffers;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Jpeg;

internal sealed partial class JpegFrameState
{
    /// <summary>
    /// 以 MCU 行为单位重建 RGB：每次只 IDCT 一个 MCU 行对应的空间块，
    /// 再按行做色度上采样/色彩转换，输出后即可丢弃该窄带。
    /// </summary>
    internal RowSource CreateRowSource()
        => new(this, frame, components, quantTables, hasJfif, hasAdobe, adobeTransform, useFloatingPointIdct);

    internal sealed class RowSource : IRowImageSource
    {
        private readonly JpegFrameState _state;
        private readonly FrameHeader _frame;
        private readonly ComponentState[] _components;
        private readonly QuantizationTable[] _quantTables;
        private readonly bool _useFloatingPointIdct;
        private readonly int[] _componentOrder;
        private readonly bool _convertYCbCr;
        private readonly bool _fusedSimd;
        private readonly bool _is420;
        private readonly int _mcuWidth;
        private readonly byte[]? _rgbBand;
        private readonly int _channels; // 实际组件通道数
        private readonly int _fullWidth;
        private readonly int _fullHeight;
        private readonly int[] _planeWidths;
        private readonly int[] _planeHeights;
        private readonly int[] _bandHeights;
        private readonly int[] _componentBandStarts;
        private readonly byte[][] _bands;
        private readonly int[] _x0;
        private readonly int[] _x1;
        private readonly byte[] _xWeight;
        private readonly int _bandHeight;
        private int _mcuRow;
        private int _bandStartY;
        private int _bandValidHeight;
        private int _rowInBand;

        internal RowSource(
            JpegFrameState state,
            FrameHeader frame,
            ComponentState[] components,
            QuantizationTable[] quantTables,
            bool hasJfif,
            bool hasAdobe,
            byte adobeTransform,
            bool useFloatingPointIdct)
        {
            _state = state;
            _frame = frame;
            _components = components;
            _quantTables = quantTables;
            _useFloatingPointIdct = useFloatingPointIdct;

            JpegColorSpace colorSpace = JpegDecoder.DetermineColorSpace(components, hasJfif, hasAdobe, adobeTransform);
            _componentOrder = JpegDecoder.BuildComponentOrder(components, colorSpace);
            _convertYCbCr = colorSpace == JpegColorSpace.YCbCr;
            _channels = _componentOrder.Length;
            bool y420 = components.Length == 3
                && components[0].H == 2 && components[0].V == 2
                && components[1].H == 1 && components[1].V == 1
                && components[2].H == 1 && components[2].V == 1;
            bool y444 = components.Length == 3
                && components[0].H == 1 && components[0].V == 1
                && components[1].H == 1 && components[1].V == 1
                && components[2].H == 1 && components[2].V == 1;
            _fusedSimd = _convertYCbCr && _channels == 3 && (y420 || y444);
            _is420 = y420;
            _mcuWidth = y420 ? 16 : 8;
            if (_channels is not (1 or 3))
            {
                throw new NotSupportedException($"JPEG row streaming does not support {_channels}-channel output");
            }

            Width = frame.Width;
            Height = frame.Height;
            Channels = 3;
            HasAlpha = false;

            _fullWidth = frame.McuX * frame.MaxH * 8;
            _fullHeight = frame.McuY * frame.MaxV * 8;
            _bandHeight = frame.MaxV * 8;
            _planeWidths = new int[components.Length];
            _planeHeights = new int[components.Length];
            _bandHeights = new int[components.Length];
            _componentBandStarts = new int[components.Length];
            _bands = new byte[components.Length][];
            for (int i = 0; i < components.Length; i++)
            {
                int pw = frame.McuX * components[i].H * 8;
                int ph = frame.McuY * components[i].V * 8;
                _planeWidths[i] = pw;
                _planeHeights[i] = ph;
                _bandHeights[i] = components[i].V * 8;
                _bands[i] = new byte[pw * _bandHeights[i]];
            }

            _rgbBand = _fusedSimd ? new byte[Width * _bandHeight * 3] : null;

            int mapLength = Width * _channels;
            _x0 = new int[mapLength];
            _x1 = new int[mapLength];
            _xWeight = new byte[mapLength];
            for (int c = 0; c < _channels; c++)
            {
                int compIndex = _componentOrder[c];
                for (int x = 0; x < Width; x++)
                {
                    // 与整幅 SIMD 重建路径一致：色度按最近邻复制（水平/垂直）。
                    int sx = (int)((long)x * _planeWidths[compIndex] / _fullWidth);
                    sx = Math.Clamp(sx, 0, _planeWidths[compIndex] - 1);
                    int index = c * Width + x;
                    _x0[index] = sx;
                    _x1[index] = sx;
                    _xWeight[index] = 0;
                }
            }
        }

        public int Width { get; }
        public int Height { get; }
        public int Channels { get; }
        public bool HasAlpha { get; }

        public bool ReadRow(Span<byte> row)
        {
            if (row.Length < Width * 3) throw new ArgumentException("Row buffer too small", nameof(row));
            if (_rowInBand >= _bandValidHeight && !DecodeNextBand()) return false;

            if (_fusedSimd)
            {
                int source = _rowInBand * Width * 3;
                _rgbBand!.AsSpan(source, Width * 3).CopyTo(row);
                _rowInBand++;
                return true;
            }

            int localY = _rowInBand;
            int output = 0;
            int globalY = _bandStartY + localY;

            Span<int> y0 = stackalloc int[3];
            Span<int> y1 = stackalloc int[3];
            Span<int> yw = stackalloc int[3];

            for (int c = 0; c < _channels; c++)
            {
                int compIndex = _componentOrder[c];
                int sy = (int)((long)globalY * _planeHeights[compIndex] / _fullHeight);
                sy = Math.Clamp(sy, 0, _planeHeights[compIndex] - 1);
                int bandStart = _componentBandStarts[compIndex];
                int maxLocal = _bandHeights[compIndex] - 1;
                y0[c] = Math.Clamp(sy - bandStart, 0, maxLocal);
                y1[c] = y0[c];
                yw[c] = 0;
            }

            byte r = 0, g = 0, b = 0;
            for (int x = 0; x < Width; x++)
            {
                int gray = 0;
                for (int c = 0; c < _channels; c++)
                {
                    int compIndex = _componentOrder[c];
                    byte[] band = _bands[compIndex];
                    int stride = _planeWidths[compIndex];
                    int mapIndex = c * Width + x;
                    int sx0 = _x0[mapIndex];
                    int sx1 = _x1[mapIndex];
                    int wx = _xWeight[mapIndex];
                    int i0 = y0[c] * stride + sx0;
                    int i1 = y0[c] * stride + sx1;
                    int i2 = y1[c] * stride + sx0;
                    int i3 = y1[c] * stride + sx1;
                    if ((uint)i0 >= (uint)band.Length || (uint)i1 >= (uint)band.Length
                        || (uint)i2 >= (uint)band.Length || (uint)i3 >= (uint)band.Length)
                    {
                        throw new IndexOutOfRangeException(
                            $"band index out of range: c={c} comp={compIndex} x={x} y0={y0[c]} y1={y1[c]} " +
                            $"stride={stride} bandLen={band.Length} sx0={sx0} sx1={sx1}");
                    }
                    int top = ((band[i0] * (256 - wx)) + (band[i1] * wx) + 128) >> 8;
                    int bottom = ((band[i2] * (256 - wx)) + (band[i3] * wx) + 128) >> 8;
                    int value = ((top * (256 - yw[c])) + (bottom * yw[c]) + 128) >> 8;
                    if (_channels == 1) gray = value;
                    else if (c == 0) r = (byte)value;
                    else if (c == 1) g = (byte)value;
                    else b = (byte)value;
                }

                if (_channels == 1)
                {
                    r = g = b = (byte)gray;
                }
                else if (_convertYCbCr)
                {
                    int y = r;
                    int cb = g - 128;
                    int cr = b - 128;
                    int rv = y + ((cr * 91881) >> 16);
                    int gv = y - (((cb * 22554) + (cr * 46802)) >> 16);
                    int bv = y + ((cb * 116130) >> 16);
                    r = (byte)(rv < 0 ? 0 : rv > 255 ? 255 : rv);
                    g = (byte)(gv < 0 ? 0 : gv > 255 ? 255 : gv);
                    b = (byte)(bv < 0 ? 0 : bv > 255 ? 255 : bv);
                }
                row[output++] = r;
                row[output++] = g;
                row[output++] = b;
            }

            _rowInBand++;
            return true;
        }

        private bool DecodeNextBand()
        {
            if (_mcuRow >= _frame.McuY) return false;

            if (_fusedSimd)
            {
                DecodeFusedBand();
                _bandStartY = _mcuRow * _bandHeight;
                _bandValidHeight = Math.Min(_bandHeight, Height - _bandStartY);
                _rowInBand = 0;
                _mcuRow++;
                return _bandValidHeight > 0;
            }

            for (int i = 0; i < _components.Length; i++)
            {
                ComponentState component = _components[i];
                int startBlockRow = _mcuRow * component.V;
                _componentBandStarts[i] = startBlockRow * 8;
                component.DecodeSpatialBand(
                    _bands[i],
                    _planeWidths[i],
                    _planeWidths[i],
                    startBlockRow,
                    component.V,
                    _quantTables[component.QuantTableId].Table,
                    _useFloatingPointIdct);
            }

            _bandStartY = _mcuRow * _bandHeight;
            _bandValidHeight = Math.Min(_bandHeight, Height - _bandStartY);
            _rowInBand = 0;
            _mcuRow++;
            return _bandValidHeight > 0;
        }

        private void DecodeFusedBand()
        {
            int my = _mcuRow;
            byte[] band = _rgbBand!;
            int stride = Width * 3;
            ComponentState yComp = _components[_componentOrder[0]];
            ComponentState cbComp = _components[_componentOrder[1]];
            ComponentState crComp = _components[_componentOrder[2]];
            ushort[] yQuant = _quantTables[yComp.QuantTableId].Table;
            ushort[] cbQuant = _quantTables[cbComp.QuantTableId].Table;
            ushort[] crQuant = _quantTables[crComp.QuantTableId].Table;
            int bandRows = Math.Min(_bandHeight, Height - my * _bandHeight);
            band.AsSpan(0, Width * bandRows * 3).Clear();

            for (int mx = 0; mx < _frame.McuX; mx++)
            {
                int px = mx * _mcuWidth;
                if (px >= Width) break;

                if (_is420)
                {
                    int yBlocksX = yComp.BlocksX;
                    ReadOnlySpan<short> y0 = yComp.GetBlockSpan(((my * 2) + 0) * yBlocksX + (mx * 2 + 0));
                    ReadOnlySpan<short> y1 = yComp.GetBlockSpan(((my * 2) + 0) * yBlocksX + (mx * 2 + 1));
                    ReadOnlySpan<short> y2 = yComp.GetBlockSpan(((my * 2) + 1) * yBlocksX + (mx * 2 + 0));
                    ReadOnlySpan<short> y3 = yComp.GetBlockSpan(((my * 2) + 1) * yBlocksX + (mx * 2 + 1));
                    ReadOnlySpan<short> cb = cbComp.GetBlockSpan(my * cbComp.BlocksX + mx);
                    ReadOnlySpan<short> cr = crComp.GetBlockSpan(my * crComp.BlocksX + mx);

                    if (px + 16 <= Width && bandRows >= 16)
                    {
                        SimdJpegPipeline.TransformAndConvertYCbCr420(
                            y0, y1, y2, y3, yQuant, cb, cbQuant, cr, crQuant,
                            band.AsSpan(px * 3), stride);
                    }
                    else
                    {
                        Span<byte> temp = stackalloc byte[16 * 16 * 3];
                        SimdJpegPipeline.TransformAndConvertYCbCr420(
                            y0, y1, y2, y3, yQuant, cb, cbQuant, cr, crQuant, temp, 16 * 3);
                        int copyW = Math.Min(16, Width - px) * 3;
                        int copyH = Math.Min(16, bandRows);
                        for (int y = 0; y < copyH; y++)
                        {
                            temp.Slice(y * 16 * 3, copyW).CopyTo(band.AsSpan(y * stride + px * 3));
                        }
                    }
                }
                else
                {
                    ReadOnlySpan<short> y = yComp.GetBlockSpan(my * yComp.BlocksX + mx);
                    ReadOnlySpan<short> cb = cbComp.GetBlockSpan(my * cbComp.BlocksX + mx);
                    ReadOnlySpan<short> cr = crComp.GetBlockSpan(my * crComp.BlocksX + mx);
                    if (px + 8 <= Width && bandRows >= 8)
                    {
                        SimdJpegPipeline.TransformAndConvertYCbCr8x8(
                            y, yQuant, cb, cbQuant, cr, crQuant, band.AsSpan(px * 3), stride);
                    }
                    else
                    {
                        Span<byte> temp = stackalloc byte[8 * 8 * 3];
                        SimdJpegPipeline.TransformAndConvertYCbCr8x8(
                            y, yQuant, cb, cbQuant, cr, crQuant, temp, 8 * 3);
                        int copyW = Math.Min(8, Width - px) * 3;
                        int copyH = Math.Min(8, bandRows);
                        for (int yy = 0; yy < copyH; yy++)
                        {
                            temp.Slice(yy * 8 * 3, copyW).CopyTo(band.AsSpan(yy * stride + px * 3));
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            // 状态机资源由外层 JpegRowSource 负责释放；这里只丢弃窄带。
            Array.Clear(_bands);
            if (_rgbBand != null) Array.Clear(_rgbBand);
        }
    }
}
