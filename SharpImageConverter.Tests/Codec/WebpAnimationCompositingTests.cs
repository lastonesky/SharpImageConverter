using System.Runtime.InteropServices;
using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 托管动图（WebpDecoder.DecodeAnimation）的 ANMF blend / dispose 语义，
/// 与 libwebp 的 WebPAnimDecoder 逐帧逐像素对拍（bit-exact）。
/// 测试夹具用 libwebp 编码每个子帧的 VP8L 位流，再手工拼装 ANMF，
/// 因此 libwebp 能完整解码这些文件，参考结果就是 libwebp 的合成结果。
/// </summary>
public class WebpAnimationCompositingTests
{
    [Fact]
    public void Animation_BlendOverPreviousFrame_BitExact()
    {
        const int cw = 32, ch = 20;
        var frames = new[]
        {
            new FrameSpec(PatternRgba(cw, ch, seed: 1, opaque: true), 0, 0, cw, ch, 100, Blend: false, DisposeBg: false),
            new FrameSpec(PatternRgba(20, 12, seed: 2, opaque: false), 6, 4, 20, 12, 120, Blend: true, DisposeBg: false),
            new FrameSpec(PatternRgba(20, 12, seed: 3, opaque: false), 6, 4, 20, 12, 140, Blend: true, DisposeBg: false),
            new FrameSpec(PatternRgba(10, 6, seed: 4, opaque: false), 2, 2, 10, 6, 160, Blend: true, DisposeBg: false),
        };

        AssertBitExact(frames, cw, ch);
    }

    [Fact]
    public void Animation_NoBlendOverwrites_BitExact()
    {
        const int cw = 30, ch = 18;
        var frames = new[]
        {
            new FrameSpec(PatternRgba(cw, ch, seed: 5, opaque: true), 0, 0, cw, ch, 90, Blend: false, DisposeBg: false),
            // 帧标志 bit1 = 1：不混合，半透明像素直接覆盖画布（形成"洞"）
            new FrameSpec(PatternRgba(18, 10, seed: 6, opaque: false), 6, 4, 18, 10, 90, Blend: false, DisposeBg: false),
            new FrameSpec(PatternRgba(12, 8, seed: 7, opaque: false), 12, 6, 12, 8, 90, Blend: true, DisposeBg: false),
        };

        AssertBitExact(frames, cw, ch);
    }

    [Fact]
    public void Animation_DisposeBackground_ThenBlend_BitExact()
    {
        const int cw = 32, ch = 24;
        var frames = new[]
        {
            new FrameSpec(PatternRgba(cw, ch, seed: 8, opaque: true), 0, 0, cw, ch, 100, Blend: false, DisposeBg: false),
            // dispose=BACKGROUND：下一帧绘制前，本帧矩形要恢复为透明黑
            new FrameSpec(PatternRgba(24, 10, seed: 9, opaque: true), 4, 6, 24, 10, 100, Blend: false, DisposeBg: true),
            // 一半落在被清除区域，一半落在帧 1 留下的内容上
            new FrameSpec(PatternRgba(20, 12, seed: 10, opaque: false), 2, 2, 20, 12, 100, Blend: true, DisposeBg: false),
            // 最后一帧：部分在帧 2 矩形内，部分在其外
            new FrameSpec(PatternRgba(12, 8, seed: 11, opaque: false), 16, 10, 12, 8, 100, Blend: true, DisposeBg: false),
        };

        AssertBitExact(frames, cw, ch);
    }

    [Fact]
    public void Animation_PartialFirstFrame_BlendAgainstTransparentCanvas_BitExact()
    {
        const int cw = 24, ch = 16;
        var frames = new[]
        {
            new FrameSpec(PatternRgba(12, 8, seed: 12, opaque: true), 10, 6, 12, 8, 80, Blend: true, DisposeBg: false),
            new FrameSpec(PatternRgba(cw, ch, seed: 13, opaque: false), 0, 0, cw, ch, 80, Blend: true, DisposeBg: false),
            new FrameSpec(PatternRgba(6, 4, seed: 14, opaque: false), 18, 12, 6, 4, 80, Blend: false, DisposeBg: true),
        };

        AssertBitExact(frames, cw, ch);
    }

    [Fact]
    public void Animation_DecodeRgba_ReturnsLastCompositedFrame()
    {
        const int cw = 20, ch = 14;
        var frames = new[]
        {
            new FrameSpec(PatternRgba(cw, ch, seed: 15, opaque: true), 0, 0, cw, ch, 100, Blend: false, DisposeBg: true),
            new FrameSpec(PatternRgba(10, 6, seed: 16, opaque: false), 4, 4, 10, 6, 100, Blend: true, DisposeBg: false),
        };

        byte[] webp = BuildAnimation(cw, ch, frames);
        var animation = WebpDecoder.DecodeAnimation(webp);

        byte[] last = WebpDecoder.DecodeRgba(webp, out int w, out int h);
        Assert.Equal(cw, w);
        Assert.Equal(ch, h);
        PixelAssert.Equal(animation.Frames[^1].Rgba, last, w, h);
    }

    [Fact]
    public void Animation_OutOfCanvasFrame_RejectedByBothBackends()
    {
        const int cw = 16, ch = 16;
        // 帧矩形 x=14,w=6 → 20 > 16，违反 RFC 9649 §2.7.1.1（帧矩形必须完全位于画布内）
        var frames = new[]
        {
            new FrameSpec(PatternRgba(6, 6, seed: 17, opaque: true), 14, 4, 6, 6, 100, Blend: false, DisposeBg: false),
        };

        byte[] webp = BuildAnimation(cw, ch, frames);

        Assert.Throws<WebpContainerException>(() => WebpDecoder.DecodeAnimation(webp));
        // libwebp 的 demux 同样判定文件非法，WebPAnimDecoderNewInternal 返回 NULL。
        Assert.False(LibWebpAnimDecoder.TryDecode(webp, out _, out _, out _));
    }

    private static void AssertBitExact(FrameSpec[] frames, int canvasWidth, int canvasHeight)
    {
        byte[] webp = BuildAnimation(canvasWidth, canvasHeight, frames);

        var csharp = WebpDecoder.DecodeAnimation(webp);
        Assert.Equal(canvasWidth, csharp.CanvasWidth);
        Assert.Equal(canvasHeight, csharp.CanvasHeight);
        Assert.Equal(frames.Length, csharp.Frames.Length);

        Assert.True(LibWebpAnimDecoder.TryDecode(webp, out int lw, out int lh, out byte[][] lib), "libwebp 解码失败");
        Assert.Equal(canvasWidth, lw);
        Assert.Equal(canvasHeight, lh);
        Assert.Equal(frames.Length, lib.Length);

        for (int i = 0; i < frames.Length; i++)
        {
            var frame = csharp.Frames[i];
            Assert.Equal(frames[i].X, frame.X);
            Assert.Equal(frames[i].Y, frame.Y);
            Assert.Equal(frames[i].W, frame.Width);
            Assert.Equal(frames[i].H, frame.Height);
            Assert.Equal(frames[i].DurationMs, frame.DurationMs);
            PixelAssert.Equal(lib[i], frame.Rgba, canvasWidth, canvasHeight);
        }
    }

    private readonly struct FrameSpec
    {
        public FrameSpec(byte[] rgba, int x, int y, int w, int h, int durationMs, bool Blend, bool DisposeBg)
        {
            Rgba = rgba;
            X = x;
            Y = y;
            W = w;
            H = h;
            DurationMs = durationMs;
            this.Blend = Blend;
            this.DisposeBg = DisposeBg;
            bool opaque = true;
            for (int i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255) { opaque = false; break; }
            }
            Opaque = opaque;
        }

        public byte[] Rgba { get; }
        public int X { get; }
        public int Y { get; }
        public int W { get; }
        public int H { get; }
        public int DurationMs { get; }
        public bool Blend { get; }
        public bool DisposeBg { get; }
        public bool Opaque { get; }
    }

    // ==================================================================
    //  ANMF / RIFF 手工拼装
    // ==================================================================

    private static byte[] BuildAnimation(int canvasWidth, int canvasHeight, FrameSpec[] frames)
    {
        bool anyAlpha = frames.Any(f => !f.Opaque);

        var vp8x = new byte[10];
        vp8x[0] = (byte)(0x02 | (anyAlpha ? 0x10 : 0x00)); // Animation | Alpha
        WriteUInt24(vp8x, 4, canvasWidth - 1);
        WriteUInt24(vp8x, 7, canvasHeight - 1);

        var anim = new byte[6]; // background color = 透明黑，loop count = 0（无限）

        var parts = new List<(string FourCC, byte[] Data)> { ("VP8X", vp8x), ("ANIM", anim) };
        foreach (var f in frames)
        {
            var header = new byte[16];
            // RFC 9649 §2.7.1.1：ANMF 里的 Frame X / Frame Y 是坐标的一半（解析时乘 2），故这里必须为偶数。
            Assert.True((f.X & 1) == 0 && (f.Y & 1) == 0, "测试帧偏移必须为偶数");
            WriteUInt24(header, 0, f.X / 2);
            WriteUInt24(header, 3, f.Y / 2);
            WriteUInt24(header, 6, f.W - 1);
            WriteUInt24(header, 9, f.H - 1);
            WriteUInt24(header, 12, f.DurationMs);
            // bit0 = dispose to background，bit1 = 1 表示不混合
            header[15] = (byte)((f.DisposeBg ? 0x01 : 0x00) | (f.Blend ? 0x00 : 0x02));

            var payload = Concat(header, Chunk("VP8L", EncodeVp8LChunk(f.Rgba, f.W, f.H)));
            parts.Add(("ANMF", payload));
        }

        return BuildRiff(parts.ToArray());
    }

    private static byte[] EncodeVp8LChunk(byte[] rgba, int w, int h)
    {
        byte[] file = WebpCodec.EncodeRgba(rgba, w, h, new WebpEncoderOptions(75f, lossless: true));
        var container = WebpContainer.Load(file);
        var chunk = container.FindChunk(WebpChunkId.Vp8L);
        Assert.True(chunk.HasValue, "libwebp 未输出 VP8L chunk");
        return chunk!.Value.GetData(container.RawData).ToArray();
    }

    private static byte[] BuildRiff((string FourCC, byte[] Data)[] chunks)
    {
        var encoded = chunks.Select(c => Chunk(c.FourCC, c.Data)).ToArray();
        int total = 4 + encoded.Sum(c => c.Length);
        var riff = new byte[8 + total];
        riff[0] = (byte)'R'; riff[1] = (byte)'I'; riff[2] = (byte)'F'; riff[3] = (byte)'F';
        BitConverter.TryWriteBytes(riff.AsSpan(4), (uint)total);
        riff[8] = (byte)'W'; riff[9] = (byte)'E'; riff[10] = (byte)'B'; riff[11] = (byte)'P';
        int pos = 12;
        foreach (var c in encoded) { c.CopyTo(riff, pos); pos += c.Length; }
        return riff;
    }

    private static byte[] Chunk(string fourcc, byte[] data)
    {
        var bytes = new byte[8 + data.Length + (data.Length & 1)];
        bytes[0] = (byte)fourcc[0];
        bytes[1] = (byte)fourcc[1];
        bytes[2] = (byte)fourcc[2];
        bytes[3] = (byte)fourcc[3];
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)data.Length);
        data.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(a => a.Length)];
        int pos = 0;
        foreach (var a in arrays) { a.CopyTo(result, pos); pos += a.Length; }
        return result;
    }

    private static void WriteUInt24(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }

    private static byte[] PatternRgba(int w, int h, int seed, bool opaque)
    {
        var rgba = new byte[w * h * 4];
        int s = seed * 131;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            rgba[o + 0] = (byte)((x * 5 + y * 3 + s) & 0xFF);
            rgba[o + 1] = (byte)((x * 2 + y * 7 + s) & 0xFF);
            rgba[o + 2] = (byte)((x * 9 + y * 11 + s) & 0xFF);
            // 覆盖 0、255 与中间值，确保混合公式的三条分支都被执行
            rgba[o + 3] = opaque ? (byte)255 : (byte)((x * 17 + y * 29 + s) & 0xFF);
        }
        return rgba;
    }

    /// <summary>
    /// libwebp 的 WebPAnimDecoder（libwebpdemux）封装：给出每帧合成后的画布快照。
    /// WebPDemux/AnimDecoder 不复制输入位流，因此必须保持 GCHandle 固定直到 Delete。
    /// </summary>
    internal static class LibWebpAnimDecoder
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WebPDataNative
        {
            public IntPtr Bytes;
            public nuint Size;
        }

        [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderNewInternal", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NewInternal(ref WebPDataNative data, IntPtr options, int abiVersion);

        [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderGetInfo", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetInfo(IntPtr dec, IntPtr info);

        [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderHasMoreFrames", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HasMoreFrames(IntPtr dec);

        [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderGetNext", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetNext(IntPtr dec, out IntPtr buffer, out int timestamp);

        [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderDelete", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Delete(IntPtr dec);

        public static bool TryDecode(byte[] webp, out int width, out int height, out byte[][] frames)
        {
            width = 0;
            height = 0;
            frames = Array.Empty<byte[]>();
            if (webp.Length == 0) return false;

            var pin = GCHandle.Alloc(webp, GCHandleType.Pinned);
            IntPtr info = Marshal.AllocHGlobal(64);
            IntPtr dec = IntPtr.Zero;
            try
            {
                var data = new WebPDataNative { Bytes = pin.AddrOfPinnedObject(), Size = (nuint)webp.Length };
                // ABI 版本按从新到旧尝试；库里声明的是 0x0107，旧库会拒绝更新的版本。
                for (int abi = 0x0107; abi >= 0x0100 && dec == IntPtr.Zero; abi--)
                    dec = NewInternal(ref data, IntPtr.Zero, abi);
                if (dec == IntPtr.Zero) return false;

                Marshal.Copy(new byte[64], 0, info, 64);
                if (GetInfo(dec, info) == 0) return false;
                width = Marshal.ReadInt32(info, 0);
                height = Marshal.ReadInt32(info, 4);
                int frameCount = Marshal.ReadInt32(info, 16);

                var list = new List<byte[]>(frameCount);
                while (HasMoreFrames(dec) != 0)
                {
                    if (GetNext(dec, out IntPtr buffer, out _) == 0) return false;
                    var canvas = new byte[width * height * 4];
                    Marshal.Copy(buffer, canvas, 0, canvas.Length);
                    list.Add(canvas);
                }

                frames = list.ToArray();
                return frameCount > 0 && frames.Length == frameCount;
            }
            finally
            {
                if (dec != IntPtr.Zero) Delete(dec);
                Marshal.FreeHGlobal(info);
                pin.Free();
            }
        }
    }
}
