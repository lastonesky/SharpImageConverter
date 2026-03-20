using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
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
            byte[]? backBuffer = null; // For disposal == 3

            if (hasGct && bgIndex < gctColors)
            {
                byte r = gct[bgIndex * 3], g = gct[bgIndex * 3 + 1], b = gct[bgIndex * 3 + 2];
                if (ctx.Mode == DecodeMode.Rgba32)
                {
                    for (int i = 0; i < canvas.Length; i += 4) { canvas[i] = r; canvas[i+1] = g; canvas[i+2] = b; canvas[i+3] = 255; }
                }
                else
                {
                    for (int i = 0; i < canvas.Length; i += 3) { canvas[i] = r; canvas[i+1] = g; canvas[i+2] = b; }
                }
            }

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
                    new LzwDecoder(stream).Decode(indices.AsSpan(0, iw * ih), iw, ih, lzwMin);

                    if (disposal == 3)
                    {
                        backBuffer ??= new byte[canvas.Length];
                        Buffer.BlockCopy(canvas, 0, backBuffer, 0, canvas.Length);
                    }

                    RenderFrame(canvas, indices, width, height, ix, iy, iw, ih, interlace, transIndex, palette, palCount, ctx.Mode == DecodeMode.Rgba32);
                    pool.Return(indices);

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
                    if (disposal == 2) // Restore to background
                    {
                        FillRect(canvas, width, height, ix, iy, iw, ih, ctx.Mode == DecodeMode.Rgba32, hasGct ? gct[bgIndex*3] : (byte)0, hasGct ? gct[bgIndex*3+1] : (byte)0, hasGct ? gct[bgIndex*3+2] : (byte)0);
                    }
                    else if (disposal == 3 && backBuffer != null) // Restore to previous
                    {
                        Buffer.BlockCopy(backBuffer, 0, canvas, 0, canvas.Length);
                    }

                    disposal = 0; transIndex = -1; delayCs = 0;
                    if (ctx.Mode == DecodeMode.Rgb24) return;
                }
            }
            if (ctx.RgbFrames?.Count == 0) ctx.RgbFrames.Add(new Image<Rgb24>(width, height, canvas));
        }

        private void RenderFrame(byte[] canvas, byte[] indices, int w, int h, int ix, int iy, int iw, int ih, bool interlace, int trans, byte[] pal, int palColors, bool rgba)
        {
            int comp = rgba ? 4 : 3;
            int stride = w * comp;
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
