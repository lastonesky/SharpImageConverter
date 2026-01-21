using SharpImageConverter.Core;

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
        var dec = new JpegDecoder();
        return dec.Decode(stream);
    }
}

public sealed class JpegEncoderAdapter : IImageEncoder
{
    public void EncodeRgb24(string path, Image<Rgb24> image)
    {
        JpegEncoder.Write(path, image.Width, image.Height, image.Buffer, 75);
    }

    public void EncodeRgb24(Stream stream, Image<Rgb24> image)
    {
        JpegEncoder.Write(stream, image.Width, image.Height, image.Buffer, 75);
    }
}