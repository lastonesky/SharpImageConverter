using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>动图的一帧：Rgba 是该帧显示完成后的整幅画布快照（CanvasWidth × CanvasHeight, R,G,B,A）。</summary>
public sealed record WebpFrame(byte[] Rgba, int X, int Y, int Width, int Height, int DurationMs);

/// <summary>解码后的动图：画布尺寸、循环次数（0 = 无限）与逐帧画布快照。</summary>
public sealed record WebpAnimation(int CanvasWidth, int CanvasHeight, int LoopCount, WebpFrame[] Frames);

/// <summary>
/// 纯 C# WebP 解码器入口。
///
/// 支持范围：
/// - VP8L 无损静态图（可叠加 ALPH 独立 alpha 通道）；
/// - VP8X 动图（逐帧 ANMF 内 VP8L 解码，输出每帧的整幅画布快照）。
///
/// 设计目标：
/// - 与 libwebp 并存，独立命名空间，不影响现有互操作路径；
/// - 返回标准化 RGBA 像素数据（R,G,B,A 每像素 4 字节），便于接入统一图像抽象层；
/// - 提供 <see cref="DecodeRgbaInto(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>
///   零拷贝重载：直接把像素解到调用方的缓冲区，整幅图像只存在一份；
/// - 遇到当前不支持的特性（有损 VP8、动图的 VP8 帧）抛 <see cref="WebpUnsupportedException"/>，
///   由调用方回退到 libwebp。
/// </summary>
public static class ManagedWebpDecoder
{
    /// <summary>
    /// 解码 WebP 文件到 RGBA 像素缓冲区（动图返回最后一帧的整幅画布）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="width">输出宽度。</param>
    /// <param name="height">输出高度。</param>
    /// <returns>像素数据（每像素 R,G,B,A 4 字节，连续排列）。</returns>
    public static byte[] DecodeRgba(string path, out int width, out int height)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return DecodeRgba(fs, out width, out height);
    }

    /// <summary>
    /// 解码 WebP 流到 RGBA 像素缓冲区。
    /// </summary>
    public static byte[] DecodeRgba(Stream stream, out int width, out int height)
    {
        var container = WebpContainer.Load(stream);
        return DecodeRgbaFromContainer(container, out width, out height);
    }

    /// <summary>
    /// 从原始字节数组直接解码。
    /// </summary>
    public static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height)
    {
        var container = WebpContainer.Load(data);
        return DecodeRgbaFromContainer(container, out width, out height);
    }

    /// <summary>
    /// 从完整字节数组解码（不复制输入文件，减少一次整文件分配）。
    /// </summary>
    public static byte[] DecodeRgba(byte[] data, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        var container = WebpContainer.Load(data);
        return DecodeRgbaFromContainer(container, out width, out height);
    }

    /// <summary>
    /// 从完整字节数组解码到调用方缓冲区（静态图零整幅拷贝）。
    /// </summary>
    public static void DecodeRgbaInto(byte[] data, Span<byte> dst, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        var container = WebpContainer.Load(data);
        DecodeRgbaIntoContainer(container, dst, out width, out height);
    }

    /// <summary>
    /// 解码 WebP 到调用方提供的 RGBA 缓冲区（静态图零整幅拷贝）。
    /// </summary>
    /// <remarks>
    /// 动图会解码全部帧后把最后一帧画布复制到 <paramref name="dst"/>。
    /// </remarks>
    /// <param name="data">完整 WebP 文件字节。</param>
    /// <param name="dst">输出缓冲区，长度至少 width*height*4。</param>
    /// <param name="width">输出宽度。</param>
    /// <param name="height">输出高度。</param>
    public static void DecodeRgbaInto(ReadOnlySpan<byte> data, Span<byte> dst, out int width, out int height)
    {
        var container = WebpContainer.Load(data);
        DecodeRgbaIntoContainer(container, dst, out width, out height);
    }

    /// <summary>
    /// 解码 WebP 流到调用方提供的 RGBA 缓冲区。
    /// </summary>
    public static void DecodeRgbaInto(Stream stream, Span<byte> dst, out int width, out int height)
    {
        var container = WebpContainer.Load(stream);
        DecodeRgbaIntoContainer(container, dst, out width, out height);
    }

    /// <summary>
    /// 解码动图文件。
    /// </summary>
    public static WebpAnimation DecodeAnimation(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return DecodeAnimation(fs);
    }

    /// <summary>
    /// 解码动图流。
    /// </summary>
    public static WebpAnimation DecodeAnimation(Stream stream)
    {
        var container = WebpContainer.Load(stream);
        return DecodeAnimationFromContainer(container);
    }

    /// <summary>
    /// 从原始字节数组解码动图。
    /// </summary>
    public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data)
    {
        var container = WebpContainer.Load(data);
        return DecodeAnimationFromContainer(container);
    }

    private static byte[] DecodeRgbaFromContainer(WebpContainer container, out int width, out int height)
    {
        // 动图：复用动图解码流程，返回最后一帧的整幅画布快照
        if (container.IsAnimation)
        {
            var animation = DecodeAnimationFromContainer(container);
            if (animation.Frames.Length == 0)
                throw new WebpContainerException("动图缺少可解码的帧");
            var last = animation.Frames[animation.Frames.Length - 1];
            width = animation.CanvasWidth;
            height = animation.CanvasHeight;
            return last.Rgba;
        }

        var primary = RequirePrimaryChunk(container);
        EnsureVp8L(primary);
        Vp8LDecoder.ReadSize(primary.GetData(container.RawData), out width, out height);

        var rgba = new byte[checked(width * height * 4)];
        DecodeStaticInto(container, primary, rgba, width, height);
        return rgba;
    }

    private static void DecodeRgbaIntoContainer(WebpContainer container, Span<byte> dst, out int width, out int height)
    {
        if (container.IsAnimation)
        {
            var animation = DecodeAnimationFromContainer(container);
            if (animation.Frames.Length == 0)
                throw new WebpContainerException("动图缺少可解码的帧");
            var last = animation.Frames[animation.Frames.Length - 1];
            width = animation.CanvasWidth;
            height = animation.CanvasHeight;
            int need = checked(width * height * 4);
            if (dst.Length < need)
                throw new ArgumentException($"输出缓冲区不足：需要 {need} 字节，实际 {dst.Length}", nameof(dst));
            last.Rgba.AsSpan().CopyTo(dst);
            return;
        }

        var primary = RequirePrimaryChunk(container);
        EnsureVp8L(primary);
        Vp8LDecoder.ReadSize(primary.GetData(container.RawData), out width, out height);

        int required = checked(width * height * 4);
        if (dst.Length < required)
            throw new ArgumentException($"输出缓冲区不足：需要 {required} 字节，实际 {dst.Length}", nameof(dst));

        DecodeStaticInto(container, primary, dst[..required], width, height);
    }

    /// <summary>静态图（非动图）的主解码路径：VP8L 直接解到 dst，再叠 ALPH。</summary>
    private static void DecodeStaticInto(WebpContainer container, WebpChunk primary, Span<byte> dst,
                                         int width, int height)
    {
        EnsureVp8L(primary);

        Vp8LDecoder.DecodeInto(primary.GetData(container.RawData), dst, out int decodedWidth, out int decodedHeight);
        if (decodedWidth != width || decodedHeight != height)
            throw new WebpContainerException($"VP8L 帧尺寸 {decodedWidth}x{decodedHeight} 与容器声明 {width}x{height} 不符");

        // 若存在 ALPH 独立 alpha 通道，用其覆盖每个像素的 A 字节
        var alph = container.FindChunk(WebpChunkId.Alph);
        if (alph.HasValue)
        {
            var alpha = AlphDecoder.Decode(alph.Value.GetData(container.RawData), width, height);
            if (alpha.Length != width * height)
                throw new WebpContainerException($"ALPH 通道长度 {alpha.Length} 与图像尺寸 {width}x{height} 不符");
            for (int i = 0; i < alpha.Length; i++)
                dst[i * 4 + 3] = alpha[i];
        }
    }

    /// <summary>主图像 chunk 必须是 VP8L；有损 VP8 走 libwebp。</summary>
    private static void EnsureVp8L(WebpChunk primary)
    {
        if (primary.Id == WebpChunkId.Vp8)
            throw new WebpUnsupportedException("有损 VP8 格式暂不支持（下一批实现）");
        if (primary.Id != WebpChunkId.Vp8L)
            throw new WebpContainerException($"主图像 chunk 类型未处理: {primary.Id}");
    }

    private static WebpChunk RequirePrimaryChunk(WebpContainer container)
    {
        if (container.Kind == WebpContainerKind.Unknown)
            throw new WebpContainerException("无法识别的 WebP 文件类型");
        if (container.Kind == WebpContainerKind.Vp8)
            throw new WebpUnsupportedException("有损 VP8 格式暂不支持（下一批实现）");

        var primary = container.GetPrimaryImageChunk();
        if (!primary.HasValue)
            throw new WebpContainerException("主图像 chunk 未找到");
        return primary.Value;
    }

    private static WebpAnimation DecodeAnimationFromContainer(WebpContainer container)
    {
        if (!container.IsAnimation)
            throw new WebpContainerException("该文件不是动图（缺少 VP8X Animation 标志）");

        int canvasWidth = container.CanvasWidth;
        int canvasHeight = container.CanvasHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
            throw new WebpContainerException($"动图画布尺寸无效：{canvasWidth}x{canvasHeight}");
        if (canvasWidth > 16383 || canvasHeight > 16383)
            throw new WebpUnsupportedException("动图画布尺寸超出实现上限 16383");

        var canvas = new byte[canvasWidth * canvasHeight * 4];   // 全透明

        var frames = new List<WebpFrame>();
        var anmfs = container.FindAllChunks(WebpChunkId.Anmf);
        if (anmfs.Count == 0) throw new WebpContainerException("动图缺少 ANMF 帧");

        bool prevDispose = false;
        int prevX = 0, prevY = 0, prevW = 0, prevH = 0;

        foreach (var anmf in anmfs)
        {
            var header = WebpContainer.ParseAnmfHeader(container.RawData, anmf);
            if (header.Width <= 0 || header.Height <= 0)
                throw new WebpContainerException($"ANMF 帧尺寸无效：{header.Width}x{header.Height}");
            if (header.X < 0 || header.Y < 0 || header.X + header.Width > canvasWidth || header.Y + header.Height > canvasHeight)
                throw new WebpContainerException($"ANMF 帧矩形 ({header.X},{header.Y},{header.Width},{header.Height}) 超出画布 {canvasWidth}x{canvasHeight}");

            // 上一帧 dispose=BACKGROUND：先把上一帧矩形清为透明
            if (prevDispose)
                ClearRect(canvas, canvasWidth, prevX, prevY, prevW, prevH);

            var subs = container.GetAnmfSubChunks(anmf);
            WebpChunk? vp8l = null, vp8 = null;
            foreach (var s in subs)
            {
                if (s.Id == WebpChunkId.Vp8L) vp8l = s;
                else if (s.Id == WebpChunkId.Vp8) vp8 = s;
            }

            if (vp8l == null)
            {
                if (vp8 != null) throw new WebpUnsupportedException("动图帧使用有损 VP8 编码，当前未实现");
                throw new WebpContainerException("ANMF 帧缺少 VP8L/VP8 图像子 chunk");
            }

            var frameRgba = Vp8LDecoder.Decode(vp8l.Value.GetData(container.RawData), out int fw, out int fh);
            if (fw != header.Width || fh != header.Height)
                throw new WebpContainerException($"ANMF 帧图像尺寸 {fw}x{fh} 与帧头 {header.Width}x{header.Height} 不符");

            // 无视混合（no-blend 与否均直接覆盖）
            for (int y = 0; y < fh; y++)
            {
                int dst = ((header.Y + y) * canvasWidth + header.X) * 4;
                Array.Copy(frameRgba, y * fw * 4, canvas, dst, fw * 4);
            }

            frames.Add(new WebpFrame(canvas.ToArray(), header.X, header.Y, header.Width, header.Height, header.DurationMs));

            prevDispose = header.DisposeToBackground;
            prevX = header.X;
            prevY = header.Y;
            prevW = header.Width;
            prevH = header.Height;
        }

        return new WebpAnimation(canvasWidth, canvasHeight, container.LoopCount, frames.ToArray());
    }

    /// <summary>把画布上指定矩形区域清为全透明（A=0, RGB=0）。</summary>
    private static void ClearRect(byte[] canvas, int canvasWidth, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        for (int row = 0; row < h; row++)
        {
            int start = ((y + row) * canvasWidth + x) * 4;
            Array.Clear(canvas, start, w * 4);
        }
    }
}
