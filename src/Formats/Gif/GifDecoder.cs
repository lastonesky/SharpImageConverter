using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif
{
    public sealed class GifAnimation(IReadOnlyList<Image<Rgb24>> frames, IReadOnlyList<int> frameDurationsMs, int loopCount)
    {
        public IReadOnlyList<Image<Rgb24>> Frames { get; } = frames;
        public IReadOnlyList<int> FrameDurationsMs { get; } = frameDurationsMs;
        public int LoopCount { get; } = loopCount;
    }

    /// <summary>
    /// GIF 解码器，支持多帧与单帧解码。
    /// </summary>
    public class GifDecoder : IImageDecoder
    {
        private static readonly int[] InterlaceStart = { 0, 4, 2, 1 };
        private static readonly int[] InterlaceInc = { 8, 8, 4, 2 };

        private enum DecodeMode { Rgb24, Rgba32, Animation }

        /// <summary>
        /// 是否采集解码各阶段耗时并输出诊断日志，默认关闭。
        /// 关闭时不会调用 Stopwatch，热路径无额外开销。
        /// </summary>
        public bool EnableDiagnostics { get; set; }

        /// <summary>
        /// 诊断日志输出委托，为 null 时回落到 <see cref="Trace"/>。
        /// </summary>
        public Action<string>? DiagnosticsLog { get; set; }

        /// <summary>
        /// 最近一次解码的耗时统计，未开启诊断时为 null。
        /// </summary>
        public GifTiming? LastTiming { get; private set; }

        /// <summary>
        /// 取当前时间戳；未开启诊断时返回 0，避免无谓的计时开销。
        /// </summary>
        private static long Now(GifTiming? timing) => timing is null ? 0 : Stopwatch.GetTimestamp();

        public GifAnimation DecodeAnimationRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            return DecodeAnimationRgb24(fs);
        }

        public GifAnimation DecodeAnimationRgb24(Stream stream)
        {
            var context = new DecodeContext(stream, DecodeMode.Animation);
            ExecuteDecode(context);
            return new GifAnimation(context.RgbFrames!, context.Durations!, context.LoopCount);
        }

        public Image<Rgb24> DecodeRgb24(string path)
        {
            using var fs = File.OpenRead(path);
            return DecodeRgb24(fs);
        }

        public Image<Rgb24> DecodeRgb24(Stream stream)
        {
            var context = new DecodeContext(stream, DecodeMode.Rgb24);
            ExecuteDecode(context);
            return context.RgbFrames![0];
        }

        public Image<Rgba32> DecodeRgba32(string path)
        {
            using var fs = File.OpenRead(path);
            return DecodeRgba32(fs);
        }

        public Image<Rgba32> DecodeRgba32(Stream stream)
        {
            var context = new DecodeContext(stream, DecodeMode.Rgba32);
            ExecuteDecode(context);
            return context.RgbaFrame!;
        }

        // Keep compatibility with existing API if needed
        public List<Image<Rgb24>> DecodeAllFrames(Stream stream) => [.. DecodeAnimationRgb24(stream).Frames];
        public List<Image<Rgb24>> DecodeAllFrames(string path) => [.. DecodeAnimationRgb24(path).Frames];

        private class DecodeContext(Stream stream, DecodeMode mode)
        {
            public Stream Stream = stream;
            public DecodeMode Mode = mode;
            public List<Image<Rgb24>>? RgbFrames;
            public List<int>? Durations;
            public Image<Rgba32>? RgbaFrame;
            public int LoopCount = 1;
        }

        private void ExecuteDecode(DecodeContext ctx)
        {
            NativeBufferOwner<byte>? backBuffer = null;
            var timing = EnableDiagnostics ? new GifTiming(GifTimingKind.Decode) : null;
            long t0 = Now(timing);
            try
            {
                ExecuteDecodeCore(ctx, ref backBuffer, timing);
            }
            finally
            {
                backBuffer?.Dispose();
            }
            long t1 = Now(timing);

            if (timing is not null)
            {
                int frames = Math.Max(1, ctx.RgbFrames?.Count ?? 1);
                timing.TotalTicks = t1 - t0;
                timing.FrameCount = frames;
                timing.PixelCount *= frames;
                timing.Label = ctx.Mode switch
                {
                    DecodeMode.Rgb24 => "rgb24",
                    DecodeMode.Rgba32 => "rgba32",
                    _ => "animation",
                };
                LastTiming = timing;
                GifTiming.Emit(timing, DiagnosticsLog);
            }
        }

        private void ExecuteDecodeCore(DecodeContext ctx, ref NativeBufferOwner<byte>? backBuffer, GifTiming? timing)
        {
            long th0 = Now(timing);
            var stream = ctx.Stream;
            byte[] sig = new byte[6];
            ReadExact(stream, sig, 0, 6);
            if (sig[0] != 'G' || sig[1] != 'I' || sig[2] != 'F') throw new InvalidDataException("Not a GIF file");

            byte[] lsd = new byte[7];
            ReadExact(stream, lsd, 0, 7);
            int width = lsd[0] | (lsd[1] << 8);
            int height = lsd[2] | (lsd[3] << 8);
            byte packed = lsd[4];
            byte bgIndex = lsd[5];

            bool hasGct = (packed & 0x80) != 0;
            int gctColors = hasGct ? 1 << ((packed & 0x07) + 1) : 0;
            byte[] gct = new byte[768];
            if (hasGct) ReadExact(stream, gct, 0, gctColors * 3);

            int pixelCount = width * height;
            int components = ctx.Mode == DecodeMode.Rgba32 ? 4 : 3;
            byte[] canvas = new byte[pixelCount * components];

            if (timing is not null)
            {
                timing.PixelCount = pixelCount;
                timing.HeaderTicks = Now(timing) - th0;
            }

            // 背景色填充推迟到第一帧解出索引之后：若首帧整幅覆盖且每个索引都落在调色板内、
            // 又没有透明索引，则每个像素都会被渲染覆盖，这次整画布填充是死写，可以整体跳过。
            bool hasBgColor = hasGct && bgIndex < gctColors;
            byte bgR = hasBgColor ? gct[bgIndex * 3] : (byte)0;
            byte bgG = hasBgColor ? gct[bgIndex * 3 + 1] : (byte)0;
            byte bgB = hasBgColor ? gct[bgIndex * 3 + 2] : (byte)0;
            bool canvasReady = false;

            if (ctx.Mode != DecodeMode.Rgba32)
            {
                ctx.RgbFrames = new List<Image<Rgb24>>();
                ctx.Durations = new List<int>();
            }

            int transIndex = -1;
            int disposal = 0;
            int delayCs = 0;
            byte[] desc = new byte[9];
            byte[] lct = new byte[768];
            var pool = ArrayPool<byte>.Shared;
            using var lzwDecoder = new LzwDecoder(stream);

            while (true)
            {
                int blockType = stream.ReadByte();
                if (blockType == -1 || blockType == 0x3B) break;

                if (blockType == 0x21) // Extension
                {
                    int label = stream.ReadByte();
                    if (label == 0xF9) // GCE
                    {
                        stream.ReadByte(); // size (4)
                        byte[] gce = new byte[4];
                        ReadExact(stream, gce, 0, 4);
                        disposal = (gce[0] >> 2) & 0x07;
                        bool hasTrans = (gce[0] & 1) != 0;
                        delayCs = gce[1] | (gce[2] << 8);
                        transIndex = hasTrans ? gce[3] : -1;
                        stream.ReadByte(); // terminator
                    }
                    else if (label == 0xFF) // App Extension
                    {
                        int size = stream.ReadByte();
                        if (size == 11)
                        {
                            byte[] app = new byte[11];
                            ReadExact(stream, app, 0, 11);
                            if (System.Text.Encoding.ASCII.GetString(app) == "NETSCAPE2.0")
                            {
                                int subLen = stream.ReadByte();
                                if (subLen == 3)
                                {
                                    stream.ReadByte(); // 1
                                    int rep = stream.ReadByte() | (stream.ReadByte() << 8);
                                    ctx.LoopCount = rep == 0 ? 0 : rep;
                                }
                            }
                        }
                        SkipBlocks(stream);
                    }
                    else SkipBlocks(stream);
                }
                else if (blockType == 0x2C) // Image
                {
                    ReadExact(stream, desc, 0, 9);
                    int ix = desc[0] | (desc[1] << 8), iy = desc[2] | (desc[3] << 8);
                    int iw = desc[4] | (desc[5] << 8), ih = desc[6] | (desc[7] << 8);
                    byte imgPacked = desc[8];
                    bool hasLct = (imgPacked & 0x80) != 0;
                    bool interlace = (imgPacked & 0x40) != 0;
                    int lctColors = hasLct ? 1 << ((imgPacked & 0x07) + 1) : 0;
                    if (hasLct) ReadExact(stream, lct, 0, lctColors * 3);

                    byte[] palette = hasLct ? lct : gct;
                    int palCount = hasLct ? lctColors : gctColors;

                    int lzwMin = stream.ReadByte();
                    byte[] indices = pool.Rent(iw * ih);
                    long tl0 = Now(timing);
                    lzwDecoder.Decode(indices.AsSpan(0, iw * ih), iw, ih, lzwMin);
                    long tl1 = Now(timing);

                    long tb0 = Now(timing);
                    bool opaqueFullCover = !interlace && transIndex < 0 &&
                        ix == 0 && iy == 0 && iw == width && ih == height &&
                        AllIndicesInRange(indices, iw * ih, palCount);

                    if (!canvasReady)
                    {
                        if (hasBgColor && !opaqueFullCover)
                        {
                            FillBackground(canvas, components, bgR, bgG, bgB);
                        }
                        canvasReady = true;
                    }
                    long tb1 = Now(timing);

                    if (disposal == 3)
                    {
                        backBuffer ??= NativeBufferOwner<byte>.Allocate(canvas.Length);
                        canvas.AsSpan().CopyTo(backBuffer.Span);
                    }
                    long tb2 = Now(timing);

                    RenderFrame(canvas, indices, width, height, ix, iy, iw, ih, interlace, transIndex, palette, palCount, ctx.Mode == DecodeMode.Rgba32, opaqueFullCover);
                    long tr1 = Now(timing);
                    pool.Return(indices);

                    if (timing is not null)
                    {
                        timing.LzwTicks += tl1 - tl0;
                        timing.BackgroundTicks += (tb1 - tb0) + (tb2 - tb1);
                        timing.RenderTicks += tr1 - tb2;
                    }

                    if (ctx.Mode == DecodeMode.Rgba32)
                    {
                        ctx.RgbaFrame = new Image<Rgba32>(width, height, canvas);
                        return;
                    }

                    if (ctx.Mode == DecodeMode.Rgb24)
                    {
                        ctx.RgbFrames!.Add(new Image<Rgb24>(width, height, canvas));
                        ctx.Durations!.Add(delayCs * 10 < 10 ? 10 : delayCs * 10);
                        return;
                    }

                    ctx.RgbFrames!.Add(new Image<Rgb24>(width, height, (byte[])canvas.Clone()));
                    ctx.Durations!.Add(delayCs * 10 < 10 ? 10 : delayCs * 10);

                    // Post-processing disposal
                    long td0 = Now(timing);
                    if (disposal == 2) // Restore to background
                    {
                        FillRect(canvas, width, height, ix, iy, iw, ih, ctx.Mode == DecodeMode.Rgba32, hasGct ? gct[bgIndex*3] : (byte)0, hasGct ? gct[bgIndex*3+1] : (byte)0, hasGct ? gct[bgIndex*3+2] : (byte)0);
                    }
                    else if (disposal == 3 && backBuffer != null) // Restore to previous
                    {
                        backBuffer.Span.CopyTo(canvas.AsSpan());
                    }
                    long td1 = Now(timing);
                    if (timing is not null) timing.BackgroundTicks += td1 - td0;

                    disposal = 0; transIndex = -1; delayCs = 0;
                    if (ctx.Mode == DecodeMode.Rgb24) return;
                }
            }
            long tf0 = Now(timing);
            if (!canvasReady && hasBgColor)
            {
                FillBackground(canvas, components, bgR, bgG, bgB);
            }
            long tf1 = Now(timing);
            if (timing is not null) timing.BackgroundTicks += tf1 - tf0;

            if (ctx.RgbFrames?.Count == 0) ctx.RgbFrames.Add(new Image<Rgb24>(width, height, canvas));
        }

        private void RenderFrame(byte[] canvas, byte[] indices, int w, int h, int ix, int iy, int iw, int ih, bool interlace, int trans, byte[] pal, int palColors, bool rgba, bool opaqueFullCover)
        {
            int comp = rgba ? 4 : 3;
            int stride = w * comp;

            // 整幅覆盖 + 无透明 + 索引全部落在调色板内时，没有任何边界判断和散写，
            // 走连续展开的专用路径（含 AVX2 gather 版本）。
            if (opaqueFullCover)
            {
                RenderFullCanvasOpaque(canvas, indices, pal, palColors, w * h, rgba);
                return;
            }

            if (interlace)
            {
                int idxPtr = 0;
                for (int pass = 0; pass < 4; pass++)
                {
                    for (int y = InterlaceStart[pass]; y < ih; y += InterlaceInc[pass])
                    {
                        int dy = iy + y;
                        if (dy < h)
                        {
                            int rowOff = dy * stride;
                            for (int x = 0; x < iw; x++)
                            {
                                int dx = ix + x;
                                if (dx < w)
                                {
                                    byte idx = indices[idxPtr + x];
                                    int dOff = rowOff + dx * comp;
                                    if (idx != trans && idx < palColors)
                                    {
                                        int pOff = idx * 3;
                                        canvas[dOff] = pal[pOff];
                                        canvas[dOff+1] = pal[pOff+1];
                                        canvas[dOff+2] = pal[pOff+2];
                                        if (rgba) canvas[dOff+3] = 255;
                                    }
                                    else if (rgba && idx == trans)
                                    {
                                        canvas[dOff+3] = 0;
                                    }
                                }
                            }
                        }
                        idxPtr += iw;
                    }
                }
            }
            else
            {
                for (int y = 0; y < ih; y++)
                {
                    int dy = iy + y;
                    if (dy >= h) continue;
                    int rowOff = dy * stride;
                    int iOff = y * iw;
                    for (int x = 0; x < iw; x++)
                    {
                        int dx = ix + x;
                        if (dx < w)
                        {
                            byte idx = indices[iOff + x];
                            int dOff = rowOff + dx * comp;
                            if (idx != trans && idx < palColors)
                            {
                                int pOff = idx * 3;
                                canvas[dOff] = pal[pOff];
                                canvas[dOff+1] = pal[pOff+1];
                                canvas[dOff+2] = pal[pOff+2];
                                if (rgba) canvas[dOff+3] = 255;
                            }
                            else if (rgba && idx == trans)
                            {
                                canvas[dOff+3] = 0;
                            }
                        }
                    }
                }
            }
        }

        private void FillRect(byte[] canvas, int w, int h, int ix, int iy, int iw, int ih, bool rgba, byte r, byte g, byte b)
        {
            int comp = rgba ? 4 : 3;
            int stride = w * comp;
            for (int y = 0; y < ih; y++)
            {
                int dy = iy + y;
                if (dy >= h) continue;
                int rowOff = dy * stride;
                for (int x = 0; x < iw; x++)
                {
                    int dx = ix + x;
                    if (dx < w)
                    {
                        int dOff = rowOff + dx * comp;
                        canvas[dOff] = r; canvas[dOff+1] = g; canvas[dOff+2] = b;
                        if (rgba) canvas[dOff+3] = 255;
                    }
                }
            }
        }

        /// <summary>
        /// 用单一背景色填充整块画布：先构造 16 像素的模式块，再以指数式自我拷贝扩散到整块缓冲。
        /// 相比逐像素写 3/4 字节，拷贝走的是向量化 memcpy，只受内存带宽限制。
        /// </summary>
        private static void FillBackground(byte[] canvas, int components, byte r, byte g, byte b)
        {
            int patternLength = 16 * components;
            if (canvas.Length <= 0) return;

            if (canvas.Length < patternLength * 2)
            {
                for (int i = 0; i + components <= canvas.Length; i += components)
                {
                    canvas[i] = r; canvas[i + 1] = g; canvas[i + 2] = b;
                    if (components == 4) canvas[i + 3] = 255;
                }
                return;
            }

            Span<byte> head = canvas.AsSpan(0, patternLength);
            for (int i = 0; i < patternLength; i += components)
            {
                head[i] = r; head[i + 1] = g; head[i + 2] = b;
                if (components == 4) head[i + 3] = 255;
            }

            int filled = patternLength;
            while (filled < canvas.Length)
            {
                int copyLength = Math.Min(filled, canvas.Length - filled);
                canvas.AsSpan(0, copyLength).CopyTo(canvas.AsSpan(filled, copyLength));
                filled += copyLength;
            }
        }

        /// <summary>
        /// 检查索引缓冲的全部取值是否都小于调色板颜色数（因此渲染时每个像素都会被写入）。
        /// </summary>
        private static bool AllIndicesInRange(byte[] indices, int count, int palColors)
        {
            if (palColors >= 256) return true;
            if (count <= 0) return true;

            ref byte src = ref MemoryMarshal.GetReference(indices.AsSpan());
            int i = 0;
            byte max = 0;

            if (Vector.IsHardwareAccelerated)
            {
                int width = Vector<byte>.Count;
                Vector<byte> vmax = Vector<byte>.Zero;
                for (; i + width <= count; i += width)
                {
                    vmax = Vector.Max(vmax, Vector.LoadUnsafe(ref src, (nuint)i));
                }
                for (int lane = 0; lane < width; lane++)
                {
                    byte v = vmax[lane];
                    if (v > max) max = v;
                }
            }

            for (; i < count; i++)
            {
                byte v = indices[i];
                if (v > max) max = v;
            }
            return max < palColors;
        }

        /// <summary>
        /// 整画布、无透明索引的调色板展开：输出地址完全连续，无边界判断。
        /// AVX2 路径用 gather 一次取 8 个调色板颜色，再压成 24 字节（RGB24）/ 直接 32 字节（RGBA32）。
        /// </summary>
        private static unsafe void RenderFullCanvasOpaque(byte[] canvas, byte[] indices, byte[] pal, int palColors, int pixelCount, bool rgba)
        {
            if (pixelCount <= 0) return;

            // 调色板展开成 32 位色（低 3 字节为 RGB，RGBA 时第 4 字节为不透明 alpha）
            int* palPtr = stackalloc int[256];
            int count = Math.Min(256, palColors);
            int alphaBits = rgba ? 255 << 24 : 0;
            for (int i = 0; i < count; i++)
            {
                palPtr[i] = pal[i * 3] | (pal[i * 3 + 1] << 8) | (pal[i * 3 + 2] << 16) | alphaBits;
            }
            for (int i = count; i < 256; i++)
            {
                palPtr[i] = 0;
            }

            fixed (byte* pCanvas = canvas)
            fixed (byte* pIndices = indices)
            {
                byte* dst = pCanvas;
                byte* srcIdx = pIndices;
                int i = 0;

                if (rgba)
                {
                    if (Avx2.IsSupported)
                    {
                        for (; i + 8 <= pixelCount; i += 8)
                        {
                            Vector256<int> colors = Avx2.GatherVector256(palPtr, Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref *srcIdx, (nuint)i)), 4);
                            Unsafe.WriteUnaligned(dst + (nuint)i * 4, colors);
                        }
                    }
                    for (; i < pixelCount; i++)
                    {
                        Unsafe.WriteUnaligned(dst + (nuint)i * 4, palPtr[srcIdx[i]]);
                    }
                    return;
                }

                if (Avx2.IsSupported)
                {
                    // 8 像素一组：gather 出 8 个 32 位色后，每个 128 位通道压成 12 字节（共 24 字节）。
                    // 先写 0..15（其中 12..15 是低通道的填充，会被下一次写入覆盖），再把高通道写到 12..27。
                    for (; i + 10 <= pixelCount; i += 8)
                    {
                        Vector256<int> colors = Avx2.GatherVector256(palPtr, Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref *srcIdx, (nuint)i)), 4);
                        Vector256<byte> packed = Avx2.Shuffle(colors.AsByte(), RgbaToRgbShuffle);
                        Unsafe.WriteUnaligned(dst + (nuint)i * 3, packed.GetLower());
                        Unsafe.WriteUnaligned(dst + (nuint)i * 3 + 12, packed.GetUpper());
                    }
                }

                // 标量收尾：每像素一次 32 位写并按 3 字节步进，多出的第 4 字节由下一像素覆盖
                for (; i < pixelCount - 1; i++)
                {
                    Unsafe.WriteUnaligned(dst + (nuint)i * 3, palPtr[srcIdx[i]]);
                }
                if (i < pixelCount)
                {
                    int color = palPtr[srcIdx[i]];
                    byte* tail = dst + (nuint)i * 3;
                    tail[0] = (byte)color;
                    tail[1] = (byte)(color >> 8);
                    tail[2] = (byte)(color >> 16);
                }
            }
        }

        // RGBA(4 像素 = 16 字节) -> RGB(4 像素 = 12 字节)，与 Core/SimdHelper 中的同名掩码一致；
        // 256 位版本是两个通道各用一份相同掩码（vpshufb 不跨 128 位通道）。
        private static readonly Vector256<byte> RgbaToRgbShuffle = Vector256.Create(
            Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80),
            Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80));

        private void ReadExact(Stream s, byte[] buf, int off, int len)
        {
            int total = 0;
            while (total < len)
            {
                int n = s.Read(buf, off + total, len - total);
                if (n <= 0) throw new EndOfStreamException();
                total += n;
            }
        }

        private void SkipBlocks(Stream s)
        {
            while (true)
            {
                int len = s.ReadByte();
                if (len <= 0) break;
                if (s.CanSeek) s.Seek(len, SeekOrigin.Current);
                else for (int i = 0; i < len; i++) s.ReadByte();
            }
        }
    }
}
