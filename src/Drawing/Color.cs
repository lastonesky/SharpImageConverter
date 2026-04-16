using System;

namespace SharpImageConverter.Drawing;

/// <summary>
/// Lightweight color struct compatible with common System.Drawing color usage.
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    public byte A { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    private Color(byte a, byte r, byte g, byte b)
    {
        A = a;
        R = r;
        G = g;
        B = b;
    }

    public static Color FromArgb(int a, int r, int g, int b)
    {
        if ((uint)a > 255) throw new ArgumentOutOfRangeException(nameof(a));
        if ((uint)r > 255) throw new ArgumentOutOfRangeException(nameof(r));
        if ((uint)g > 255) throw new ArgumentOutOfRangeException(nameof(g));
        if ((uint)b > 255) throw new ArgumentOutOfRangeException(nameof(b));
        return new Color((byte)a, (byte)r, (byte)g, (byte)b);
    }

    public static Color FromArgb(int r, int g, int b)
    {
        return FromArgb(255, r, g, b);
    }

    public static Color FromRgb(int r, int g, int b)
    {
        return FromArgb(255, r, g, b);
    }

    public int ToArgb()
    {
        return (A << 24) | (R << 16) | (G << 8) | B;
    }

    public bool Equals(Color other)
    {
        return A == other.A && R == other.R && G == other.G && B == other.B;
    }

    public override bool Equals(object? obj)
    {
        return obj is Color other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(A, R, G, B);
    }

    public static bool operator ==(Color left, Color right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Color left, Color right)
    {
        return !left.Equals(right);
    }

    public static Color Transparent => new(0, 0, 0, 0);
    public static Color Black => new(255, 0, 0, 0);
    public static Color White => new(255, 255, 255, 255);
    public static Color Red => new(255, 255, 0, 0);
    public static Color Green => new(255, 0, 128, 0);
    public static Color Blue => new(255, 0, 0, 255);
}
