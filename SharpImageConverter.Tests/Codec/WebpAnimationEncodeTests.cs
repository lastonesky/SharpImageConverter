using System;
using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// 纯 C# 动图编码（VP8X + ANIM + ANMF）的输入校验测试。
/// </summary>
public class WebpAnimationEncodeTests
{
    [Fact]
    public void FrameSizeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            WebpAnimationWriter.Encode(
                new[] { new WebpAnimationFrameInput(new byte[10], 10) }, 8, 8, 0));
    }
}
