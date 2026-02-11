using System;
using System.IO;
using SharpImageConverter.Formats.Jpeg;
using Xunit;

namespace SharpImageConverter.Tests;

public sealed class CmykJpegNoApp14Tests
{
    [Fact]
    public void CmykJpeg_Decode_ShouldNotThrow_WhenNoAdobeApp14()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "video-001.cmyk.jpeg");
        byte[] data = File.ReadAllBytes(path);

        var img = StaticJpegDecoder.Decode(data);
        _ = img.Rgba32;
    }
}

