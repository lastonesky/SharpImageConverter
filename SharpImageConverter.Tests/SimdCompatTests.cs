using System;
using System.Runtime.Intrinsics;
using SharpImageConverter.Core;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// <see cref="SimdCompat"/> 的指令级对拍测试。
    /// <para>
    /// 动机：本库的向量化原先全部建立在 x86 内在函数上，arm64 侧新增的 NEON 分派
    /// 属于"照文档翻译"。开发过程中确实踩过一次坑——NEON <c>EXT</c> 的方向写反，
    /// 导致 <c>pslldq</c>/<c>psrldq</c> 互换、PNG 去滤波产物错乱（6 个测试失败）。
    /// 单靠"上层功能测试变绿"不足以定位这类逐位错误，因此这里对每个原语单独写一份
    /// <strong>按 Intel SDM / ARM ARM 语义独立推导</strong>的标量参考实现，逐 lane 比对。
    /// </para>
    /// <para>
    /// 参考实现不引用被测代码，也不以"当前实现"为依据，因此能捕获分派写错、
    /// 方向写反、饱和区间取错、取模/归零语义混淆等问题。
    /// </para>
    /// </summary>
    public class SimdCompatTests
    {
        // ---------------------------------------------------------------- 工具

        private static T[] Rand<T>(int seed) where T : struct
        {
            int n = Vector128<T>.Count;
            var a = new T[n];
            uint x = (uint)seed * 2654435761u + 1u;
            for (int i = 0; i < n; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                object v = typeof(T) switch
                {
                    var t when t == typeof(byte) => (byte)(x >> 24),
                    var t when t == typeof(sbyte) => (sbyte)(x >> 24),
                    var t when t == typeof(short) => (short)(x >> 16),
                    var t when t == typeof(ushort) => (ushort)(x >> 16),
                    var t when t == typeof(int) => (int)x,
                    var t when t == typeof(uint) => x,
                    var t when t == typeof(long) => (long)(((ulong)x << 32) | (x >> 7)),
                    var t when t == typeof(ulong) => ((ulong)x << 32) | (x >> 7),
                    _ => throw new NotSupportedException(typeof(T).Name),
                };
                a[i] = (T)v;
            }
            return a;
        }

        private static Vector128<T> V<T>(T[] a) where T : struct
        {
            var v = Vector128<T>.Zero;
            for (int i = 0; i < Vector128<T>.Count; i++) v = v.WithElement(i, a[i]);
            return v;
        }

        private static T[] A<T>(Vector128<T> v) where T : struct
        {
            var a = new T[Vector128<T>.Count];
            for (int i = 0; i < a.Length; i++) a[i] = v.GetElement(i);
            return a;
        }

        private static void AssertEq<T>(T[] expected, Vector128<T> actual, string what) where T : struct
            => Assert.Equal(expected, A(actual));

        // 参考实现：pshufb —— bit7 置位则归零，否则索引为 mask & 0x0F
        private static byte[] PshufbRef(byte[] v, byte[] m)
        {
            var o = new byte[16];
            for (int i = 0; i < 16; i++)
                o[i] = (m[i] & 0x80) != 0 ? (byte)0 : v[m[i] & 0x0F];
            return o;
        }

        // 参考实现：punpckl*/punpckh* —— 按元素宽度在低/高半区内交织
        private static T[] UnpackRef<T>(T[] a, T[] b, bool high) where T : struct
        {
            int lanes = Vector128<T>.Count;
            int half = lanes / 2;
            int off = high ? half : 0;
            var o = new T[lanes];
            for (int i = 0; i < half; i++) { o[2 * i] = a[off + i]; o[2 * i + 1] = b[off + i]; }
            return o;
        }

        // ---------------------------------------------------------------- 查表

        /// <summary>
        /// pshufb 对掩码字节 16..127 取低 4 位作索引；NEON tbl 对这一区间返回 0。
        /// 这条差异是 arm64 移植最容易静默出错的地方，必须显式覆盖。
        /// </summary>
        [Fact]
        public void ShuffleBytes_MatchesPshufb_ForInRangeMasks()
        {
            for (int seed = 1; seed <= 32; seed++)
            {
                var v = Rand<byte>(seed);
                var m = new byte[16];
                var r = new Random(seed);
                for (int i = 0; i < 16; i++)
                    m[i] = r.Next(3) == 0 ? (byte)0x80 : (byte)r.Next(16);

                AssertEq(PshufbRef(v, m), SimdCompat.ShuffleBytes(V(v), V(m)), "in-range pshufb");
            }
        }

        /// <summary>越界掩码（16..127）：pshufb 折叠为低 4 位，归一化后 NEON 必须一致。</summary>
        [Fact]
        public void ShuffleBytes_MatchesPshufb_ForOutOfRangeMasks()
        {
            for (int seed = 1; seed <= 32; seed++)
            {
                var v = Rand<byte>(seed);
                var m = new byte[16];
                var r = new Random(seed + 1000);
                for (int i = 0; i < 16; i++)
                    m[i] = (byte)r.Next(256); // 覆盖 0..255 全域，含 16..127

                AssertEq(PshufbRef(v, m), SimdCompat.ShuffleBytes(V(v), V(m)), "out-of-range pshufb");
            }
        }

        /// <summary>文档化约束：Raw 版本只保证 [0,15] ∪ [128,255] 掩码与 pshufb 一致。</summary>
        [Fact]
        public void ShuffleBytesRaw_MatchesPshufb_WhenMaskIsWellFormed()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var v = Rand<byte>(seed);
                var m = new byte[16];
                var r = new Random(seed + 2000);
                for (int i = 0; i < 16; i++)
                    m[i] = r.Next(2) == 0 ? (byte)r.Next(16) : (byte)(0x80 | r.Next(16));

                AssertEq(PshufbRef(v, m), SimdCompat.ShuffleBytesRaw(V(v), V(m)), "well-formed raw pshufb");
            }
        }

        // ---------------------------------------------------------------- 交织

        [Fact]
        public void Unpack_MatchesPunpck_ForAllElementTypes()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var ab = Rand<byte>(seed); var bb = Rand<byte>(seed + 7);
                AssertEq(UnpackRef(ab, bb, false), SimdCompat.UnpackLow(V(ab), V(bb)), "punpcklbw");
                AssertEq(UnpackRef(ab, bb, true), SimdCompat.UnpackHigh(V(ab), V(bb)), "punpckhbw");

                var asb = Rand<sbyte>(seed); var bsb = Rand<sbyte>(seed + 7);
                AssertEq(UnpackRef(asb, bsb, false), SimdCompat.UnpackLow(V(asb), V(bsb)), "punpcklbw(sbyte)");
                AssertEq(UnpackRef(asb, bsb, true), SimdCompat.UnpackHigh(V(asb), V(bsb)), "punpckhbw(sbyte)");

                var as1 = Rand<short>(seed); var bs1 = Rand<short>(seed + 7);
                AssertEq(UnpackRef(as1, bs1, false), SimdCompat.UnpackLow(V(as1), V(bs1)), "punpcklwd");
                AssertEq(UnpackRef(as1, bs1, true), SimdCompat.UnpackHigh(V(as1), V(bs1)), "punpckhwd");

                var au1 = Rand<ushort>(seed); var bu1 = Rand<ushort>(seed + 7);
                AssertEq(UnpackRef(au1, bu1, false), SimdCompat.UnpackLow(V(au1), V(bu1)), "punpcklwd(ushort)");
                AssertEq(UnpackRef(au1, bu1, true), SimdCompat.UnpackHigh(V(au1), V(bu1)), "punpckhwd(ushort)");

                var ai = Rand<int>(seed); var bi = Rand<int>(seed + 7);
                AssertEq(UnpackRef(ai, bi, false), SimdCompat.UnpackLow(V(ai), V(bi)), "punpckldq");
                AssertEq(UnpackRef(ai, bi, true), SimdCompat.UnpackHigh(V(ai), V(bi)), "punpckhdq");

                var au = Rand<uint>(seed); var bu = Rand<uint>(seed + 7);
                AssertEq(UnpackRef(au, bu, false), SimdCompat.UnpackLow(V(au), V(bu)), "punpckldq(uint)");
                AssertEq(UnpackRef(au, bu, true), SimdCompat.UnpackHigh(V(au), V(bu)), "punpckhdq(uint)");

                var al = Rand<long>(seed); var bl = Rand<long>(seed + 7);
                AssertEq(UnpackRef(al, bl, false), SimdCompat.UnpackLow(V(al), V(bl)), "punpcklqdq");
                AssertEq(UnpackRef(al, bl, true), SimdCompat.UnpackHigh(V(al), V(bl)), "punpckhqdq");

                var aul = Rand<ulong>(seed); var bul = Rand<ulong>(seed + 7);
                AssertEq(UnpackRef(aul, bul, false), SimdCompat.UnpackLow(V(aul), V(bul)), "punpcklqdq(ulong)");
                AssertEq(UnpackRef(aul, bul, true), SimdCompat.UnpackHigh(V(aul), V(bul)), "punpckhqdq(ulong)");
            }
        }

        [Fact]
        public void UnpackLowHighBytes_MatchByteVariants()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a = Rand<byte>(seed); var b = Rand<byte>(seed + 5);
                AssertEq(UnpackRef(a, b, false), SimdCompat.UnpackLowBytes(V(a), V(b)), "UnpackLowBytes");
                AssertEq(UnpackRef(a, b, true), SimdCompat.UnpackHighBytes(V(a), V(b)), "UnpackHighBytes");
            }
        }

        // ---------------------------------------------------------------- 整字节移位

        /// <summary>
        /// psrldq / pslldq —— 整寄存器按字节平移，空出的字节补 0。
        /// 这正是当初把 NEON EXT 方向写反的地方，务必逐字节覆盖 n=1..15。
        /// </summary>
        [Fact]
        public void ByteShifts_MatchPsrldqAndPslldq()
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                var v = Rand<byte>(seed);
                for (byte n = 0; n < 16; n++)
                {
                    var left = new byte[16];
                    for (int i = 0; i < 16; i++) left[i] = i >= n ? v[i - n] : (byte)0;
                    AssertEq(left, SimdCompat.ShiftLeftBytes(V(v), n), $"pslldq n={n}");

                    var right = new byte[16];
                    for (int i = 0; i < 16; i++) right[i] = i + n < 16 ? v[i + n] : (byte)0;
                    AssertEq(right, SimdCompat.ShiftRightBytes(V(v), n), $"psrldq n={n}");
                }
            }
        }

        // ---------------------------------------------------------------- 饱和收窄

        /// <summary>packuswb：int16 → uint8，负值夹到 0、>255 夹到 255（无符号饱和）。</summary>
        [Fact]
        public void PackUnsignedSaturate_MatchesPackuswb()
        {
            short[] edge = { short.MinValue, -1, 0, 1, 127, 128, 254, 255, 256, 300, 1000, short.MaxValue, -300, 5, unchecked((short)60000), 42 };
            var lo = new short[8]; var hi = new short[8];
            Array.Copy(edge, 0, lo, 0, 8);
            Array.Copy(edge, 8, hi, 0, 8);

            var expected = new byte[16];
            for (int i = 0; i < 8; i++) expected[i] = (byte)Math.Clamp((int)lo[i], 0, 255);
            for (int i = 0; i < 8; i++) expected[8 + i] = (byte)Math.Clamp((int)hi[i], 0, 255);

            AssertEq(expected, SimdCompat.PackUnsignedSaturate(V(lo), V(hi)), "packuswb");
        }

        /// <summary>packssdw：int32 → int16，按有符号区间 [-32768, 32767] 饱和。</summary>
        [Fact]
        public void PackSignedSaturateInt32_MatchesPackssdw()
        {
            int[] edge = { int.MinValue, -100000, -32769, -32768, -1, 0, 32767, 32768, 100000, int.MaxValue, 5, -5, 65535, -65535, 1, -1 };
            var lo = new int[4]; var hi = new int[4];
            Array.Copy(edge, 0, lo, 0, 4);
            Array.Copy(edge, 4, hi, 0, 4);

            var expected = new short[8];
            for (int i = 0; i < 4; i++) expected[i] = (short)Math.Clamp(lo[i], short.MinValue, short.MaxValue);
            for (int i = 0; i < 4; i++) expected[4 + i] = (short)Math.Clamp(hi[i], short.MinValue, short.MaxValue);

            AssertEq(expected, SimdCompat.PackSignedSaturateInt32(V(lo), V(hi)), "packssdw");
        }

        // ---------------------------------------------------------------- 乘法

        /// <summary>pmullw：逐 16 位相乘只保留低 16 位（不饱和、不回绕到 32 位）。</summary>
        [Fact]
        public void MultiplyLowInt16_MatchesPmullw()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a = Rand<short>(seed); var b = Rand<short>(seed + 3);
                var e = new short[8];
                for (int i = 0; i < 8; i++) e[i] = unchecked((short)(a[i] * b[i]));
                AssertEq(e, SimdCompat.MultiplyLowInt16(V(a), V(b)), "pmullw");
            }
        }

        /// <summary>pmulld：逐 32 位相乘只保留低 32 位。</summary>
        [Fact]
        public void MultiplyLowInt32_MatchesPmulld()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a = Rand<int>(seed); var b = Rand<int>(seed + 3);
                var e = new int[4];
                for (int i = 0; i < 4; i++) e[i] = unchecked(a[i] * b[i]);
                AssertEq(e, SimdCompat.MultiplyLowInt32(V(a), V(b)), "pmulld");
            }
        }

        /// <summary>pmaddwd：两两相乘后按相邻对求和为 32 位（<b>不</b>饱和）。</summary>
        [Fact]
        public void MultiplyAddAdjacent_MatchesPmaddwd()
        {
            var a = new short[] { 32767, 32767, -32768, -32768, 1, -1, 1000, -1000 };
            var b = new short[] { 32767, 32767, -32768, -32768, -1, 1, 1000, 1000 };

            var e = new int[4];
            for (int i = 0; i < 4; i++) e[i] = unchecked(a[2 * i] * b[2 * i] + a[2 * i + 1] * b[2 * i + 1]);

            AssertEq(e, SimdCompat.MultiplyAddAdjacent(V(a), V(b)), "pmaddwd");

            // 随机对照
            for (int seed = 1; seed <= 16; seed++)
            {
                var ra = Rand<short>(seed); var rb = Rand<short>(seed + 11);
                var re = new int[4];
                for (int i = 0; i < 4; i++) re[i] = ra[2 * i] * rb[2 * i] + ra[2 * i + 1] * rb[2 * i + 1];
                AssertEq(re, SimdCompat.MultiplyAddAdjacent(V(ra), V(rb)), "pmaddwd random");
            }
        }

        /// <summary>phaddd：[a0+a1, a2+a3, b0+b1, b2+b3]。</summary>
        [Fact]
        public void HorizontalAddInt32_MatchesPhaddd()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a = Rand<int>(seed); var b = Rand<int>(seed + 9);
                var e = new[] {
                    unchecked(a[0] + a[1]), unchecked(a[2] + a[3]),
                    unchecked(b[0] + b[1]), unchecked(b[2] + b[3]),
                };
                AssertEq(e, SimdCompat.HorizontalAddInt32(V(a), V(b)), "phaddd");
            }
        }

        // ---------------------------------------------------------------- 绝对差之和

        /// <summary>psadbw：每 8 字节求 |a-b| 之和，结果放在 word lane 0 与 lane 4。</summary>
        [Fact]
        public void SumAbsoluteDifferences_MatchesPsadbw()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a = Rand<byte>(seed); var b = Rand<byte>(seed + 13);
                var e = new ushort[8];
                int s0 = 0, s1 = 0;
                for (int i = 0; i < 8; i++) s0 += Math.Abs(a[i] - b[i]);
                for (int i = 8; i < 16; i++) s1 += Math.Abs(a[i] - b[i]);
                e[0] = (ushort)s0; e[4] = (ushort)s1;
                AssertEq(e, SimdCompat.SumAbsoluteDifferences(V(a), V(b)), "psadbw");
            }
        }

        // ---------------------------------------------------------------- 逐字节算术 / 位运算

        [Fact]
        public void ByteArithmetic_WrapsModulo256()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a = Rand<byte>(seed); var b = Rand<byte>(seed + 21);

                var add = new byte[16]; var sub = new byte[16];
                var or = new byte[16]; var and = new byte[16]; var xor = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    add[i] = unchecked((byte)(a[i] + b[i]));
                    sub[i] = unchecked((byte)(a[i] - b[i]));
                    or[i] = (byte)(a[i] | b[i]);
                    and[i] = (byte)(a[i] & b[i]);
                    xor[i] = (byte)(a[i] ^ b[i]);
                }

                AssertEq(add, SimdCompat.AddBytes(V(a), V(b)), "paddb");
                AssertEq(sub, SimdCompat.SubtractBytes(V(a), V(b)), "psubb");
                AssertEq(or, SimdCompat.OrBytes(V(a), V(b)), "por");
                AssertEq(and, SimdCompat.AndBytes(V(a), V(b)), "pand");
                AssertEq(xor, SimdCompat.XorBytes(V(a), V(b)), "pxor");
            }
        }

        [Fact]
        public void WordAndDwordArithmetic_WrapModulo2PowN()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var a16 = Rand<short>(seed); var b16 = Rand<short>(seed + 2);
                var e16 = new short[8];
                for (int i = 0; i < 8; i++) e16[i] = unchecked((short)(a16[i] + b16[i]));
                AssertEq(e16, SimdCompat.AddInt16(V(a16), V(b16)), "paddw");

                var au = Rand<ushort>(seed); var bu = Rand<ushort>(seed + 2);
                var eu = new ushort[8];
                for (int i = 0; i < 8; i++) eu[i] = unchecked((ushort)(au[i] - bu[i]));
                AssertEq(eu, SimdCompat.SubtractUInt16(V(au), V(bu)), "psubw(ushort)");

                var a32 = Rand<int>(seed); var b32 = Rand<int>(seed + 2);
                var e32 = new int[4];
                for (int i = 0; i < 4; i++) e32[i] = unchecked(a32[i] + b32[i]);
                AssertEq(e32, SimdCompat.AddInt32(V(a32), V(b32)), "paddd");
            }
        }

        // ---------------------------------------------------------------- 算术移位

        [Fact]
        public void LogicalShifts_FillWithZero()
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                var v16 = Rand<short>(seed);
                var vu = Rand<ushort>(seed);
                var v32 = Rand<int>(seed);

                for (byte c = 0; c < 16; c++)
                {
                    var e16 = new short[8];
                    for (int i = 0; i < 8; i++) e16[i] = unchecked((short)((ushort)v16[i] >> c));
                    AssertEq(e16, SimdCompat.ShiftRightLogicalInt16(V(v16), c), $"psrlw c={c}");

                    var eu = new ushort[8];
                    for (int i = 0; i < 8; i++) eu[i] = (ushort)(vu[i] >> c);
                    AssertEq(eu, SimdCompat.ShiftRightLogicalUInt16(V(vu), c), $"psrlw(ushort) c={c}");

                    var e32 = new int[4];
                    for (int i = 0; i < 4; i++) e32[i] = unchecked((int)((uint)v32[i] >> c));
                    AssertEq(e32, SimdCompat.ShiftRightLogicalInt32(V(v32), c), $"psrld c={c}");
                }

                for (byte c = 0; c < 16; c++)
                {
                    var l16 = new ushort[8];
                    for (int i = 0; i < 8; i++) l16[i] = unchecked((ushort)(vu[i] << c));
                    AssertEq(l16, SimdCompat.ShiftLeftLogicalUInt16(V(vu), c), $"psllw c={c}");

                    var l32 = new int[4];
                    for (int i = 0; i < 4; i++) l32[i] = unchecked(v32[i] << c);
                    AssertEq(l32, SimdCompat.ShiftLeftLogicalInt32(V(v32), c), $"pslld c={c}");
                }
            }
        }

        /// <summary>psraw / psrad：算术右移必须做符号扩展。</summary>
        [Fact]
        public void ArithmeticShifts_SignExtend()
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                var v16 = Rand<short>(seed);
                var v32 = Rand<int>(seed);
                v16[0] = short.MinValue; v16[1] = -1;
                v32[0] = int.MinValue; v32[1] = -1;

                for (byte c = 0; c < 16; c++)
                {
                    var e16 = new short[8];
                    for (int i = 0; i < 8; i++) e16[i] = unchecked((short)(v16[i] >> c));
                    AssertEq(e16, SimdCompat.ShiftRightArithmeticInt16(V(v16), c), $"psraw c={c}");

                    var e32 = new int[4];
                    for (int i = 0; i < 4; i++) e32[i] = v32[i] >> c;
                    AssertEq(e32, SimdCompat.ShiftRightArithmeticInt32(V(v32), c), $"psrad c={c}");
                }
            }
        }

        // ---------------------------------------------------------------- 加宽

        [Fact]
        public void WidenBytes_ZeroExtends()
        {
            for (int seed = 1; seed <= 16; seed++)
            {
                var v = Rand<byte>(seed);
                var lo = new ushort[8]; var hi = new ushort[8];
                for (int i = 0; i < 8; i++) { lo[i] = v[i]; hi[i] = v[8 + i]; }
                AssertEq(lo, SimdCompat.WidenLowerBytes(V(v)), "punpcklbw+0");
                AssertEq(hi, SimdCompat.WidenUpperBytes(V(v)), "punpckhbw+0");
            }
        }

        // ---------------------------------------------------------------- 最值 / 比较

        [Fact]
        public void MinMaxBytes_AreUnsigned()
        {
            var a = new byte[] { 0, 255, 1, 254, 128, 127, 10, 200, 5, 250, 3, 4, 99, 100, 0, 255 };
            var b = new byte[] { 255, 0, 2, 253, 127, 128, 200, 10, 250, 5, 4, 3, 100, 99, 255, 0 };

            var mn = new byte[16]; var mx = new byte[16];
            for (int i = 0; i < 16; i++) { mn[i] = Math.Min(a[i], b[i]); mx[i] = Math.Max(a[i], b[i]); }

            AssertEq(mn, SimdCompat.MinBytes(V(a), V(b)), "pminub");
            AssertEq(mx, SimdCompat.MaxBytes(V(a), V(b)), "pmaxub");
        }

        [Fact]
        public void MinMaxUInt16_AreUnsigned()
        {
            var a = new ushort[] { 0, 65535, 1, 65534, 32768, 32767, 10, 60000 };
            var b = new ushort[] { 65535, 0, 2, 65533, 32767, 32768, 60000, 10 };

            var mn = new ushort[8]; var mx = new ushort[8];
            for (int i = 0; i < 8; i++) { mn[i] = Math.Min(a[i], b[i]); mx[i] = Math.Max(a[i], b[i]); }

            AssertEq(mn, SimdCompat.MinUInt16(V(a), V(b)), "pminuw");
            AssertEq(mx, SimdCompat.MaxUInt16(V(a), V(b)), "pmaxuw");
        }

        [Fact]
        public void CompareEqualInt32_ProducesAllOnesMask()
        {
            var a = new[] { 1, -2, 3, int.MinValue };
            var b = new[] { 1, 2, 3, int.MaxValue };
            var e = new[] { -1, 0, -1, 0 };
            AssertEq(e, SimdCompat.CompareEqualInt32(V(a), V(b)), "pcmpeqd");
        }

        [Fact]
        public void CompareGreaterThanInt16_IsSigned()
        {
            var a = new short[] { 1, -1, 32767, -32768, 0, 5, -5, 100 };
            var b = new short[] { 0, 0, 32767, 0, 0, -5, 5, 100 };
            var e = new short[8];
            for (int i = 0; i < 8; i++) e[i] = a[i] > b[i] ? (short)-1 : (short)0;
            AssertEq(e, SimdCompat.CompareGreaterThanInt16(V(a), V(b)), "pcmpgtw");
        }

        // ---------------------------------------------------------------- 浮点截断

        /// <summary>cvttps2dq：向零截断（而非就近取整）；越界给出 0x80000000。</summary>
        [Fact]
        public void ConvertToInt32WithTruncation_TruncatesTowardZero()
        {
            var f = new[] { 1.9f, -1.9f, 0.5f, -0.5f };
            var e = new[] { 1, -1, 0, 0 };
            AssertEq(e, SimdCompat.ConvertToInt32WithTruncation(Vector128.Create(f[0], f[1], f[2], f[3])), "cvttps2dq");
        }

        // ---------------------------------------------------------------- 载入 / 存储

        [Fact]
        public void LoadStoreHelpers_RoundTrip()
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                var src = Rand<byte>(seed);
                AssertEq(src, SimdCompat.LoadBytes(ref src[0], 0), "LoadBytes");

                var dst = new byte[16];
                SimdCompat.StoreBytes(V(src), ref dst[0], 0);
                Assert.Equal(src, dst);

                var sh = Rand<short>(seed);
                AssertEq(sh, SimdCompat.LoadInt16(ref sh[0], 0), "LoadInt16");
                var shDst = new short[8];
                SimdCompat.StoreInt16(V(sh), ref shDst[0], 0);
                Assert.Equal(sh, shDst);

                var i32 = Rand<int>(seed);
                AssertEq(i32, SimdCompat.LoadInt32(ref i32[0], 0), "LoadInt32");
                var i32Dst = new int[4];
                SimdCompat.StoreInt32(V(i32), ref i32Dst[0], 0);
                Assert.Equal(i32, i32Dst);
            }
        }
    }
}
