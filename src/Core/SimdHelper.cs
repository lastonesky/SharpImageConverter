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

        // 首选 128 位整字节加法：一条 paddb 处理 16 字节，天然回绕。
        if (Sse2.IsSupported)
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
        else if (Vector.IsHardwareAccelerated && length >= Vector<byte>.Count)
        {
            int simd = Vector<byte>.Count;
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

    /// <summary>
    /// 就地把 RGB24 缓冲区转为灰度：y = (77*R + 150*G + 29*B) &gt;&gt; 8，三通道写入同一值。
    /// </summary>
    public static void GrayscaleRgb24InPlace(Span<byte> rgb)
    {
        // 每批处理 16 个像素（48 字节）
        int blocks = rgb.Length / 48;
        int i = 0;

        if (Ssse3.IsSupported && blocks > 0)
        {
            ref byte r = ref MemoryMarshal.GetReference(rgb);
            Vector128<byte> zero = Vector128<byte>.Zero;
            Vector128<short> w77 = Vector128.Create((short)77);
            Vector128<short> w150 = Vector128.Create((short)150);
            Vector128<short> w29 = Vector128.Create((short)29);

            for (int b = 0; b < blocks; b++)
            {
                nuint o = (nuint)(b * 48);
                Vector128<byte> a = Vector128.LoadUnsafe(ref r, o);
                Vector128<byte> c = Vector128.LoadUnsafe(ref r, o + 16);
                Vector128<byte> d = Vector128.LoadUnsafe(ref r, o + 32);

                Vector128<byte> rv = Sse2.Or(
                    Sse2.Or(Ssse3.Shuffle(a, DeinterleaveRFromA), Ssse3.Shuffle(c, DeinterleaveRFromB)),
                    Ssse3.Shuffle(d, DeinterleaveRFromC));
                Vector128<byte> gv = Sse2.Or(
                    Sse2.Or(Ssse3.Shuffle(a, DeinterleaveGFromA), Ssse3.Shuffle(c, DeinterleaveGFromB)),
                    Ssse3.Shuffle(d, DeinterleaveGFromC));
                Vector128<byte> bv = Sse2.Or(
                    Sse2.Or(Ssse3.Shuffle(a, DeinterleaveBFromA), Ssse3.Shuffle(c, DeinterleaveBFromB)),
                    Ssse3.Shuffle(d, DeinterleaveBFromC));

                // 字节 -> ushort，16 位定点运算即可容纳 (77+150+29)*255 = 65280
                Vector128<ushort> grayLo = GrayCombine(
                    Sse2.UnpackLow(rv, zero).AsUInt16(),
                    Sse2.UnpackLow(gv, zero).AsUInt16(),
                    Sse2.UnpackLow(bv, zero).AsUInt16(),
                    w77, w150, w29);
                Vector128<ushort> grayHi = GrayCombine(
                    Sse2.UnpackHigh(rv, zero).AsUInt16(),
                    Sse2.UnpackHigh(gv, zero).AsUInt16(),
                    Sse2.UnpackHigh(bv, zero).AsUInt16(),
                    w77, w150, w29);

                // packuswb 只接受 short 输入；灰度值域 [0,255]，饱和不会截断有效数据
                Vector128<byte> gray = Sse2.PackUnsignedSaturate(grayLo.AsInt16(), grayHi.AsInt16());

                Vector128.StoreUnsafe(Ssse3.Shuffle(gray, GrayExpand0), ref r, o);
                Vector128.StoreUnsafe(Ssse3.Shuffle(gray, GrayExpand1), ref r, o + 16);
                Vector128.StoreUnsafe(Ssse3.Shuffle(gray, GrayExpand2), ref r, o + 32);
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
        // Sse2.MultiplyLow 只有 short 重载；两个操作数均 <= 255，
        // 乘积 <= 38250，低 16 位与无符号乘法完全一致，因此这里按 16 位回绕语义是安全的。
        Vector128<short> t = Sse2.MultiplyLow(r.AsInt16(), w77);
        t = Sse2.Add(t, Sse2.MultiplyLow(g.AsInt16(), w150));
        t = Sse2.Add(t, Sse2.MultiplyLow(b.AsInt16(), w29));
        return Sse2.ShiftRightLogical(t.AsUInt16(), 8);
    }

    /// <summary>
    /// Gray8 -> RGB24（每字节复制 3 次）。要求 rgb.Length == gray.Length * 3。
    /// </summary>
    public static void ExpandGrayToRgb(ReadOnlySpan<byte> gray, Span<byte> rgb)
    {
        int n = gray.Length;
        int i = 0;

        if (Ssse3.IsSupported)
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
                    Vector128.StoreUnsafe(Ssse3.Shuffle(g, GrayExpand0), ref dst, o);
                    Vector128.StoreUnsafe(Ssse3.Shuffle(g, GrayExpand1), ref dst, o + 16);
                    Vector128.StoreUnsafe(Ssse3.Shuffle(g, GrayExpand2), ref dst, o + 32);
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

        if (Ssse3.IsSupported)
        {
            // 每批 4 像素：读 16 字节，写 12 字节
            int blocks = rgba.Length / 16;
            if (blocks > 0)
            {
                ref byte src = ref MemoryMarshal.GetReference(rgba);
                ref byte dst = ref MemoryMarshal.GetReference(rgb);
                for (int b = 0; b < blocks; b++)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref src, (nuint)(b * 16));
                    Vector128<byte> packed = Ssse3.Shuffle(v, RgbaToRgbShuffle);
                    int o = b * 12;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o), packed.AsUInt32().GetElement(0));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + 4), packed.AsUInt32().GetElement(1));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, o + 8), packed.AsUInt32().GetElement(2));
                }
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

        if (Ssse3.IsSupported)
        {
            ref byte src = ref MemoryMarshal.GetReference(rgb);
            ref byte dst = ref MemoryMarshal.GetReference(rgba);
            // 每批 4 像素：读 12 字节（需要 16 字节可读才安全），写 16 字节
            int b = 0;
            while (b * 12 + 16 <= rgb.Length)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref src, (nuint)(b * 12));
                Vector128<byte> expanded = Sse2.Or(Ssse3.Shuffle(v, RgbToRgbaShuffle), RgbToRgbaAlpha);
                Vector128.StoreUnsafe(expanded, ref dst, (nuint)(b * 16));
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
