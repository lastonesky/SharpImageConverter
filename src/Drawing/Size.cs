using System;

namespace SharpImageConverter.Drawing;

/// <summary>
/// Lightweight size struct similar to System.Drawing.Size.
/// </summary>
public readonly struct Size : IEquatable<Size>
{
    public int Width { get; }
    public int Height { get; }

    public bool IsEmpty => Width == 0 && Height == 0;

    public Size(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void Deconstruct(out int width, out int height)
    {
        width = Width;
        height = Height;
    }

    public bool Equals(Size other)
    {
        return Width == other.Width && Height == other.Height;
    }

    public override bool Equals(object? obj)
    {
        return obj is Size other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Width, Height);
    }

    public static bool operator ==(Size left, Size right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Size left, Size right)
    {
        return !left.Equals(right);
    }
}
