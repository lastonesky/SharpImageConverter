using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// GIF 编码使用的量化/抖动方案。
/// </summary>
public enum GifQuantizerKind
{
    /// <summary>八叉树量化 + Bayer 有序抖动（默认，速度更快，输出与原实现不同）。</summary>
    OctreeBayer = 0,
    /// <summary>Wu 量化 + Floyd–Steinberg 误差扩散（原实现，输出逐字节稳定）。</summary>
    WuFloydSteinberg = 1,
}

/// <summary>
/// GIF 编码器，支持 RGB24 与 RGBA32 编码（含量化与 LZW 压缩）
/// </summary>
public class GifEncoder
{
    private readonly byte[] _headerBuf = new byte[1024];

    /// <summary>
    /// 是否开启 Floyd-Steinberg 抖动，默认开启。
    /// </summary>
    public bool EnableDithering { get; set; } = true;

    /// <summary>
    /// 量化/抖动方案。默认 <see cref="GifQuantizerKind.OctreeBayer"/>（八叉树 + Bayer 有序抖动，更快）；
    /// 设回 <see cref="GifQuantizerKind.WuFloydSteinberg"/> 可复用原 Wu + Floyd–Steinberg 实现。
    /// </summary>
    public GifQuantizerKind QuantizerKind { get; set; } = GifQuantizerKind.OctreeBayer;

    /// <summary>
    /// Bayer 有序抖动幅度（仅 <see cref="GifQuantizerKind.OctreeBayer"/> 生效），默认 8 = 一个量化步长。
    /// 调大会显著放大颗粒与缩放摩尔纹，详见 <see cref="OctreeQuantizer.DitherStrength"/>。
    /// </summary>
    public int DitherStrength { get; set; } = 8;

    /// <summary>
    /// 是否采集编码各阶段耗时并输出诊断日志，默认关闭。
    /// 关闭时不会调用 Stopwatch，热路径无额外开销。
    /// </summary>
    public bool EnableDiagnostics { get; set; }

    /// <summary>
    /// 诊断日志输出委托，为 null 时回落到 <see cref="Trace"/>。
    /// </summary>
    public Action<string>? DiagnosticsLog { get; set; }

    /// <summary>
    /// 最近一次编码的耗时统计，未开启诊断时为 null。
    /// </summary>
    public GifTiming? LastTiming { get; private set; }

    /// <summary>
    /// 取当前时间戳；未开启诊断时返回 0，避免无谓的计时开销。
    /// </summary>
    private static long Now(GifTiming? timing) => timing is null ? 0 : Stopwatch.GetTimestamp();

    private GifTiming? BeginTiming(int pixelCount, int frameCount)
    {
        return EnableDiagnostics ? new GifTiming(GifTimingKind.Encode, pixelCount, frameCount) : null;
    }

    private void FinishTiming(GifTiming? timing)
    {
        if (timing is null) return;
        LastTiming = timing;
        GifTiming.Emit(timing, DiagnosticsLog);
    }

    public void Encode(ImageFrame image, Stream stream)
    {
        var timing = BeginTiming(image.Width * image.Height, 1);
        long t0 = Now(timing);
        EncodeCore(image, stream, timing);
        long t1 = Now(timing);

        if (timing is not null)
        {
            timing.TotalTicks = t1 - t0;
            timing.Label = "rgb24";
        }
        FinishTiming(timing);
    }

    /// <summary>
    /// 单帧编码核心：量化 → 头与调色板 → LZW。各阶段耗时累加进 <paramref name="timing"/>。
    /// </summary>
    private void EncodeCore(ImageFrame image, Stream stream, GifTiming? timing)
    {
        long t0 = Now(timing);
        var (palette, indices) = QuantizePixels(image.Pixels, image.Width, image.Height);
        long t1 = Now(timing);

        int paletteCount = palette.Length / 3;
        int depth = GetColorDepth(paletteCount);
        int actualTableSize = 1 << (depth + 1);

        // Header & LSD
        int ptr = 0;
        WriteAscii(_headerBuf, ref ptr, "GIF89a");
        WriteShort(_headerBuf, ref ptr, image.Width);
        WriteShort(_headerBuf, ref ptr, image.Height);
        _headerBuf[ptr++] = (byte)(0x80 | (0x07 << 4) | depth); // GCT Flag, 8-bit res, size
        _headerBuf[ptr++] = 0; // BG index
        _headerBuf[ptr++] = 0; // Aspect
        stream.Write(_headerBuf, 0, ptr);

        // Global Color Table
        stream.Write(palette);
        if (actualTableSize * 3 > palette.Length)
        {
            byte[] padding = new byte[actualTableSize * 3 - palette.Length];
            stream.Write(padding);
        }

        // Image Descriptor
        ptr = 0;
        _headerBuf[ptr++] = 0x2C;
        WriteShort(_headerBuf, ref ptr, 0);
        WriteShort(_headerBuf, ref ptr, 0);
        WriteShort(_headerBuf, ref ptr, image.Width);
        WriteShort(_headerBuf, ref ptr, image.Height);
        _headerBuf[ptr++] = 0; // No local table
        stream.Write(_headerBuf, 0, ptr);
        long t2 = Now(timing);

        // LZW
        using var lzwEncoder = new LzwEncoder(stream);
        lzwEncoder.Encode(indices, image.Width, image.Height, Math.Max(2, depth + 1));
        long t3 = Now(timing);
        stream.WriteByte(0x3B); // Trailer
        long t4 = Now(timing);

        if (timing is not null)
        {
            timing.QuantizeTicks += t1 - t0;
            timing.HeaderTicks += (t2 - t1) + (t4 - t3);
            timing.LzwTicks += t3 - t2;
            timing.PaletteColors = paletteCount;
        }
    }

    public void EncodeRgba(int width, int height, byte[] rgba, Stream stream)
    {
        var timing = BeginTiming(width * height, 1);
        long t0 = Now(timing);

        bool hasTransparent = false;
        for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] < 128) { hasTransparent = true; break; } }
        long tScanEnd = Now(timing);

        if (!hasTransparent)
        {
            byte[] rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2];
            }
            long tPrepEnd = Now(timing);

            EncodeCore(new ImageFrame(width, height, rgb), stream, timing);
            long tEnd = Now(timing);

            if (timing is not null)
            {
                timing.PrepareTicks = tPrepEnd - t0;
                timing.TotalTicks = tEnd - t0;
                timing.Label = "rgba32(opaque)";
            }
            FinishTiming(timing);
            return;
        }

        long t1 = Now(timing);
        QuantizeRgbaWithTransparency(width, height, rgba, out var palette, out var indices, out int depth);
        long t2 = Now(timing);

        int ptr = 0;
        WriteAscii(_headerBuf, ref ptr, "GIF89a");
        WriteShort(_headerBuf, ref ptr, width);
        WriteShort(_headerBuf, ref ptr, height);
        _headerBuf[ptr++] = (byte)(0x80 | (0x07 << 4) | depth);
        _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);

        stream.Write(palette);

        // GCE for transparency
        ptr = 0;
        _headerBuf[ptr++] = 0x21; _headerBuf[ptr++] = 0xF9; _headerBuf[ptr++] = 4;
        _headerBuf[ptr++] = 0x01; // Transparent flag
        _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0; // Delay
        _headerBuf[ptr++] = 0; // Transparent index
        _headerBuf[ptr++] = 0; // Terminator

        // Image Descriptor
        _headerBuf[ptr++] = 0x2C;
        WriteShort(_headerBuf, ref ptr, 0); WriteShort(_headerBuf, ref ptr, 0);
        WriteShort(_headerBuf, ref ptr, width); WriteShort(_headerBuf, ref ptr, height);
        _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);
        long t3 = Now(timing);

        using var lzwEncoder = new LzwEncoder(stream);
        lzwEncoder.Encode(indices, width, height, Math.Max(2, depth + 1));
        long t4 = Now(timing);
        stream.WriteByte(0x3B);
        long t5 = Now(timing);

        if (timing is not null)
        {
            timing.PrepareTicks = tScanEnd - t0;
            timing.QuantizeTicks = t2 - t1;
            timing.HeaderTicks = (t3 - t2) + (t5 - t4);
            timing.LzwTicks = t4 - t3;
            timing.TotalTicks = t5 - t0;
            timing.PaletteColors = palette.Length / 3;
            timing.Label = "rgba32(alpha)";
        }
        FinishTiming(timing);
    }

    public void EncodeAnimation(IReadOnlyList<ImageFrame> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, Stream stream)
    {
        if (frames.Count == 0) return;
        int w = frames[0].Width, h = frames[0].Height;

        var timing = BeginTiming(w * h * frames.Count, frames.Count);
        long t0 = Now(timing);

        int ptr = 0;
        WriteAscii(_headerBuf, ref ptr, "GIF89a");
        WriteShort(_headerBuf, ref ptr, w); WriteShort(_headerBuf, ref ptr, h);
        _headerBuf[ptr++] = 0x70; // No GCT, 8-bit res
        _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);

        WriteNetscapeExtension(stream, loopCount);
        long tHeaderEnd = Now(timing);
        if (timing is not null) timing.HeaderTicks = tHeaderEnd - t0;

        using var lzwEncoder = new LzwEncoder(stream);

        for (int i = 0; i < frames.Count; i++)
        {
            long tq0 = Now(timing);
            var (pal, inds) = QuantizePixels(frames[i].Pixels, w, h);
            long tq1 = Now(timing);
            int depth = GetColorDepth(pal.Length / 3);

            ptr = 0;
            // GCE
            _headerBuf[ptr++] = 0x21; _headerBuf[ptr++] = 0xF9; _headerBuf[ptr++] = 4;
            _headerBuf[ptr++] = 0; // Disposal=0
            int delay = (frameDurationsMs[i] + 5) / 10;
            WriteShort(_headerBuf, ref ptr, Math.Clamp(delay, 0, 65535));
            _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0;

            // Image Descriptor
            _headerBuf[ptr++] = 0x2C;
            WriteShort(_headerBuf, ref ptr, 0); WriteShort(_headerBuf, ref ptr, 0);
            WriteShort(_headerBuf, ref ptr, w); WriteShort(_headerBuf, ref ptr, h);
            _headerBuf[ptr++] = (byte)(0x80 | depth);
            stream.Write(_headerBuf, 0, ptr);

            // Local Color Table
            stream.Write(pal);
            int pad = (1 << (depth + 1)) * 3 - pal.Length;
            if (pad > 0) stream.Write(new byte[pad]);
            long tq2 = Now(timing);

            lzwEncoder.Encode(inds, w, h, Math.Max(2, depth + 1));
            long tq3 = Now(timing);

            if (timing is not null)
            {
                timing.QuantizeTicks += tq1 - tq0;
                timing.HeaderTicks += tq2 - tq1;
                timing.LzwTicks += tq3 - tq2;
                timing.PaletteColors = Math.Max(timing.PaletteColors, pal.Length / 3);
            }
        }
        stream.WriteByte(0x3B);
        long t1 = Now(timing);

        if (timing is not null)
        {
            timing.TotalTicks = t1 - t0;
            timing.Label = "animation";
        }
        FinishTiming(timing);
    }

    public void EncodeAnimationRgba(int width, int height, IReadOnlyList<byte[]> rgbaFrames, IReadOnlyList<int> frameDurationsMs, int loopCount, Stream stream)
    {
        if (rgbaFrames.Count == 0) return;

        var timing = BeginTiming(width * height * rgbaFrames.Count, rgbaFrames.Count);
        long t0 = Now(timing);

        int ptr = 0;
        WriteAscii(_headerBuf, ref ptr, "GIF89a");
        WriteShort(_headerBuf, ref ptr, width); WriteShort(_headerBuf, ref ptr, height);
        _headerBuf[ptr++] = 0x70;
        _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);

        WriteNetscapeExtension(stream, loopCount);
        long tHeaderEnd = Now(timing);
        if (timing is not null) timing.HeaderTicks = tHeaderEnd - t0;

        using var lzwEncoder = new LzwEncoder(stream);

        for (int i = 0; i < rgbaFrames.Count; i++)
        {
            long tq0 = Now(timing);
            QuantizeRgbaWithTransparency(width, height, rgbaFrames[i], out var pal, out var inds, out int depth);
            long tq1 = Now(timing);

            ptr = 0;
            // GCE
            _headerBuf[ptr++] = 0x21; _headerBuf[ptr++] = 0xF9; _headerBuf[ptr++] = 4;
            _headerBuf[ptr++] = 0x09; // Disposal=2 (restore to BG), HasTrans=1
            int delay = (frameDurationsMs[i] + 5) / 10;
            WriteShort(_headerBuf, ref ptr, Math.Clamp(delay, 0, 65535));
            _headerBuf[ptr++] = 0; // TransIndex=0
            _headerBuf[ptr++] = 0;

            // Image Descriptor
            _headerBuf[ptr++] = 0x2C;
            WriteShort(_headerBuf, ref ptr, 0); WriteShort(_headerBuf, ref ptr, 0);
            WriteShort(_headerBuf, ref ptr, width); WriteShort(_headerBuf, ref ptr, height);
            _headerBuf[ptr++] = (byte)(0x80 | depth);
            stream.Write(_headerBuf, 0, ptr);

            stream.Write(pal);
            long tq2 = Now(timing);
            lzwEncoder.Encode(inds, width, height, Math.Max(2, depth + 1));
            long tq3 = Now(timing);

            if (timing is not null)
            {
                timing.QuantizeTicks += tq1 - tq0;
                timing.HeaderTicks += tq2 - tq1;
                timing.LzwTicks += tq3 - tq2;
                timing.PaletteColors = Math.Max(timing.PaletteColors, pal.Length / 3);
            }
        }
        stream.WriteByte(0x3B);
        long t1 = Now(timing);

        if (timing is not null)
        {
            timing.TotalTicks = t1 - t0;
            timing.Label = "animation(rgba)";
        }
        FinishTiming(timing);
    }

    /// <summary>
    /// 按 <see cref="QuantizerKind"/> 选择量化器，对 RGB24 像素生成调色板与索引。
    /// 新方案（八叉树 + Bayer）为默认；原 Wu + Floyd–Steinberg 保留作可切换项。
    /// </summary>
    private (byte[] Palette, byte[] Indices) QuantizePixels(ReadOnlySpan<byte> pixels, int width, int height)
    {
        return QuantizerKind == GifQuantizerKind.WuFloydSteinberg
            ? Quantizer.Quantize(pixels, width, height, EnableDithering)
            : OctreeQuantizer.Quantize(pixels, width, height, EnableDithering, DitherStrength);
    }

    private static int GetColorDepth(int count)
    {
        int depth = 0;
        while ((1 << (depth + 1)) < count) depth++;
        return Math.Min(depth, 7);
    }

    private void WriteNetscapeExtension(Stream stream, int loopCount)
    {
        int ptr = 0;
        _headerBuf[ptr++] = 0x21; _headerBuf[ptr++] = 0xFF; _headerBuf[ptr++] = 11;
        WriteAscii(_headerBuf, ref ptr, "NETSCAPE2.0");
        _headerBuf[ptr++] = 3; _headerBuf[ptr++] = 1;
        WriteShort(_headerBuf, ref ptr, Math.Clamp(loopCount, 0, 65535));
        _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);
    }

    private void WriteAscii(byte[] buf, ref int ptr, string s)
    {
        for (int i = 0; i < s.Length; i++) buf[ptr++] = (byte)s[i];
    }

    private void WriteShort(byte[] buf, ref int ptr, int v)
    {
        buf[ptr++] = (byte)(v & 0xFF);
        buf[ptr++] = (byte)((v >> 8) & 0xFF);
    }

    private void QuantizeRgbaWithTransparency(int width, int height, byte[] rgba, out byte[] palette, out byte[] indices, out int depth)
    {
        int pixelCount = width * height;
        using var opaqueOwner = NativeBufferOwner<byte>.Allocate(pixelCount * 3);
        var opaque = opaqueOwner.Span;
        using var maskOwner = NativeBufferOwner<byte>.Allocate(pixelCount);
        var mask = maskOwner.Span;
        int opPtr = 0;
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 4;
            if (rgba[s + 3] < 128) mask[i] = 0;
            else
            {
                mask[i] = 1;
                opaque[opPtr] = rgba[s]; opaque[opPtr + 1] = rgba[s + 1]; opaque[opPtr + 2] = rgba[s + 2];
                opPtr += 3;
            }
        }

        var (pal, indsOpaque) = QuantizePixels(opaque, width, height);
        int palCount = pal.Length / 3;
        depth = GetColorDepth(palCount + 1);
        int actualSize = 1 << (depth + 1);
        palette = new byte[actualSize * 3];
        Array.Copy(pal, 0, palette, 3, pal.Length);

        indices = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            indices[i] = mask[i] == 0 ? (byte)0 : (byte)(indsOpaque[i] + 1);
        }
    }
}
