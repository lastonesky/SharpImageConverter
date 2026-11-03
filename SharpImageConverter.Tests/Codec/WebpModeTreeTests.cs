using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// VP8 4×4 亮度模式树（B_PRED）的写/读往返验证。
///
/// 背景：这张树的编码端与解码端是两段手写分支，极易失配。历史 bug：写侧 B_HE(3) 只写了
/// 4 位（p0..p2=1,p3=0），漏掉树里 p4=0 那一位，于是解码端从该子块起整体错位——64×64 q75
/// 实测 PSNR 从 40.7 dB 掉到 12.0 dB，且密探发现不了（解码器读出的模式与编码器写的不同，
/// 但位流本身符合语法）。现在两侧共用 <see cref="Vp8ModeTree"/>，本测试对所有
/// (模式, top, left) 组合做穷举往返，防止再退化。
/// </summary>
public class WebpModeTreeTests
{
    [Fact]
    public void Bmode_WriteRead_RoundTrips_AllModesAndContexts()
    {
        // top/left 的取值域是 0..9（模式编号本身），覆盖 kBModesProba 的两维。
        for (int top = 0; top < 10; top++)
        {
            for (int left = 0; left < 10; left++)
            {
                var bw = new Vp8BoolEncoder(256);
                for (int mode = 0; mode < 10; mode++)
                    Vp8ModeTree.Write(bw, mode, top, left);
                byte[] bits = bw.Finish();

                var br = new Vp8BoolDecoder(bits);
                for (int mode = 0; mode < 10; mode++)
                {
                    int got = Vp8ModeTree.Read(br, top, left);
                    Assert.True(mode == got,
                        $"top={top} left={left} 写入模式 {mode} 却解出 {got}");
                }
            }
        }
    }

    /// <summary>
    /// 位长自检：每种模式占用的位数是定值（树结构决定），若有人改树必然踩到这里。
    /// 统计方式是按 <see cref="Vp8BoolEncoder"/> 的输出长度粗判不现实，因此改为
    /// 「同一上下文下把 10 种模式各写 N 遍，解码端必须解出同样的序列」——
    /// 用重复来放大任何一位的漂移。
    /// </summary>
    [Fact]
    public void Bmode_RepeatedMixedSequence_StaysInSync()
    {
        int[] pattern = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 3, 0, 5, 4, 1, 2, 6, 7, 8 };
        var bw = new Vp8BoolEncoder(1024);
        for (int rep = 0; rep < 50; rep++)
        {
            foreach (int mode in pattern)
                Vp8ModeTree.Write(bw, mode, mode, pattern[(mode + rep) % pattern.Length]);
        }
        byte[] bits = bw.Finish();

        var br = new Vp8BoolDecoder(bits);
        for (int rep = 0; rep < 50; rep++)
        {
            foreach (int mode in pattern)
            {
                int got = Vp8ModeTree.Read(br, mode, pattern[(mode + rep) % pattern.Length]);
                Assert.Equal(mode, got);
            }
        }
    }
}
