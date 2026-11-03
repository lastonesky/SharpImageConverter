// VP8 帧内预测器（intra predictors）的纯托管移植。
// 逐函数忠实移植 libwebp src/dsp/dec.c 的 plain-C 参考实现（非 SIMD），
// 对应 RFC 6386 第 12 章（Intra Prediction）。
// 坐标约定与 C 代码一致：off 为当前块左上角下标，stride 为行距；
// C 中 dst[-BPS] 对应此处的 dst[off - stride]，dst[-1] 对应 dst[off - 1]。

namespace SharpImageConverter.Formats.Webp;

internal static class Vp8IntraPred
{
    // mode 编号与 RFC 6386 一致：0=DC, 1=TM, 2=V(垂直), 3=H(水平)。
    // hasTop=false 表示该宏块位于图像首行（上边界不可用）；
    // hasLeft=false 表示位于首列。对应 libwebp 的 *NoTop/*NoLeft/*NoTopLeft 变体分派。
    public static void Pred16(int mode, bool hasTop, bool hasLeft, byte[] y, int off, int stride)
    {
        switch (mode)
        {
            case 0:
                // DC 模式按边界可用性分派到 libwebp 的 DC16/DC16NoTop/DC16NoLeft/DC16NoTopLeft
                if (hasTop)
                {
                    if (hasLeft) Dc16(y, off, stride);
                    else Dc16NoLeft(y, off, stride);
                }
                else
                {
                    if (hasLeft) Dc16NoTop(y, off, stride);
                    else Dc16NoTopLeft(y, off, stride);
                }
                break;
            case 1: Tm16(y, off, stride); break;
            case 2: Ve16(y, off, stride); break;
            case 3: He16(y, off, stride); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public static void Pred8(int mode, bool hasTop, bool hasLeft, byte[] uv, int off, int stride)
    {
        switch (mode)
        {
            case 0:
                // 色度 DC 模式按边界可用性分派到 DC8uv/DC8uvNoTop/DC8uvNoLeft/DC8uvNoTopLeft
                if (hasTop)
                {
                    if (hasLeft) Dc8uv(uv, off, stride);
                    else Dc8uvNoLeft(uv, off, stride);
                }
                else
                {
                    if (hasLeft) Dc8uvNoTop(uv, off, stride);
                    else Dc8uvNoTopLeft(uv, off, stride);
                }
                break;
            case 1: Tm8uv(uv, off, stride); break;
            case 2: Ve8uv(uv, off, stride); break;
            case 3: He8uv(uv, off, stride); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    // 4×4 子块预测，mode 0..9（0=B_DC,1=B_TM,2=B_VE,3=B_HE,4=B_RD,5=B_VR,6=B_LD,7=B_VL,8=B_HD,9=B_HU）。
    // 调用方保证上/左/右上边界字节已在缓冲区内填好；实现直接按 C 代码的相对偏移读取。
    public static void Pred4(int mode, byte[] y, int off, int stride)
    {
        switch (mode)
        {
            case 0: Dc4(y, off, stride); break;
            case 1: Tm4(y, off, stride); break;
            case 2: Ve4(y, off, stride); break;
            case 3: He4(y, off, stride); break;
            case 4: Rd4(y, off, stride); break;
            case 5: Vr4(y, off, stride); break;
            case 6: Ld4(y, off, stride); break;
            case 7: Vl4(y, off, stride); break;
            case 8: Hd4(y, off, stride); break;
            case 9: Hu4(y, off, stride); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    // 对应 libwebp 的 clip_8b / VP8kclip1 查表语义：截断到 [0,255]。
    private static byte Clip255(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    //-----------------------------------------------------------------------------
    // TrueMotion（TM 预测，RFC 6386 §12.3），对应 libwebp 的 TrueMotion() 内联函数。
    // 每个像素 = clip255(左侧像素 + 上方像素 - 左上像素)。
    private static void TrueMotion(byte[] dst, int off, int stride, int size)
    {
        int top = off - stride;                 // C: const uint8_t* top = dst - BPS;
        int topLeft = dst[top - 1];             // C: top[-1]（VP8kclip1 表基址偏移量）
        for (int y = 0; y < size; ++y)
        {
            int rowOff = off + y * stride;
            int delta = dst[rowOff - 1] - topLeft; // C: clip = clip0 + dst[-1]
            for (int x = 0; x < size; ++x)
            {
                // C: dst[x] = clip[top[x]]，即 VP8kclip1[top[x] - top[-1] + dst[-1]]
                dst[rowOff + x] = Clip255(delta + dst[top + x]);
            }
        }
    }

    // 对应 TM4_C / TM8uv_C / TM16_C
    private static void Tm4(byte[] dst, int off, int stride) => TrueMotion(dst, off, stride, 4);
    private static void Tm8uv(byte[] dst, int off, int stride) => TrueMotion(dst, off, stride, 8);
    private static void Tm16(byte[] dst, int off, int stride) => TrueMotion(dst, off, stride, 16);

    //-----------------------------------------------------------------------------
    // 16x16 亮度预测（RFC 6386 §12.3）

    // 对应 VE16_C：垂直预测，每行复制上一行（top 行）。
    private static void Ve16(byte[] dst, int off, int stride)
    {
        for (int j = 0; j < 16; ++j)
        {
            Array.Copy(dst, off - stride, dst, off + j * stride, 16);
        }
    }

    // 对应 HE16_C：水平预测，每行用其左侧像素填充。
    private static void He16(byte[] dst, int off, int stride)
    {
        for (int j = 0; j < 16; ++j)
        {
            Array.Fill(dst, dst[off + j * stride - 1], off + j * stride, 16);
        }
    }

    // 对应 libwebp 的 Put16：以常量填充 16×16 块。
    private static void Put16(int v, byte[] dst, int off, int stride)
    {
        for (int j = 0; j < 16; ++j)
        {
            Array.Fill(dst, (byte)v, off + j * stride, 16);
        }
    }

    // 对应 DC16_C：DC 预测（上、左边界均可用），均值含 16 的舍入偏置后右移 5。
    private static void Dc16(byte[] dst, int off, int stride)
    {
        int dc = 16;
        for (int j = 0; j < 16; ++j)
        {
            dc += dst[off - 1 + j * stride] + dst[off + j - stride];
        }
        Put16(dc >> 5, dst, off, stride);
    }

    // 对应 DC16NoTop_C：上边界不可用，仅用左侧 16 像素求均值。
    private static void Dc16NoTop(byte[] dst, int off, int stride)
    {
        int dc = 8;
        for (int j = 0; j < 16; ++j)
        {
            dc += dst[off - 1 + j * stride];
        }
        Put16(dc >> 4, dst, off, stride);
    }

    // 对应 DC16NoLeft_C：左边界不可用，仅用上方 16 像素求均值。
    private static void Dc16NoLeft(byte[] dst, int off, int stride)
    {
        int dc = 8;
        for (int i = 0; i < 16; ++i)
        {
            dc += dst[off + i - stride];
        }
        Put16(dc >> 4, dst, off, stride);
    }

    // 对应 DC16NoTopLeft_C：上、左边界均不可用，填 0x80。
    private static void Dc16NoTopLeft(byte[] dst, int off, int stride)
    {
        Put16(0x80, dst, off, stride);
    }

    //-----------------------------------------------------------------------------
    // 4x4 亮度预测（RFC 6386 §12.4），AVG3/AVG2 宏与 C 完全一致

    // 对应 C 宏 AVG3(a, b, c) = ((uint8_t)(((a) + 2*(b) + (c) + 2) >> 2))
    private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

    // 对应 C 宏 AVG2(a, b) = (((a) + (b) + 1) >> 1)
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    // 对应 VE4_C：垂直预测（4×4），top 行含左侧一格（top[-1]）与右上 4 格（top[1..4]）。
    private static void Ve4(byte[] dst, int off, int stride)
    {
        int top = off - stride;
        // 与 C 中 vals[4] 相同：由 top[-1..4] 五点滑动平均得到
        byte v0 = Avg3(dst[top - 1], dst[top + 0], dst[top + 1]);
        byte v1 = Avg3(dst[top + 0], dst[top + 1], dst[top + 2]);
        byte v2 = Avg3(dst[top + 1], dst[top + 2], dst[top + 3]);
        byte v3 = Avg3(dst[top + 2], dst[top + 3], dst[top + 4]);
        for (int i = 0; i < 4; ++i)
        {
            int row = off + i * stride;
            dst[row + 0] = v0;
            dst[row + 1] = v1;
            dst[row + 2] = v2;
            dst[row + 3] = v3;
        }
    }

    // 对应 HE4_C：水平预测（4×4），用到左上 X=dst[-1-BPS] 与左侧 4 个像素。
    private static void He4(byte[] dst, int off, int stride)
    {
        int a = dst[off - 1 - stride];
        int b = dst[off - 1];
        int c = dst[off - 1 + stride];
        int d = dst[off - 1 + 2 * stride];
        int e = dst[off - 1 + 3 * stride];
        // C 用 WebPUint32ToMem(0x01010101U * AVG3(...)) 一次写 4 个相同字节，此处逐字节填充
        Array.Fill(dst, Avg3(a, b, c), off + 0 * stride, 4);
        Array.Fill(dst, Avg3(b, c, d), off + 1 * stride, 4);
        Array.Fill(dst, Avg3(c, d, e), off + 2 * stride, 4);
        Array.Fill(dst, Avg3(d, e, e), off + 3 * stride, 4);
    }

    // 对应 DC4_C：4×4 DC 预测，8 个边界像素加偏置 4 后右移 3。
    private static void Dc4(byte[] dst, int off, int stride)
    {
        int dc = 4;
        for (int i = 0; i < 4; ++i)
        {
            dc += dst[off + i - stride] + dst[off - 1 + i * stride];
        }
        byte v = (byte)(dc >> 3);
        for (int i = 0; i < 4; ++i)
        {
            Array.Fill(dst, v, off + i * stride, 4);
        }
    }

    // 对应 RD4_C：Down-Right（右下对角）预测。DST(x, y) = dst[off + x + y*stride]。
    private static void Rd4(byte[] dst, int off, int stride)
    {
        int i0 = dst[off - 1 + 0 * stride];
        int j = dst[off - 1 + 1 * stride];
        int k = dst[off - 1 + 2 * stride];
        int l = dst[off - 1 + 3 * stride];
        int x = dst[off - 1 - stride];
        int a = dst[off + 0 - stride];
        int b = dst[off + 1 - stride];
        int c = dst[off + 2 - stride];
        int d = dst[off + 3 - stride];
        dst[off + 0 + 3 * stride] = Avg3(j, k, l);
        dst[off + 1 + 3 * stride] = dst[off + 0 + 2 * stride] = Avg3(i0, j, k);
        dst[off + 2 + 3 * stride] = dst[off + 1 + 2 * stride] = dst[off + 0 + 1 * stride] = Avg3(x, i0, j);
        dst[off + 3 + 3 * stride] = dst[off + 2 + 2 * stride] = dst[off + 1 + 1 * stride] = dst[off + 0 + 0 * stride] = Avg3(a, x, i0);
        dst[off + 3 + 2 * stride] = dst[off + 2 + 1 * stride] = dst[off + 1 + 0 * stride] = Avg3(b, a, x);
        dst[off + 3 + 1 * stride] = dst[off + 2 + 0 * stride] = Avg3(c, b, a);
        dst[off + 3 + 0 * stride] = Avg3(d, c, b);
    }

    // 对应 LD4_C：Down-Left（左下对角）预测，依赖 top 行 8 个像素（含右上 4 格）。
    private static void Ld4(byte[] dst, int off, int stride)
    {
        int a = dst[off + 0 - stride];
        int b = dst[off + 1 - stride];
        int c = dst[off + 2 - stride];
        int d = dst[off + 3 - stride];
        int e = dst[off + 4 - stride];
        int f = dst[off + 5 - stride];
        int g = dst[off + 6 - stride];
        int h = dst[off + 7 - stride];
        dst[off + 0 + 0 * stride] = Avg3(a, b, c);
        dst[off + 1 + 0 * stride] = dst[off + 0 + 1 * stride] = Avg3(b, c, d);
        dst[off + 2 + 0 * stride] = dst[off + 1 + 1 * stride] = dst[off + 0 + 2 * stride] = Avg3(c, d, e);
        dst[off + 3 + 0 * stride] = dst[off + 2 + 1 * stride] = dst[off + 1 + 2 * stride] = dst[off + 0 + 3 * stride] = Avg3(d, e, f);
        dst[off + 3 + 1 * stride] = dst[off + 2 + 2 * stride] = dst[off + 1 + 3 * stride] = Avg3(e, f, g);
        dst[off + 3 + 2 * stride] = dst[off + 2 + 3 * stride] = Avg3(f, g, h);
        dst[off + 3 + 3 * stride] = Avg3(g, h, h);
    }

    // 对应 VR4_C：Vertical-Right 预测。
    private static void Vr4(byte[] dst, int off, int stride)
    {
        int i0 = dst[off - 1 + 0 * stride];
        int j = dst[off - 1 + 1 * stride];
        int k = dst[off - 1 + 2 * stride];
        int x = dst[off - 1 - stride];
        int a = dst[off + 0 - stride];
        int b = dst[off + 1 - stride];
        int c = dst[off + 2 - stride];
        int d = dst[off + 3 - stride];
        dst[off + 0 + 0 * stride] = dst[off + 1 + 2 * stride] = Avg2(x, a);
        dst[off + 1 + 0 * stride] = dst[off + 2 + 2 * stride] = Avg2(a, b);
        dst[off + 2 + 0 * stride] = dst[off + 3 + 2 * stride] = Avg2(b, c);
        dst[off + 3 + 0 * stride] = Avg2(c, d);

        dst[off + 0 + 3 * stride] = Avg3(k, j, i0);
        dst[off + 0 + 2 * stride] = Avg3(j, i0, x);
        dst[off + 0 + 1 * stride] = dst[off + 1 + 3 * stride] = Avg3(i0, x, a);
        dst[off + 1 + 1 * stride] = dst[off + 2 + 3 * stride] = Avg3(x, a, b);
        dst[off + 2 + 1 * stride] = dst[off + 3 + 3 * stride] = Avg3(a, b, c);
        dst[off + 3 + 1 * stride] = Avg3(b, c, d);
    }

    // 对应 VL4_C：Vertical-Left 预测，依赖 top 行 8 个像素（含右上 4 格）。
    private static void Vl4(byte[] dst, int off, int stride)
    {
        int a = dst[off + 0 - stride];
        int b = dst[off + 1 - stride];
        int c = dst[off + 2 - stride];
        int d = dst[off + 3 - stride];
        int e = dst[off + 4 - stride];
        int f = dst[off + 5 - stride];
        int g = dst[off + 6 - stride];
        int h = dst[off + 7 - stride];
        dst[off + 0 + 0 * stride] = Avg2(a, b);
        dst[off + 1 + 0 * stride] = dst[off + 0 + 2 * stride] = Avg2(b, c);
        dst[off + 2 + 0 * stride] = dst[off + 1 + 2 * stride] = Avg2(c, d);
        dst[off + 3 + 0 * stride] = dst[off + 2 + 2 * stride] = Avg2(d, e);

        dst[off + 0 + 1 * stride] = Avg3(a, b, c);
        dst[off + 1 + 1 * stride] = dst[off + 0 + 3 * stride] = Avg3(b, c, d);
        dst[off + 2 + 1 * stride] = dst[off + 1 + 3 * stride] = Avg3(c, d, e);
        dst[off + 3 + 1 * stride] = dst[off + 2 + 3 * stride] = Avg3(d, e, f);
        dst[off + 3 + 2 * stride] = Avg3(e, f, g);
        dst[off + 3 + 3 * stride] = Avg3(f, g, h);
    }

    // 对应 HU4_C：Horizontal-Up 预测，仅用左侧 4 个像素。
    private static void Hu4(byte[] dst, int off, int stride)
    {
        int i0 = dst[off - 1 + 0 * stride];
        int j = dst[off - 1 + 1 * stride];
        int k = dst[off - 1 + 2 * stride];
        int l = dst[off - 1 + 3 * stride];
        dst[off + 0 + 0 * stride] = Avg2(i0, j);
        dst[off + 2 + 0 * stride] = dst[off + 0 + 1 * stride] = Avg2(j, k);
        dst[off + 2 + 1 * stride] = dst[off + 0 + 2 * stride] = Avg2(k, l);
        dst[off + 1 + 0 * stride] = Avg3(i0, j, k);
        dst[off + 3 + 0 * stride] = dst[off + 1 + 1 * stride] = Avg3(j, k, l);
        dst[off + 3 + 1 * stride] = dst[off + 1 + 2 * stride] = Avg3(k, l, l);
        dst[off + 3 + 2 * stride] = dst[off + 2 + 2 * stride] = dst[off + 0 + 3 * stride] =
            dst[off + 1 + 3 * stride] = dst[off + 2 + 3 * stride] = dst[off + 3 + 3 * stride] = (byte)l;
    }

    // 对应 HD4_C：Horizontal-Down 预测，用左上、左侧 4 像素与上方前 3 像素。
    private static void Hd4(byte[] dst, int off, int stride)
    {
        int i0 = dst[off - 1 + 0 * stride];
        int j = dst[off - 1 + 1 * stride];
        int k = dst[off - 1 + 2 * stride];
        int l = dst[off - 1 + 3 * stride];
        int x = dst[off - 1 - stride];
        int a = dst[off + 0 - stride];
        int b = dst[off + 1 - stride];
        int c = dst[off + 2 - stride];

        dst[off + 0 + 0 * stride] = dst[off + 2 + 1 * stride] = Avg2(i0, x);
        dst[off + 0 + 1 * stride] = dst[off + 2 + 2 * stride] = Avg2(j, i0);
        dst[off + 0 + 2 * stride] = dst[off + 2 + 3 * stride] = Avg2(k, j);
        dst[off + 0 + 3 * stride] = Avg2(l, k);

        dst[off + 3 + 0 * stride] = Avg3(a, b, c);
        dst[off + 2 + 0 * stride] = Avg3(x, a, b);
        dst[off + 1 + 0 * stride] = dst[off + 3 + 1 * stride] = Avg3(i0, x, a);
        dst[off + 1 + 1 * stride] = dst[off + 3 + 2 * stride] = Avg3(j, i0, x);
        dst[off + 1 + 2 * stride] = dst[off + 3 + 3 * stride] = Avg3(k, j, i0);
        dst[off + 1 + 3 * stride] = Avg3(l, k, j);
    }

    //-----------------------------------------------------------------------------
    // 8x8 色度预测（RFC 6386 §12.3）

    // 对应 VE8uv_C：垂直预测，每行复制上一行。
    private static void Ve8uv(byte[] dst, int off, int stride)
    {
        for (int j = 0; j < 8; ++j)
        {
            Array.Copy(dst, off - stride, dst, off + j * stride, 8);
        }
    }

    // 对应 HE8uv_C：水平预测，每行用其左侧像素填充。
    private static void He8uv(byte[] dst, int off, int stride)
    {
        for (int j = 0; j < 8; ++j)
        {
            Array.Fill(dst, dst[off + j * stride - 1], off + j * stride, 8);
        }
    }

    // 对应 libwebp 的 Put8x8uv：以常量填充 8×8 块。
    private static void Put8x8uv(int value, byte[] dst, int off, int stride)
    {
        for (int j = 0; j < 8; ++j)
        {
            Array.Fill(dst, (byte)value, off + j * stride, 8);
        }
    }

    // 对应 DC8uv_C：色度 DC（上、左边界均可用），偏置 8 后右移 4。
    private static void Dc8uv(byte[] dst, int off, int stride)
    {
        int dc0 = 8;
        for (int i = 0; i < 8; ++i)
        {
            dc0 += dst[off + i - stride] + dst[off - 1 + i * stride];
        }
        Put8x8uv(dc0 >> 4, dst, off, stride);
    }

    // 对应 DC8uvNoLeft_C：左边界不可用，仅用上方 8 像素，偏置 4 后右移 3。
    private static void Dc8uvNoLeft(byte[] dst, int off, int stride)
    {
        int dc0 = 4;
        for (int i = 0; i < 8; ++i)
        {
            dc0 += dst[off + i - stride];
        }
        Put8x8uv(dc0 >> 3, dst, off, stride);
    }

    // 对应 DC8uvNoTop_C：上边界不可用，仅用左侧 8 像素，偏置 4 后右移 3。
    private static void Dc8uvNoTop(byte[] dst, int off, int stride)
    {
        int dc0 = 4;
        for (int i = 0; i < 8; ++i)
        {
            dc0 += dst[off - 1 + i * stride];
        }
        Put8x8uv(dc0 >> 3, dst, off, stride);
    }

    // 对应 DC8uvNoTopLeft_C：上、左边界均不可用，填 0x80。
    private static void Dc8uvNoTopLeft(byte[] dst, int off, int stride)
    {
        Put8x8uv(0x80, dst, off, stride);
    }
}
