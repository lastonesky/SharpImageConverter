using System;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8L 行增量解码器：不预先解出整帧，只按输出行的需要推进位流。
///
/// 规范约束与设计取舍：
/// - VP8L 的 LZ77 反向引用最大距离为 1,048,456 像素（distance prefix 39 的 GetCopyValue
///   上限 1,048,576 再减去 PlaneCode 偏移 120），最大匹配长度 4,096 像素。因此任何合法
///   码流都要求解码器至少保留最近 <c>maxDistance + maxLength</c> 个变换域像素；
///   这里使用固定容量的环形历史缓冲（约 4.2 MB，与图像总尺寸无关）。
/// - 四种 transform 的逆变换都是行内局部或只依赖上一行输出（predictor），因此可以逐行
///   应用；这也正是无需整帧工作区的原因。
/// - 位流本身是顺序的，读取器跨行调用时用 <see cref="LsbBitReaderSnapshot"/> 保存/恢复
///   位置，不需要随机访问已解码像素。
/// </summary>
internal sealed class Vp8LRowDecoder
{
    /// <summary>VP8L LZ77 最大反向距离（像素）。</summary>
    internal const int MaxCopyDistance = 1048456;

    /// <summary>VP8L LZ77 最大匹配长度（像素）。</summary>
    internal const int MaxCopyLength = 4096;

    private static readonly uint[] EmptyRow = new uint[1];

    private readonly byte[] _payload;
    private readonly Vp8LImageState _state;
    private readonly Vp8LTransformInfo[] _reversedTransforms;
    private readonly Vp8LTransformInfo? _predictorTransform;

    private readonly int _width;          // 变换域宽度（token 流的行宽）
    private readonly int _height;         // 变换域高度（= 输出高度）
    private readonly int _outputWidth;    // 逆变换完成后的宽度
    private readonly int _outputHeight;
    private readonly int _totalPixels;

    private readonly uint[] _ring;
    private readonly int _ringSize;
    private readonly uint[] _rowA;
    private readonly uint[] _rowB;
    private readonly uint[] _predictor;   // [上一行 | 当前行]，仅在存在 predictor 时分配

    private LsbBitReaderSnapshot _snapshot;
    private int _pos;                     // 已解码的变换域像素数（绝对）
    private int _col;
    private int _row;
    private int _nextRow;

    /// <summary>原始（逆变换完成后）图像宽度。</summary>
    public int Width => _outputWidth;

    /// <summary>原始（逆变换完成后）图像高度。</summary>
    public int Height => _outputHeight;

    /// <summary>环形历史缓冲的像素容量（用于内存验证/测试）。</summary>
    internal int RingSize => _ringSize;

    public Vp8LRowDecoder(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _payload = payload;

        _state = Vp8LDecoder.ReadLevel0State(payload, out int width, out int height, out _snapshot);
        _outputWidth = width;
        _outputHeight = height;
        _width = _state.DecodeWidth;
        _height = _state.DecodeHeight;
        _totalPixels = checked(_width * _height);

        int count = _state.Transforms.Count;
        _reversedTransforms = new Vp8LTransformInfo[count];
        for (int i = 0; i < count; i++)
            _reversedTransforms[i] = _state.Transforms[count - 1 - i];

        for (int i = 0; i < count; i++)
        {
            if (_reversedTransforms[i].Type == Vp8LTransforms.TransformType.Predictor)
            {
                _predictorTransform = _reversedTransforms[i];
                break;
            }
        }

        // 环形缓冲只需覆盖最远的反向引用 + 最长匹配 + 一个输出行（保证当前行在解码到
        // 行尾之后仍有完整原始数据可读）。图像本身更小时直接用整幅大小，避免无意义开销。
        long needed = (long)MaxCopyDistance + MaxCopyLength + _width + 1;
        _ringSize = (int)Math.Min((long)_totalPixels, needed);
        if (_ringSize < 1) _ringSize = 1;
        _ring = new uint[_ringSize];

        _rowA = new uint[Math.Max(1, _outputWidth)];
        _rowB = new uint[Math.Max(1, _outputWidth)];

        if (_predictorTransform is { } predictor)
        {
            int pw = predictor.XSize;
            _predictor = new uint[Math.Max(1, pw * 2)];
        }
        else
        {
            _predictor = EmptyRow;
        }
    }

    /// <summary>
    /// 读取下一行 ARGB 像素（A 在高字节，即内存序 B,G,R,A 的 <see cref="uint"/> 表示）。
    /// 返回 false 表示所有行已输出。调用方负责把 ARGB 转成所需通道顺序。
    /// </summary>
    public bool TryReadArgbRow(Span<uint> row)
    {
        if (row.Length < _outputWidth)
            throw new ArgumentException("行缓冲区不足", nameof(row));
        if (_nextRow >= _outputHeight)
            return false;

        int y = _nextRow;

        // ---- 1. 推进位流直到本行最后一个像素被解出（copy 可能跨行，允许越界到后续行） ----
        var reader = new LsbBitReader(_payload, _snapshot);
        DecodeUntil(ref reader, (y + 1) * _width);
        _snapshot = reader.Snapshot();

        // ---- 2. 从环形缓冲取出本行原始（变换域）像素 ----
        int rowStart = y * _width;
        int offset = rowStart % _ringSize;
        int first = Math.Min(_width, _ringSize - offset);
        Array.Copy(_ring, offset, _rowA, 0, first);
        if (first < _width)
            Array.Copy(_ring, 0, _rowA, first, _width - first);

        // ---- 3. 逆变换（按 transform 读取顺序的逆序逐行应用） ----
        Span<uint> cur = _rowA.AsSpan(0, _width);
        Span<uint> alt = _rowB.AsSpan(0, _outputWidth);
        int curWidth = _width;

        foreach (var transform in _reversedTransforms)
        {
            switch (transform.Type)
            {
                case Vp8LTransforms.TransformType.ColorIndexing:
                {
                    if (transform.Data == null)
                        throw new WebpBitstreamException("Color Indexing transform 缺少调色板");
                    int origWidth = transform.XSize;
                    Vp8LTransforms.ApplyColorIndexingInverseRow(
                        cur[..curWidth], alt[..origWidth], origWidth, transform.Data, transform.Bits);
                    var tmp = cur;
                    cur = alt;
                    alt = tmp;
                    curWidth = origWidth;
                    break;
                }
                case Vp8LTransforms.TransformType.SubtractGreen:
                    Vp8LTransforms.ApplySubtractGreenInverseRow(cur, 0, curWidth);
                    break;
                case Vp8LTransforms.TransformType.CrossColor:
                {
                    if (transform.Data == null)
                        throw new WebpBitstreamException("Cross-color transform 缺少乘数数据");
                    if (curWidth != transform.XSize)
                        throw new WebpBitstreamException(
                            $"Cross-color 行宽 {curWidth} 与 transform 宽度 {transform.XSize} 不符");
                    int tilesPerRow = (transform.XSize + (1 << transform.Bits) - 1) >> transform.Bits;
                    Vp8LTransforms.ApplyColorTransformInverseRow(
                        cur, 0, curWidth, transform.Data, tilesPerRow, y, transform.Bits);
                    break;
                }
                case Vp8LTransforms.TransformType.Predictor:
                    ApplyPredictorRow(cur, curWidth, transform, y);
                    break;
                default:
                    throw new WebpBitstreamException($"未知的 transform 类型 {transform.Type}");
            }
        }

        if (curWidth != _outputWidth)
            throw new WebpBitstreamException(
                $"逆变换结束时行宽 {curWidth} 与图像宽度 {_outputWidth} 不符");

        cur[.._outputWidth].CopyTo(row);
        _nextRow++;
        return true;
    }

    /// <summary>
    /// 应用 predictor 逆变换的单行。缓冲区布局为 [上一行输出 | 当前行]：
    /// 这样 <c>top[x + 1]</c> 在最后一列越界读到的正是当前行首像素（与整幅解码器的线性
    /// 布局完全一致，libwebp 的 top 指针语义依赖这一点）。
    /// </summary>
    private void ApplyPredictorRow(Span<uint> row, int width, Vp8LTransformInfo transform, int y)
    {
        if (transform.Data == null)
            throw new WebpBitstreamException("Predictor transform 缺少模式数据");

        int pw = transform.XSize;
        if (width != pw)
            throw new WebpBitstreamException(
                $"Predictor 行宽 {width} 与 transform 宽度 {pw} 不符");

        if (y > 0)
            Array.Copy(_predictor, pw, _predictor, 0, pw);

        row[..width].CopyTo(_predictor.AsSpan(pw));
        int tilesPerRow = (pw + (1 << transform.Bits) - 1) >> transform.Bits;
        Vp8LTransforms.ApplyPredictorInverseRow(_predictor, pw, 0, pw, transform.Data,
                                                tilesPerRow, y, transform.Bits);
        _predictor.AsSpan(pw, width).CopyTo(row);
    }

    /// <summary>
    /// 推进位流直到已解码像素数达到 <paramref name="targetPos"/>。语义与整图解码器的
    /// <c>DecodePixels</c> 内层循环一致（literal / LZ77 / color cache 三分支）。
    /// </summary>
    private void DecodeUntil(ref LsbBitReader reader, int targetPos)
    {
        var colorCache = _state.ColorCache;
        int colorCacheLimit = NumLiteralCodes + NumLengthCodes + (colorCache != null ? colorCache.Size : 0);

        while (_pos < targetPos)
        {
            var tables = _state.Tables;
            int groupIdx = 0;

            if (_state.HuffmanImage != null)
            {
                int hIdx = (_row >> _state.HuffPrecision) * _state.HuffStride + (_col >> _state.HuffPrecision);
                if ((uint)hIdx >= (uint)_state.HuffmanImage.Length)
                    throw new WebpBitstreamException(
                        $"meta-Huffman 索引 {hIdx} 超出范围 0~{_state.HuffmanImage.Length - 1}");
                groupIdx = (int)_state.HuffmanImage[hIdx];
                if ((uint)groupIdx >= (uint)_state.NumGroups)
                    throw new WebpBitstreamException(
                        $"meta-Huffman group {groupIdx} 超出范围 0~{_state.NumGroups - 1}");
            }

            int baseIdx = groupIdx * 5;
            int code = tables[baseIdx].DecodeSymbol(ref reader);

            if (code < NumLiteralCodes)
            {
                byte g = (byte)code;
                byte r = (byte)tables[baseIdx + 1].DecodeSymbol(ref reader);
                byte b = (byte)tables[baseIdx + 2].DecodeSymbol(ref reader);
                byte a = (byte)tables[baseIdx + 3].DecodeSymbol(ref reader);
                uint pixel = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

                _ring[_pos % _ringSize] = pixel;
                if (colorCache != null) colorCache.Insert(pixel);

                _pos++;
                _col++;
                if (_col >= _width) { _col = 0; _row++; }
            }
            else if (code < NumLiteralCodes + NumLengthCodes)
            {
                int length = Vp8LDistance.DecodeLength(code - NumLiteralCodes, ref reader);
                int distSymbol = tables[baseIdx + 4].DecodeSymbol(ref reader);
                int distance = Vp8LDistance.DecodeDistance(distSymbol, _width, ref reader);

                if (distance > _pos)
                    throw new WebpBitstreamException($"LZ77 距离 {distance} 超出已解码范围（pos={_pos}）");
                if (length > _totalPixels - _pos)
                    throw new WebpBitstreamException(
                        $"LZ77 长度 {length} 超出剩余像素（剩余 {_totalPixels - _pos}）");

                int from = (_pos - distance) % _ringSize;
                int to = _pos % _ringSize;
                for (int i = 0; i < length; i++)
                {
                    uint pixel = _ring[from];
                    _ring[to] = pixel;
                    if (colorCache != null) colorCache.Insert(pixel);
                    if (++from == _ringSize) from = 0;
                    if (++to == _ringSize) to = 0;
                }

                int advanced = _col + length;
                _row += advanced / _width;
                _col = advanced % _width;
                _pos += length;
            }
            else if (code < colorCacheLimit)
            {
                if (colorCache == null)
                    throw new WebpBitstreamException("green Huffman 码引用颜色缓存，但 color cache 未启用");
                uint pixel = colorCache.Lookup(code - (NumLiteralCodes + NumLengthCodes));
                _ring[_pos % _ringSize] = pixel;
                colorCache.Insert(pixel);

                _pos++;
                _col++;
                if (_col >= _width) { _col = 0; _row++; }
            }
            else
            {
                throw new WebpBitstreamException($"green Huffman 码 {code} 不在有效区间");
            }
        }
    }

    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
}
