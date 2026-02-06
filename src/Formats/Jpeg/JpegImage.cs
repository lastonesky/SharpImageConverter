namespace SharpImageConverter.Formats.Jpeg;

public enum JpegPixelFormat
{
    Gray8,
    Rgb24,
    Rgba32,
    YCbCr24,
    Cmyk32,
    Ycck32,
    Unknown32
}

public enum JpegColorSpace
{
    Gray,
    Rgb,
    YCbCr,
    Cmyk,
    Ycck,
    Unknown4
}

public readonly record struct JpegColorInfo(JpegColorSpace ColorSpace, bool HasAdobeTransform, byte AdobeTransform, ReadOnlyMemory<byte>? IccProfile);

public sealed class JpegImage
{
    private readonly byte[] pixelData;
    private byte[]? rgba32Cache;

    public JpegImage(int width, int height, byte[] rgba32)
        : this(width, height, JpegPixelFormat.Rgba32, 8, new JpegColorInfo(JpegColorSpace.Rgb, false, 0, null), rgba32)
    {
    }

    public JpegImage(int width, int height, JpegPixelFormat pixelFormat, int bitsPerSample, JpegColorInfo colorInfo, byte[] pixelData)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (bitsPerSample <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        int bytesPerPixel = pixelFormat switch
        {
            JpegPixelFormat.Gray8 => 1,
            JpegPixelFormat.Rgb24 => 3,
            JpegPixelFormat.Rgba32 => 4,
            JpegPixelFormat.YCbCr24 => 3,
            JpegPixelFormat.Cmyk32 => 4,
            JpegPixelFormat.Ycck32 => 4,
            JpegPixelFormat.Unknown32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
        };

        int expected = checked(width * height * bytesPerPixel);
        if (pixelData.Length < expected)
        {
            throw new ArgumentException("像素数据长度不足。", nameof(pixelData));
        }

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        BitsPerSample = bitsPerSample;
        ColorInfo = colorInfo;
        this.pixelData = pixelData;
    }

    public int Width { get; }
    public int Height { get; }
    public JpegPixelFormat PixelFormat { get; }
    public int BitsPerSample { get; }
    public JpegColorInfo ColorInfo { get; }

    public ReadOnlySpan<byte> PixelData => pixelData;
    internal byte[] PixelDataArray => pixelData;

    public byte[] Rgba32
    {
        get
        {
            if (PixelFormat == JpegPixelFormat.Rgba32)
            {
                return pixelData;
            }

            rgba32Cache ??= ConvertToRgba32();
            return rgba32Cache;
        }
    }

    private byte[] ConvertToRgba32()
    {
        int count = checked(Width * Height);
        byte[] dst = new byte[checked(count * 4)];

        if (PixelFormat == JpegPixelFormat.Gray8)
        {
            ReadOnlySpan<byte> src = pixelData.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                byte lum = src[i];
                int o = i * 4;
                dst[o + 0] = lum;
                dst[o + 1] = lum;
                dst[o + 2] = lum;
                dst[o + 3] = 255;
            }

            return dst;
        }

        if (PixelFormat == JpegPixelFormat.Rgb24)
        {
            ReadOnlySpan<byte> src = pixelData.AsSpan(0, checked(count * 3));
            int si = 0;
            for (int i = 0; i < count; i++)
            {
                byte r = src[si++];
                byte g = src[si++];
                byte b = src[si++];
                int o = i * 4;
                dst[o + 0] = r;
                dst[o + 1] = g;
                dst[o + 2] = b;
                dst[o + 3] = 255;
            }

            return dst;
        }

        if (PixelFormat == JpegPixelFormat.YCbCr24)
        {
            ReadOnlySpan<byte> src = pixelData.AsSpan(0, checked(count * 3));
            int si = 0;
            for (int i = 0; i < count; i++)
            {
                byte y = src[si++];
                byte cb = src[si++];
                byte cr = src[si++];
                // YCbCr -> RGB
                YCbCrToRgb(y, cb, cr, out byte r, out byte g, out byte b);
                int o = i * 4;
                dst[o + 0] = r;
                dst[o + 1] = g;
                dst[o + 2] = b;
                dst[o + 3] = 255;
            }

            return dst;
        }

        if (PixelFormat == JpegPixelFormat.Cmyk32)
        {
            ReadOnlySpan<byte> src = pixelData.AsSpan(0, checked(count * 4));
            int si = 0;
            bool invert = ColorInfo.HasAdobeTransform;
            for (int i = 0; i < count; i++)
            {
                int c = src[si++];
                int m = src[si++];
                int y = src[si++];
                int k = src[si++];
                if (invert)
                {
                    // Adobe CMYK 反相存储 -> 还原为 CMYK
                    c = 255 - c;
                    m = 255 - m;
                    y = 255 - y;
                    k = 255 - k;
                }

                // CMYK -> RGB（K 作为黑版压暗因子参与乘法）
                int invK = 255 - k;
                int r = ((255 - c) * invK + 127) / 255;
                int g = ((255 - m) * invK + 127) / 255;
                int b = ((255 - y) * invK + 127) / 255;
                int o = i * 4;
                dst[o + 0] = (byte)r;
                dst[o + 1] = (byte)g;
                dst[o + 2] = (byte)b;
                dst[o + 3] = 255;
            }

            return dst;
        }

        if (PixelFormat == JpegPixelFormat.Ycck32)
        {
            ReadOnlySpan<byte> src = pixelData.AsSpan(0, checked(count * 4));
            int si = 0;
            bool invert = ColorInfo.HasAdobeTransform;
            for (int i = 0; i < count; i++)
            {
                int y = src[si++];
                int cb = src[si++];
                int cr = src[si++];
                int k = src[si++];
                if (invert)
                {
                    // Adobe YCCK 反相存储 -> 还原为 YCCK
                    y = 255 - y;
                    cb = 255 - cb;
                    cr = 255 - cr;
                    k = 255 - k;
                }

                // YCCK -> YCbCr -> RGB
                YCbCrToRgb(y, cb, cr, out byte r, out byte g, out byte b);
                // RGB -> 应用 K 压暗
                int invK = 255 - k;
                int r2 = (r * invK + 127) / 255;
                int g2 = (g * invK + 127) / 255;
                int b2 = (b * invK + 127) / 255;
                int o = i * 4;
                dst[o + 0] = (byte)r2;
                dst[o + 1] = (byte)g2;
                dst[o + 2] = (byte)b2;
                dst[o + 3] = 255;
            }

            return dst;
        }

        throw new InvalidOperationException($"Unsupported pixel format: {PixelFormat}.");
    }

    private static void YCbCrToRgb(int y, int cb, int cr, out byte r, out byte g, out byte b)
    {
        int cbShift = cb - 128;
        int crShift = cr - 128;

        int rr = y + ((91881 * crShift) >> 16);
        int gg = y - ((22554 * cbShift + 46802 * crShift) >> 16);
        int bb = y + ((116130 * cbShift) >> 16);

        r = ClampToByte(rr);
        g = ClampToByte(gg);
        b = ClampToByte(bb);
    }

    private static byte ClampToByte(int v)
    {
        if ((uint)v <= 255u)
        {
            return (byte)v;
        }

        return v < 0 ? (byte)0 : (byte)255;
    }
}
