using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Png;

// 探针：
//   verify   <png...>            独立解码（只用 System.IO.Compression）比对 RGB/alpha 的 SHA256
//   mk-rgba  <in> <out>          生成带 alpha 渐变的 RGBA PNG
//   refilter <palette.png>       把调色板 PNG 的索引流用 Up / Adaptive 重新编码，比较体积
if (args.Length >= 2 && args[0] == "mk-rgba")
{
    var src = Configuration.Default.LoadRgba32(args[1]);
    int w = src.Width, h = src.Height;
    var rgba = (byte[])src.Buffer.Clone();
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            long dx = (long)x * 2 - w, dy = (long)y * 2 - h;
            if (dx * dx * 4 + dy * dy < (long)w * w) rgba[(y * w + x) * 4 + 3] = (byte)(x * 255 / Math.Max(1, w - 1));
        }
    }
    PngWriter.WriteRgba(args[2], w, h, rgba);
    Console.WriteLine($"wrote {args[2]} {w}x{h}");
    return;
}

if (args.Length > 0 && args[0] == "verify")
{
    foreach (string p in args.Skip(1))
    {
        var (w, h, ct, hash) = DecodePng(p);
        Console.WriteLine($"{Path.GetFileName(p),-22} {w}x{h} colortype={ct} sha256={hash}");
    }
    return;
}

if (args.Length > 1 && args[0] == "refilter")
{
    var (w, h, indices, plte, trns) = DecodePalette(args[1]);
    Console.WriteLine($"{Path.GetFileName(args[1])} {w}x{h} colors={plte.Length / 3}");
    foreach (var f in new[] { PngFilterMode.Up, PngFilterMode.Adaptive })
    {
        using var ms = new MemoryStream();
        PngWriter.WritePalette(ms, w, h, indices, plte, trns, null, f, CompressionLevel.Optimal);
        Console.WriteLine($"  {f,-9} {ms.Length,10:N0} bytes");
    }
    return;
}

Console.WriteLine("用法: PngProbe verify <a.png>... | mk-rgba <in> <out> | refilter <palette.png>");
return;

static (int W, int H, byte[] Indices, byte[] Plte, byte[]? Trns) DecodePalette(string path)
{
    byte[] file = File.ReadAllBytes(path);
    int w = 0, h = 0;
    byte[]? plte = null, trns = null;
    using var idat = new MemoryStream();
    int i = 8;
    while (i + 8 <= file.Length)
    {
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(i, 4));
        string type = System.Text.Encoding.ASCII.GetString(file, i + 4, 4);
        var span = file.AsSpan(i + 8, len);
        switch (type)
        {
            case "IHDR":
                w = (int)BinaryPrimitives.ReadUInt32BigEndian(span);
                h = (int)BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                if (span[9] != 3) throw new InvalidDataException("不是调色板 PNG");
                break;
            case "PLTE": plte = span.ToArray(); break;
            case "tRNS": trns = span.ToArray(); break;
            case "IDAT": idat.Write(span); break;
            case "IEND": i = file.Length; break;
        }
        i += 12 + len;
    }
    if (plte == null) throw new InvalidDataException("缺少 PLTE");

    idat.Position = 0;
    using var z = new ZLibStream(idat, CompressionMode.Decompress);
    var prev = new byte[w];
    var cur = new byte[w];
    var indices = new byte[w * h];
    for (int y = 0; y < h; y++)
    {
        int f = z.ReadByte();
        z.ReadExactly(cur, 0, w);
        for (int x = 0; x < w; x++)
        {
            int a = x >= 1 ? cur[x - 1] : 0;
            int b = prev[x];
            int c = x >= 1 ? prev[x - 1] : 0;
            int add = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c), _ => 0 };
            cur[x] = (byte)(cur[x] + add);
        }
        Buffer.BlockCopy(cur, 0, indices, y * w, w);
        (prev, cur) = (cur, prev);
    }
    return (w, h, indices, plte, trns);
}

static (int W, int H, int ColorType, string Hash) DecodePng(string path)
{
    byte[] file = File.ReadAllBytes(path);
    int w = 0, h = 0, ct = 0;
    using var idat = new MemoryStream();
    int i = 8;
    while (i + 8 <= file.Length)
    {
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(i, 4));
        string type = System.Text.Encoding.ASCII.GetString(file, i + 4, 4);
        if (type == "IHDR")
        {
            w = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(i + 8, 4));
            h = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(i + 12, 4));
            ct = file[i + 17];
        }
        else if (type == "IDAT") idat.Write(file, i + 8, len);
        else if (type == "IEND") break;
        i += 12 + len;
    }

    int bpp = ct == 6 ? 4 : (ct == 3 ? 1 : 3);
    int stride = w * bpp;
    idat.Position = 0;
    using var z = new ZLibStream(idat, CompressionMode.Decompress);
    var prev = new byte[stride];
    var cur = new byte[stride];
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var rgb = new byte[w * 3];
    var alpha = new byte[w];
    for (int y = 0; y < h; y++)
    {
        int f = z.ReadByte();
        z.ReadExactly(cur, 0, stride);
        for (int x = 0; x < stride; x++)
        {
            int a = x >= bpp ? cur[x - bpp] : 0;
            int b = prev[x];
            int c = x >= bpp ? prev[x - bpp] : 0;
            int add = f switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException($"bad filter {f} at row {y}"),
            };
            cur[x] = (byte)(cur[x] + add);
        }
        for (int x = 0, j = 0; x < w; x++)
        {
            rgb[x * 3] = cur[j];
            rgb[x * 3 + 1] = cur[j + 1];
            rgb[x * 3 + 2] = cur[j + 2];
            alpha[x] = bpp == 4 ? cur[j + 3] : (byte)255;
            j += bpp;
        }
        sha.AppendData(rgb);
        sha.AppendData(alpha);
        (prev, cur) = (cur, prev);
    }
    return (w, h, ct, Convert.ToHexString(sha.GetHashAndReset()));
}

static int Paeth(int a, int b, int c)
{
    int p = a + b - c;
    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
    if (pa <= pb && pa <= pc) return a;
    return pb <= pc ? b : c;
}
