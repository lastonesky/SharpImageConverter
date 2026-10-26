using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>
/// optimize 搜索期的「采样后 YCbCr 样本」缓存：把 produce 阶段的产物按 MCU 存成 <c>sbyte</c>。
/// </summary>
/// <remarks>
/// 动机：produce（RGB→YCbCr + 4:2:0 下采样 + 电平平移）只读源图和几何，**与质量参数无关**，
/// 而 optimize 搜索要在同一张图上评估 5~6 个质量档 —— 143 MP 实测 produce 单体 186 ms，
/// 占单候选的 56%，重复 6 次纯属白算。
///
/// 存 <c>sbyte</c> 而不是 <c>int</c>：样本在 produce 处已做电平平移，取值域正好是 [-128, 127]
/// （<see cref="JpegEncoder.FillMcu420RgbToYCbCr"/> 里 <c>yyVal - 128</c>），一个字节刚好装下。
/// 143 MP 的 4:2:0 是 55.9 万 MCU × 384 B ≈ 215 MB；存 int 会变成 859 MB，且回放时的写带宽翻四倍。
///
/// 缓存按 MCU 光栅下标定位、与写入顺序无关，因此采集阶段可以按行并行；回放时各线程只读自己
/// 负责的 MCU 区间，也不需要任何同步。
/// </remarks>
internal sealed class JpegSampleCache : IDisposable
{
    private byte[]? data;
    private int width;
    private int height;
    private JpegChromaSubsampling subsampling;
    private int mcuX;
    private int mcuY;
    private int mcuSizeX;
    private int mcuSizeY;
    private int blockCount;
    private bool filled;

    public int McuX => mcuX;

    public int McuY => mcuY;

    /// <summary>每个 MCU 的块数：4:2:0 为 6（4Y+2C），4:2:2 为 4（2Y+2C），4:4:4 为 3。</summary>
    public int BlockCount => blockCount;

    /// <summary>缓存是否已经按这组几何采样过。</summary>
    public bool Matches(int w, int h, bool use420)
        => Matches(w, h, use420 ? JpegChromaSubsampling.Yuv420 : JpegChromaSubsampling.Yuv444);

    public bool Matches(int w, int h, JpegChromaSubsampling useSubsampling)
        => filled && width == w && height == h && subsampling == useSubsampling;

    /// <summary>
    /// 采样整幅图并写入缓存。几何变化时重新分配；否则覆盖写。
    /// </summary>
    public void Capture(byte[] rgb24, int w, int h, bool use420)
        => Capture(rgb24, w, h, use420 ? JpegChromaSubsampling.Yuv420 : JpegChromaSubsampling.Yuv444);

    public void Capture(byte[] rgb24, int w, int h, JpegChromaSubsampling useSubsampling)
    {
        if (data is null || width != w || height != h || subsampling != useSubsampling)
        {
            filled = false;
            width = w;
            height = h;
            subsampling = useSubsampling;
            mcuSizeX = JpegEncoder.SamplingH(useSubsampling) * 8;
            mcuSizeY = JpegEncoder.SamplingV(useSubsampling) * 8;
            mcuX = (w + mcuSizeX - 1) / mcuSizeX;
            mcuY = (h + mcuSizeY - 1) / mcuSizeY;
            blockCount = JpegEncoder.BlockCountOf(useSubsampling);
            // 每个字节都会被下面覆写，不必清零（215 MB 的清零是 10 ms 量级的白付）。
            data = GC.AllocateUninitializedArray<byte>(checked(mcuX * mcuY * blockCount * 64));
        }

        byte[] target = data;
        Parallel.For(0, mcuY, my => CaptureRow(rgb24, target, my));
        filled = true;
    }

    /// <summary>按下标取某个 MCU 的样本（顺序与编码器的块顺序一致）。</summary>
    public ReadOnlySpan<sbyte> GetMcu(int sequence)
    {
        int stride = blockCount * 64;
        return MemoryMarshal.Cast<byte, sbyte>(data.AsSpan(sequence * stride, stride));
    }

    private void CaptureRow(byte[] rgb24, byte[] target, int my)
    {
        Span<int> samples = stackalloc int[6 * 64];
        int stride = blockCount * 64;
        for (int mx = 0; mx < mcuX; mx++)
        {
            // 与 ProduceRgbSamples 共用同一个分派入口（含边界复制），保证样本逐位相同。
            JpegEncoder.FillRgbMcuSamples(
                rgb24, width, height, subsampling,
                mx * mcuSizeX, my * mcuSizeY,
                samples[..stride], null);

            Span<byte> dst = target.AsSpan((my * mcuX + mx) * stride, stride);
            for (int k = 0; k < stride; k++)
            {
                dst[k] = (byte)(sbyte)samples[k];
            }
        }
    }

    public void Dispose()
    {
        data = null;
        filled = false;
    }
}
