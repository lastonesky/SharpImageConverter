using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Core;

internal static class SimdHelper
{
    public const int DefaultAlignment = 64;

    /// <summary>
    /// 逐字节做 mod 256 回绕加法（PNG Up 滤波语义：dst[i] = (dst[i] + src[i]) &amp; 0xFF）。
    /// 注意：这里必须用"回绕加"而不是饱和加，否则 PNG 解码结果会错。
    /// </summary>
    public static void AddBytesInPlace(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        int length = destination.Length;
        if (source.Length < length) length = source.Length;
        if (length == 0) return;

        ref byte dstRef = ref MemoryMarshal.GetReference(destination);
        ref byte srcRef = ref MemoryMarshal.GetReference(source);

        int i = 0;

        // 分派阶梯从最宽到最窄：Sse2.IsSupported 在 x64 上恒为真，
        // 若排在 Vector<T> 之前会把 AVX2/AVX-512 宽档变成永不可达的死代码。
        int vectorWidth = Vector.IsHardwareAccelerated ? Vector<byte>.Count : 0;

        if (vectorWidth > Vector128<byte>.Count && length >= vectorWidth)
        {
            int simd = vectorWidth;
            for (; i <= length - simd; i += simd)
            {
                nuint o = (nuint)i;
                var a = Vector.LoadUnsafe(ref dstRef, o);
                var b = Vector.LoadUnsafe(ref srcRef, o);
                Vector.StoreUnsafe(a + b, ref dstRef, o);
            }
        }
        // 首选 128 位整字节加法：一条 paddb 处理 16 字节，天然回绕。
        else if (Sse2.IsSupported)
        {
            int limit = length - Vector128<byte>.Count;
            for (; i <= limit; i += Vector128<byte>.Count)
            {
                nuint o = (nuint)i;
                Vector128<byte> a = Vector128.LoadUnsafe(ref dstRef, o);
                Vector128<byte> b = Vector128.LoadUnsafe(ref srcRef, o);
                Vector128.StoreUnsafe(Sse2.Add(a, b), ref dstRef, o);
            }
        }
        else if (AdvSimd.IsSupported)
        {
            int limit = length - Vector128<byte>.Count;
            for (; i <= limit; i += Vector128<byte>.Count)
            {
                nuint o = (nuint)i;
                Vector128<byte> a = Vector128.LoadUnsafe(ref dstRef, o);
                Vector128<byte> b = Vector128.LoadUnsafe(ref srcRef, o);
                Vector128.StoreUnsafe(AdvSimd.Add(a, b), ref dstRef, o);
            }
        }
        else if (vectorWidth > 0 && length >= vectorWidth)
        {
            int simd = vectorWidth;
            for (; i <= length - simd; i += simd)
            {
                nuint o = (nuint)i;
                var a = Vector.LoadUnsafe(ref dstRef, o);
                var b = Vector.LoadUnsafe(ref srcRef, o);
                Vector.StoreUnsafe(a + b, ref dstRef, o);
            }
        }

        for (; i < length; i++)
        {
            destination[i] = (byte)(destination[i] + source[i]);
        }
    }

    // ---------------------------------------------------------------------
    // 反交错掩码：把 16 个 RGB 像素（48 字节，记为 A=0..15 / B=16..31 / C=32..47）
    // 拆成 3 个各含 16 个分量的字节向量。掩码字节最高位为 1 时 pshufb 结果置 0，
    // 因此三段结果可以直接按位或合并。
    // ---------------------------------------------------------------------
    private static readonly Vector128<byte> DeinterleaveRFromA = Vector128.Create((byte)0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> DeinterleaveRFromB = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> DeinterleaveRFromC = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 1, 4, 7, 10, 13);

    private static readonly Vector128<byte> DeinterleaveGFromA = Vector128.Create((byte)1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> DeinterleaveGFromB = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0, 3, 6, 9, 12, 15, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> DeinterleaveGFromC = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2, 5, 8, 11, 14);

    private static readonly Vector128<byte> DeinterleaveBFromA = Vector128.Create((byte)2, 5, 8, 11, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> DeinterleaveBFromB = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 1, 4, 7, 10, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> DeinterleaveBFromC = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 3, 6, 9, 12, 15);

    // 交错掩码：16 个灰度字节 -> 48 字节 RGB（每字节重复 3 次），分 3 次 16 字节存储。
    private static readonly Vector128<byte> GrayExpand0 = Vector128.Create((byte)0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5);
    private static readonly Vector128<byte> GrayExpand1 = Vector128.Create((byte)5, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10);
    private static readonly Vector128<byte> GrayExpand2 = Vector128.Create((byte)10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15);

    // RGBA(4 像素,16 字节) -> RGB(4 像素,12 字节)
    private static readonly Vector128<byte> RgbaToRgbShuffle = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

    // RGB(4 像素,12 字节) -> RGBA(4 像素,16 字节)：shuffle 负责搬数据，Or 补上不透明 alpha
    private static readonly Vector128<byte> RgbToRgbaShuffle = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
    private static readonly Vector128<byte> RgbToRgbaAlpha = Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);

    // ---- BGR / BGRA 互转掩码（详见下方各转换方法） ----
    // RGB24 ⇄ BGR24：每个像素交换 R(0) 与 B(2)，每批 4 像素（12 字节）。
    private static readonly Vector128<byte> SwapRgbBgrMask = Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 0x80, 0x80, 0x80, 0x80);
    // RGB24 -> BGRA32：搬成 [B G R ?]，再 Or 上 alpha。
    private static readonly Vector128<byte> RgbToBgraMask = Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);
    // BGRA32 -> RGB24：丢弃 alpha，搬成 [R G B]。
    private static readonly Vector128<byte> BgraToRgbMask = Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);
    // BGR24 -> BGRA32：通道顺序保持 [B G R]，仅补 alpha。
    private static readonly Vector128<byte> BgrToBgraMask = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
    // BGRA32 -> BGR24：丢弃 alpha，通道顺序保持 [B G R]。
    private static readonly Vector128<byte> BgraToBgrMask = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> BgraAlpha = Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);

    // ---- RGBA32 与 BGRA32 / 24 位格式的互转掩码 ----
    // BGRA32 ⇄ RGBA32：交换每个像素的 B(0) 与 R(2)，alpha(3) 保持不变，每批 4 像素（16 字节）。
    // 该置换完全发生在像素内、且源目标等长，因此支持就地交换（读写均为整 16 字节）。
    private static readonly Vector128<byte> SwapRgbaBgraMask = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
    // RGBA32 -> BGR24：丢弃 alpha 并交换 R/B，搬成 [B G R]。
    private static readonly Vector128<byte> RgbaToBgrMask = Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);
    // BGR24 -> RGBA32：搬成 [R G B ?]，再 Or 上 alpha。
    private static readonly Vector128<byte> BgrToRgbaMask = Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80);

    /// <summary>
    /// 就地把 RGB24 缓冲区转为灰度：y = (77*R + 150*G + 29*B) &gt;&gt; 8，三通道写入同一值。
    /// </summary>
    public static void GrayscaleRgb24InPlace(Span<byte> rgb)
    {
        // 每批处理 16 个像素（48 字节）
        int blocks = rgb.Length / 48;
        int i = 0;

        if (SimdCompat.ByteShuffleSupported && blocks > 0)
        {
            ref byte r = ref MemoryMarshal.GetReference(rgb);
            Vector128<short> w77 = Vector128.Create((short)77);
            Vector128<short> w150 = Vector128.Create((short)150);
            Vector128<short> w29 = Vector128.Create((short)29);

            for (int b = 0; b < blocks; b++)
            {
                nuint o = (nuint)(b * 48);
                Vector128<byte> a = Vector128.LoadUnsafe(ref r, o);
                Vector128<byte> c = Vector128.LoadUnsafe(ref r, o + 16);
                Vector128<byte> d = Vector128.LoadUnsafe(ref r, o + 32);

                Vector128<byte> rv = SimdCompat.OrBytes(
                    SimdCompat.OrBytes(
                        SimdCompat.ShuffleBytes(a, DeinterleaveRFromA),
                        SimdCompat.ShuffleBytes(c, DeinterleaveRFromB)),
                    SimdCompat.ShuffleBytes(d, DeinterleaveRFromC));
                Vector128<byte> gv = SimdCompat.OrBytes(
                    SimdCompat.OrBytes(
                        SimdCompat.ShuffleBytes(a, DeinterleaveGFromA),
                        SimdCompat.ShuffleBytes(c, DeinterleaveGFromB)),
                    SimdCompat.ShuffleBytes(d, DeinterleaveGFromC));
                Vector128<byte> bv = SimdCompat.OrBytes(
                    SimdCompat.OrBytes(
                        SimdCompat.ShuffleBytes(a, DeinterleaveBFromA),
                        SimdCompat.ShuffleBytes(c, DeinterleaveBFromB)),
                    SimdCompat.ShuffleBytes(d, DeinterleaveBFromC));

                // 字节 -> ushort，16 位定点运算即可容纳 (77+150+29)*255 = 65280
                Vector128<ushort> grayLo = GrayCombine(
                    SimdCompat.WidenLowerBytes(rv),
                    SimdCompat.WidenLowerBytes(gv),
                    SimdCompat.WidenLowerBytes(bv),
                    w77, w150, w29);
                Vector128<ushort> grayHi = GrayCombine(
                    SimdCompat.WidenUpperBytes(rv),
                    SimdCompat.WidenUpperBytes(gv),
                    SimdCompat.WidenUpperBytes(bv),
                    w77, w150, w29);

                // packuswb 只接受 short 输入；灰度值域 [0,255]，饱和不会截断有效数据
                Vector128<byte> gray = SimdCompat.PackUnsignedSaturate(grayLo.AsInt16(), grayHi.AsInt16());

                SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(gray, GrayExpand0), ref r, o);
                SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(gray, GrayExpand1), ref r, o + 16);
                SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(gray, GrayExpand2), ref r, o + 32);
            }

            i = blocks * 16;
        }

        for (int p = i * 3; p + 2 < rgb.Length; p += 3)
        {
            int y = (77 * rgb[p] + 150 * rgb[p + 1] + 29 * rgb[p + 2]) >> 8;
            byte v = (byte)y;
            rgb[p] = v;
            rgb[p + 1] = v;
            rgb[p + 2] = v;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> GrayCombine(
        Vector128<ushort> r,
        Vector128<ushort> g,
        Vector128<ushort> b,
        Vector128<short> w77,
        Vector128<short> w150,
        Vector128<short> w29)
    {
        // MultiplyLow 只有 short 重载（pmullw / NEON mul）：两个操作数均 <= 255，
        // 乘积 <= 38250，低 16 位与无符号乘法完全一致，因此这里按 16 位回绕语义是安全的。
        Vector128<short> t = SimdCompat.MultiplyLowInt16(r.AsInt16(), w77);
        t = SimdCompat.AddInt16(t, SimdCompat.MultiplyLowInt16(g.AsInt16(), w150));
        t = SimdCompat.AddInt16(t, SimdCompat.MultiplyLowInt16(b.AsInt16(), w29));
        return SimdCompat.ShiftRightLogicalUInt16(t.AsUInt16(), 8);
    }

    /// <summary>
    /// Gray8 -> RGB24（每字节复制 3 次）。要求 rgb.Length == gray.Length * 3。
    /// </summary>
    public static void ExpandGrayToRgb(ReadOnlySpan<byte> gray, Span<byte> rgb)
    {
        int n = gray.Length;
        int i = 0;

        if (SimdCompat.ByteShuffleSupported)
        {
            int blocks = n / 16;
            if (blocks > 0)
            {
                ref byte src = ref MemoryMarshal.GetReference(gray);
                ref byte dst = ref MemoryMarshal.GetReference(rgb);
                for (int b = 0; b < blocks; b++)
                {
                    Vector128<byte> g = Vector128.LoadUnsafe(ref src, (nuint)(b * 16));
                    nuint o = (nuint)(b * 48);
                    SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(g, GrayExpand0), ref dst, o);
                    SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(g, GrayExpand1), ref dst, o + 16);
                    SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(g, GrayExpand2), ref dst, o + 32);
                }
                i = blocks * 16;
            }
        }

        for (; i < n; i++)
        {
            byte v = gray[i];
            int j = i * 3;
            rgb[j] = v;
            rgb[j + 1] = v;
            rgb[j + 2] = v;
        }
    }

    /// <summary>
    /// RGBA32 -> RGB24（丢弃 alpha）。要求 rgb.Length == (rgba.Length / 4) * 3。
    /// </summary>
    public static void PackRgbaToRgb(ReadOnlySpan<byte> rgba, Span<byte> rgb)
    {
        int pixels = rgba.Length / 4;
        int p = 0;

        if (SimdCompat.ByteShuffleSupported)
        {
            // 每批 4 像素：读 16 字节，写 12 字节
            int blocks = rgba.Length / 16;
            if (blocks > 0)
            {
                ref byte src = ref MemoryMarshal.GetReference(rgba);
                ref byte dst = ref MemoryMarshal.GetReference(rgb);

                // 除最后一块外都直接写满 16 字节：高 4 字节是 shuffle 掩码置零的垃圾，
                // 但它们仍然落在本缓冲区内，且会被下一块的真实数据覆盖，
                // 因此用一次 16 字节存储替代原先"取 3 个标量再写 3 次"的做法。
                int fullBlocks = blocks - 1;
                for (int b = 0; b < fullBlocks; b++)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref src, (nuint)(b * 16));
                    SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(v, RgbaToRgbShuffle), ref dst, (nuint)(b * 12));
                }

                // 最后一块没有"下一块"来覆盖尾部，只能写真实的 12 字节：
                // 拆成 8 字节 + 4 字节两次存储（仍是 2 次，而非原先逐元素 3 次）。
                int last = fullBlocks;
                Vector128<byte> lastV = Vector128.LoadUnsafe(ref src, (nuint)(last * 16));
                Vector128<byte> lastPacked = SimdCompat.ShuffleBytes(lastV, RgbaToRgbShuffle);
                int o = last * 12;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), lastPacked.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + 8), lastPacked.AsUInt32().GetElement(2));

                p = blocks * 4;
            }
        }

        for (; p < pixels; p++)
        {
            rgb[p * 3 + 0] = rgba[p * 4 + 0];
            rgb[p * 3 + 1] = rgba[p * 4 + 1];
            rgb[p * 3 + 2] = rgba[p * 4 + 2];
        }
    }

    /// <summary>
    /// RGB24 -> RGBA32（alpha 置 255）。要求 rgba.Length == (rgb.Length / 3) * 4。
    /// </summary>
    public static void ExpandRgbToRgba(ReadOnlySpan<byte> rgb, Span<byte> rgba)
    {
        int pixels = rgb.Length / 3;
        int p = 0;

        if (SimdCompat.ByteShuffleSupported)
        {
            ref byte src = ref MemoryMarshal.GetReference(rgb);
            ref byte dst = ref MemoryMarshal.GetReference(rgba);
            // 每批 4 像素：读 12 字节（需要 16 字节可读才安全），写 16 字节
            int b = 0;
            while (b * 12 + 16 <= rgb.Length)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref src, (nuint)(b * 12));
                Vector128<byte> expanded = SimdCompat.OrBytes(
                    SimdCompat.ShuffleBytes(v, RgbToRgbaShuffle),
                    RgbToRgbaAlpha);
                SimdCompat.StoreBytes(expanded, ref dst, (nuint)(b * 16));
                b++;
            }
            p = b * 4;
        }

        for (; p < pixels; p++)
        {
            rgba[p * 4 + 0] = rgb[p * 3 + 0];
            rgba[p * 4 + 1] = rgb[p * 3 + 1];
            rgba[p * 4 + 2] = rgb[p * 3 + 2];
            rgba[p * 4 + 3] = 255;
        }
    }

    // ===================== BGR / BGRA 兼容转换 =====================

    /// <summary>
    /// RGB24 ⇄ BGR24：交换每个像素的 R 与 B 通道。源与目标长度必须相等（同为 24bpp）。
    /// 支持 source == destination（就地交换），此时不分配任何额外缓冲区；
    /// SIMD 路径每批处理 4 像素（12 字节），就地时必须只写 12 字节以免破坏下一批的源数据。
    /// </summary>
    public static void SwapRgbBgr24(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length != source.Length)
            throw new ArgumentException("源与目标缓冲区长度必须相等（均为 24bpp）", nameof(destination));
        int length = destination.Length;
        int i = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(source);
            ref byte d = ref MemoryMarshal.GetReference(destination);
            while (i + 16 <= length)
            {
                nuint o = (nuint)i;
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> r = Ssse3.Shuffle(v, SwapRgbBgrMask);
                // 就地交换若写 16 字节会覆盖下一批要读的源字节，这里只写真实的 12 字节（8 + 4）
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, o), r.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, o + 8), r.AsUInt32().GetElement(2));
                i += 12;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(source);
            ref byte d = ref MemoryMarshal.GetReference(destination);
            while (i + 16 <= length)
            {
                nuint o = (nuint)i;
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> r = AdvSimd.Arm64.VectorTableLookup(SwapRgbBgrMask, v);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, o), r.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, o + 8), r.AsUInt32().GetElement(2));
                i += 12;
            }
        }

        int totalPixels = length / 3;
        for (int p = i / 3; p < totalPixels; p++)
        {
            int idx = p * 3;
            byte r0 = source[idx];
            byte b0 = source[idx + 2];
            destination[idx] = b0;
            destination[idx + 1] = source[idx + 1];
            destination[idx + 2] = r0;
        }
    }

    /// <summary>
    /// RGB24 → BGRA32（交换 R/B，alpha 置 255）。要求 bgra.Length == (rgb.Length / 3) * 4。
    /// </summary>
    public static void ConvertRgb24ToBgra32(ReadOnlySpan<byte> rgb, Span<byte> bgra)
    {
        int pixels = rgb.Length / 3;
        int b = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(rgb);
            ref byte d = ref MemoryMarshal.GetReference(bgra);
            while (b * 12 + 16 <= rgb.Length)
            {
                nuint o = (nuint)(b * 12);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = Sse2.Or(Ssse3.Shuffle(v, RgbToBgraMask), BgraAlpha);
                Vector128.StoreUnsafe(e, ref d, (nuint)(b * 16));
                b++;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(rgb);
            ref byte d = ref MemoryMarshal.GetReference(bgra);
            while (b * 12 + 16 <= rgb.Length)
            {
                nuint o = (nuint)(b * 12);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(RgbToBgraMask, v), BgraAlpha);
                Vector128.StoreUnsafe(e, ref d, (nuint)(b * 16));
                b++;
            }
        }

        for (int p = b * 4; p < pixels; p++)
        {
            int si = p * 3, di = p * 4;
            bgra[di] = rgb[si + 2];
            bgra[di + 1] = rgb[si + 1];
            bgra[di + 2] = rgb[si];
            bgra[di + 3] = 255;
        }
    }

    /// <summary>
    /// BGR24 → BGRA32（通道顺序保持 [B G R]，alpha 置 255）。要求 bgra.Length == (bgr.Length / 3) * 4。
    /// </summary>
    public static void ConvertBgr24ToBgra32(ReadOnlySpan<byte> bgr, Span<byte> bgra)
    {
        int pixels = bgr.Length / 3;
        int b = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgr);
            ref byte d = ref MemoryMarshal.GetReference(bgra);
            while (b * 12 + 16 <= bgr.Length)
            {
                nuint o = (nuint)(b * 12);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = Sse2.Or(Ssse3.Shuffle(v, BgrToBgraMask), BgraAlpha);
                Vector128.StoreUnsafe(e, ref d, (nuint)(b * 16));
                b++;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgr);
            ref byte d = ref MemoryMarshal.GetReference(bgra);
            while (b * 12 + 16 <= bgr.Length)
            {
                nuint o = (nuint)(b * 12);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(BgrToBgraMask, v), BgraAlpha);
                Vector128.StoreUnsafe(e, ref d, (nuint)(b * 16));
                b++;
            }
        }

        for (int p = b * 4; p < pixels; p++)
        {
            int si = p * 3, di = p * 4;
            bgra[di] = bgr[si];
            bgra[di + 1] = bgr[si + 1];
            bgra[di + 2] = bgr[si + 2];
            bgra[di + 3] = 255;
        }
    }

    /// <summary>
    /// BGRA32 → RGB24（丢弃 alpha，交换 R/B）。要求 rgb.Length == (bgra.Length / 4) * 3。
    /// </summary>
    public static void ConvertBgra32ToRgb24(ReadOnlySpan<byte> bgra, Span<byte> rgb)
    {
        int pixels = bgra.Length / 4;
        int b = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgra);
            ref byte d = ref MemoryMarshal.GetReference(rgb);
            while (b * 16 + 16 <= bgra.Length)
            {
                nuint o = (nuint)(b * 16);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = Ssse3.Shuffle(v, BgraToRgbMask);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12)), e.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12 + 8)), e.AsUInt32().GetElement(2));
                b++;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgra);
            ref byte d = ref MemoryMarshal.GetReference(rgb);
            while (b * 16 + 16 <= bgra.Length)
            {
                nuint o = (nuint)(b * 16);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = AdvSimd.Arm64.VectorTableLookup(BgraToRgbMask, v);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12)), e.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12 + 8)), e.AsUInt32().GetElement(2));
                b++;
            }
        }

        for (int p = b * 4; p < pixels; p++)
        {
            int si = p * 4, di = p * 3;
            rgb[di] = bgra[si + 2];
            rgb[di + 1] = bgra[si + 1];
            rgb[di + 2] = bgra[si];
        }
    }

    /// <summary>
    /// BGRA32 → BGR24（丢弃 alpha，通道顺序保持 [B G R]）。要求 bgr.Length == (bgra.Length / 4) * 3。
    /// </summary>
    public static void ConvertBgra32ToBgr24(ReadOnlySpan<byte> bgra, Span<byte> bgr)
    {
        int pixels = bgra.Length / 4;
        int b = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgra);
            ref byte d = ref MemoryMarshal.GetReference(bgr);
            while (b * 16 + 16 <= bgra.Length)
            {
                nuint o = (nuint)(b * 16);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = Ssse3.Shuffle(v, BgraToBgrMask);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12)), e.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12 + 8)), e.AsUInt32().GetElement(2));
                b++;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgra);
            ref byte d = ref MemoryMarshal.GetReference(bgr);
            while (b * 16 + 16 <= bgra.Length)
            {
                nuint o = (nuint)(b * 16);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = AdvSimd.Arm64.VectorTableLookup(BgraToBgrMask, v);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12)), e.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12 + 8)), e.AsUInt32().GetElement(2));
                b++;
            }
        }

        for (int p = b * 4; p < pixels; p++)
        {
            int si = p * 4, di = p * 3;
            bgr[di] = bgra[si];
            bgr[di + 1] = bgra[si + 1];
            bgr[di + 2] = bgra[si + 2];
        }
    }

    /// <summary>
    /// BGRA32 ⇄ RGBA32：交换每个像素的 B 与 R 通道，alpha 保持不变。源与目标长度必须相等（同为 32bpp）。
    /// 支持 source == destination（就地交换），此时不分配任何额外缓冲区。
    /// 与 24 位不同，SIMD 路径每批恰好读/写 16 字节（4 像素），就地时完全对齐、无需部分写入保护。
    /// </summary>
    public static void SwapRgbaBgra32(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length != source.Length)
            throw new ArgumentException("源与目标缓冲区长度必须相等（均为 32bpp）", nameof(destination));
        if ((destination.Length & 3) != 0)
            throw new ArgumentException("32bpp 缓冲区长度必须是 4 的倍数", nameof(destination));

        int length = destination.Length;
        int i = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(source);
            ref byte d = ref MemoryMarshal.GetReference(destination);
            while (i + 16 <= length)
            {
                nuint o = (nuint)i;
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128.StoreUnsafe(Ssse3.Shuffle(v, SwapRgbaBgraMask), ref d, o);
                i += 16;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(source);
            ref byte d = ref MemoryMarshal.GetReference(destination);
            while (i + 16 <= length)
            {
                nuint o = (nuint)i;
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128.StoreUnsafe(AdvSimd.Arm64.VectorTableLookup(SwapRgbaBgraMask, v), ref d, o);
                i += 16;
            }
        }

        for (int p = i; p + 3 < length; p += 4)
        {
            // 先取出两端的原始字节，保证 source == destination 时就地交换安全
            byte b0 = source[p];
            byte r0 = source[p + 2];
            destination[p] = r0;
            destination[p + 1] = source[p + 1];
            destination[p + 2] = b0;
            destination[p + 3] = source[p + 3];
        }
    }

    /// <summary>
    /// RGBA32 → BGR24（丢弃 alpha，交换 R/B）。要求 bgr.Length == (rgba.Length / 4) * 3。
    /// </summary>
    public static void ConvertRgba32ToBgr24(ReadOnlySpan<byte> rgba, Span<byte> bgr)
    {
        int pixels = rgba.Length / 4;
        int b = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(rgba);
            ref byte d = ref MemoryMarshal.GetReference(bgr);
            while (b * 16 + 16 <= rgba.Length)
            {
                nuint o = (nuint)(b * 16);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = Ssse3.Shuffle(v, RgbaToBgrMask);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12)), e.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12 + 8)), e.AsUInt32().GetElement(2));
                b++;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(rgba);
            ref byte d = ref MemoryMarshal.GetReference(bgr);
            while (b * 16 + 16 <= rgba.Length)
            {
                nuint o = (nuint)(b * 16);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = AdvSimd.Arm64.VectorTableLookup(RgbaToBgrMask, v);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12)), e.AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, (nuint)(b * 12 + 8)), e.AsUInt32().GetElement(2));
                b++;
            }
        }

        for (int p = b * 4; p < pixels; p++)
        {
            int si = p * 4, di = p * 3;
            bgr[di] = rgba[si + 2];
            bgr[di + 1] = rgba[si + 1];
            bgr[di + 2] = rgba[si];
        }
    }

    /// <summary>
    /// BGR24 → RGBA32（交换 R/B，alpha 置 255）。要求 rgba.Length == (bgr.Length / 3) * 4。
    /// </summary>
    public static void ConvertBgr24ToRgba32(ReadOnlySpan<byte> bgr, Span<byte> rgba)
    {
        int pixels = bgr.Length / 3;
        int b = 0;

        if (Ssse3.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgr);
            ref byte d = ref MemoryMarshal.GetReference(rgba);
            while (b * 12 + 16 <= bgr.Length)
            {
                nuint o = (nuint)(b * 12);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = Sse2.Or(Ssse3.Shuffle(v, BgrToRgbaMask), BgraAlpha);
                Vector128.StoreUnsafe(e, ref d, (nuint)(b * 16));
                b++;
            }
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            ref byte s = ref MemoryMarshal.GetReference(bgr);
            ref byte d = ref MemoryMarshal.GetReference(rgba);
            while (b * 12 + 16 <= bgr.Length)
            {
                nuint o = (nuint)(b * 12);
                Vector128<byte> v = Vector128.LoadUnsafe(ref s, o);
                Vector128<byte> e = AdvSimd.Or(AdvSimd.Arm64.VectorTableLookup(BgrToRgbaMask, v), BgraAlpha);
                Vector128.StoreUnsafe(e, ref d, (nuint)(b * 16));
                b++;
            }
        }

        for (int p = b * 4; p < pixels; p++)
        {
            int si = p * 3, di = p * 4;
            rgba[di] = bgr[si + 2];
            rgba[di + 1] = bgr[si + 1];
            rgba[di + 2] = bgr[si];
            rgba[di + 3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NormalizeAlignment(int alignment)
    {
        if (alignment <= 0) throw new ArgumentOutOfRangeException(nameof(alignment));
        if (alignment < IntPtr.Size) alignment = IntPtr.Size;
        if (!IsPowerOfTwo(alignment))
        {
            alignment = (int)BitOperations.RoundUpToPowerOf2((uint)alignment);
        }
        return alignment;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundUpToMultiple(int value, int multiple)
    {
        if (multiple <= 0) throw new ArgumentOutOfRangeException(nameof(multiple));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        int remainder = value % multiple;
        if (remainder == 0) return value;
        return checked(value + (multiple - remainder));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundDownToMultiple(int value, int multiple)
    {
        if (multiple <= 0) throw new ArgumentOutOfRangeException(nameof(multiple));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value - (value % multiple);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsAligned(nuint address, int alignment)
    {
        alignment = NormalizeAlignment(alignment);
        return (address & (nuint)(alignment - 1)) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPaddedLength(int length, int padMultiple)
    {
        if (padMultiple <= 1) return length;
        return RoundUpToMultiple(length, padMultiple);
    }

    public static AlignedBuffer<byte> AllocateAlignedBytes(int length, int alignment = DefaultAlignment, bool clear = false, int padToMultiple = 0)
    {
        int padded = GetPaddedLength(length, padToMultiple);
        return AlignedBuffer<byte>.Allocate(padded, alignment, clear);
    }

    public static AlignedBuffer<byte> CopyToAlignedBytes(ReadOnlySpan<byte> source, int alignment = DefaultAlignment, int padToMultiple = 0, byte padValue = 0)
    {
        int length = source.Length;
        int padded = GetPaddedLength(length, padToMultiple);
        var buffer = AlignedBuffer<byte>.Allocate(padded, alignment, clear: false);
        if (length != 0)
        {
            source.CopyTo(buffer.Span.Slice(0, length));
        }
        if (padded != length)
        {
            buffer.Span.Slice(length, padded - length).Fill(padValue);
        }
        return buffer;
    }
}

internal unsafe sealed class AlignedBuffer<T> : IDisposable where T : unmanaged
{
    private void* _ptr;
    private readonly int _length;
    private readonly int _alignment;
    private readonly nuint _byteLength;
    private bool _disposed;

    private AlignedBuffer(void* ptr, int length, int alignment, nuint byteLength)
    {
        _ptr = ptr;
        _length = length;
        _alignment = alignment;
        _byteLength = byteLength;
    }

    ~AlignedBuffer()
    {
        Dispose(false);
    }

    public int Length => _length;
    public int Alignment => _alignment;
    public nuint ByteLength => _byteLength;
    public nint Address => (nint)_ptr;

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AlignedBuffer<T>));
            return new Span<T>(_ptr, _length);
        }
    }

    public Span<byte> Bytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AlignedBuffer<T>));
            return new Span<byte>(_ptr, checked((int)_byteLength));
        }
    }

    public static AlignedBuffer<T> Allocate(int length, int alignment, bool clear)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        alignment = SimdHelper.NormalizeAlignment(alignment);

        if (length == 0)
        {
            return new AlignedBuffer<T>(null, 0, alignment, 0);
        }

        nuint byteLength = checked((nuint)length * (nuint)sizeof(T));
        void* ptr = NativeMemory.AlignedAlloc(byteLength, (nuint)alignment);
        if (ptr is null) throw new OutOfMemoryException();
        if (clear)
        {
            NativeMemory.Clear(ptr, byteLength);
        }

        return new AlignedBuffer<T>(ptr, length, alignment, byteLength);
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AlignedBuffer<T>));
        if (_ptr is null || _byteLength == 0) return;
        NativeMemory.Clear(_ptr, _byteLength);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        void* ptr = _ptr;
        _ptr = null;
        if (ptr is not null)
        {
            NativeMemory.AlignedFree(ptr);
        }
    }
}
