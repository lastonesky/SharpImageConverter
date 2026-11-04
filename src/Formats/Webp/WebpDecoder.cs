using System;
using System.Collections.Generic;
using System.IO;

namespace SharpImageConverter.Formats.Webp;

/// <summary>动图的一帧：Rgba 是该帧显示完成后的整幅画布快照（CanvasWidth × CanvasHeight, R,G,B,A）。</summary>
public sealed record WebpFrame(byte[] Rgba, int X, int Y, int Width, int Height, int DurationMs);

/// <summary>解码后的动图：画布尺寸、循环次数（0 = 无限）与逐帧画布快照。</summary>
public sealed record WebpAnimation(int CanvasWidth, int CanvasHeight, int LoopCount, WebpFrame[] Frames);

/// <summary>
/// 动图容器信息：画布尺寸、帧数与循环次数（0 = 无限）。
/// 仅解析 VP8X/ANIM/ANMF 头，不解码任何帧像素，因此可用于在解码前预分配或做准入判断。
/// </summary>
public sealed record WebpAnimationInfo(int CanvasWidth, int CanvasHeight, int FrameCount, int LoopCount);

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
/// - 纯托管实现，不依赖任何 native 库；解码结果与 libwebp 逐像素一致（对照在测试工程）；
/// - 返回标准化 RGBA 像素数据（R,G,B,A 每像素 4 字节），便于接入统一图像抽象层；
/// - 提供 <see cref="DecodeRgbaInto(ReadOnlySpan{byte}, Span{byte}, out int, out int)"/>
///   零拷贝重载：直接把像素解到调用方的缓冲区，整幅图像只存在一份；
/// - 遇到无法解码的输入时抛 <see cref="WebpUnsupportedException"/>，兜底清单：
///   VP8 非关键帧（RFC 9649 规定有损 WebP 只有帧内编码，任何实现都解不出来）、
///   VP8 关键帧被标记 <c>show_frame=0</c>、ALPH 的保留压缩方式（非 0/1）、
///   VP8L <c>version≠0</c>、动图画布超出 16383，以及只在单图/流式入口出现的动图输入。
///   这些都是**格式或入口层面不受支持**，没有可回退的实现；调用方应把它当成
///   「输入不可解码」处理，而不是重试其它后端。
///
/// 不支持清单已随实现推进多次收缩（ALPH 的 <c>preprocessing</c> 0/1、ALPH 四种预测滤波、
/// VP8 4×4 帧内预测都已支持），修改此处时请一并核对 <c>WebpUnsupportedException</c> 的
/// 实际抛出点，不要凭印象写。
/// </summary>
public static class WebpDecoder
{
    // Bound RGBA allocations before multiplication/allocation. 64 MP caps one image at 256 MiB.
    private const long MaxDecodedPixels = 64_000_000;
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

    /// <summary>
    /// 读取动图容器信息（不解码像素）。
    /// </summary>
    /// <exception cref="WebpContainerException">文件不是动图、缺少 ANMF 或帧头损坏。</exception>
    public static WebpAnimationInfo ReadAnimationInfo(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return ReadAnimationInfo(fs);
    }

    /// <summary>
    /// 读取动图容器信息（不解码像素）。
    /// </summary>
    public static WebpAnimationInfo ReadAnimationInfo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadAnimationInfoFromContainer(WebpContainer.Load(stream));
    }

    /// <summary>
    /// 读取动图容器信息（不解码像素）。
    /// </summary>
    public static WebpAnimationInfo ReadAnimationInfo(ReadOnlySpan<byte> data)
        => ReadAnimationInfoFromContainer(WebpContainer.Load(data));

    /// <summary>
    /// 读取动图容器信息（不解码像素）。
    /// </summary>
    public static WebpAnimationInfo ReadAnimationInfo(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ReadAnimationInfoFromContainer(WebpContainer.Load(data));
    }

    /// <summary>
    /// 逐帧惰性解码动图，返回完整的画布快照序列。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DecodeAnimation(string)"/> 不同，本方法不会一次性保留全部帧：
    /// 枚举器每次只合成一帧并返回该帧快照，之后仅保留两份画布状态（当前帧合成结果与
    /// 上一帧 disposal 后的状态），峰值内存为 O(画布) 而不是 O(帧数 × 画布)，
    /// 适合长动图或网络服务逐帧转码。<br/>
    /// 返回的 <see cref="WebpFrame.Rgba"/> 由调用方持有；调用方宣布帧处理完毕后即可释放，
    /// 枚举器不会复用该数组。序列可以多次枚举（每次重新解码），不同枚举之间的状态互不共享。
    /// </remarks>
    /// <exception cref="WebpContainerException">文件不是动图、缺少 ANMF 或帧头损坏。</exception>
    /// <exception cref="WebpUnsupportedException">帧数据使用当前实现不支持的特性。</exception>
    public static IEnumerable<WebpFrame> EnumerateFrames(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return EnumerateFrames(fs);
    }

    /// <summary>
    /// 逐帧惰性解码动图（流会在返回前读完，之后可立即释放）。
    /// </summary>
    public static IEnumerable<WebpFrame> EnumerateFrames(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CreateLazyFrames(WebpContainer.Load(stream));
    }

    /// <summary>
    /// 逐帧惰性解码动图（不复制输入数组，容器在枚举期间引用该数组，调用方不得修改）。
    /// </summary>
    public static IEnumerable<WebpFrame> EnumerateFrames(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return CreateLazyFrames(WebpContainer.Load(data));
    }

    private static IEnumerable<WebpFrame> CreateLazyFrames(WebpContainer container)
    {
        // 在返回序列前完成整份文件的容器/帧头校验：调用方拿到序列时文件已确认可用。
        var info = ReadAnimationInfoFromContainer(container);
        return EnumerateFramesCore(container, info);
    }

    private static byte[] DecodeRgbaFromContainer(WebpContainer container, out int width, out int height)
    {
        // 动图：逐帧解码并只保留最后一帧画布，避免为整段动图驻留全部快照
        if (container.IsAnimation)
        {
            var info = ReadAnimationInfoFromContainer(container);
            WebpFrame? last = null;
            foreach (var frame in EnumerateFramesCore(container, info))
                last = frame;
            if (last is null)
                throw new WebpContainerException("动图缺少可解码的帧");
            width = info.CanvasWidth;
            height = info.CanvasHeight;
            return last.Rgba;
        }

        var primary = RequirePrimaryChunk(container);

        // 有损 VP8：仅支持关键帧（非关键帧由 Vp8Decoder 抛 WebpUnsupportedException）
        if (primary.Id == WebpChunkId.Vp8)
        {
            var rgba8 = DecodeVp8Chunk(primary.GetData(container.RawData), out width, out height);
            ValidateStaticCanvas(container, width, height);
            ValidatePixelCount(width, height);
            ApplyAlphChunk(container, rgba8, width, height);
            return rgba8;
        }

        EnsureVp8L(primary);
        Vp8LDecoder.ReadSize(primary.GetData(container.RawData), out width, out height);
        ValidateStaticCanvas(container, width, height);
        ValidatePixelCount(width, height);

        var rgba = new byte[checked(width * height * 4)];
        DecodeStaticInto(container, primary, rgba, width, height);
        return rgba;
    }

    private static void DecodeRgbaIntoContainer(WebpContainer container, Span<byte> dst, out int width, out int height)
    {
        if (container.IsAnimation)
        {
            // 与 DecodeRgba 共用惰性路径：只保留最后一帧快照，避免全部帧驻留
            var info = ReadAnimationInfoFromContainer(container);
            WebpFrame? last = null;
            foreach (var frame in EnumerateFramesCore(container, info))
                last = frame;
            if (last is null)
                throw new WebpContainerException("动图缺少可解码的帧");
            width = info.CanvasWidth;
            height = info.CanvasHeight;
            int need = checked(width * height * 4);
            if (dst.Length < need)
                throw new ArgumentException($"输出缓冲区不足：需要 {need} 字节，实际 {dst.Length}", nameof(dst));
            last.Rgba.AsSpan(0, need).CopyTo(dst);
            return;
        }

        var primary = RequirePrimaryChunk(container);

        if (primary.Id == WebpChunkId.Vp8)
        {
            var rgba8 = DecodeVp8Chunk(primary.GetData(container.RawData), out width, out height);
            ValidateStaticCanvas(container, width, height);
            ValidatePixelCount(width, height);
            ApplyAlphChunk(container, rgba8, width, height);
            int need = checked(width * height * 4);
            if (dst.Length < need)
                throw new ArgumentException($"输出缓冲区不足：需要 {need} 字节，实际 {dst.Length}", nameof(dst));
            rgba8.AsSpan(0, need).CopyTo(dst);
            return;
        }

        EnsureVp8L(primary);
        Vp8LDecoder.ReadSize(primary.GetData(container.RawData), out width, out height);
        ValidateStaticCanvas(container, width, height);
        ValidatePixelCount(width, height);

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

    private static void ValidateStaticCanvas(WebpContainer container, int width, int height)
    {
        if (container.CanvasWidth > 0 && container.CanvasHeight > 0
            && (container.CanvasWidth != width || container.CanvasHeight != height))
            throw new WebpContainerException(
                $"VP8X 画布尺寸 {container.CanvasWidth}x{container.CanvasHeight} 与静态图像尺寸 {width}x{height} 不符");
    }

    internal static void ValidatePixelCount(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxDecodedPixels)
            throw new WebpUnsupportedException(
                $"图像尺寸 {width}x{height} 超出解码像素上限 {MaxDecodedPixels:N0}");
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

    /// <summary>校验动图容器头并统计帧数（只读 VP8X/ANIM/ANMF 头，不解码帧像素）。</summary>
    private static WebpAnimationInfo ReadAnimationInfoFromContainer(WebpContainer container)
    {
        if (!container.IsAnimation)
            throw new WebpContainerException("该文件不是动图（缺少 VP8X Animation 标志）");

        int canvasWidth = container.CanvasWidth;
        int canvasHeight = container.CanvasHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
            throw new WebpContainerException($"动图画布尺寸无效：{canvasWidth}x{canvasHeight}");
        if (canvasWidth > 16383 || canvasHeight > 16383)
            throw new WebpUnsupportedException("动图画布尺寸超出实现上限 16383");
        ValidatePixelCount(canvasWidth, canvasHeight);

        int frameCount = container.FindAllChunks(WebpChunkId.Anmf).Count;
        if (frameCount == 0)
            throw new WebpContainerException("动图缺少 ANMF 帧");

        return new WebpAnimationInfo(canvasWidth, canvasHeight, frameCount, container.LoopCount);
    }

    private static WebpAnimation DecodeAnimationFromContainer(WebpContainer container)
    {
        var info = ReadAnimationInfoFromContainer(container);
        var frames = new List<WebpFrame>(info.FrameCount);
        foreach (var frame in EnumerateFramesCore(container, info))
            frames.Add(frame);

        return new WebpAnimation(info.CanvasWidth, info.CanvasHeight, info.LoopCount, frames.ToArray());
    }

    /// <summary>
    /// 动图逐帧合成核心（惰性）：每次 MoveNext 只解码当前 ANMF 子帧并合成到画布，
    /// 返回快照后立即推进到下一帧状态。枚举器自身只持有两份画布（当前帧与上一帧 disposal 后）。
    /// </summary>
    private static IEnumerable<WebpFrame> EnumerateFramesCore(WebpContainer container, WebpAnimationInfo info)
    {
        int canvasWidth = info.CanvasWidth;
        int canvasHeight = info.CanvasHeight;

        var canvas = new byte[canvasWidth * canvasHeight * 4];
        // 上一帧绘制前（已应用上一帧 disposal）的画布；混合运算的 dst 就是它。
        var prevDisposed = new byte[canvas.Length];

        var anmfs = container.FindAllChunks(WebpChunkId.Anmf);

        bool prevDispose = false;
        bool prevWasKeyFrame = false;
        int prevX = 0, prevY = 0, prevW = 0, prevH = 0;
        int frameNum = 0;

        foreach (var anmf in anmfs)
        {
            var header = WebpContainer.ParseAnmfHeader(container.RawData, anmf);
            if (header.Width <= 0 || header.Height <= 0)
                throw new WebpContainerException($"ANMF 帧尺寸无效：{header.Width}x{header.Height}");
            ValidatePixelCount(header.Width, header.Height);
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

            // 快照所有权交给调用方；枚举器继续复用 canvas 合成下一帧。
            yield return new WebpFrame(canvas.ToArray(), header.X, header.Y, header.Width, header.Height, header.DurationMs);

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
