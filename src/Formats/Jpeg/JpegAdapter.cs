using SharpImageConverter.Core;
using SharpImageConverter;

namespace SharpImageConverter.Formats.Jpeg;
public sealed class JpegDecoderAdapter : IImageDecoder
{
    public Image<Rgb24> DecodeRgb24(string path)
    {
        using var fs = File.OpenRead(path);
        return DecodeRgb24(fs);
    }

    public Image<Rgb24> DecodeRgb24(Stream stream)
    {
        var frame = ImageFrame.LoadJpeg(stream);
        return new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels, frame.Metadata);
    }
}

public sealed class JpegEncoderAdapter : IImageEncoder
{
    public int Quality { get; set; } = 75;
    public bool Subsample420 { get; set; } = true;
    public bool KeepMetadata { get; set; } = true;
    public bool EnableDiagnostics { get; set; }

    public void EncodeRgb24(string path, Image<Rgb24> image)
    {
        JpegEncoder.Encode(image, path, CurrentOptions());
    }

    public void EncodeRgb24(Stream stream, Image<Rgb24> image)
    {
        JpegEncoder.Encode(image, stream, CurrentOptions());
    }

    /// <summary>
    /// 按 <see cref="SaveOptions.Jpeg"/> 编码；未提供时用本实例的属性。
    /// </summary>
    /// <remarks>
    /// 走选项而不是改本实例属性：<see cref="Configuration.Default"/> 里的编码器是进程级单例，
    /// 逐次保存改它的 <see cref="Quality"/> 会让并发保存互相踩。
    /// </remarks>
    /// <param name="stream">输出数据流</param>
    /// <param name="image">Rgb24 图像</param>
    /// <param name="options">保存选项</param>
    public void EncodeRgb24(Stream stream, Image<Rgb24> image, SaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        JpegEncoder.Encode(image, stream, options.Jpeg ?? CurrentOptions());
    }

    private JpegEncoderOptions CurrentOptions()
        => new(Quality, Subsample420, KeepMetadata, EnableDiagnostics);
}
