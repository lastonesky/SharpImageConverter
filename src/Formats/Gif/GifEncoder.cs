using System;
using System.IO;

namespace PictureSharp.Formats.Gif;

public class GifEncoder
{
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
