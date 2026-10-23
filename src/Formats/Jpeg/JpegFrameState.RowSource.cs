using System;
using System.Buffers;
using System.IO;
using System.Runtime.Intrinsics;
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

        // ---- 4:2:0 fancy 上采样缓冲（SIMD 条带路径） ----
        private readonly bool _fancy420Simd;
        private readonly int _chromaStride;
        private readonly int _filterLen;
        private readonly int _realChromaW;
        private readonly int _realChromaH;
        private readonly byte[] _cbPlane = null!;
        private readonly byte[] _crPlane = null!;
        private readonly byte[] _nextCbPlane = null!;
        private readonly byte[] _nextCrPlane = null!;
        private readonly byte[] _prevCbRow = null!;
        private readonly byte[] _prevCrRow = null!;
        private readonly byte[] _nextCbRow = null!;
        private readonly byte[] _nextCrRow = null!;
        private readonly short[] _cbF = null!;
        private readonly short[] _crF = null!;

        // ---- 4:2:2 fancy 上采样缓冲（标量路径，仅水平滤波） ----
        private readonly bool _fancy422;
        private readonly short[] _fancyCb = null!;
        private readonly short[] _fancyCr = null!;

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
            // 浮点 IDCT 只有标量路径（FloatingPointIDCT），融合条带用的是 SIMD 定点 IDCT；
            // 请求浮点时必须绕开融合路径，否则该选项在流式解码里被静默忽略。
            _fusedSimd = !_useFloatingPointIdct && _convertYCbCr && _channels == 3 && (y420 || y444);
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

            // 色度过窄（不超过 2 个样本）时 libjpeg 自己也退回复制，保持最近邻。
            _realChromaW = (Width + 1) >> 1;
            _realChromaH = (Height + 1) >> 1;
            bool y422 = components.Length == 3
                && components[0].H == 2 && components[0].V == 1
                && components[1].H == 1 && components[1].V == 1
                && components[2].H == 1 && components[2].V == 1;
            _fancy420Simd = _fusedSimd && _is420 && _realChromaW > 2;
            _fancy422 = _convertYCbCr && _channels == 3 && y422 && _realChromaW > 2;
            if (_fancy420Simd)
            {
                _chromaStride = frame.McuX * 8;
                _filterLen = frame.McuX * 16;
                _cbPlane = new byte[_chromaStride * 8];
                _crPlane = new byte[_chromaStride * 8];
                _nextCbPlane = new byte[_chromaStride * 8];
                _nextCrPlane = new byte[_chromaStride * 8];
                _prevCbRow = new byte[_chromaStride];
                _prevCrRow = new byte[_chromaStride];
                _nextCbRow = new byte[_chromaStride];
                _nextCrRow = new byte[_chromaStride];
                _cbF = new short[_filterLen * 16];
                _crF = new short[_filterLen * 16];
            }
            if (_fancy422)
            {
                _fancyCb = new short[((Width + 15) >> 4) * 16];
                _fancyCr = new short[((Width + 15) >> 4) * 16];
            }

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

            if (_fancy422)
            {
                ComputeFancy422Rows(globalY);
            }

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
                    if (_fancy422 && c > 0)
                    {
                        // 4:2:2 色度：行内 fancy 水平滤波结果已预算好，直接取值。
                        int fancy = c == 1 ? _fancyCb[x] : _fancyCr[x];
                        if (c == 1) g = (byte)fancy; else b = (byte)fancy;
                        continue;
                    }
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
                    // 移位前加 ONE_HALF（1 << 15）四舍五入，与 libjpeg jdcolor.c 一致，消除普遍的 ±1 色差。
                    int rv = y + ((cr * 91881 + 32768) >> 16);
                    int gv = y - (((cb * 22554) + (cr * 46802) + 32768) >> 16);
                    int bv = y + ((cb * 116130 + 32768) >> 16);
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

        private unsafe void ComputeFancy422Rows(int globalY)
        {
            int cb = _componentOrder[1];
            int cr = _componentOrder[2];
            int cbLocal = globalY - _componentBandStarts[cb];
            int crLocal = globalY - _componentBandStarts[cr];
            fixed (byte* pCbBand = _bands[cb])
            fixed (byte* pCrBand = _bands[cr])
            fixed (short* pFcb = _fancyCb)
            fixed (short* pFcr = _fancyCr)
            {
                SimdJpegPipeline.FancyH1x2(pCbBand + (long)cbLocal * _planeWidths[cb], _planeWidths[cb], _realChromaW, pFcb);
                SimdJpegPipeline.FancyH1x2(pCrBand + (long)crLocal * _planeWidths[cr], _planeWidths[cr], _realChromaW, pFcr);
            }
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
            if (_fancy420Simd)
            {
                DecodeFusedBand420Fancy();
                return;
            }

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

        /// <summary>4:2:0 带状 fancy 渲染：色度平面 + 上下文行缓存，与整幅解码的滤波逐位一致。</summary>
        private unsafe void DecodeFusedBand420Fancy()
        {
            int my = _mcuRow;
            byte[] band = _rgbBand!;
            int stride = Width * 3;
            int mcuX = _frame.McuX;
            ComponentState yComp = _components[_componentOrder[0]];
            ComponentState cbComp = _components[_componentOrder[1]];
            ComponentState crComp = _components[_componentOrder[2]];
            ushort[] yQuant = _quantTables[yComp.QuantTableId].Table;
            ushort[] cbQuant = _quantTables[cbComp.QuantTableId].Table;
            ushort[] crQuant = _quantTables[crComp.QuantTableId].Table;
            int bandRows = Math.Min(_bandHeight, Height - my * _bandHeight);
            band.AsSpan(0, Width * bandRows * 3).Clear();
            Span<byte> temp = stackalloc byte[16 * 16 * 3];

            fixed (byte* pBand = band)
            fixed (byte* pCbPlane = _cbPlane)
            fixed (byte* pCrPlane = _crPlane)
            fixed (byte* pNextCbPlane = _nextCbPlane)
            fixed (byte* pNextCrPlane = _nextCrPlane)
            fixed (byte* pPrevCb = _prevCbRow)
            fixed (byte* pPrevCr = _prevCrRow)
            fixed (byte* pNextCb = _nextCbRow)
            fixed (byte* pNextCr = _nextCrRow)
            fixed (short* pCbF = _cbF)
            fixed (short* pCrF = _crF)
            fixed (byte* pTemp = temp)
            {
                // 1) 本带色度块 IDCT → 0..255 字节平面。
                for (int mx = 0; mx < mcuX; mx++)
                {
                    int bi = my * cbComp.BlocksX + mx;
                    SimdJpegPipeline.Idct8x8ToPlane(cbComp.GetBlockSpan(bi), cbQuant, pCbPlane + mx * 8, _chromaStride);
                    SimdJpegPipeline.Idct8x8ToPlane(crComp.GetBlockSpan(bi), crQuant, pCrPlane + mx * 8, _chromaStride);
                }

                // 2) 上下文行：首带/末带镜像钳制，其余取相邻带（系数全量可用，直接 IDCT 对应块行）。
                if (my == 0)
                {
                    Buffer.MemoryCopy(pCbPlane, pPrevCb, _chromaStride, _chromaStride);
                    Buffer.MemoryCopy(pCrPlane, pPrevCr, _chromaStride, _chromaStride);
                }
                if (my + 1 >= _frame.McuY)
                {
                    Buffer.MemoryCopy(pCbPlane + 7 * _chromaStride, pNextCb, _chromaStride, _chromaStride);
                    Buffer.MemoryCopy(pCrPlane + 7 * _chromaStride, pNextCr, _chromaStride, _chromaStride);
                }
                else
                {
                    for (int mx = 0; mx < mcuX; mx++)
                    {
                        int bi = (my + 1) * cbComp.BlocksX + mx;
                        SimdJpegPipeline.Idct8x8ToPlane(cbComp.GetBlockSpan(bi), cbQuant, pNextCbPlane + mx * 8, _chromaStride);
                        SimdJpegPipeline.Idct8x8ToPlane(crComp.GetBlockSpan(bi), crQuant, pNextCrPlane + mx * 8, _chromaStride);
                    }
                    Buffer.MemoryCopy(pNextCbPlane, pNextCb, _chromaStride, _chromaStride);
                    Buffer.MemoryCopy(pNextCrPlane, pNextCr, _chromaStride, _chromaStride);
                }

                // 3) 逐输出行 fancy 过滤。
                for (int j = 0; j < bandRows; j++)
                {
                    int r0 = j >> 1;
                    byte* row0Cb = pCbPlane + r0 * _chromaStride;
                    byte* row0Cr = pCrPlane + r0 * _chromaStride;
                    byte* row1Cb = ((j & 1) == 0)
                        ? (r0 > 0 ? pCbPlane + (r0 - 1) * _chromaStride : pPrevCb)
                        : (r0 < 7 ? pCbPlane + (r0 + 1) * _chromaStride : pNextCb);
                    byte* row1Cr = ((j & 1) == 0)
                        ? (r0 > 0 ? pCrPlane + (r0 - 1) * _chromaStride : pPrevCr)
                        : (r0 < 7 ? pCrPlane + (r0 + 1) * _chromaStride : pNextCr);
                    SimdJpegPipeline.FancyVh2x2(row0Cb, row1Cb, _chromaStride, _realChromaW, pCbF + (long)j * _filterLen);
                    SimdJpegPipeline.FancyVh2x2(row0Cr, row1Cr, _chromaStride, _realChromaW, pCrF + (long)j * _filterLen);
                }

                // 4) 逐 MCU：Y IDCT + 过滤色度 → RGB24。
                for (int mx = 0; mx < mcuX; mx++)
                {
                    int px = mx * _mcuWidth;
                    if (px >= Width) break;

                    int blocksX = yComp.BlocksX;
                    int yTop = (my * 2) * blocksX + mx * 2;
                    int yBot = (my * 2 + 1) * blocksX + mx * 2;
                    SimdJpegPipeline.Block8x8Vectors vy0 = SimdJpegPipeline.Idct8x8ToVectors(yComp.GetBlockSpan(yTop), yQuant);
                    SimdJpegPipeline.Block8x8Vectors vy1 = SimdJpegPipeline.Idct8x8ToVectors(yComp.GetBlockSpan(yTop + 1), yQuant);
                    SimdJpegPipeline.Block8x8Vectors vy2 = SimdJpegPipeline.Idct8x8ToVectors(yComp.GetBlockSpan(yBot), yQuant);
                    SimdJpegPipeline.Block8x8Vectors vy3 = SimdJpegPipeline.Idct8x8ToVectors(yComp.GetBlockSpan(yBot + 1), yQuant);

                    bool direct = px + 16 <= Width && bandRows >= 16;
                    byte* pDestBase = direct ? pBand + px * 3 : pTemp;
                    int destStride = direct ? stride : 16 * 3;

                    for (int j = 0; j < bandRows; j++)
                    {
                        Vector128<short> yL, yR;
                        if (j < 8)
                        {
                            yL = SimdJpegPipeline.GetRow(in vy0, j);
                            yR = SimdJpegPipeline.GetRow(in vy1, j);
                        }
                        else
                        {
                            yL = SimdJpegPipeline.GetRow(in vy2, j - 8);
                            yR = SimdJpegPipeline.GetRow(in vy3, j - 8);
                        }

                        short* fcb = pCbF + (long)j * _filterLen + px;
                        short* fcr = pCrF + (long)j * _filterLen + px;
                        Vector128<short> cbL = Vector128.Load(fcb) - SimdJpegPipeline.ChromaLevel128;
                        Vector128<short> cbR = Vector128.Load(fcb + 8) - SimdJpegPipeline.ChromaLevel128;
                        Vector128<short> crL = Vector128.Load(fcr) - SimdJpegPipeline.ChromaLevel128;
                        Vector128<short> crR = Vector128.Load(fcr + 8) - SimdJpegPipeline.ChromaLevel128;

                        byte* pRow = pDestBase + (long)j * destStride;
                        SimdJpegPipeline.ConvertRowYCbCrToRgb(yL, cbL, crL, pRow);
                        SimdJpegPipeline.ConvertRowYCbCrToRgb(yR, cbR, crR, pRow + 24);
                    }

                    if (!direct)
                    {
                        int copyW = Math.Min(16, Width - px);
                        for (int j = 0; j < bandRows; j++)
                        {
                            Buffer.MemoryCopy(pTemp + (long)j * 16 * 3, pBand + (long)j * stride + px * 3, copyW * 3, copyW * 3);
                        }
                    }
                }

                // 5) 保存本带最后色度行，作下一带的上文。
                Buffer.MemoryCopy(pCbPlane + 7 * _chromaStride, pPrevCb, _chromaStride, _chromaStride);
                Buffer.MemoryCopy(pCrPlane + 7 * _chromaStride, pPrevCr, _chromaStride, _chromaStride);
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
