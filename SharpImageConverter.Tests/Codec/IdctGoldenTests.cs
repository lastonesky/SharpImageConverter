using System;
using System.IO;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// IDCT 舍入契约黄金测试：标量 FastIDCT 与 SIMD 管线都必须与 libjpeg-turbo 的
/// jpeg_idct_islow（jidctint.c）逐位一致。参考实现逐语句移植自自动生成的 LjIdctIslow
/// （SharpImage 仓库，一 C 语句对一 C# 语句），其正确性已在 143MP 语料上对拍 libjpeg-turbo。
/// </summary>
public class IdctGoldenTests
{
    /// <summary>libjpeg jpeg_idct_islow 的 8-bit 逐语句移植（P1 = PASS1_BITS = 2）。</summary>
    private static void IslowReference(ReadOnlySpan<short> coef, ReadOnlySpan<ushort> q, Span<byte> dest, int stride)
    {
        const int P1 = 2;
        const int Pass2Shift = 13 + P1 + 3;
        const int Pass2Half = 1 << (Pass2Shift - 1);
        unchecked
        {
            Span<int> ws = stackalloc int[64];
            for (int ctr = 0; ctr < 8; ctr++)
            {
                int ip = ctr, qp = ctr, wp = ctr;
                if (coef[ip + 8] == 0 && coef[ip + 16] == 0 && coef[ip + 24] == 0 && coef[ip + 32] == 0 &&
                    coef[ip + 40] == 0 && coef[ip + 48] == 0 && coef[ip + 56] == 0)
                {
                    int dcval = coef[ip] * q[qp] << P1;
                    for (int k = 0; k < 8; k++) ws[wp + 8 * k] = dcval;
                    continue;
                }

                int z2 = coef[ip + 16] * q[qp + 16];
                int z3 = coef[ip + 48] * q[qp + 48];
                int z1 = (z2 + z3) * 4433;
                int tmp2 = z1 + z3 * -15137;
                int tmp3 = z1 + z2 * 6270;
                z2 = coef[ip] * q[qp];
                z3 = coef[ip + 32] * q[qp + 32];
                int tmp0 = (z2 + z3) << 13;
                int tmp1 = (z2 - z3) << 13;
                int tmp10 = tmp0 + tmp3;
                int tmp13 = tmp0 - tmp3;
                int tmp11 = tmp1 + tmp2;
                int tmp12 = tmp1 - tmp2;
                tmp0 = coef[ip + 56] * q[qp + 56];
                tmp1 = coef[ip + 40] * q[qp + 40];
                tmp2 = coef[ip + 24] * q[qp + 24];
                tmp3 = coef[ip + 8] * q[qp + 8];
                z1 = tmp0 + tmp3;
                z2 = tmp1 + tmp2;
                z3 = tmp0 + tmp2;
                int z4 = tmp1 + tmp3;
                int z5 = (z3 + z4) * 9633;
                tmp0 *= 2446;
                tmp1 *= 16819;
                tmp2 *= 25172;
                tmp3 *= 12299;
                z1 *= -7373;
                z2 *= -20995;
                z3 *= -16069;
                z4 *= -3196;
                z3 += z5;
                z4 += z5;
                tmp0 += z1 + z3;
                tmp1 += z2 + z4;
                tmp2 += z2 + z3;
                tmp3 += z1 + z4;
                const int Pass1Shift = 13 - P1;
                const int Pass1Half = 1 << (Pass1Shift - 1);
                ws[wp] = (tmp10 + tmp3 + Pass1Half) >> Pass1Shift;
                ws[wp + 56] = (tmp10 - tmp3 + Pass1Half) >> Pass1Shift;
                ws[wp + 8] = (tmp11 + tmp2 + Pass1Half) >> Pass1Shift;
                ws[wp + 48] = (tmp11 - tmp2 + Pass1Half) >> Pass1Shift;
                ws[wp + 16] = (tmp12 + tmp1 + Pass1Half) >> Pass1Shift;
                ws[wp + 40] = (tmp12 - tmp1 + Pass1Half) >> Pass1Shift;
                ws[wp + 24] = (tmp13 + tmp0 + Pass1Half) >> Pass1Shift;
                ws[wp + 32] = (tmp13 - tmp0 + Pass1Half) >> Pass1Shift;
            }

            for (int ctr = 0; ctr < 8; ctr++)
            {
                int wp = ctr * 8;
                int op = ctr * stride;
                if (ws[wp + 1] == 0 && ws[wp + 2] == 0 && ws[wp + 3] == 0 && ws[wp + 4] == 0 &&
                    ws[wp + 5] == 0 && ws[wp + 6] == 0 && ws[wp + 7] == 0)
                {
                    int dcval = Clamp255(((ws[wp] + (1 << (P1 + 2))) >> (P1 + 3)) + 128);
                    for (int k = 0; k < 8; k++) dest[op + k] = (byte)dcval;
                    continue;
                }

                int z2b = ws[wp + 2];
                int z3b = ws[wp + 6];
                int z1b = (z2b + z3b) * 4433;
                int tmp2b = z1b + z3b * -15137;
                int tmp3b = z1b + z2b * 6270;
                int tmp0b = (ws[wp] + ws[wp + 4]) << 13;
                int tmp1b = (ws[wp] - ws[wp + 4]) << 13;
                int tmp10b = tmp0b + tmp3b;
                int tmp13b = tmp0b - tmp3b;
                int tmp11b = tmp1b + tmp2b;
                int tmp12b = tmp1b - tmp2b;
                tmp0b = ws[wp + 7];
                tmp1b = ws[wp + 5];
                tmp2b = ws[wp + 3];
                tmp3b = ws[wp + 1];
                z1b = tmp0b + tmp3b;
                z2b = tmp1b + tmp2b;
                z3b = tmp0b + tmp2b;
                int z4b = tmp1b + tmp3b;
                int z5b = (z3b + z4b) * 9633;
                tmp0b *= 2446;
                tmp1b *= 16819;
                tmp2b *= 25172;
                tmp3b *= 12299;
                z1b *= -7373;
                z2b *= -20995;
                z3b *= -16069;
                z4b *= -3196;
                z3b += z5b;
                z4b += z5b;
                tmp0b += z1b + z3b;
                tmp1b += z2b + z4b;
                tmp2b += z2b + z3b;
                tmp3b += z1b + z4b;
                dest[op] = (byte)Clamp255(((tmp10b + tmp3b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 7] = (byte)Clamp255(((tmp10b - tmp3b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 1] = (byte)Clamp255(((tmp11b + tmp2b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 6] = (byte)Clamp255(((tmp11b - tmp2b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 2] = (byte)Clamp255(((tmp12b + tmp1b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 5] = (byte)Clamp255(((tmp12b - tmp1b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 3] = (byte)Clamp255(((tmp13b + tmp0b + Pass2Half) >> Pass2Shift) + 128);
                dest[op + 4] = (byte)Clamp255(((tmp13b - tmp0b + Pass2Half) >> Pass2Shift) + 128);
            }
        }

        static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }

    private static void CompareAll(short[] coef, ushort[] q, string context)
    {
        Span<byte> reference = stackalloc byte[64];
        Span<byte> scalar = stackalloc byte[64];
        Span<byte> simd = stackalloc byte[64];
        IslowReference(coef, q, reference, 8);
        FastIDCT.Transform(coef, q, scalar, 8);
        unsafe
        {
            fixed (byte* p = simd)
            {
                SimdJpegPipeline.Idct8x8ToPlane(coef, q, p, 8);
            }
        }

        for (int i = 0; i < 64; i++)
        {
            Assert.True(scalar[i] == reference[i], $"{context}: scalar FastIDCT 与 islow 参考不一致，索引 {i}，{scalar[i]} != {reference[i]}");
            Assert.True(simd[i] == reference[i], $"{context}: SIMD IDCT 与 islow 参考不一致，索引 {i}，{simd[i]} != {reference[i]}");
        }
    }

    [Fact]
    public void RandomBlocks_MatchIslow()
    {
        var rng = new Random(1234);
        ushort[] q = new ushort[64];
        short[] coef = new short[64];
        for (int iter = 0; iter < 20000; iter++)
        {
            // 合法域：T.81 对 8-bit 规定反量化系数 |coef×q| ≤ 2^11 量级，
            // 同时保证 SIMD 16 位乘法不回绕；超域行为在 libjpeg 各构建间本就不确定。
            for (int i = 0; i < 64; i++) q[i] = (ushort)rng.Next(1, 256);
            for (int i = 0; i < 64; i++)
            {
                int limit = 1024 / q[i];
                coef[i] = (short)rng.Next(-limit, limit + 1);
            }
            if (iter % 3 == 1) Array.Clear(coef, 1, 63); // DC-only 快捷路径
            CompareAll(coef, q, $"随机块 #{iter}");
        }
    }

    [Fact]
    public void ExtremeBlocks_MatchIslow()
    {
        ushort[] q = new ushort[64];
        short[] coef = new short[64];
        var rng = new Random(99);
        for (int i = 0; i < 64; i++) q[i] = 255;
        for (int i = 0; i < 64; i++) coef[i] = (short)rng.Next(-4, 5); // 4×255 = 1020：真实编码器域（工作区不溢出16位）
        CompareAll(coef, q, "q255 × ±4（真实编码器域）");
        for (int i = 0; i < 64; i++) coef[i] = -4;
        CompareAll(coef, q, "q255 × -4 全块");
        Array.Fill(q, (ushort)1);
        for (int i = 0; i < 64; i++) coef[i] = 1023;
        CompareAll(coef, q, "q1 × +1023 全块");
    }

    [Fact]
    public void RealJpegCoefficients_MatchIslow()
    {
        // 真实语料解析出的系数逐块对拍——覆盖真实编码器产生的全部数值分布。
        string examples = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples");
        foreach (string path in Directory.GetFiles(examples, "*.jp*g"))
        {
            byte[] data = File.ReadAllBytes(path);
            JpegFrameState state = JpegDecoder.ParseForTranscode(data, out _);
            try
            {
                ComponentState[] components = state.Components;
                for (int ci = 0; ci < components.Length; ci++)
                {
                    ComponentState comp = components[ci];
                    ushort[] q = state.QuantTables[comp.QuantTableId].Table;
                    for (int b = 0; b < comp.BlocksX * comp.BlocksY; b++)
                    {
                        CompareAll(comp.GetBlockSpan(b).ToArray(), q, $"{Path.GetFileName(path)} comp{ci} block{b}");
                    }
                }
            }
            finally
            {
                state.DisposeResources();
            }
        }
    }

    [Fact]
    public void TempProbeExtreme()
    {
        var rng = new Random(99);
        ushort[] q = new ushort[64];
        short[] coef = new short[64];
        for (int i = 0; i < 64; i++) q[i] = 255;
        for (int i = 0; i < 64; i++) coef[i] = (short)rng.Next(-8, 9);
        Span<byte> r = stackalloc byte[64];
        Span<byte> s = stackalloc byte[64];
        Span<byte> v = stackalloc byte[64];
        IslowReference(coef, q, r, 8);
        FastIDCT.Transform(coef, q, s, 8);
        unsafe { fixed (byte* p = v) { SimdJpegPipeline.Idct8x8ToPlane(coef, q, p, 8); } }
        Console.WriteLine("COEF: " + string.Join(",", coef.ToArray()));
        Console.WriteLine("REF: " + string.Join(",", r.ToArray()));
        Console.WriteLine("SCA: " + string.Join(",", s.ToArray()));
        Console.WriteLine("SIMD: " + string.Join(",", v.ToArray()));
    }

    [Fact]
    public void TempProbeFidelityBlocks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench", "fidelity");
        if (!Directory.Exists(dir)
            || !File.Exists(Path.Combine(dir, "photo1_444_q50.jpg"))
            || !File.Exists(Path.Combine(dir, "grad444_q50.jpg")))
        {
            // 本测试是临时探针：依赖本机 %TEMP%\bench\fidelity 下跑 bench 留下的语料，
            // 机器间不保证存在（CI / 清理过 TEMP 的机器都没有），缺语料时跳过而不是失败。
            Assert.Skip("缺少 %TEMP%\\bench\\fidelity 语料（仅本机调试用），跳过");
        }
        foreach (string name in new[] { "photo1_444_q50.jpg", "grad444_q50.jpg" })
        {
            byte[] data = File.ReadAllBytes(Path.Combine(dir, name));
            JpegFrameState state = JpegDecoder.ParseForTranscode(data, out _);
            try
            {
                int badScalar = 0, badSimd = 0, shown = 0;
                Span<byte> r = stackalloc byte[64];
                Span<byte> s = stackalloc byte[64];
                Span<byte> v = stackalloc byte[64];
                for (int ci = 0; ci < state.Components.Length; ci++)
                {
                    ComponentState comp = state.Components[ci];
                    ushort[] q = state.QuantTables[comp.QuantTableId].Table;
                    for (int b = 0; b < comp.BlocksX * comp.BlocksY; b++)
                    {
                        short[] c = comp.GetBlockSpan(b).ToArray();
                        IslowReference(c, q, r, 8);
                        FastIDCT.Transform(c, q, s, 8);
                        unsafe { fixed (byte* p = v) { SimdJpegPipeline.Idct8x8ToPlane(c, q, p, 8); } }
                        bool sBad = false, vBad = false;
                        for (int i = 0; i < 64; i++) { if (s[i] != r[i]) sBad = true; if (v[i] != r[i]) vBad = true; }
                        if (sBad) badScalar++;
                        if (vBad) badSimd++;
                        if (vBad && shown < 2)
                        {
                            shown++;
                            Console.WriteLine($"{name} comp{ci} block{b} SIMD≠参考");
                            Console.WriteLine("COEF: " + string.Join(",", c));
                            Console.WriteLine("REF : " + string.Join(",", r.ToArray()));
                            Console.WriteLine("SIMD: " + string.Join(",", v.ToArray()));
                        }
                    }
                }
                Console.WriteLine($"{name}: scalarBad={badScalar} simdBad={badSimd}");
            }
            finally { state.DisposeResources(); }
        }
    }
}
