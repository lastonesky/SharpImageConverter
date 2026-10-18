using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// 基线交错 JPEG 的逐 MCU 行解码器：熵解码一个 MCU 行后立即做 SIMD IDCT/色彩转换，
/// 不再保留整幅量化系数。progressive/非交错/444 以外的采样由调用方回退。
/// </summary>
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
            if (!_rows.TryTake(out RowBlocks? frame, Timeout.Infinite))
            {
                if (_error != null) _error.Throw();
                return false;
            }
            DecodeBand(frame);
        }

        int source = _rowInBand * Width * 3;
        _band.AsSpan(source, Width * 3).CopyTo(row);
        _rowInBand++;
        return true;
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

    private void DecodeBand(RowBlocks frame)
    {
        int bandStartY = frame.RowIndex * _bandHeight;
        _bandRows = Math.Min(_bandHeight, Height - bandStartY);
        _bandStartY = bandStartY;
        _rowInBand = 0;
        _band.AsSpan().Clear();

        int stride = Width * 3;
        int mcuX = _state.Frame.McuX;
        for (int mx = 0; mx < mcuX; mx++)
        {
            int px = mx * _mcuWidth;
            if (px >= Width) break;
            ReadOnlySpan<short> blocks = frame.Blocks.AsSpan(mx * _rowBlockStride, _rowBlockStride);

            if (_is420)
            {
                ReadOnlySpan<short> y0 = blocks.Slice(0, 64);
                ReadOnlySpan<short> y1 = blocks.Slice(64, 64);
                ReadOnlySpan<short> y2 = blocks.Slice(128, 64);
                ReadOnlySpan<short> y3 = blocks.Slice(192, 64);
                ReadOnlySpan<short> cb = blocks.Slice(256, 64);
                ReadOnlySpan<short> cr = blocks.Slice(320, 64);

                if (px + 16 <= Width && _bandRows >= 16)
                {
                    SimdJpegPipeline.TransformAndConvertYCbCr420(
                        y0, y1, y2, y3, _yQuant, cb, _cbQuant, cr, _crQuant,
                        _band.AsSpan(px * 3), stride);
                }
                else
                {
                    Span<byte> temp = stackalloc byte[16 * 16 * 3];
                    SimdJpegPipeline.TransformAndConvertYCbCr420(
                        y0, y1, y2, y3, _yQuant, cb, _cbQuant, cr, _crQuant,
                        temp, 16 * 3);
                    int copyW = Math.Min(16, Width - px) * 3;
                    int copyH = Math.Min(16, _bandRows);
                    for (int y = 0; y < copyH; y++)
                    {
                        temp.Slice(y * 16 * 3, copyW).CopyTo(_band.AsSpan(y * stride + px * 3));
                    }
                }
            }
            else
            {
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
