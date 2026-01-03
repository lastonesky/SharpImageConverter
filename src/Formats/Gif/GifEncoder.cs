using System;
using System.IO;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// GIF 编码器，支持 RGB24 与 RGBA32 编码（含量化与 LZW 压缩）
/// </summary>
public class GifEncoder
{
    /// <summary>
    /// 编码图像帧到流
    /// </summary>
    /// <param name="image">图像帧</param>
    /// <param name="stream">输出流</param>
    public void Encode(ImageFrame image, Stream stream)
    {
        // 1. Quantize
        var quantizer = new Quantizer();
        var (palette, indices) = quantizer.Quantize(image.Pixels, image.Width, image.Height);

        // 2. Write Header
        WriteAscii(stream, "GIF89a");
        
        // 3. Write LSD
        WriteShort(stream, image.Width);
        WriteShort(stream, image.Height);
        
        // Calculate palette size power of 2
        int paletteCount = palette.Length / 3;
        int depth = 0;
        while ((1 << (depth + 1)) < paletteCount) depth++;
        if (depth > 7) depth = 7;
        
        // Packed field: 
        // 1 (Global Table Flag)
        // 111 (Color Res: 8 bits - usually fixed to max)
        // 0 (Sort Flag)
        // size (Size of Global Table: 2^(size+1))
        int packed = 0x80 | (0x07 << 4) | depth;
        stream.WriteByte((byte)packed);
        
        stream.WriteByte(0); // Background Color Index
        stream.WriteByte(0); // Pixel Aspect Ratio
        
        // 4. Write Global Color Table
        int actualTableSize = 1 << (depth + 1);
        stream.Write(palette, 0, palette.Length);
        
        // Pad with zeros if palette is smaller than power of 2
        int paddingBytes = (actualTableSize * 3) - palette.Length;
        for (int i = 0; i < paddingBytes; i++)
        {
            stream.WriteByte(0);
        }

        // 5. Write Image Descriptor
        stream.WriteByte(0x2C); // Separator ','
        WriteShort(stream, 0); // Left Position
        WriteShort(stream, 0); // Top Position
        WriteShort(stream, image.Width);
        WriteShort(stream, image.Height);
        stream.WriteByte(0); // Packed: Local Table Flag(0), Interlace(0), Sort(0), Reserved(0), Size(0)
        
        // 6. Write Image Data
        // LZW Minimum Code Size should be at least 2.
        int lzwMinCodeSize = Math.Max(2, depth + 1);
        var lzw = new LzwEncoder(stream);
        lzw.Encode(indices, image.Width, image.Height, lzwMinCodeSize);
        
        // 7. Write Trailer
        stream.WriteByte(0x3B); // ';'
    }
    
    /// <summary>
    /// 编码 RGBA 图像数据到流（支持透明度处理）
    /// </summary>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="rgba">RGBA32 像素数据</param>
    /// <param name="stream">输出流</param>
    public void EncodeRgba(int width, int height, byte[] rgba, Stream stream)
    {
        bool hasTransparent = false;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 0) { hasTransparent = true; break; }
        }
        if (!hasTransparent)
        {
            var rgb = new byte[width * height * 3];
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
            {
                rgb[j + 0] = rgba[i + 0];
                rgb[j + 1] = rgba[i + 1];
                rgb[j + 2] = rgba[i + 2];
            }
            Encode(new ImageFrame(width, height, rgb), stream);
            return;
        }
        var opaque = new byte[width * height * 3];
        var opaqueMask = new byte[width * height];
        int pi = 0;
        for (int idx = 0; idx < width * height; idx++)
        {
            int s = idx * 4;
            byte a = rgba[s + 3];
            opaqueMask[idx] = a == 0 ? (byte)0 : (byte)1;
            opaque[pi + 0] = rgba[s + 0];
            opaque[pi + 1] = rgba[s + 1];
            opaque[pi + 2] = rgba[s + 2];
            pi += 3;
        }
        var quantizer = new Quantizer();
        var (pal, indsOpaque) = quantizer.Quantize(opaque, width, height);
        int palCount = pal.Length / 3;
        int depth = 0;
        while ((1 << (depth + 1)) < (palCount + 1)) depth++;
        if (depth > 7) depth = 7;
        int actualSize = 1 << (depth + 1);
        var palette = new byte[actualSize * 3];
        palette[0] = 0; palette[1] = 0; palette[2] = 0;
        for (int i = 0; i < palCount; i++)
        {
            palette[(i + 1) * 3 + 0] = pal[i * 3 + 0];
            palette[(i + 1) * 3 + 1] = pal[i * 3 + 1];
            palette[(i + 1) * 3 + 2] = pal[i * 3 + 2];
        }
        var indices = new byte[width * height];
        for (int i = 0; i < indices.Length; i++)
        {
            if (opaqueMask[i] == 0) indices[i] = 0;
            else indices[i] = (byte)(indsOpaque[i] + 1);
        }
        WriteAscii(stream, "GIF89a");
        WriteShort(stream, width);
        WriteShort(stream, height);
        int packed = 0x80 | (0x07 << 4) | depth;
        stream.WriteByte((byte)packed);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.Write(palette, 0, palette.Length);
        stream.WriteByte(0x21);
        stream.WriteByte(0xF9);
        stream.WriteByte(4);
        stream.WriteByte(0x01);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(0x2C);
        WriteShort(stream, 0);
        WriteShort(stream, 0);
        WriteShort(stream, width);
        WriteShort(stream, height);
        stream.WriteByte(0);
        int lzwMinCodeSize = Math.Max(2, depth + 1);
        var lzw = new LzwEncoder(stream);
        lzw.Encode(indices, width, height, lzwMinCodeSize);
        stream.WriteByte(0x3B);
    }
    
    private void WriteAscii(Stream stream, string s)
    {
        foreach (char c in s) stream.WriteByte((byte)c);
    }
    
    private void WriteShort(Stream stream, int v)
    {
        stream.WriteByte((byte)(v & 0xFF));
        stream.WriteByte((byte)((v >> 8) & 0xFF));
    }
}
