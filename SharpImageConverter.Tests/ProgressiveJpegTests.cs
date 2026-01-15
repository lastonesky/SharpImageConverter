using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using SharpImageConverter;
using Xunit;
using Xunit.Sdk;

namespace SharpImageConverter.Tests;

public sealed class ProgressiveJpegTests
{
    [Fact]
    public void ProgressiveJpeg_Decode_ShouldSucceed()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "progressive.jpg");
        using var fs = File.OpenRead(path);
        var dec = new JpegDecoder();
        try
        {
            var img = dec.Decode(fs);
            Assert.True(img.Width > 0);
            Assert.True(img.Height > 0);
        }
        catch (Exception ex)
        {
            string diag = GetDecoderDiagnostics(dec);
            throw new XunitException($"Decode failed: {ex.GetType().Name}: {ex.Message}\n{diag}");
        }
    }

    [Fact]
    public void ProgressiveJpeg_FastHuffman_ShouldMatchSlowPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "progressive.jpg");

        byte[] fastHash;
        int w, h;
        {
            using var fs = File.OpenRead(path);
            var dec = new JpegDecoder { EnableHuffmanFastPath = true };
            var img = dec.Decode(fs);
            w = img.Width;
            h = img.Height;
            fastHash = Hash(img.Buffer);
        }

        byte[] slowHash;
        {
            using var fs = File.OpenRead(path);
            var dec = new JpegDecoder { EnableHuffmanFastPath = false };
            var img = dec.Decode(fs);
            Assert.Equal(w, img.Width);
            Assert.Equal(h, img.Height);
            slowHash = Hash(img.Buffer);
        }

        Assert.Equal(slowHash, fastHash);
    }

    private static byte[] Hash(byte[] data)
    {
        return SHA256.HashData(data);
    }

    private static string GetDecoderDiagnostics(JpegDecoder dec)
    {
        static object? GetPrivate(object o, string name)
        {
            return o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);
        }

        object? htablesById = GetPrivate(dec, "_htablesByClassAndId");
        object? htables = GetPrivate(dec, "_htables");
        object? frame = GetPrivate(dec, "_frame");

        object? ht00 = null;
        try
        {
            if (htablesById is Array a) ht00 = a.GetValue(0, 0);
        }
        catch { }

        int listCount = 0;
        try
        {
            if (htables is ICollection c) listCount = c.Count;
        }
        catch { }

        string frameInfo = frame == null ? "null" : frame.GetType().Name;
        return $"_frame={frameInfo} _htables.Count={listCount} ht[0,0]={(ht00 != null)}";
    }
}
