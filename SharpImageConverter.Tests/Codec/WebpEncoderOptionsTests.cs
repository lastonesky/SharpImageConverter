using Xunit;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>
/// WebP 编码器适配器的单值 setter 必须只改自己那一项、其余原样带过。
///
/// 背景：<c>WebpEncoderOptions</c> 有 Quality / ConcurrencyStrategy / Method / Lossless 四项，
/// 早期三个 setter 各自手写 <c>new WebpEncoderOptions(...)</c> 并漏传 <c>lossless</c>
/// （该参数在末尾且有默认值 false），于是
/// <code>
/// adapter.Lossless = true;
/// adapter.Quality = 80f;   // ← Lossless 静默变回 false，产物从 VP8L 变 VP8 有损
/// </code>
/// 编译通过、无警告、无异常，只在字节数上暴露。现在 setter 统一用 <c>options with { ... }</c>，
/// 本测试对 6 条 setter 逐条固定该行为。
/// </summary>
public class WebpEncoderOptionsTests
{
    public static TheoryData<string> SetterNames => new() { "Quality", "Method", "ConcurrencyStrategy" };

    [Theory]
    [MemberData(nameof(SetterNames))]
    public void RgbAdapter_Setter_KeepsLossless(string setter)
    {
        var adapter = new WebpEncoderAdapter { Options = new WebpEncoderOptions(75f, lossless: true) };
        Assert.True(adapter.Options.Lossless);

        Touch(adapter, setter);

        Assert.True(adapter.Options.Lossless, $"改 {setter} 之后 Lossless 被重置了");
    }

    [Theory]
    [MemberData(nameof(SetterNames))]
    public void RgbaAdapter_Setter_KeepsLossless(string setter)
    {
        var adapter = new WebpEncoderAdapterRgba { Options = new WebpEncoderOptions(75f, lossless: true) };
        Assert.True(adapter.Options.Lossless);

        Touch(adapter, setter);

        Assert.True(adapter.Options.Lossless, $"改 {setter} 之后 Lossless 被重置了");
    }

    /// <summary>反向检查：setter 不能只保住 Lossless 而把别人写坏。</summary>
    [Fact]
    public void RgbAdapter_Setters_DoNotClobberOtherFields()
    {
        var adapter = new WebpEncoderAdapter();
        var baseline = new WebpEncoderOptions(75f, WebpConcurrencyStrategy.Serial, 5, lossless: true);
        adapter.Options = baseline;

        adapter.Quality = 42f;
        Assert.Equal(42f, adapter.Quality);
        Assert.Equal(WebpConcurrencyStrategy.Serial, adapter.ConcurrencyStrategy);
        Assert.Equal(5, adapter.Method);
        Assert.True(adapter.Options.Lossless);

        adapter.Method = 2;
        Assert.Equal(42f, adapter.Quality);
        Assert.Equal(WebpConcurrencyStrategy.Serial, adapter.ConcurrencyStrategy);
        Assert.Equal(2, adapter.Method);
        Assert.True(adapter.Options.Lossless);

        adapter.ConcurrencyStrategy = WebpConcurrencyStrategy.Parallel;
        Assert.Equal(42f, adapter.Quality);
        Assert.Equal(WebpConcurrencyStrategy.Parallel, adapter.ConcurrencyStrategy);
        Assert.Equal(2, adapter.Method);
        Assert.True(adapter.Options.Lossless);
    }

    private static void Touch(WebpEncoderAdapter adapter, string setter)
    {
        switch (setter)
        {
            case "Quality": adapter.Quality = 42f; break;
            case "Method": adapter.Method = 1; break;
            default: adapter.ConcurrencyStrategy = WebpConcurrencyStrategy.Serial; break;
        }
    }

    private static void Touch(WebpEncoderAdapterRgba adapter, string setter)
    {
        switch (setter)
        {
            case "Quality": adapter.Quality = 42f; break;
            case "Method": adapter.Method = 1; break;
            default: adapter.ConcurrencyStrategy = WebpConcurrencyStrategy.Serial; break;
        }
    }
}
