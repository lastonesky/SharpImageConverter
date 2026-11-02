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
/// - VP8 有损静态图关键帧（intra-only，含环内滤波与 fancy upsampling 上采样）；
/// - VP8L 无损静态图（可叠加 ALPH 独立 alpha 通道）；
/// - VP8X 动图（逐帧 ANMF 内 VP8/VP8L 解码，按帧标志做 alpha 混合与 dispose 语义，
///   输出每帧显示完成后的整幅画布快照）。
///
/// 设计目标：
/// - 与 libwebp 并存，独立命名空间，不影响现有互操作路径；
/// - 返回标准化 RGBA 像素数据（R,G,B,A 每像素 4 字节），便于接入统一图像抽象层；
/// - 提供 <see cref="DecodeRgbaInto(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>
///   零拷贝重载：直接把像素解到调用方的缓冲区，整幅图像只存在一份；
/// - 遇到当前不支持的特性（ALPH level-reduction）抛 <see cref="WebpUnsupportedException"/>，
///   由调用方回退到 libwebp；VP8 非关键帧同样抛该异常，但它不属于 WebP（RFC 9649 要求有损
///   WebP 为 VP8 帧内编码、ANMF 帧数据为关键帧），libwebp 也无法解码，回退不会成功。
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

        // 有损 VP8：仅支持关键帧（非关键帧由 Vp8Decoder 抛 WebpUnsupportedException）
        if (primary.Id == WebpChunkId.Vp8)
        {
            var rgba8 = DecodeVp8Chunk(primary.GetData(container.RawData), out width, out height);
            ApplyAlphChunk(container, rgba8, width, height);
            return rgba8;
        }

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

        if (primary.Id == WebpChunkId.Vp8)
        {
            var rgba8 = DecodeVp8Chunk(primary.GetData(container.RawData), out width, out height);
            ApplyAlphChunk(container, rgba8, width, height);
            int need = checked(width * height * 4);
            if (dst.Length < need)
                throw new ArgumentException($"输出缓冲区不足：需要 {need} 字节，实际 {dst.Length}", nameof(dst));
            rgba8.AsSpan(0, need).CopyTo(dst);
            return;
        }

        EnsureVp8L(primary);
        Vp8LDecoder.ReadSize(primary.GetData(container.RawData), out width, out height);

        int required = checked(width * height * 4);
        if (dst.Length < required)
            throw new ArgumentException($"输出缓冲区不足：需要 {required} 字节，实际 {dst.Length}", nameof(dst));

        DecodeStaticInto(container, primary, dst[..required], width, height);
    }

    /// <summary>静态图（非动图）的 VP8L 解码路径：直接解到 dst，再叠 ALPH。</summary>
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

    /// <summary>主图像 chunk 必须是 VP8L（VP8 已在调用方分流）。</summary>
    private static void EnsureVp8L(WebpChunk primary)
    {
        if (primary.Id != WebpChunkId.Vp8L)
            throw new WebpContainerException($"主图像 chunk 类型未处理: {primary.Id}");
    }

    private static WebpChunk RequirePrimaryChunk(WebpContainer container)
    {
        if (container.Kind == WebpContainerKind.Unknown)
            throw new WebpContainerException("无法识别的 WebP 文件类型");

        var primary = container.GetPrimaryImageChunk();
        if (!primary.HasValue)
            throw new WebpContainerException("主图像 chunk 未找到");
        return primary.Value;
    }

    /// <summary>解码 VP8 chunk 为 RGBA（仅关键帧；非关键帧抛 <see cref="WebpUnsupportedException"/>）。</summary>
    private static byte[] DecodeVp8Chunk(ReadOnlySpan<byte> data, out int width, out int height)
    {
        var img = Vp8Decoder.Decode(data);
        width = img.Width;
        height = img.Height;
        return Vp8YuvToRgb.ConvertToRgba(img.Y, img.U, img.V, img.Width, img.Height, img.StrideY, img.StrideUV);
    }

    /// <summary>若容器含 ALPH 独立 alpha 通道，用其覆盖每个像素的 A 字节。</summary>
    private static void ApplyAlphChunk(WebpContainer container, byte[] rgba, int width, int height)
    {
        var alph = container.FindChunk(WebpChunkId.Alph);
        if (!alph.HasValue) return;
        ApplyAlphaChunk(alph.Value.GetData(container.RawData), rgba, width, height);
    }

    /// <summary>用 ALPH chunk 数据覆盖 RGBA 缓冲区的 A 字节（静态图与 ANMF 子帧共用）。</summary>
    private static void ApplyAlphaChunk(ReadOnlySpan<byte> alphData, byte[] rgba, int width, int height)
    {
        var alpha = AlphDecoder.Decode(alphData, width, height);
        if (alpha.Length != width * height)
            throw new WebpContainerException($"ALPH 通道长度 {alpha.Length} 与图像尺寸 {width}x{height} 不符");
        for (int i = 0; i < alpha.Length; i++)
            rgba[i * 4 + 3] = alpha[i];
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

        var canvas = new byte[canvasWidth * canvasHeight * 4];
        // 上一帧绘制前（已应用上一帧 disposal）的画布；混合运算的 dst 就是它。
        var prevDisposed = new byte[canvas.Length];

        var frames = new List<WebpFrame>();
        var anmfs = container.FindAllChunks(WebpChunkId.Anmf);
        if (anmfs.Count == 0) throw new WebpContainerException("动图缺少 ANMF 帧");

        bool prevDispose = false;
        bool prevWasKeyFrame = false;
        int prevX = 0, prevY = 0, prevW = 0, prevH = 0;
        int frameNum = 0;

        foreach (var anmf in anmfs)
        {
            var header = WebpContainer.ParseAnmfHeader(container.RawData, anmf);
            if (header.Width <= 0 || header.Height <= 0)
                throw new WebpContainerException($"ANMF 帧尺寸无效：{header.Width}x{header.Height}");
            // WebP 规范（RFC 9649 §2.7.1.1）要求帧矩形完全位于画布内；
            // libwebp 的 demux（IsValidExtendedFormat）同样按此拒绝整份文件。
            if (header.X < 0 || header.Y < 0 || header.X + header.Width > canvasWidth || header.Y + header.Height > canvasHeight)
                throw new WebpContainerException($"ANMF 帧矩形 ({header.X},{header.Y},{header.Width},{header.Height}) 超出画布 {canvasWidth}x{canvasHeight}");

            frameNum++;

            var subs = container.GetAnmfSubChunks(anmf);
            WebpChunk? vp8l = null, vp8 = null, alph = null;
            foreach (var s in subs)
            {
                if (s.Id == WebpChunkId.Vp8L) vp8l = s;
                else if (s.Id == WebpChunkId.Vp8) vp8 = s;
                else if (s.Id == WebpChunkId.Alph) alph = s;
            }

            if (vp8l == null)
            {
                if (vp8 != null && !Vp8Decoder.IsKeyFrame(vp8.Value.GetData(container.RawData)))
                    throw new WebpUnsupportedException(
                        "ANMF 帧的 VP8 数据不是关键帧，不符合 WebP 规范（RFC 9649 要求帧数据为 VP8 关键帧；libwebp 同样拒绝）");
                if (vp8 == null)
                    throw new WebpContainerException("ANMF 帧缺少 VP8L/VP8 图像子 chunk");
            }

            // 该帧自身是否携带 alpha（决定 key-frame 判定，与 libwebp 的 WebPBitstreamFeatures.has_alpha 等价）。
            bool hasAlpha = alph.HasValue ||
                (vp8l != null && Vp8LHeaderHasAlpha(vp8l.Value.GetData(container.RawData)));

            // 与 libwebp anim_decode.c 的 IsKeyFrame 判定一致：关键帧意味着画布从全透明重新开始。
            bool isKeyFrame = frameNum == 1
                || (header.Width == canvasWidth && header.Height == canvasHeight && (!hasAlpha || header.NoBlend))
                || (prevDispose && ((prevW == canvasWidth && prevH == canvasHeight) || prevWasKeyFrame));

            if (isKeyFrame)
                Array.Clear(canvas, 0, canvas.Length);
            else
                Array.Copy(prevDisposed, canvas, canvas.Length);

            // VP8L 或 VP8 关键帧子帧
            byte[] frameRgba;
            int fw, fh;
            if (vp8l != null)
            {
                frameRgba = Vp8LDecoder.Decode(vp8l.Value.GetData(container.RawData), out fw, out fh);
            }
            else
            {
                frameRgba = DecodeVp8Chunk(vp8!.Value.GetData(container.RawData), out fw, out fh);
                if (alph.HasValue)
                    ApplyAlphaChunk(alph.Value.GetData(container.RawData), frameRgba, fw, fh);
            }
            if (fw != header.Width || fh != header.Height)
                throw new WebpContainerException($"ANMF 帧图像尺寸 {fw}x{fh} 与帧头 {header.Width}x{header.Height} 不符");

            BlitRect(canvas, canvasWidth, header.X, header.Y, fw, fh, frameRgba);

            // 混合规则（RFC 9649 §2.7.1.1）：帧标志 bit1 = 1 表示不混合；
            // 关键帧没有可混合的上一帧内容，libwebp 同样跳过混合。
            if (frameNum > 1 && !header.NoBlend && !isKeyFrame)
                BlendRect(canvas, prevDisposed, canvasWidth, header.X, header.Y, fw, fh,
                    prevDispose, prevX, prevY, prevW, prevH);

            frames.Add(new WebpFrame(canvas.ToArray(), header.X, header.Y, header.Width, header.Height, header.DurationMs));

            // 为下一帧准备输入画布：保留本帧结果，再按 dispose 语义清除本帧矩形。
            Array.Copy(canvas, prevDisposed, canvas.Length);
            if (header.DisposeToBackground)
                ClearRect(prevDisposed, canvasWidth, header.X, header.Y, header.Width, header.Height);

            prevDispose = header.DisposeToBackground;
            prevWasKeyFrame = isKeyFrame;
            prevX = header.X;
            prevY = header.Y;
            prevW = header.Width;
            prevH = header.Height;
        }

        return new WebpAnimation(canvasWidth, canvasHeight, container.LoopCount, frames.ToArray());
    }

    /// <summary>VP8L 图像头中的 alpha_is_used 位（5 字节头，bit 36 → byte[4] 的 bit 4）。</summary>
    private static bool Vp8LHeaderHasAlpha(ReadOnlySpan<byte> data)
        => data.Length >= 5 && (data[4] & 0x10) != 0;

    /// <summary>把帧图像按左上角 (x,y) 覆盖到画布上（行优先拷贝，行距不同时逐行复制）。</summary>
    private static void BlitRect(byte[] canvas, int canvasWidth, int x, int y, int w, int h, byte[] rgba)
    {
        for (int row = 0; row < h; row++)
        {
            int dst = ((y + row) * canvasWidth + x) * 4;
            Array.Copy(rgba, row * w * 4, canvas, dst, w * 4);
        }
    }

    /// <summary>
    /// 按 libwebp anim_decode.c 的 BlendPixelNonPremult 逐像素做 alpha 合成：
    /// 源 alpha 为 0 时保留画布原值，为 255 时直接采用源像素，其余用
    /// dst_factor_a = (dst_a * (256 - src_a)) >> 8、scale = 2^24 / (src_a + dst_factor_a)
    /// 的整数近似公式。
    /// 另需复刻 libwebp 的区域规则：若上一帧 dispose=BACKGROUND，则画布中上一帧矩形
    /// 已被清为透明，libwebp 只对当前帧矩形中“落在上一帧矩形之外”的部分做混合
    /// （FindBlendRangeAtRow）；对透明目标做近似混合会产生 ±1 偏差，不能整块混合。
    /// </summary>
    private static void BlendRect(byte[] canvas, byte[] prevDisposed, int canvasWidth,
        int x, int y, int w, int h, bool prevDisposedToBackground,
        int prevX, int prevY, int prevW, int prevH)
    {
        for (int row = 0; row < h; row++)
        {
            int canvasY = y + row;
            if (!prevDisposedToBackground)
            {
                BlendSpan(canvas, prevDisposed, canvasWidth, canvasY, x, w);
                continue;
            }

            // 与 libwebp 的 FindBlendRangeAtRow 等价：把本行拆成两段位于上一帧矩形之外的部分。
            int srcMaxX = x + w;
            int prevMaxX = prevX + prevW;
            int prevMaxY = prevY + prevH;
            if (canvasY < prevY || canvasY >= prevMaxY || x >= prevMaxX || srcMaxX <= prevX)
            {
                BlendSpan(canvas, prevDisposed, canvasWidth, canvasY, x, w);
            }
            else
            {
                if (x < prevX)
                    BlendSpan(canvas, prevDisposed, canvasWidth, canvasY, x, prevX - x);
                if (srcMaxX > prevMaxX)
                    BlendSpan(canvas, prevDisposed, canvasWidth, canvasY, prevMaxX, srcMaxX - prevMaxX);
            }
        }
    }

    /// <summary>对画布第 canvasY 行 [startX, startX+width) 的像素做 alpha 混合。</summary>
    private static void BlendSpan(byte[] canvas, byte[] prevDisposed, int canvasWidth,
        int canvasY, int startX, int width)
    {
        int index = (canvasY * canvasWidth + startX) * 4;
        for (int i = 0; i < width; i++, index += 4)
        {
            int srcA = canvas[index + 3];
            if (srcA == 255) continue;
            if (srcA == 0)
            {
                prevDisposed.AsSpan(index, 4).CopyTo(canvas.AsSpan(index, 4));
                continue;
            }

            int dstA = prevDisposed[index + 3];
            int dstFactorA = (dstA * (256 - srcA)) >> 8;
            int blendA = srcA + dstFactorA;
            uint scale = (1u << 24) / (uint)blendA;
            for (int c = 0; c < 3; c++)
            {
                uint unscaled = (uint)(canvas[index + c] * srcA + prevDisposed[index + c] * dstFactorA);
                canvas[index + c] = (byte)((unscaled * scale) >> 24);
            }
            canvas[index + 3] = (byte)blendA;
        }
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
