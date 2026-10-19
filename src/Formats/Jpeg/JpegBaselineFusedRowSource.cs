using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// 基线交错 JPEG 的逐 MCU 行解码器：熵解码一个 MCU 行后立即做 SIMD IDCT/色彩转换，
/// 不再保留整幅量化系数。progressive/非交错/420、444 以外的采样由调用方回退。
/// </summary>
/// <remarks>
/// <para>两种模式共享同一条「解一行 → 立即 IDCT 成一行 RGB 窄带」的流水，差别只在谁驱动：</para>
/// <list type="bullet">
/// <item><b>同步模式</b>（<see cref="JpegExecutionMode.IsSequential"/>，单线程运行时）：熵解码在
/// <see cref="ReadRow"/> 的调用线程上就地完成，只用一段 MCU 行缓冲。没有线程、没有阻塞集合，
/// 因此不需求 Monitor / SemaphoreSlim —— 单线程 wasm 里对这两者的阻塞等待会直接抛
/// <c>PlatformNotSupportedException</c>。</item>
/// <item><b>并行模式</b>：生产者线程熵解码同一个游标、把行块投进容量 4 的
/// <see cref="BlockingCollection{T}"/>，消费线程（即 <see cref="ReadRow"/> 的调用方）做 IDCT。</item>
/// </list>
/// <para>
/// 同步模式<b>不是</b>一次预解码整幅：熵游标与重启动状态本就只能顺序推进，预解码省不下任何时间，
/// 却会把峰值内存从「一行块」抬到「整幅系数」（4:4:4 约 4 B/px），还得外加每个行块一次分配。
/// </para>
/// <para>
/// <b>为什么同步模式把整段位读器状态抓进字段。</b>
/// 位读器 <see cref="JpegBitReader"/> 是 ref struct，装不进字段；但「每解完一行就重建一个」
/// 也<b>不成立</b>——MCU 行边界<b>不是</b>字节边界。只有扫描起点与 RST 边界才会
/// <c>AlignToByte + DiscardBufferedBits</c>，普通行尾位缓冲里通常还剩 0~7 个位
/// （实测 bitCount 观测到 2 / 6 / 12），而 <see cref="JpegBitReader.BytesConsumed"/> 只是
/// 已取字节数，不带位偏移。按字节位置重建等价于把那些残位当垃圾丢掉，下一行从错误边界起解，
/// 于是报 <c>Bad AC coefficients</c>。做法是只持有几个可直接赋值的标量
/// （偏移 / 已消费字节 / 位缓冲原文 / 残位数 / 待定 marker / 填充字节数 / 是否读尽），
/// 每行把字段搬进一个 ref struct 局部、解完再搬回去。
/// </para>
/// <para>
/// 注意 <c>_bitOffset</c> 是相对于 <c>_data.AsSpan(_entropyOffset)</c> 的<b>相对</b>偏移，
/// 起点为 0，不是相对 <c>_data</c> 的绝对位置；写成 <c>entropyOffset</c> 会让首行从错位处起解。
/// </para>
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
    private readonly bool _syncMode;
    private readonly int _bandHeight;
    private readonly int _mcuWidth;
    private readonly int _blockCount;
    private readonly int _rowBlockStride;
    private readonly int _rowBlocksLength;
    private readonly BlockingCollection<RowBlocks>? _rows;
    private readonly Task? _producer;
    private readonly byte[] _band;
    private readonly ushort[] _yQuant;
    private readonly ushort[] _cbQuant;
    private readonly ushort[] _crQuant;
    private ExceptionDispatchInfo? _error;
    private int _rowInBand;
    private int _bandRows;
    private int _bandStartY;
    private bool _disposed;

    // ── 同步模式专用状态 ──────────────────────────────────────────────
    // 行缓冲只分配一次并反复复用（并行模式是每行 new 一个投进队列，回收靠 GC）。
    private short[]? _syncBlocks;

    // 位读器的可赋值标量（见类上的 remarks）。只在字节源模式下使用，不做流式。
    private int _bitOffset;
    private int _bitConsumed;
    private uint _bitBufferRaw;
    private int _bitCount;
    private int _bitPendingMarker;
    private int _bitPadByteCount;
    private bool _bitReachedEnd;
    private int _syncNextRow;
    private JpegFrameState.BaselineRestartState _syncRestart;

    internal JpegBaselineFusedRowSource(
        JpegFrameState state,
        byte[] data,
        int entropyOffset,
        ScanHeader scan,
        ComponentState[] components,
        bool is420)
    {
        _state = state;
        _data = data;
        _entropyOffset = entropyOffset;
        _scan = scan;
        _components = components;
        _is420 = is420;
        _syncMode = JpegExecutionMode.IsSequential;
        _bandHeight = is420 ? 16 : 8;
        _mcuWidth = is420 ? 16 : 8;
        _blockCount = state.CountBlocksPerMcuRow(components);
        _rowBlockStride = _blockCount * 64;
        _rowBlocksLength = state.Frame.McuX * _rowBlockStride;
        Width = state.Frame.Width;
        Height = state.Frame.Height;
        Channels = 3;
        HasAlpha = false;

        _yQuant = state.QuantTables[components[0].QuantTableId].Table;
        _cbQuant = state.QuantTables[components[1].QuantTableId].Table;
        _crQuant = state.QuantTables[components[2].QuantTableId].Table;
        _band = new byte[Width * _bandHeight * 3];

        // 表绑定与 DC 预测器初值必须在扫描起点做一次，两种模式都需要：
        // PrepareBaselineRowScan 只做校验，不再顺带绑定。
        JpegFrameState.BindBaselineRowScanTables(scan, components);
        for (int i = 0; i < components.Length; i++) components[i].ResetPredictors();

        if (_syncMode)
        {
            _syncBlocks = new short[_rowBlocksLength];
            // 位读器以 _data.AsSpan(_entropyOffset) 为原点，因此这里存的是相对偏移（起点为 0），
            // 不是相对 _data 的绝对位置。
            _bitOffset = 0;
            _bitPendingMarker = -1;
            _syncNextRow = 0;
            _syncRestart = new JpegFrameState.BaselineRestartState
            {
                ExpectedRst = (int)JpegMarker.RST0,
                UnitsUntilRestart = state.RestartIntervalValue,
                EobRun = 0,
            };
        }
        else
        {
            _rows = new BlockingCollection<RowBlocks>(boundedCapacity: 4);
            _producer = Task.Run(Produce);
        }
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
            if (_syncMode)
            {
                // 就地解一行——与并行模式下生产者线程做的事完全相同，只是不跨线程。
                if (_syncNextRow >= _state.Frame.McuY) return false;
                frame = ReadSyncRow();
            }
            else
            {
                if (!_rows!.TryTake(out frame, Timeout.Infinite))
                {
                    if (_error != null) _error.Throw();
                    return false;
                }
            }
            DecodeBand(frame);
        }

        int source = _rowInBand * Width * 3;
        _band.AsSpan(source, Width * 3).CopyTo(row);
        _rowInBand++;
        return true;
    }

    private RowBlocks ReadSyncRow()
    {
        var reader = new JpegBitReader(
            _data.AsSpan(_entropyOffset),
            _bitOffset,
            _bitConsumed,
            _bitBufferRaw,
            _bitCount,
            _bitPendingMarker,
            _bitPadByteCount,
            _bitReachedEnd);

        var restart = _syncRestart;
        short[] blocks = _syncBlocks!;
        int rowIndex = _syncNextRow;
        try
        {
            _state.DecodeBaselineRowBlocks(_scan, _components, ref reader, rowIndex, blocks, ref restart);
        }
        finally
        {
            // 正常路径与异常路径都把状态存回去，保证游标不会停在半途。
            _bitOffset = reader.Offset;
            _bitConsumed = reader.BytesConsumed;
            _bitBufferRaw = reader.BitBufferRaw;
            _bitCount = reader.BitCount;
            _bitPendingMarker = reader.PendingMarker;
            _bitPadByteCount = reader.PadByteCount;
            _bitReachedEnd = reader.ReachedEndOfData;
            _syncRestart = restart;
            _syncNextRow = rowIndex + 1;
        }

        return new RowBlocks(rowIndex, blocks);
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
                short[] blocks = new short[_rowBlocksLength];
                _state.DecodeBaselineRowBlocks(_scan, _components, ref reader, my, blocks, ref restart);
                _rows!.Add(new RowBlocks(my, blocks));
            }
        }
        catch (Exception ex)
        {
            _error = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            _rows!.CompleteAdding();
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
        _syncBlocks = null;
        if (_rows != null)
        {
            _rows.CompleteAdding();
            try { _producer!.Wait(2000); } catch { }
            _rows.Dispose();
        }
        _state.DisposeResources();
    }
}
