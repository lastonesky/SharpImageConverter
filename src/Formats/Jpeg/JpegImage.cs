namespace SharpImageConverter.Formats.Jpeg;

public enum JpegPixelFormat
{
    Gray8,
    Rgb24,
    Rgba32
}

public sealed class JpegImage
{
    private readonly byte[] pixelData;
    private byte[]? rgba32Cache;

    public JpegImage(int width, int height, byte[] rgba32)
        : this(width, height, JpegPixelFormat.Rgba32, 8, rgba32)
    {
    }

    public JpegImage(int width, int height, JpegPixelFormat pixelFormat, int bitsPerSample, byte[] pixelData)
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
        this.pixelData = pixelData;
    }

    public int Width { get; }
    public int Height { get; }
    public JpegPixelFormat PixelFormat { get; }
    public int BitsPerSample { get; }

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

        throw new InvalidOperationException($"Unsupported pixel format: {PixelFormat}.");
    }
}
