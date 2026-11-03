// VP8 亮度 4×4 帧内模式（B_PRED）模式树的读/写单一实现。
//
// 这张树的编码端与解码端必须逐位互逆：任一侧少写/多写一位，都会让后续所有模式与残差
// 整体错位（历史上就踩过：B_HE 漏写 p[4] 那一位，导致 q75 下 PSNR 掉到 12 dB）。
// 因此两侧共用本类，并配 Vp8ModeTreeTests 的全组合往返测试。
//
// 树结构（RFC 6386 §11.2 的 b_mode_tree，9 个内部结点对应 kBModesProba 的 9 个概率）：
//   p0=0 → B_DC                                    (1 bit)
//   p0=1,p1=0 → B_TM                               (2)
//   p0p1=11,p2=0 → B_VE                            (3)
//   p0p1p2=111,p3=0,p4=0 → B_HE                    (5)
//   p3=0,p4=1,p5=0 → B_RD                          (6)
//   p3=0,p4=1,p5=1 → B_VR                          (6)
//   p3=1,p6=0 → B_LD                               (5)
//   p3=1,p6=1,p7=0 → B_VL                          (6)
//   p3=1,p6=1,p7=1,p8=0 → B_HD                     (7)
//   p3=1,p6=1,p7=1,p8=1 → B_HU                     (7)
namespace SharpImageConverter.Formats.Webp;

/// <summary>VP8 4×4 亮度帧内模式树的共用编/解码实现。</summary>
internal static class Vp8ModeTree
{
    /// <summary>
    /// 读一个 4×4 亮度模式（对应 libwebp tree_dec.c 的 B 模式硬编码分支）。
    /// 返回 B_DC_PRED..B_HU_PRED（0..9）。
    /// </summary>
    internal static int Read(Vp8BoolDecoder br, int top, int left)
    {
        byte[,,] p = Vp8Tables.BModesProba;
        if (br.GetBit(p[top, left, 0]) == 0) return 0;  // B_DC_PRED
        if (br.GetBit(p[top, left, 1]) == 0) return 1;  // B_TM_PRED
        if (br.GetBit(p[top, left, 2]) == 0) return 2;  // B_VE_PRED
        if (br.GetBit(p[top, left, 3]) == 0)
        {
            return br.GetBit(p[top, left, 4]) == 0
                ? 3                                        // B_HE_PRED
                : (br.GetBit(p[top, left, 5]) == 0 ? 4 : 5); // B_RD_PRED / B_VR_PRED
        }
        return br.GetBit(p[top, left, 6]) == 0
            ? 6                                            // B_LD_PRED
            : br.GetBit(p[top, left, 7]) == 0
                ? 7                                        // B_VL_PRED
                : (br.GetBit(p[top, left, 8]) == 0 ? 8 : 9); // B_HD_PRED / B_HU_PRED
    }

    /// <summary>写一个 4×4 亮度模式，位序列与 <see cref="Read"/> 严格互逆。</summary>
    internal static void Write(Vp8BoolEncoder bw, int mode, int top, int left)
    {
        // 逐层沿树下行：每一层先写「是否离开本层」，离开即返回。
        // 与 Read 的分支一一对应，任何一位都不能省。
        byte[,,] p = Vp8Tables.BModesProba;
        bw.PutBit(mode == 0 ? 0 : 1, p[top, left, 0]);
        if (mode == 0) return;

        bw.PutBit(mode == 1 ? 0 : 1, p[top, left, 1]);
        if (mode == 1) return;

        bw.PutBit(mode == 2 ? 0 : 1, p[top, left, 2]);
        if (mode == 2) return;

        bool upper = mode >= 6;                 // p3 分叉：0..5 走左支，6..9 走右支
        bw.PutBit(upper ? 1 : 0, p[top, left, 3]);
        if (!upper)
        {
            bw.PutBit(mode == 3 ? 0 : 1, p[top, left, 4]);
            if (mode == 3) return;
            bw.PutBit(mode == 5 ? 1 : 0, p[top, left, 5]);
            return;
        }

        bw.PutBit(mode == 6 ? 0 : 1, p[top, left, 6]);
        if (mode == 6) return;

        bw.PutBit(mode == 7 ? 0 : 1, p[top, left, 7]);
        if (mode == 7) return;

        bw.PutBit(mode == 9 ? 1 : 0, p[top, left, 8]);
    }
}
