using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif;

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

    public void Encode(ImageFrame image, Stream stream)
    {
        var (palette, indices) = Quantizer.Quantize(image.Pixels, image.Width, image.Height, EnableDithering);
        
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

        // LZW
        new LzwEncoder(stream).Encode(indices, image.Width, image.Height, Math.Max(2, depth + 1));
        stream.WriteByte(0x3B); // Trailer
    }

    public void EncodeRgba(int width, int height, byte[] rgba, Stream stream)
    {
        bool hasTransparent = false;
        for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] < 128) { hasTransparent = true; break; } }
        
        if (!hasTransparent)
        {
            byte[] rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j] = rgba[i]; rgb[j + 1] = rgba[i + 1]; rgb[j + 2] = rgba[i + 2];
            }
            Encode(new ImageFrame(width, height, rgb), stream);
            return;
        }

        QuantizeRgbaWithTransparency(width, height, rgba, out var palette, out var indices, out int depth);
        
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

        new LzwEncoder(stream).Encode(indices, width, height, Math.Max(2, depth + 1));
        stream.WriteByte(0x3B);
    }

    public void EncodeAnimation(IReadOnlyList<ImageFrame> frames, IReadOnlyList<int> frameDurationsMs, int loopCount, Stream stream)
    {
        if (frames.Count == 0) return;
        int w = frames[0].Width, h = frames[0].Height;

        int ptr = 0;
        WriteAscii(_headerBuf, ref ptr, "GIF89a");
        WriteShort(_headerBuf, ref ptr, w); WriteShort(_headerBuf, ref ptr, h);
        _headerBuf[ptr++] = 0x70; // No GCT, 8-bit res
        _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);

        WriteNetscapeExtension(stream, loopCount);

        for (int i = 0; i < frames.Count; i++)
        {
            var (pal, inds) = Quantizer.Quantize(frames[i].Pixels, w, h, EnableDithering);
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

            new LzwEncoder(stream).Encode(inds, w, h, Math.Max(2, depth + 1));
        }
        stream.WriteByte(0x3B);
    }

    public void EncodeAnimationRgba(int width, int height, IReadOnlyList<byte[]> rgbaFrames, IReadOnlyList<int> frameDurationsMs, int loopCount, Stream stream)
    {
        if (rgbaFrames.Count == 0) return;

        int ptr = 0;
        WriteAscii(_headerBuf, ref ptr, "GIF89a");
        WriteShort(_headerBuf, ref ptr, width); WriteShort(_headerBuf, ref ptr, height);
        _headerBuf[ptr++] = 0x70;
        _headerBuf[ptr++] = 0; _headerBuf[ptr++] = 0;
        stream.Write(_headerBuf, 0, ptr);

        WriteNetscapeExtension(stream, loopCount);

        for (int i = 0; i < rgbaFrames.Count; i++)
        {
            QuantizeRgbaWithTransparency(width, height, rgbaFrames[i], out var pal, out var inds, out int depth);
            
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
            new LzwEncoder(stream).Encode(inds, width, height, Math.Max(2, depth + 1));
        }
        stream.WriteByte(0x3B);
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
        byte[] opaque = new byte[pixelCount * 3];
        byte[] mask = new byte[pixelCount];
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

        var (pal, indsOpaque) = Quantizer.Quantize(opaque, width, height, EnableDithering);
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
