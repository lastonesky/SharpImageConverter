using System;
using System.Globalization;
using System.IO;

namespace SharpImageConverter.Core;

/// <summary>
/// 整幅（非流式）解码的尺寸准入。
/// </summary>
/// <remarks>
/// <para>
/// 解码器返回整幅像素，因此像素缓冲必须能由单个数组表示：长度与索引都受 int 限制
/// （.NET 的 <see cref="Array.MaxLength"/> 约 21 亿字节）。这条上限不是业务阈值，
/// 而是「整幅解码」这种 API 形态的硬边界——超过它，任何 <c>new byte[...]</c> 与下标运算
/// 都会溢出，只能拒绝。
/// </para>
/// <para>
/// 需要更大的图像时走逐行流式路径（<c>StreamingConversion</c>，内存与图像尺寸无关）。
/// 可用环境变量 <c>SIC_MAX_DECODE_PIXELS</c> 调低（服务端限流），但无法调高于该硬边界。
/// </para>
/// </remarks>
internal static class DecodeLimits
{
    private const string EnvName = "SIC_MAX_DECODE_PIXELS";

    /// <summary>整幅解码的像素上限（按 4 字节/像素的数组表示能力推导）。</summary>
    internal static readonly long MaxPixels = ResolveMaxPixels();

    /// <summary>单个缓冲区的字节上限（留出对齐与 SIMD 填充的余量）。</summary>
    internal const long MaxBufferBytes = int.MaxValue - 1024L;

    private static long ResolveMaxPixels()
    {
        // 4 B/px 的可表示上限。留 1024 字节余量，避开 Array.MaxLength 与 SIMD 对齐填充的边界。
        long hardLimit = MaxBufferBytes / 4;
        string? env = Environment.GetEnvironmentVariable(EnvName);
        if (!string.IsNullOrEmpty(env)
            && long.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            && value > 0
            && value < hardLimit)
        {
            return value;
        }
        return hardLimit;
    }

    /// <summary>校验宽高为正且像素数不超过整幅解码上限。</summary>
    /// <param name="formatName">格式名（用于报错文案）</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="bytesPerPixel">本次解码的每像素输出字节数（3=RGB，4=RGBA）</param>
    /// <exception cref="InvalidDataException">尺寸非法或超出整幅解码上限</exception>
    internal static void ValidatePixelCount(string formatName, int width, int height, int bytesPerPixel = 4)
    {
        if (width <= 0)
        {
            throw new InvalidDataException($"{formatName} 宽度无效：{width}");
        }
        if (height <= 0)
        {
            throw new InvalidDataException($"{formatName} 高度无效：{height}");
        }

        // 输出缓冲 = 像素数 × 每像素字节数，两者都必须落在单个数组的 int 索引范围内。
        long maxPixels = Math.Min(MaxPixels, MaxBufferBytes / Math.Max(1, bytesPerPixel));
        long pixels = (long)width * height;
        if (pixels > maxPixels)
        {
            throw new InvalidDataException(
                $"{formatName} 图像尺寸 {width}×{height}（{pixels:N0} 像素）超出单幅解码上限 {maxPixels:N0} 像素"
                + "（输出缓冲会超出单个数组的 int 索引范围）");
        }
    }

    /// <summary>校验某个中间缓冲区的字节数能否用单个数组表示。</summary>
    /// <param name="formatName">格式名（用于报错文案）</param>
    /// <param name="bytes">字节数（调用方用 long 计算，避免先溢出）</param>
    /// <param name="what">缓冲区描述，如「解压后扫描线」</param>
    /// <exception cref="InvalidDataException">超出单幅解码上限</exception>
    internal static void ValidateBufferBytes(string formatName, long bytes, string what)
    {
        if (bytes < 0 || bytes > MaxBufferBytes)
        {
            throw new InvalidDataException(
                $"{formatName} {what} {bytes:N0} 字节超出单幅解码上限 {MaxBufferBytes:N0} 字节"
                + "（单个数组的 int 索引范围）");
        }
    }
}