using System;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// ALPH chunk 的行增量解码器（有损 VP8 图像的独立 alpha 平面）。
///
/// - 压缩方式 0（原始字节）：直接按行拷贝。
/// - 压缩方式 1（VP8L 裸位流）：复用 <see cref="Vp8LRowDecoder"/>，每行取 GREEN 通道，
///   常驻像素与主图 VP8L 行解码共用同一环形缓冲上限。
/// - 逆滤波（none / horizontal / vertical / gradient）逐行应用；horizontal 的预测起点是
///   上一行首像素，vertical / gradient 的首行退化为 horizontal，与 libwebp 一致。
/// </summary>
internal sealed class AlphRowSource
{
    private const int CompressionNone = 0;
    private const int CompressionVp8L = 1;

    private readonly byte[] _data;
    private readonly int _width;
    private readonly int _height;
    private readonly int _filter;
    private readonly Vp8LRowDecoder? _vp8lDecoder;
    private readonly uint[]? _argbRow;
    private readonly byte[] _prevRow;
    private int _nextRow;

    /// <summary>
    /// 使用 ALPH chunk 原始字节（不含 chunk 头，含 1 字节 ALPH 头）创建行解码器。
    /// </summary>
    /// <exception cref="WebpBitstreamException">ALPH 头或负载非法/截断。</exception>
    /// <exception cref="WebpUnsupportedException">压缩方式为保留值。</exception>
    internal AlphRowSource(byte[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (width < 1 || height < 1)
            throw new WebpBitstreamException($"ALPH 尺寸非法：{width}x{height}");
        if (data.Length < 1)
            throw new WebpBitstreamException("ALPH 数据过短，缺少头部字节");

        byte header = data[0];
        int compression = header & 0x03;
        _filter = (header >> 2) & 0x03;
        int preprocessing = (header >> 4) & 0x03;
        int reserved = (header >> 6) & 0x03;

        if (reserved != 0)
            throw new WebpBitstreamException($"ALPH 头部保留位非零：0x{header:X2}");
        if (preprocessing > 1)
            throw new WebpBitstreamException($"ALPH 预处理方式 {preprocessing} 为保留值（合法值 0/1）");

        _data = data;
        _width = width;
        _height = height;
        _prevRow = new byte[width];

        if (compression == CompressionNone)
        {
            long pixelCount = (long)width * height;
            if (data.Length - 1 < pixelCount)
                throw new WebpBitstreamException(
                    $"ALPH 未压缩数据长度 {data.Length - 1} 小于所需 {width}x{height}={pixelCount}");
        }
        else if (compression == CompressionVp8L)
        {
            _vp8lDecoder = Vp8LRowDecoder.CreateBare(data, dataOffset: 1, width, height);
            if (_vp8lDecoder.Width != width || _vp8lDecoder.Height != height)
                throw new WebpBitstreamException(
                    $"ALPH VP8L 输出尺寸 {_vp8lDecoder.Width}x{_vp8lDecoder.Height} 与 {width}x{height} 不符");
            _argbRow = new uint[width];
        }
        else
        {
            throw new WebpUnsupportedException($"ALPH 压缩方式 {compression}（保留值）不支持");
        }
    }

    /// <summary>
    /// 读取下一行 alpha（每像素 1 字节，<paramref name="dst"/> 至少 width 字节）。
    /// 数据耗尽后返回 false。
    /// </summary>
    internal bool TryReadRow(Span<byte> dst)
    {
        if (dst.Length < _width)
            throw new ArgumentException("alpha 行缓冲区不足。", nameof(dst));
        if (_nextRow >= _height)
            return false;

        if (_vp8lDecoder == null)
        {
            _data.AsSpan(1 + _nextRow * _width, _width).CopyTo(dst);
        }
        else
        {
            if (!_vp8lDecoder.TryReadArgbRow(_argbRow))
                throw new WebpBitstreamException("ALPH VP8L 位流在图像结束前耗尽");
            for (int x = 0; x < _width; x++)
                dst[x] = (byte)((_argbRow![x] >> 8) & 0xFF); // GREEN 通道
        }

        ApplyFilterRow(dst);
        dst[.._width].CopyTo(_prevRow);
        _nextRow++;
        return true;
    }

    /// <summary>按行应用 ALPH 逆滤波（与 <see cref="AlphDecoder"/> 的整图实现语义一致）。</summary>
    private void ApplyFilterRow(Span<byte> row)
    {
        int y = _nextRow;
        switch (_filter)
        {
            case 0: // none
                break;

            case 1: // horizontal：pred 从上一行首像素起逐像素滚动
            {
                int pred = y == 0 ? 0 : _prevRow[0];
                for (int x = 0; x < _width; x++)
                {
                    pred = (byte)(pred + row[x]);
                    row[x] = (byte)pred;
                }
                break;
            }

            case 2: // vertical：首行退化为 horizontal
                if (y == 0)
                {
                    for (int x = 1; x < _width; x++)
                        row[x] = (byte)(row[x] + row[x - 1]);
                }
                else
                {
                    for (int x = 0; x < _width; x++)
                        row[x] = (byte)(row[x] + _prevRow[x]);
                }
                break;

            case 3: // gradient：首行退化为 horizontal
                if (y == 0)
                {
                    for (int x = 1; x < _width; x++)
                        row[x] = (byte)(row[x] + row[x - 1]);
                }
                else
                {
                    row[0] = (byte)(row[0] + _prevRow[0]);
                    for (int x = 1; x < _width; x++)
                    {
                        int pred = row[x - 1] + _prevRow[x] - _prevRow[x - 1];
                        if (pred < 0) pred = 0;
                        else if (pred > 255) pred = 255;
                        row[x] = (byte)(row[x] + pred);
                    }
                }
                break;
        }
    }
}
