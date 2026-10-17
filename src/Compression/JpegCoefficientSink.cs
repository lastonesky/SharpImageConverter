using System;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Compression;

/// <summary>
/// 把编码器在熵编码之前交出的量化系数，按「解码器组件平面」布局收好，供搜索期直接重建。
/// </summary>
/// <remarks>
/// 布局与 <see cref="JpegFrameState.DecodeScan"/> 解出来的系数完全一致（每个组件一块连续的
/// <c>short</c> 平面，块内 64 个按自然顺序），因此 <see cref="Reconstruct"/> 可以走与正常解码
/// 相同的 <see cref="JpegFrameState.ReconstructImage()"/>，像素逐字节相同。
/// 差别只在于：这里没有字节流，省掉了「熵编码 → 熵解码」这一趟无损但昂贵的往返。
///
/// 复用策略：几何（宽高、采样方式）不变时，组件与系数缓冲只分配一次
/// （143 MP 约 430 MB，走 <c>NativeMemory</c>，不进 GC 堆），后续候选直接覆盖写入。
/// </remarks>
internal sealed class JpegCoefficientSink : IMcuCoefficientSink, IDisposable
{
    private readonly JpegFrameState state = new(useFloatingPointIdct: false);
    private readonly QuantizationTable[] tables = [new QuantizationTable(), new QuantizationTable()];
    private ComponentState[] components = [];
    private byte[]? pixelBuffer;
    private FrameHeader frame;
    private int mcuX;
    private int width;
    private int height;
    private bool subsample420;
    private bool ready;

    public void Begin(int width, int height, bool subsample420, ReadOnlySpan<byte> lumaTable, ReadOnlySpan<byte> chromaTable)
    {
        // 量化表每个候选都不同，必须每次刷新；它只有 64 项，成本可忽略。
        for (int i = 0; i < 64; i++)
        {
            tables[0].Table[i] = lumaTable[i];
            tables[1].Table[i] = chromaTable[i];
        }

        if (ready && this.width == width && this.height == height && this.subsample420 == subsample420)
        {
            // 几何未变：系数缓冲原样复用，只重新贴上本轮的量化表。
            state.AdoptCoefficients(frame, components, tables);
            return;
        }

        ReleaseComponents();

        this.width = width;
        this.height = height;
        this.subsample420 = subsample420;
        int mcuSize = subsample420 ? 16 : 8;
        mcuX = (width + mcuSize - 1) / mcuSize;
        int mcuY = (height + mcuSize - 1) / mcuSize;
        byte h = subsample420 ? (byte)2 : (byte)1;
        byte v = subsample420 ? (byte)2 : (byte)1;

        components =
        [
            new ComponentState(1, h, v, quantTableId: 0),
            new ComponentState(2, 1, 1, quantTableId: 1),
            new ComponentState(3, 1, 1, quantTableId: 1),
        ];
        foreach (var c in components)
        {
            c.SetGeometry(mcuX, mcuY);
            // 每个块都会被 Accept 覆盖，不必清零：省掉一次整幅平面（143 MP 约 430 MB）的写入。
            c.EnsureCoefficientBuffer(isProgressive: false);
        }

        frame = new FrameHeader(width, height, 8, h, v, mcuX, mcuY);
        state.AdoptCoefficients(frame, components, tables);
        ready = true;
    }

    public void Accept(int sequence, ReadOnlySpan<int> blocks, int blockCount, byte[] order)
    {
        int my = sequence / mcuX;
        int mx = sequence - my * mcuX;
        ComponentState y = components[0];
        int cursor = 0;
        int lumaSeen = 0;

        for (int i = 0; i < blockCount; i++)
        {
            int compIndex = order[i];
            int blockIndex;
            if (compIndex == 0)
            {
                // MCU 内的亮度子块按 (0,0)/(1,0)/(0,1)/(1,1) 排列 —— 与
                // SimdJpegEncodePipeline.RgbToYCbCr420 的 y00/y10/y01/y11 参数顺序一致。
                int dx = lumaSeen % y.H;
                int dy = lumaSeen / y.H;
                lumaSeen++;
                blockIndex = (my * y.V + dy) * y.BlocksX + (mx * y.H + dx);
            }
            else
            {
                ComponentState c = components[compIndex];
                blockIndex = my * c.BlocksX + mx;
            }

            Span<short> dst = components[compIndex].GetBlockSpan(blockIndex);
            ReadOnlySpan<int> src = blocks.Slice(cursor, 64);
            for (int k = 0; k < 64; k++)
            {
                dst[k] = (short)src[k];
            }
            cursor += 64;
        }
    }

    /// <summary>
    /// 用当前收到的系数重建 RGB24（走与正常解码相同的 SIMD 重建路径）。
    /// </summary>
    /// <remarks>
    /// 输出缓冲跨候选复用：143 MP 的 429 MB <c>new byte[]</c> 要 26 ms（运行时会清零），
    /// 而重建会把每个像素都写满，清零纯属浪费。这里用未初始化分配、只分配一次。
    /// 代价是返回的数组是「借」的 —— 下一次 <see cref="Reconstruct"/> 会覆写它，
    /// 所以调用方必须立刻消费（搜索期正是「重建 → Compare」紧邻）。
    /// </remarks>
    public byte[] Reconstruct()
    {
        int needed = checked(width * height * 3);
        if (pixelBuffer is null || pixelBuffer.Length != needed)
        {
            pixelBuffer = GC.AllocateUninitializedArray<byte>(needed);
        }
        return state.ReconstructImage(pixelBuffer).ToRgb24();
    }

    public void Dispose()
    {
        ReleaseComponents();
        pixelBuffer = null;
    }

    private void ReleaseComponents()
    {
        foreach (var c in components)
        {
            c.FreeCoefficientBuffer();
        }
        components = [];
        ready = false;
    }
}
