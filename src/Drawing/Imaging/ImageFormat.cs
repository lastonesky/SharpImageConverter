using System;

namespace SharpImageConverter.Drawing.Imaging;

/// <summary>
/// Class-style image format descriptor similar to System.Drawing.Imaging.ImageFormat.
/// </summary>
public sealed class ImageFormat : IEquatable<ImageFormat>
{
    private readonly string name;

    private ImageFormat(string name)
    {
        this.name = name;
    }

    public static ImageFormat Bmp { get; } = new("Bmp");
    public static ImageFormat Png { get; } = new("Png");
    public static ImageFormat Jpeg { get; } = new("Jpeg");
    public static ImageFormat Gif { get; } = new("Gif");
    public static ImageFormat Webp { get; } = new("Webp");

    public bool Equals(ImageFormat? other)
    {
        return other is not null && string.Equals(name, other.name, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is ImageFormat other && Equals(other);
    }

    public override int GetHashCode()
    {
        return name.GetHashCode(StringComparison.Ordinal);
    }

    public override string ToString()
    {
        return name;
    }
}
