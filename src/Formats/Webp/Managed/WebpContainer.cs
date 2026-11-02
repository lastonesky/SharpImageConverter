using System.IO;

namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// WebP 容器类型：静态图（含或不含 VP8X）或动图。
/// </summary>
public enum WebpContainerKind
{
    /// <summary>单帧 VP8 有损（本批不支持解码）。</summary>
    Vp8,
    /// <summary>单帧 VP8L 无损。</summary>
    Vp8L,
    /// <summary>VP8X 扩展格式（可能是单帧也可能是动图）。</summary>
    Vp8X,
    /// <summary>识别失败或未知。</summary>
    Unknown,
}

/// <summary>
/// 表示一个 RIFF chunk：类型、数据片段（位于原始数据缓冲区中的偏移范围）。
/// </summary>
internal readonly struct WebpChunk
{
    /// <summary>FourCC 类型，如 0x4C385056 代表 "VP8L"。</summary>
    public readonly WebpChunkId Id;
    /// <summary>该 chunk 数据在缓冲区中的起始偏移（chunk type + size 字段之后）。</summary>
    public readonly int DataOffset;
    /// <summary>chunk 数据长度（不包含 type 和 size 字段，也不包含 padding byte）。</summary>
    public readonly int DataSize;

    public WebpChunk(WebpChunkId id, int dataOffset, int dataSize)
    {
        Id = id;
        DataOffset = dataOffset;
        DataSize = dataSize;
    }

    public ReadOnlySpan<byte> GetData(ReadOnlySpan<byte> buffer)
        => buffer.Slice(DataOffset, DataSize);
}

/// <summary>ANMF 帧头（16 字节）。偏移单位为 2 像素。</summary>
internal readonly struct AnmfFrameHeader
{
    /// <summary>帧左上角 X（已按 2 像素单位换算）。</summary>
    public readonly int X;
    /// <summary>帧左上角 Y（已按 2 像素单位换算）。</summary>
    public readonly int Y;
    /// <summary>帧宽度。</summary>
    public readonly int Width;
    /// <summary>帧高度。</summary>
    public readonly int Height;
    /// <summary>帧显示时长（毫秒）。</summary>
    public readonly int DurationMs;
    /// <summary>该帧显示结束后是否清除为背景（透明）。</summary>
    public readonly bool DisposeToBackground;
    /// <summary>是否禁止与上一帧混合。</summary>
    public readonly bool NoBlend;

    public AnmfFrameHeader(int x, int y, int width, int height, int durationMs, bool disposeToBackground, bool noBlend)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        DurationMs = durationMs;
        DisposeToBackground = disposeToBackground;
        NoBlend = noBlend;
    }
}

/// <summary>
/// RIFF/WEBP 容器解析与 chunk 遍历。
/// 只做结构解析，不解码图像内容。
/// </summary>
public sealed class WebpContainer
{
    private readonly byte[] _data;
    private readonly List<WebpChunk> _chunks;

    /// <summary>原始字节数据（文件头 + 所有 chunk）。</summary>
    public ReadOnlySpan<byte> RawData => _data;

    /// <summary>文件类型判别。</summary>
    public WebpContainerKind Kind { get; private set; }

    /// <summary>画布宽度（仅 VP8X / ANIM 有效，否则为 0）。</summary>
    public int CanvasWidth { get; private set; }

    /// <summary>画布高度（仅 VP8X / ANIM 有效，否则为 0）。</summary>
    public int CanvasHeight { get; private set; }

    /// <summary>是否为动画 WebP。</summary>
    public bool IsAnimation { get; private set; }

    /// <summary>是否包含 ALPH（独立 alpha 通道）。</summary>
    public bool HasAlphaChunk { get; private set; }

    /// <summary>文件整体 canvas alpha 标志（VP8X 第 4 字节的 bit 4）。</summary>
    public bool HasAlpha { get; private set; }

    /// <summary>循环次数（动图 ANIM chunk），0 = 无限循环。</summary>
    public int LoopCount { get; private set; }

    /// <summary>背景颜色（动图 ANIM chunk），BGRA 小端。</summary>
    public uint BackgroundColor { get; private set; }

    private WebpContainer(byte[] data)
    {
        _data = data;
        _chunks = new List<WebpChunk>();
    }

    /// <summary>
    /// 从文件加载并解析 WebP 容器。
    /// </summary>
    public static WebpContainer Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Load(fs);
    }

    /// <summary>
    /// 从流读取并解析 WebP 容器。
    /// </summary>
    public static WebpContainer Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] buffer;
        if (stream.CanSeek)
        {
            long len = stream.Length - stream.Position;
            if (len < WebpMagic.HeaderSize) throw new WebpContainerException("WebP 数据过短");
            buffer = new byte[len];
            int off = 0;
            while (off < buffer.Length)
            {
                int n = stream.Read(buffer, off, buffer.Length - off);
                if (n == 0) throw new WebpContainerException("WebP 流被截断");
                off += n;
            }
        }
        else
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            buffer = ms.ToArray();
            if (buffer.Length < WebpMagic.HeaderSize) throw new WebpContainerException("WebP 数据过短");
        }

        var c = new WebpContainer(buffer);
        c.Parse();
        return c;
    }

    /// <summary>
    /// 从内存缓冲区直接解析。
    /// </summary>
    /// <remarks>
    /// 传入完整 <see cref="byte"/> 数组时不会复制数据（容器直接引用该数组）；只解析 chunk 目录，
    /// 因此 24 MB 的 143 MP 文件不再多出一次整文件拷贝。
    /// 传入的数组在容器生命周期内不应被修改。
    /// </remarks>
    public static WebpContainer Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var c = new WebpContainer(data);
        c.Parse();
        return c;
    }

    /// <summary>
    /// 从内存缓冲区直接解析（会复制一份；传入 <see cref="byte"/> 数组时请用
    /// <see cref="Load(byte[])"/> 重载以避免整文件拷贝）。
    /// </summary>
    public static WebpContainer Load(ReadOnlySpan<byte> data)
    {
        byte[] copy = data.ToArray();
        var c = new WebpContainer(copy);
        c.Parse();
        return c;
    }

    private void Parse()
    {
        if (_data.Length < WebpMagic.HeaderSize)
            throw new WebpContainerException("WebP 文件头不完整");

        // 解析 RIFF 文件头
        uint riffId = BitConverter.ToUInt32(_data, 0);
        if (riffId != WebpMagic.Riff)
            throw new WebpContainerException($"不是 RIFF 文件，首四字节: 0x{riffId:X8}");

        uint riffSize = BitConverter.ToUInt32(_data, 4);
        uint webpId = BitConverter.ToUInt32(_data, 8);
        if (webpId != WebpMagic.Webp)
            throw new WebpContainerException($"RIFF 不是 WEBP，类型字节: 0x{webpId:X8}");

        // RIFF size 字段 = 整个文件大小 - 8；允许与实际不一致（有些编码器不严格遵守）
        // checked cast 会在 riffSize=0xFFFFFFFF 时溢出，用 try/catch 包成可控异常
        int expectedSize;
        try { expectedSize = checked((int)riffSize) + 8; }
        catch (OverflowException) { expectedSize = int.MaxValue; }
        // 允许文件末尾少量缺失（截断检测由 chunk 遍历时进行）

        // 遍历所有 chunk
        int pos = WebpMagic.HeaderSize;
        while (pos < _data.Length)
        {
            if (pos + 8 > _data.Length)
                throw new WebpContainerException("chunk 头被截断（type+size 不完整）");

            uint chunkIdRaw = BitConverter.ToUInt32(_data, pos);
            uint chunkSizeRaw = BitConverter.ToUInt32(_data, pos + 4);

            if (chunkSizeRaw > (uint)(_data.Length - pos - 8))
                throw new WebpContainerException($"chunk '{FourCCString(chunkIdRaw)}' 大小 {chunkSizeRaw} 超出文件范围");

            int chunkSize = (int)chunkSizeRaw;
            int dataStart = pos + 8;
            int dataEnd = dataStart + chunkSize;

            var chunkId = (WebpChunkId)chunkIdRaw;
            _chunks.Add(new WebpChunk(chunkId, dataStart, chunkSize));

            pos = dataEnd;
            // RIFF 对齐：chunk 数据后若为奇数，追加一个填充字节（填充不计入 size，内容不校验）
            if ((chunkSize & 1) != 0 && pos < _data.Length) pos++;
        }

        // 识别容器类型
        DetermineKind();
        ParseMetadata();
    }

    private void DetermineKind()
    {
        bool hasVp8 = false, hasVp8L = false, hasVp8X = false;
        foreach (var c in _chunks)
        {
            switch (c.Id)
            {
                case WebpChunkId.Vp8: hasVp8 = true; break;
                case WebpChunkId.Vp8L: hasVp8L = true; break;
                case WebpChunkId.Vp8X: hasVp8X = true; break;
            }
        }

        if (hasVp8X) Kind = WebpContainerKind.Vp8X;
        else if (hasVp8L) Kind = WebpContainerKind.Vp8L;
        else if (hasVp8) Kind = WebpContainerKind.Vp8;
        else Kind = WebpContainerKind.Unknown;
    }

    private void ParseMetadata()
    {
        foreach (var c in _chunks)
        {
            switch (c.Id)
            {
                case WebpChunkId.Vp8X:
                    ParseVp8X(c);
                    break;
                case WebpChunkId.Anim:
                    ParseAnim(c);
                    break;
                case WebpChunkId.Anmf:
                    break;
                case WebpChunkId.Alph:
                    HasAlphaChunk = true;
                    break;
            }
        }

        // 如果没有 VP8X，但有 VP8L，可以直接从 VP8L 头获取尺寸
        if (Kind == WebpContainerKind.Vp8L && CanvasWidth == 0)
        {
            var vp8l = FindChunk(WebpChunkId.Vp8L);
            if (vp8l.HasValue) ParseVp8LHeader(vp8l.Value);
        }
    }

    private void ParseVp8X(WebpChunk chunk)
    {
        var span = chunk.GetData(_data);
        if (span.Length < 10) throw new WebpContainerException("VP8X chunk 数据过短");

        // VP8X 布局：flags(1) + reserved(3) + canvas_width-1(3, LE) + canvas_height-1(3, LE)
        // flags 位定义：Rsv(2) | ICC(1) | Alpha(1) | EXIF(1) | XMP(1) | Animation(1) | R(1)
        byte flags = span[0];
        IsAnimation = (flags & (byte)Vp8XFlags.Animation) != 0;
        HasAlpha = (flags & (byte)Vp8XFlags.Alpha) != 0;

        // width-1, height-1 均为 24bit LE（注意 reserved 3 字节必须在尺寸之前跳过）
        CanvasWidth = (span[4] | (span[5] << 8) | (span[6] << 16)) + 1;
        CanvasHeight = (span[7] | (span[8] << 8) | (span[9] << 16)) + 1;

        if (CanvasWidth > 16777216 || CanvasHeight > 16777216)
            throw new WebpContainerException($"画布尺寸 {CanvasWidth}x{CanvasHeight} 超出规格上限");
    }

    private void ParseAnim(WebpChunk chunk)
    {
        var span = chunk.GetData(_data);
        if (span.Length < 6) throw new WebpContainerException("ANIM chunk 数据过短");
        BackgroundColor = (uint)(span[0] | (span[1] << 8) | (span[2] << 16) | (span[3] << 24)); // BGRA LE
        LoopCount = span[4] | (span[5] << 8);
    }

    private void ParseVp8LHeader(WebpChunk chunk)
    {
        var span = chunk.GetData(_data);
        if (span.Length < 5) throw new WebpContainerException("VP8L chunk 数据过短（需要至少 5 字节帧头）");

        // 使用 LsbBitReader 逐 bit 解析，避免手动字节拼接错误
        var reader = new LsbBitReader(span);

        // VP8L 帧头共 40 bits = 5 字节：
        //   signature(8) + width-1(14) + height-1(14) + alpha_used(1) + version(3)
        byte sig = (byte)reader.ReadBits(8);
        if (sig != 0x2F) throw new WebpBitstreamException("VP8L 签名字节错误");

        uint wMinus1 = reader.ReadBits(14);
        uint hMinus1 = reader.ReadBits(14);
        CanvasWidth = (int)wMinus1 + 1;
        CanvasHeight = (int)hMinus1 + 1;

        int alphaUsed = (int)reader.ReadBits(1);
        HasAlpha = alphaUsed != 0;

        int version = (int)reader.ReadBits(3);
        if (version != 0)
            throw new WebpUnsupportedException($"VP8L version={version} 不支持（本实现只支持 version=0）");
    }

    /// <summary>
    /// 查找指定类型的 chunk（返回第一个匹配）。
    /// </summary>
    internal WebpChunk? FindChunk(WebpChunkId id)
    {
        foreach (var c in _chunks)
            if (c.Id == id) return c;
        return null;
    }

    /// <summary>
    /// 返回所有指定类型的 chunk（动图中会有多个 ANMF）。
    /// </summary>
    internal IReadOnlyList<WebpChunk> FindAllChunks(WebpChunkId id)
    {
        var list = new List<WebpChunk>();
        foreach (var c in _chunks)
            if (c.Id == id) list.Add(c);
        return list;
    }

    /// <summary>解析 ANMF 帧头。ANMF chunk 数据前 16 字节：X(24)*2, Y(24)*2, W-1(24), H-1(24), duration(24), flags(8)。flags: bit0=dispose to background, bit1=no blend。</summary>
    internal static AnmfFrameHeader ParseAnmfHeader(ReadOnlySpan<byte> buffer, WebpChunk chunk)
    {
        var span = chunk.GetData(buffer);
        if (span.Length < 16) throw new WebpContainerException("ANMF chunk 数据过短（需要至少 16 字节帧头）");
        int x = ReadUInt24(span, 0) * 2;
        int y = ReadUInt24(span, 3) * 2;
        int w = ReadUInt24(span, 6) + 1;
        int h = ReadUInt24(span, 9) + 1;
        int duration = ReadUInt24(span, 12);
        byte flags = span[15];
        return new AnmfFrameHeader(x, y, w, h, duration, (flags & 0x01) != 0, (flags & 0x02) != 0);
    }

    /// <summary>枚举 ANMF 内的子 chunk（从帧头之后的偏移 16 开始），返回的 WebpChunk 偏移是相对整个文件的绝对偏移。</summary>
    internal IReadOnlyList<WebpChunk> GetAnmfSubChunks(WebpChunk anmf)
    {
        if (anmf.DataSize < 16) throw new WebpContainerException("ANMF chunk 数据过短");
        var list = new List<WebpChunk>();
        int pos = anmf.DataOffset + 16;
        int end = anmf.DataOffset + anmf.DataSize;
        while (pos + 8 <= end)
        {
            uint idRaw = BitConverter.ToUInt32(_data, pos);
            uint sizeRaw = BitConverter.ToUInt32(_data, pos + 4);
            if (sizeRaw > (uint)(end - pos - 8)) throw new WebpContainerException($"ANMF 子 chunk 大小 {sizeRaw} 超出帧范围");
            int size = (int)sizeRaw;
            list.Add(new WebpChunk((WebpChunkId)idRaw, pos + 8, size));
            pos += 8 + size;
            if ((size & 1) != 0) pos++;   // RIFF 偶数对齐
        }
        return list;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> s, int off)
        => s[off] | (s[off + 1] << 8) | (s[off + 2] << 16);

    /// <summary>
    /// 返回 VP8X/VP8L/VP8 主图像 chunk 的数据（不包含 ALPH）。
    /// </summary>
    internal WebpChunk? GetPrimaryImageChunk()
    {
        // 先找 VP8X（扩展格式容器本身不带图像数据，图像在 VP8L/VP8/ANMF 中）
        if (Kind == WebpContainerKind.Vp8L)
        {
            return FindChunk(WebpChunkId.Vp8L);
        }
        if (Kind == WebpContainerKind.Vp8)
        {
            return FindChunk(WebpChunkId.Vp8);
        }
        if (Kind == WebpContainerKind.Vp8X)
        {
            if (IsAnimation)
            {
                // 动图主图像在第一个 ANMF 中
                var list = FindAllChunks(WebpChunkId.Anmf);
                if (list.Count == 0) return null;
                return list[0];
            }
            else
            {
                // 单帧 VP8X，找 VP8L 或 VP8
                var vp8l = FindChunk(WebpChunkId.Vp8L);
                if (vp8l.HasValue) return vp8l;
                return FindChunk(WebpChunkId.Vp8);
            }
        }
        return null;
    }

    private static string FourCCString(uint id)
    {
        return new string(new[]
        {
            (char)(id & 0xFF),
            (char)((id >> 8) & 0xFF),
            (char)((id >> 16) & 0xFF),
            (char)((id >> 24) & 0xFF),
        });
    }
}
