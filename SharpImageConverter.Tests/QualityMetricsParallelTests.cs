using SharpImageConverter.Compression;
using Xunit;

namespace SharpImageConverter.Tests;

/// <summary>
/// <see cref="QualityMetrics.Compare"/> 的按带分片实现必须与串行实现同解。
/// </summary>
/// <remarks>
/// 分片只改变浮点累加的合并顺序，误差量级在 1e-8 dB 以内，远小于 0.01 dB 的显示精度；
/// 这条断言是为了防止将来改动分片方式时悄悄把顺序搞错（例如按行分片导致 4×4 块跨片）。
/// </remarks>
public class QualityMetricsParallelTests
{
    private static (byte[] Reference, byte[] Test) MakePair(int width, int height)
    {
        var reference = new byte[width * height * 3];
        var test = new byte[reference.Length];
        int i = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                reference[i] = (byte)((x * 3 + y) & 0xFF);
                reference[i + 1] = (byte)((x + y * 5) & 0xFF);
                reference[i + 2] = (byte)((x * 7 ^ y) & 0xFF);
                // 加上与位置相关的扰动，让误差既非均匀也非零
                test[i] = (byte)Math.Clamp(reference[i] + ((x + y) % 7) - 3, 0, 255);
                test[i + 1] = (byte)Math.Clamp(reference[i + 1] + ((x * 2) % 5) - 2, 0, 255);
                test[i + 2] = (byte)Math.Clamp(reference[i + 2] + (y % 3) - 1, 0, 255);
                i += 3;
            }
        }
        return (reference, test);
    }

    [Theory]
    [InlineData(2048, 2048)]  // 4.19 MP：刚好过并行门限
    [InlineData(3001, 1999)]  // 宽高都不是 4 的倍数，末带与末列都不完整
    [InlineData(640, 480)]    // 0.31 MP：走串行
    public void ParallelMatchesSerial(int width, int height)
    {
        var (reference, test) = MakePair(width, height);

        // 传 Span 走串行实现，传数组走分片实现。
        var serial = QualityMetrics.Compare(reference.AsSpan(), test.AsSpan(), width, height, 3, blockSize: 4);
        var parallel = QualityMetrics.Compare(reference, test, width, height, 3, blockSize: 4);

        Assert.Equal(serial.Psnr, parallel.Psnr, 6);
        Assert.Equal(serial.PerceptualPsnr, parallel.PerceptualPsnr, 6);
        Assert.Equal(serial.MeanError, parallel.MeanError, 9);
    }
}
