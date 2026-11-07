using System;

namespace SharpImageConverter.Formats.Webp;

/// <summary>
/// VP8（有损）行级流式 RGBA 解码器。
///
/// 复用 <see cref="Vp8Decoder"/> 的窗口化模式：重建平面保留 17/9 行、输出平面保留 32/16 行，
/// 每完成一个宏块行后交付其已最终化的输出行，再平移窗口。亮度按 fancy upsampling 的行对
/// （2 行）交付，天然形成 "1 个宏块行 + 1 个亮度行" 的延迟；逐行转换复用
/// <see cref="Vp8YuvToRgb.ConvertLinePair"/>，与整帧路径逐位一致。
///
/// 线程安全：实例不可跨线程并发使用；不同实例可并发解码不同图像。
/// </summary>
internal sealed class Vp8RowDecoder
{
    private readonly Vp8Decoder _decoder;
    private readonly int _width;
    private readonly int _height;

    // 行对转换暂存：2 行 × width × 4 字节。稳态下仅复用，不再分配。
    private readonly byte[] _pairRgba;

    private int _nextOutRow;
    private int _emitThrough = -1;
    private bool _needsSlide;
    private bool _flushed;
    private bool _finished;

    private int _stageCount;
    private int _stageOffset;

    /// <summary>
    /// 使用完整的 VP8 编码数据（含帧标签、分区等，不含 RIFF/chunk 头）创建行解码器。
    /// </summary>
    /// <exception cref="WebpBitstreamException">VP8 头或分区数据损坏/截断。</exception>
    internal Vp8RowDecoder(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _decoder = Vp8Decoder.CreateWindowed(payload);
        _decoder.BeginRowStream();
        _width = _decoder.Width;
        _height = _decoder.Height;
        _pairRgba = new byte[checked(_width * 8)];
    }

    internal int Width => _width;

    internal int Height => _height;

    /// <summary>是否已交付全部输出行。</summary>
    internal bool IsFinished => _finished && _stageCount == 0;

    /// <summary>
    /// 读取下一行 RGBA（每像素 4 字节，A=255）。行序为图像自上而下 0..Height-1。
    /// 数据耗尽后返回 false。VP8 码流损坏时抛出 <see cref="WebpBitstreamException"/>。
    /// </summary>
    internal bool TryReadRgbaRow(Span<byte> dst)
    {
        if (dst.Length < checked(_width * 4))
            throw new ArgumentException("目标缓冲区不足以容纳一行 RGBA。", nameof(dst));

        while (_stageCount == 0)
        {
            if (!Advance())
                return false;
        }

        _pairRgba.AsSpan(_stageOffset, _width * 4).CopyTo(dst);
        _stageCount--;
        _stageOffset += _width * 4;
        if (_stageCount == 0)
            _stageOffset = 0;
        return true;
    }

    /// <summary>
    /// 推进解码状态：优先交付已最终化的行，其次平移窗口，再解码下一宏块行。
    /// 返回 true 表示已产生至少一行暂存输出。
    /// </summary>
    private bool Advance()
    {
        while (true)
        {
            if (_nextOutRow <= _emitThrough)
            {
                ConvertNext();
                if (_stageCount > 0)
                    return true;
                continue;
            }

            if (_needsSlide)
            {
                _decoder.SlideWindows();
                _needsSlide = false;
                continue;
            }

            if (_decoder.HasMoreMacroblockRows)
            {
                _decoder.DecodeNextMacroblockRow();
                int mbY = _decoder.CurrentMacroblockRow;

                // 本次滤波完成后，亮度行 0..16*mbY（含跨界行 16*mbY）与色度行 0..8*mbY 均已最终化。
                // 宏块网格行超出图像高度时，只交付到 Height-1。
                _emitThrough = Math.Min(16 * mbY, _height - 1);

                // 最后一行宏块不再平移，保证 Flush 时剩余行仍位于当前半区。
                _needsSlide = _decoder.HasMoreMacroblockRows;
                continue;
            }

            if (!_flushed)
            {
                _flushed = true;
                _emitThrough = _height - 1;
                continue;
            }

            _finished = true;
            return false;
        }
    }

    /// <summary>转换并暂存下一行（或行对）。</summary>
    private void ConvertNext()
    {
        int r = _nextOutRow;
        int mbY = _decoder.CurrentMacroblockRow;
        int yStride = _decoder.StrideOutY;
        int uvStride = _decoder.StrideOutUv;

        if (r == 0)
        {
            // 首行：libwebp 对 image y=0 使用 top_uv = cur_uv = 0 的镜像行。
            int yOff = LumaOff(0, mbY, yStride);
            int uvOff = ChromaOff(0, mbY, uvStride);
            Vp8YuvToRgb.ConvertLinePair(
                _decoder.OutY, yOff, -1,
                _decoder.OutU, _decoder.OutV, uvOff, uvOff,
                _pairRgba, 0, -1, _width);
            _stageCount = 1;
            _nextOutRow = 1;
            return;
        }

        int chromaHeight = (_height + 1) >> 1;

        if (r + 1 < _height)
        {
            // 常规行对 (r, r+1)，r 必为奇数：上/下色度行为 (r-1)/2 与 (r-1)/2+1。
            int topY = LumaOff(r, mbY, yStride);
            int bottomY = LumaOff(r + 1, mbY, yStride);
            int topUvRow = (r - 1) >> 1;
            int topUv = ChromaOff(topUvRow, mbY, uvStride);
            int curUv = ChromaOff(topUvRow + 1, mbY, uvStride);
            Vp8YuvToRgbaPair(topY, bottomY, topUv, curUv);
            _stageCount = 2;
            _nextOutRow = r + 2;
            return;
        }

        // 尾行（高度为偶数时的最后一行）：top_uv = cur_uv = chromaHeight-1 的镜像行。
        int lastY = LumaOff(r, mbY, yStride);
        int lastUv = ChromaOff(chromaHeight - 1, mbY, uvStride);
        Vp8YuvToRgbaPair(lastY, -1, lastUv, lastUv);
        _stageCount = 1;
        _nextOutRow = r + 1;
    }

    private void Vp8YuvToRgbaPair(int topY, int bottomY, int topUv, int curUv) =>
        Vp8YuvToRgb.ConvertLinePair(
            _decoder.OutY, topY, bottomY,
            _decoder.OutU, _decoder.OutV, topUv, curUv,
            _pairRgba, 0, bottomY < 0 ? -1 : _width * 4, _width);

    // 输出窗口布局：前半区 0..15（亮度）/ 0..7（色度）为上一宏块行，后半区为当前宏块行。
    private static int LumaOff(int row, int mbY, int stride) =>
        (row < 16 * mbY ? row - 16 * (mbY - 1) : 16 + row - 16 * mbY) * stride;

    private static int ChromaOff(int row, int mbY, int stride) =>
        (row < 8 * mbY ? row - 8 * (mbY - 1) : 8 + row - 8 * mbY) * stride;
}
