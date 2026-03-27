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
        JpegEncoder.Encode(image, path, new JpegEncoderOptions(Quality, Subsample420, KeepMetadata, EnableDiagnostics));
    }

    public void EncodeRgb24(Stream stream, Image<Rgb24> image)
    {
        JpegEncoder.Encode(image, stream, new JpegEncoderOptions(Quality, Subsample420, KeepMetadata, EnableDiagnostics));
    }
}
