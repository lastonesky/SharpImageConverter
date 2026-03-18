using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SharpImageConverter.Formats.Jpeg;
using Xunit;
using Xunit.Sdk;

namespace SharpImageConverter.Tests;

public sealed class ProgressiveJpegTests
{
    [Fact]
    public void ProgressiveJpeg_Decode_ShouldSucceed()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "progressive.jpg");
        using var fs = File.OpenRead(path);
        try
        {
            var img = JpegDecoder.Decode(fs);
            Assert.True(img.Width > 0);
            Assert.True(img.Height > 0);
        }
        catch (Exception ex)
        {
            throw new XunitException($"Decode failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public async Task ProgressiveJpeg_StreamDecode_Matches_NonStreaming()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "progressive.jpg");
        byte[] data = File.ReadAllBytes(path);

        var imgSync = JpegDecoder.Decode(data);

        using var ms = new MemoryStream(data);
        var streamResult = await JpegDecoder.DecodeFromStreamAsync(ms);
        var imgStream = streamResult.Image;

        Assert.Equal(imgSync.Width, imgStream.Width);
        Assert.Equal(imgSync.Height, imgStream.Height);
        Assert.Equal(Hash(imgSync.Buffer), Hash(imgStream.Buffer));
    }

    private static byte[] Hash(byte[] data)
    {
        return SHA256.HashData(data);
    }
}
