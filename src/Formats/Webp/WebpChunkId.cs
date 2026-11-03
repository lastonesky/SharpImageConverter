namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// WebP RIFF chunk 类型（FourCC 字面量）。
/// </summary>
internal enum WebpChunkId : uint
{
    /// <summary>"VP8 "</summary>
    Vp8 = 0x20385056,
    /// <summary>"VP8L"</summary>
    Vp8L = 0x4C385056,
    /// <summary>"VP8X"</summary>
    Vp8X = 0x58385056,
    /// <summary>"ALPH"</summary>
    Alph = 0x48504C41,
    /// <summary>"ANIM"</summary>
    Anim = 0x4D494E41,
    /// <summary>"ANMF"</summary>
    Anmf = 0x464D4E41,
    /// <summary>"ICCP"</summary>
    Iccp = 0x50434349,
    /// <summary>"EXIF"</summary>
    Exif = 0x46495845,
    /// <summary>"XMP "</summary>
    Xmp = 0x20504D58,
}

/// <summary>
/// WebP 文件头标识。
/// </summary>
internal static class WebpMagic
{
    /// <summary>"RIFF" = 0x46464952</summary>
    public const uint Riff = 0x46464952;
    /// <summary>"WEBP" = 0x50424557</summary>
    public const uint Webp = 0x50424557;

    /// <summary>RIFF 文件头固定 12 字节：RIFF(4) + size(4) + WEBP(4)。</summary>
    public const int HeaderSize = 12;
}

/// <summary>
/// VP8X（扩展格式）标志位，定义文件能力。
/// </summary>
[Flags]
internal enum Vp8XFlags : byte
{
    Iccp = 0x20,
    Alpha = 0x10,
    Exif = 0x08,
    Xmp = 0x04,
    Animation = 0x02,
    Reserved = 0x01,
}
