using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Webp;

/// <summary>ANMF 帧结束后对画布的处理方式。</summary>
public enum WebpAnimationDispose
{
    /// <summary>保留该帧像素（dispose = none）。</summary>
    None = 0,

    /// <summary>把该帧矩形清除为本帧之前的背景（dispose = background）。</summary>
    Background = 1,
}

/// <summary>
/// 动图编码的一帧输入。RGBA 为整幅画布快照（CanvasWidth × CanvasHeight，非预乘 alpha）。
/// </summary>
/// <param name="Rgba">整幅画布快照，每像素 R,G,B,A 连续 4 字节。</param>
/// <param name="DurationMs">该帧显示时长（0..16777215 毫秒）。</param>
/// <param name="Blend">是否允许与上一帧混合；无法用混合精确表达目标像素时编码器会自动改写为不混合。</param>
/// <param name="Dispose">该帧结束后的画布处理方式。</param>
public sealed record WebpAnimationFrameInput(
    byte[] Rgba, int DurationMs, bool Blend = true, WebpAnimationDispose Dispose = WebpAnimationDispose.None);

/// <summary>动图编码参数。</summary>
/// <param name="Lossless">true 时每帧使用 VP8L 无损编码，false 时使用 VP8 有损编码（带 alpha 时写 ALPH）。</param>
/// <param name="Quality">有损编码质量（0..100），无损模式下忽略。</param>
public sealed record WebpAnimationEncodeOptions(bool Lossless = false, float Quality = 75f);

/// <summary>
/// 纯 C# 动图 WebP 编码器（VP8X + ANIM + 多帧 ANMF）。
///
/// 输入为整幅画布快照序列：编码器逐帧与解码端将呈现的画布状态做差，取最小变化矩形
/// （并按要求把左上角对齐到 2 像素），复用 <see cref="Vp8Encoder"/> / <see cref="Vp8LEncoder"/>
/// 编码该矩形；有损带 alpha 时用 <see cref="AlphEncoder"/> 写入 ANMF 内的 ALPH。
///
/// blend / dispose 与解码端严格互补：
/// - dispose = <see cref="WebpAnimationDispose.Background"/> 时，下一帧的“上一画布”会先清空该矩形；
/// - blend 只在能用规范混合公式精确得到目标像素时保留（目标 alpha = 255，或 alpha = 0 且
///   与上一画布像素完全相同）；其余情况改写为不混合，保证往返后每帧与输入快照一致。
/// </summary>
public static class WebpAnimationWriter
{
    /// <summary>VP8 帧宽高上限（14 位）；动图逐帧复用 VP8/VP8L 编码器。</summary>
    private const int MaxDimension = 16383;

    /// <summary>
    /// 编码动图为完整 WebP 字节。
    /// </summary>
    /// <param name="frames">帧序列（每帧为整幅画布 RGBA 快照）。</param>
    /// <param name="canvasWidth">画布宽度（1..16383）。</param>
    /// <param name="canvasHeight">画布高度（1..16383）。</param>
    /// <param name="loopCount">循环次数，0 表示无限循环（0..65535）。</param>
    /// <param name="options">编码参数；null 表示有损 q75。</param>
    /// <param name="metadata">需要透传的 ICCP / EXIF / XMP 原始字节；null 表示不写扩展 chunk。</param>
    public static byte[] Encode(IReadOnlyList<WebpAnimationFrameInput> frames, int canvasWidth, int canvasHeight,
        int loopCount, WebpAnimationEncodeOptions? options = null, ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) throw new ArgumentOutOfRangeException(nameof(frames), "动图至少需要一帧");
        if (canvasWidth < 1 || canvasWidth > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), $"画布宽度必须在 1..{MaxDimension} 之间");
        if (canvasHeight < 1 || canvasHeight > MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), $"画布高度必须在 1..{MaxDimension} 之间");

        var opt = options ?? new WebpAnimationEncodeOptions();
        if (opt.Quality < 0f || opt.Quality > 100f)
            throw new ArgumentOutOfRangeException(nameof(options), "质量必须在 0..100 之间");

        int pixelCount = checked(canvasWidth * canvasHeight);
        int canvasBytes = checked(pixelCount * 4);
        bool anyAlpha = false;

        // 先校验所有帧尺寸并计算容器级 alpha 标志。
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Rgba is null)
                throw new ArgumentException($"第 {i} 帧像素为 null", nameof(frames));
            if (frame.Rgba.Length != canvasBytes)
                throw new ArgumentException($"第 {i} 帧像素长度与画布 {canvasWidth}x{canvasHeight} 不匹配", nameof(frames));
            if (!anyAlpha)
            {
                for (int p = 0; p < pixelCount; p++)
                {
                    if (frame.Rgba[p * 4 + 3] != 255)
                    {
                        anyAlpha = true;
                        break;
                    }
                }
            }
        }

        var chunks = new List<WebpOutputChunk>();
        byte flags = (byte)(Vp8XFlags.Animation | (anyAlpha ? Vp8XFlags.Alpha : 0));
        chunks.Add(new WebpOutputChunk(WebpChunkId.Vp8X,
            WebpChunkWriter.BuildVp8X(flags, canvasWidth, canvasHeight)));
        chunks.Add(new WebpOutputChunk(WebpChunkId.Anim, WebpChunkWriter.BuildAnim(loopCount)));

        // 解码端在该帧绘制之前的画布状态（初始全透明）。编码器与解码端的差集/清理顺序必须一致。
        var state = new byte[canvasBytes];
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            chunks.Add(new WebpOutputChunk(WebpChunkId.Anmf,
                EncodeFrame(frame, state, canvasWidth, canvasHeight, opt)));
        }

        byte[] assembled = WebpChunkWriter.Assemble(chunks);
        return WebpMetadataWriter.Apply(assembled, canvasWidth, canvasHeight, metadata);
    }

    /// <summary>
    /// 编码动图并写入流。
    /// </summary>
    public static void EncodeToStream(Stream stream, IReadOnlyList<WebpAnimationFrameInput> frames,
        int canvasWidth, int canvasHeight, int loopCount, WebpAnimationEncodeOptions? options = null,
        ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = Encode(frames, canvasWidth, canvasHeight, loopCount, options, metadata);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>编码单帧为 ANMF chunk 数据，并推进调用方的画布状态。</summary>
    private static byte[] EncodeFrame(WebpAnimationFrameInput frame, byte[] state,
        int canvasWidth, int canvasHeight, WebpAnimationEncodeOptions options)
    {
        int minX = canvasWidth, minY = canvasHeight, maxX = -1, maxY = -1;
        for (int y = 0; y < canvasHeight; y++)
        {
            int rowBase = y * canvasWidth;
            for (int x = 0; x < canvasWidth; x++)
            {
                int o = (rowBase + x) * 4;
                if (frame.Rgba[o] == state[o] && frame.Rgba[o + 1] == state[o + 1]
                    && frame.Rgba[o + 2] == state[o + 2] && frame.Rgba[o + 3] == state[o + 3])
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        // 没有任何像素变化时用 1×1 的透明/混合帧占位（ANMF 宽高必须 ≥1）。
        bool unchanged = maxX < 0;
        if (unchanged)
        {
            minX = minY = maxX = maxY = 0;
        }

        // ANMF 的 X / Y 以 2 像素为单位存储，左上角必须向下对齐到偶数。
        minX &= ~1;
        minY &= ~1;
        int rectWidth = maxX + 1 - minX;
        int rectHeight = maxY + 1 - minY;

        // blend 是否能精确表达目标像素：alpha=255 直接覆盖；alpha=0 时混合结果是上一画布，
        // 因此要求目标像素与上一画布逐字节相同。其余（半透明）一律改写为不混合。
        bool noBlend = !frame.Blend || !CanBlendExactly(frame.Rgba, state, canvasWidth,
            minX, minY, rectWidth, rectHeight);

        var rectRgba = new byte[checked(rectWidth * rectHeight * 4)];
        for (int row = 0; row < rectHeight; row++)
        {
            int src = ((minY + row) * canvasWidth + minX) * 4;
            Array.Copy(frame.Rgba, src, rectRgba, row * rectWidth * 4, rectWidth * 4);
        }

        var subChunks = new List<byte>(64);
        if (options.Lossless)
        {
            byte[] vp8l = Vp8LEncoder.EncodeVp8L(rectRgba, rectWidth, rectHeight, Vp8LEncoderOptions.Auto);
            WebpChunkWriter.AppendChunk(subChunks, WebpChunkId.Vp8L, vp8l);
        }
        else
        {
            bool rectHasAlpha = HasTransparency(rectRgba, rectWidth * rectHeight);
            if (rectHasAlpha)
            {
                var alpha = new byte[rectWidth * rectHeight];
                for (int p = 0; p < alpha.Length; p++) alpha[p] = rectRgba[p * 4 + 3];
                byte[] alph = AlphEncoder.Encode(alpha, rectWidth, rectHeight);
                WebpChunkWriter.AppendChunk(subChunks, WebpChunkId.Alph, alph);
            }
            byte[] vp8 = Vp8Encoder.Encode(rectRgba, rectWidth, rectHeight, options.Quality);
            WebpChunkWriter.AppendChunk(subChunks, WebpChunkId.Vp8, vp8);
        }

        var anmf = new byte[16 + subChunks.Count];
        WebpChunkWriter.BuildAnmfHeader(minX, minY, rectWidth, rectHeight, frame.DurationMs,
            frame.Dispose == WebpAnimationDispose.Background, noBlend).CopyTo(anmf, 0);
        subChunks.CopyTo(anmf, 16);

        // 推进状态：先变成该帧的显示结果，再按 dispose 语义清除本帧矩形（与解码端一致）。
        Array.Copy(frame.Rgba, state, state.Length);
        if (frame.Dispose == WebpAnimationDispose.Background)
            ClearRect(state, canvasWidth, minX, minY, rectWidth, rectHeight);

        return anmf;
    }

    /// <summary>矩形内所有像素是否都能用规范混合公式精确还原为目标值。</summary>
    private static bool CanBlendExactly(byte[] target, byte[] state, int canvasWidth,
        int x, int y, int w, int h)
    {
        for (int row = 0; row < h; row++)
        {
            int baseIndex = ((y + row) * canvasWidth + x) * 4;
            for (int col = 0; col < w; col++)
            {
                int o = baseIndex + col * 4;
                int alpha = target[o + 3];
                if (alpha == 255) continue;
                if (alpha == 0
                    && target[o] == state[o] && target[o + 1] == state[o + 1]
                    && target[o + 2] == state[o + 2] && target[o + 3] == state[o + 3])
                    continue;
                return false;
            }
        }
        return true;
    }

    private static bool HasTransparency(byte[] rgba, int pixelCount)
    {
        for (int i = 0; i < pixelCount; i++)
            if (rgba[i * 4 + 3] != 255) return true;
        return false;
    }

    private static void ClearRect(byte[] canvas, int canvasWidth, int x, int y, int w, int h)
    {
        for (int row = 0; row < h; row++)
            Array.Clear(canvas, ((y + row) * canvasWidth + x) * 4, w * 4);
    }
}
