namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// VP8 正向变换：4×4 正 DCT 与 4×4 正 Walsh-Hadamard（Y2 块）。
///
/// 采用与 <see cref="Vp8Idct"/> 逆变换严格伴随（adjoint）的整数核：
/// 逆变换的 1-D 核为
/// <c>B = [[1, 1.3066, 1, 0.5412], [1, 0.5412, -1, -1.3066],
/// [1, -0.5412, -1, 1.3066], [1, -1.3066, 1, -0.5412]]</c>（行 = 像素，列 = 频率），
/// 且各列正交、模长平方为 4，因此最优正向变换为 <c>C = (1/2)·Bᵀ·f·B</c>。
/// 这样“正变换 → 量化 → 反量化 → 逆变换”对常值块可精确还原（DC = 8·x），
/// 与解码端 <c>&gt;&gt; 3</c> 的输出定标一致。
/// 1.3066 与 0.5412 用 libwebp 相同的 16 位定点（MUL1 / MUL2），保证整数量化行为稳定。
/// </summary>
internal static class Vp8ForwardTransform
{
    // 与 Vp8Idct 完全相同的定点常量（libwebp src/dsp/dsp.h）。
    private const int kC1 = 20091;
    private const int kC2 = 35468;

    private static int Mul1(int a) => ((a * kC1) >> 16) + a;
    private static int Mul2(int a) => (a * kC2) >> 16;

    /// <summary>
    /// 4×4 正 DCT：输入 16 个残差（光栅顺序），输出 16 个系数（光栅顺序）。
    /// </summary>
    internal static void Dct4x4(ReadOnlySpan<int> residual, Span<int> coeffs)
    {
        Span<int> tmp = stackalloc int[16];

        // 垂直方向：对每一列做 1-D 正向核（等价于 Bᵀ 作用于像素下标）。
        for (int x = 0; x < 4; x++)
        {
            int f0 = residual[x];
            int f1 = residual[4 + x];
            int f2 = residual[8 + x];
            int f3 = residual[12 + x];
            tmp[x] = f0 + f1 + f2 + f3;
            tmp[4 + x] = Mul1(f0) + Mul2(f1) - Mul2(f2) - Mul1(f3);
            tmp[8 + x] = f0 - f1 - f2 + f3;
            tmp[12 + x] = Mul2(f0) - Mul1(f1) + Mul1(f2) - Mul2(f3);
        }

        // 水平方向：对每一行做同样的 1-D 核，最后整体除以 2（伴随变换的 1/2 定标）。
        for (int row = 0; row < 4; row++)
        {
            int o = row * 4;
            int f0 = tmp[o];
            int f1 = tmp[o + 1];
            int f2 = tmp[o + 2];
            int f3 = tmp[o + 3];
            coeffs[o] = (f0 + f1 + f2 + f3 + 1) >> 1;
            coeffs[o + 1] = (Mul1(f0) + Mul2(f1) - Mul2(f2) - Mul1(f3) + 1) >> 1;
            coeffs[o + 2] = (f0 - f1 - f2 + f3 + 1) >> 1;
            coeffs[o + 3] = (Mul2(f0) - Mul1(f1) + Mul1(f2) - Mul2(f3) + 1) >> 1;
        }
    }

    // 逆 WHT 的两个 4×4 Hadamard 核（与 Vp8Idct.TransformWHT 的运算顺序一致）。
    // Mv 为第一遍（列方向）的核，Hp 为第二遍（行方向）的核。
    private static readonly int[] Mv =
    {
        1, 1, 1, 1,
        1, -1, 1, -1,
        1, -1, -1, 1,
        1, 1, -1, -1,
    };

    private static readonly int[] Hp =
    {
        1, 1, 1, 1,
        1, 1, -1, -1,
        1, -1, -1, 1,
        1, -1, 1, -1,
    };

    /// <summary>
    /// 4×4 正 WHT：输入 16 个亮度块的 DC 系数（块光栅顺序），输出 Y2 块的 16 个系数。
    /// 由 <c>逆WHT = (1/8)·Mv·X·Hpᵀ</c> 得伴随正向 <c>C = (1/2)·Mvᵀ·X·Hp</c>。
    /// </summary>
    internal static void Wht4x4(ReadOnlySpan<int> dc, Span<int> coeffs)
    {
        Span<int> tmp = stackalloc int[16];

        // 第一遍：沿行方向用 Hp（Hp 对称，Hpᵀ = Hp）。
        for (int i = 0; i < 4; i++)
        {
            int o = i * 4;
            for (int j = 0; j < 4; j++)
            {
                int s = 0;
                for (int l = 0; l < 4; l++)
                    s += Hp[l * 4 + j] * dc[o + l];
                tmp[o + j] = s;
            }
        }

        // 第二遍：沿列方向用 Mvᵀ（列 a 取 Mv 的第 a 列），最后除以 2。
        for (int a = 0; a < 4; a++)
        {
            for (int j = 0; j < 4; j++)
            {
                int s = 0;
                for (int i = 0; i < 4; i++)
                    s += Mv[i * 4 + a] * tmp[i * 4 + j];
                coeffs[a * 4 + j] = (s + 1) >> 1;
            }
        }
    }
}
