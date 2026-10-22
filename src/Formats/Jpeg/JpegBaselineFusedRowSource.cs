using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// 基线交错 JPEG 的逐 MCU 行解码器：熵解码一个 MCU 行后立即做 SIMD IDCT/色彩转换，
/// 不再保留整幅量化系数。progressive/非交错/444 以外的采样由调用方回退。
/// </summary>
/// <remarks>
/// 4:2:0 采用与整幅解码一致的 libjpeg 风格 fancy 上采样（h2v2 三角滤波）。
/// fancy 垂直滤波需要"下一带的第一行色度"作下文，而基线单遍熵解码没有前瞻，
/// 因此消费端在渲染第 k 席时先从队列取出第 k+1 席的系数（producer 本来就提前排队），
/// 只额外 IDCT 其首个色度块行作缓存，深度为 1，不影响内存模型。
/// </remarks>
internal sealed class JpegBaselineFusedRowSource : IRowImageSource
{
    private sealed class RowBlocks(int rowIndex, short[] blocks)
    {
        public int RowIndex { get; } = rowIndex;
        public short[] Blocks { get; } = blocks;
    }

    private readonly JpegFrameState _state;
    private readonly ComponentState[] _components;
    private readonly ScanHeader _scan;
    private readonly byte[] _data;
    private readonly int _entropyOffset;
    private readonly bool _is420;
    private readonly int _bandHeight;
    private readonly int _mcuWidth;
    private readonly int _blockCount;
    private readonly int _rowBlockStride;
    private readonly BlockingCollection<RowBlocks> _rows = new(boundedCapacity: 4);
    private readonly Task _producer;
    private readonly byte[] _band;
    private readonly ushort[] _yQuant;
    private readonly ushort[] _cbQuant;
    private readonly ushort[] _crQuant;
    private ExceptionDispatchInfo? _error;
    private int _rowInBand;
    private int _bandRows;
    private int _bandStartY;
    private bool _disposed;

    // ---- 4:2:0 fancy 上采样缓冲 ----
    private readonly int _chromaStride;   // 色度平面行宽（= mcuX*8）
    private readonly int _chromaCols;     // 参与滤波的色度列数（右缘填充列钳制到真实宽度）
    private readonly int _realChromaW;    // 真实色度宽度 ceil(Width/2)
    private readonly int _filterLen;      // 每个过滤行的输出宽度（mcuX*16）
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
    private RowBlocks? _lookaheadBand;
    private bool _started;

    internal JpegBaselineFusedRowSource(
        JpegFrameState state,
        byte[] data,
        ScanHeader scan,
        int entropyOffset,
        ComponentState[] components,
        bool is420)
    {
        _state = state;
        _data = data;
        _entropyOffset = entropyOffset;
        _scan = scan;
        _components = components;
        _is420 = is420;
        _bandHeight = is420 ? 16 : 8;
        _mcuWidth = is420 ? 16 : 8;
        _blockCount = state.CountBlocksPerMcuRow(components);
        _rowBlockStride = _blockCount * 64;
        Width = state.Frame.Width;
        Height = state.Frame.Height;
        Channels = 3;
        HasAlpha = false;

        _yQuant = state.QuantTables[components[0].QuantTableId].Table;
        _cbQuant = state.QuantTables[components[1].QuantTableId].Table;
        _crQuant = state.QuantTables[components[2].QuantTableId].Table;
        _band = new byte[Width * _bandHeight * 3];

        _chromaStride = state.Frame.McuX * 8;
        _chromaCols = _chromaStride;
        _realChromaW = (Width + 1) >> 1;
        _filterLen = state.Frame.McuX * 16;
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

        _producer = Task.Run(Produce);
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public bool HasAlpha { get; }

    public bool ReadRow(Span<byte> row)
    {
        if (row.Length < Width * 3) throw new ArgumentException("Row buffer too small", nameof(row));
        if (_rowInBand >= _bandRows)
        {
            RowBlocks? frame;
            if (!_started)
            {
                _started = true;
                frame = TakeRow();
                if (frame == null) return false;
            }
            else
            {
                frame = _lookaheadBand;
                if (frame == null) return false;
            }
            // 渲染第 k 席时预取第 k+1 席：fancy 垂直滤波需要下一带的首行色度作下文。
            _lookaheadBand = TakeRow();
            DecodeBand(frame, _lookaheadBand);
        }

        int source = _rowInBand * Width * 3;
        _band.AsSpan(source, Width * 3).CopyTo(row);
        _rowInBand++;
        return true;
    }

    private RowBlocks? TakeRow()
    {
        if (_rows.TryTake(out RowBlocks? frame, Timeout.Infinite)) return frame;
        _error?.Throw();
        return null;
    }

    private void Produce()
    {
        try
        {
            var reader = new JpegBitReader(_data.AsSpan(_entropyOffset));
            int restartInterval = _state.RestartIntervalValue;
            var restart = new JpegFrameState.BaselineRestartState
            {
                ExpectedRst = (int)JpegMarker.RST0,
                UnitsUntilRestart = restartInterval,
                EobRun = 0,
            };

            for (int my = 0; my < _state.Frame.McuY; my++)
            {
                short[] blocks = new short[_state.Frame.McuX * _rowBlockStride];
                _state.DecodeBaselineRowBlocks(_scan, _components, ref reader, my, blocks, ref restart);
                _rows.Add(new RowBlocks(my, blocks));
            }
        }
        catch (Exception ex)
        {
            _error = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            _rows.CompleteAdding();
        }
    }

    private void DecodeBand(RowBlocks frame, RowBlocks? next)
    {
        int bandStartY = frame.RowIndex * _bandHeight;
        _bandRows = Math.Min(_bandHeight, Height - bandStartY);
        _bandStartY = bandStartY;
        _rowInBand = 0;
        _band.AsSpan().Clear();

        if (_is420)
        {
            DecodeBand420Fancy(frame, next);
        }
        else
        {
            DecodeBand444(frame);
        }
    }

    private unsafe void DecodeBand420Fancy(RowBlocks frame, RowBlocks? next)
    {
        int stride = Width * 3;
        int mcuX = _state.Frame.McuX;
        Span<byte> temp = stackalloc byte[16 * 16 * 3];

        fixed (byte* pBand = _band)
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
            // 1) 本带色度块 IDCT → 0..255 字节平面（行0..7 对应带内色度行）。
            for (int mx = 0; mx < mcuX; mx++)
            {
                ReadOnlySpan<short> blocks = frame.Blocks.AsSpan(mx * _rowBlockStride, _rowBlockStride);
                SimdJpegPipeline.Idct8x8ToPlane(blocks.Slice(256, 64), _cbQuant, pCbPlane + mx * 8, _chromaStride);
                SimdJpegPipeline.Idct8x8ToPlane(blocks.Slice(320, 64), _crQuant, pCrPlane + mx * 8, _chromaStride);
            }

            // 2) 上下文行：首带的"上文"= 本带行0（镜像钳制）；末带的"下文"= 本带行7。
            if (frame.RowIndex == 0)
            {
                Buffer.MemoryCopy(pCbPlane, pPrevCb, _chromaStride, _chromaStride);
                Buffer.MemoryCopy(pCrPlane, pPrevCr, _chromaStride, _chromaStride);
            }
            if (next == null)
            {
                byte* lastRowCb = pCbPlane + 7 * _chromaStride;
                byte* lastRowCr = pCrPlane + 7 * _chromaStride;
                Buffer.MemoryCopy(lastRowCb, pNextCb, _chromaStride, _chromaStride);
                Buffer.MemoryCopy(lastRowCr, pNextCr, _chromaStride, _chromaStride);
            }
            else
            {
                // 只额外 IDCT 下一带的首个色度块行，取行0 作为"下文"。
                for (int mx = 0; mx < mcuX; mx++)
                {
                    ReadOnlySpan<short> nb = next.Blocks.AsSpan(mx * _rowBlockStride, _rowBlockStride);
                    SimdJpegPipeline.Idct8x8ToPlane(nb.Slice(256, 64), _cbQuant, pNextCbPlane + mx * 8, _chromaStride);
                    SimdJpegPipeline.Idct8x8ToPlane(nb.Slice(320, 64), _crQuant, pNextCrPlane + mx * 8, _chromaStride);
                }
                Buffer.MemoryCopy(pNextCbPlane, pNextCb, _chromaStride, _chromaStride);
                Buffer.MemoryCopy(pNextCrPlane, pNextCr, _chromaStride, _chromaStride);
            }

            // 3) 逐输出行 fancy 过滤（垂直 (3,1) + 水平三角滤波）。
            for (int j = 0; j < _bandRows; j++)
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
                SimdJpegPipeline.FancyVh2x2(row0Cb, row1Cb, _chromaCols, _realChromaW, pCbF + (long)j * _filterLen);
                SimdJpegPipeline.FancyVh2x2(row0Cr, row1Cr, _chromaCols, _realChromaW, pCrF + (long)j * _filterLen);
            }

            // 4) 逐 MCU：Y IDCT + 过滤色度 → RGB24。
            for (int mx = 0; mx < mcuX; mx++)
            {
                int px = mx * _mcuWidth;
                if (px >= Width) break;
                ReadOnlySpan<short> blocks = frame.Blocks.AsSpan(mx * _rowBlockStride, _rowBlockStride);
                SimdJpegPipeline.Block8x8Vectors vy0 = SimdJpegPipeline.Idct8x8ToVectors(blocks.Slice(0, 64), _yQuant);
                SimdJpegPipeline.Block8x8Vectors vy1 = SimdJpegPipeline.Idct8x8ToVectors(blocks.Slice(64, 64), _yQuant);
                SimdJpegPipeline.Block8x8Vectors vy2 = SimdJpegPipeline.Idct8x8ToVectors(blocks.Slice(128, 64), _yQuant);
                SimdJpegPipeline.Block8x8Vectors vy3 = SimdJpegPipeline.Idct8x8ToVectors(blocks.Slice(192, 64), _yQuant);

                bool direct = px + 16 <= Width && _bandRows >= 16;
                byte* pDestBase = direct ? pBand + px * 3 : pTemp;
                int destStride = direct ? stride : 16 * 3;

                for (int j = 0; j < _bandRows; j++)
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
                    for (int j = 0; j < _bandRows; j++)
                    {
                        Buffer.MemoryCopy(pTemp + (long)j * 16 * 3, pBand + (long)j * stride + px * 3, copyW * 3, copyW * 3);
                    }
                }
            }

            // 5) 保存本带最后色度行，作下一带的上文。
            byte* lastCb = pCbPlane + 7 * _chromaStride;
            byte* lastCr = pCrPlane + 7 * _chromaStride;
            Buffer.MemoryCopy(lastCb, pPrevCb, _chromaStride, _chromaStride);
            Buffer.MemoryCopy(lastCr, pPrevCr, _chromaStride, _chromaStride);
        }
    }

    private void DecodeBand444(RowBlocks frame)
    {
        int stride = Width * 3;
        int mcuX = _state.Frame.McuX;

        for (int mx = 0; mx < mcuX; mx++)
        {
            int px = mx * _mcuWidth;
            if (px >= Width) break;
            ReadOnlySpan<short> blocks = frame.Blocks.AsSpan(mx * _rowBlockStride, _rowBlockStride);

            ReadOnlySpan<short> y = blocks.Slice(0, 64);
            ReadOnlySpan<short> cb = blocks.Slice(64, 64);
            ReadOnlySpan<short> cr = blocks.Slice(128, 64);
            if (px + 8 <= Width && _bandRows >= 8)
            {
                SimdJpegPipeline.TransformAndConvertYCbCr8x8(
                    y, _yQuant, cb, _cbQuant, cr, _crQuant,
                    _band.AsSpan(px * 3), stride);
            }
            else
            {
                Span<byte> temp = stackalloc byte[8 * 8 * 3];
                SimdJpegPipeline.TransformAndConvertYCbCr8x8(
                    y, _yQuant, cb, _cbQuant, cr, _crQuant,
                    temp, 8 * 3);
                int copyW = Math.Min(8, Width - px) * 3;
                int copyH = Math.Min(8, _bandRows);
                for (int intY = 0; intY < copyH; intY++)
                {
                    temp.Slice(intY * 8 * 3, copyW).CopyTo(_band.AsSpan(intY * stride + px * 3));
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rows.CompleteAdding();
        try { _producer.Wait(2000); } catch { }
        _rows.Dispose();
        _state.DisposeResources();
    }
}
