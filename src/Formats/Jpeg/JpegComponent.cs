using SharpImageConverter.Core;
using System.Buffers;

namespace SharpImageConverter.Formats.Jpeg;

/// <summary>JPEG 量化表（64 个自然序系数，zigzag 已展开）。</summary>
internal sealed class QuantizationTable
{
    public ushort[] Table { get; } = new ushort[64];
}

internal readonly record struct FrameHeader(int Width, int Height, int Precision, int MaxH, int MaxV, int McuX, int McuY);

/// <summary>SOF 中声明的单个分量，持有该分量的系数缓冲与解码状态。</summary>
internal sealed class ComponentState
{
    public byte Id { get; }
    public byte H { get; }
    public byte V { get; }
    public byte QuantTableId { get; }
    public int BlocksX { get; private set; }
    public int BlocksY { get; private set; }
    public bool HasCoefficients { get; set; }
    public int DcPredictor { get; set; }
    public byte DcTableId { get; private set; }
    public byte AcTableId { get; private set; }

    private NativeBufferOwner<short>? _coeffBuffer;

    public ComponentState(byte id, byte h, byte v, byte quantTableId)
    {
        Id = id;
        H = h;
        V = v;
        QuantTableId = quantTableId;
    }

    public void SetGeometry(int mcuX, int mcuY)
    {
        BlocksX = checked(mcuX * H);
        BlocksY = checked(mcuY * V);
    }

    public void EnsureCoefficientBuffer(bool isProgressive)
    {
        if (_coeffBuffer == null)
        {
            nuint totalShorts = checked((nuint)BlocksX * (nuint)BlocksY * 64u);
            if (totalShorts > int.MaxValue)
            {
                ThrowHelper.ThrowInvalidData("JPEG component buffer too large.");
            }

            _coeffBuffer = NativeBufferOwner<short>.Allocate((int)totalShorts, clear: isProgressive);
        }
    }

    public void FreeCoefficientBuffer()
    {
        _coeffBuffer?.Dispose();
        _coeffBuffer = null;
    }

    public void AssignTables(byte dc, byte ac)
    {
        DcTableId = dc;
        AcTableId = ac;
    }

    public void ResetPredictors()
    {
        DcPredictor = 0;
    }

    public Span<short> GetBlockSpan(int blockIndex)
    {
        return _coeffBuffer!.Span.Slice(blockIndex * 64, 64);
    }

    public void DecodeSpatial(Span<byte> output, int width, int height, int stride, ushort[] quantTable, bool useFloatingPointIdct)
    {
        if (_coeffBuffer == null) return;

        for (int by = 0; by < BlocksY; by++)
        {
            int basePy = by * 8;
            if (basePy >= height) break;

            for (int bx = 0; bx < BlocksX; bx++)
            {
                int basePx = bx * 8;
                if (basePx >= width) break;

                int blockIdx = by * BlocksX + bx;
                Span<short> block = GetBlockSpan(blockIdx);

                int rowOffset = basePy * stride;
                Span<byte> dest = output.Slice(rowOffset + basePx);

                if (useFloatingPointIdct)
                {
                    FloatingPointIDCT.Transform(block, quantTable, dest, stride);
                }
                else
                {
                    FastIDCT.Transform(block, quantTable, dest, stride);
                }
            }
        }
    }
}

internal readonly record struct ScanHeader(ScanComponent[] Components, byte Ss, byte Se, byte Ah, byte Al);

internal readonly record struct ScanComponent(byte ComponentId, byte DcTableId, byte AcTableId);

/// <summary>
/// 收集并重组 APP2 中的 ICC_PROFILE 分块。
/// </summary>
internal sealed class IccProfileCollector : IDisposable
{
    private readonly List<(byte[] Buffer, int Length)> chunks = new();
    public void Add(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 14) return;
        if (!segment.StartsWith("ICC_PROFILE"u8)) return;
        int len = segment.Length - 14;
        if (len <= 0) return;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(len);
        segment.Slice(14).CopyTo(chunk);
        chunks.Add((chunk, len));
    }
    public byte[]? GetProfile()
    {
        if (chunks.Count == 0) return null;
        int totalLen = 0;
        foreach (var chunk in chunks) totalLen += chunk.Length;
        byte[] profile = new byte[totalLen];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Buffer.AsSpan(0, chunk.Length).CopyTo(profile.AsSpan(offset));
            offset += chunk.Length;
        }
        Dispose();
        return profile;
    }

    public void Dispose()
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            ArrayPool<byte>.Shared.Return(chunks[i].Buffer);
        }
        chunks.Clear();
    }
}

/// <summary>流式解码时持有单个 segment 的内容（租用缓冲，Dispose 归还）。</summary>
internal sealed class SegmentBuffer : IDisposable
{
    private readonly byte[] buffer;
    public int Length { get; }
    public ReadOnlySpan<byte> Span => buffer.AsSpan(0, Length);
    public SegmentBuffer(byte[] buffer, int length) { this.buffer = buffer; Length = length; }
    public void Dispose() => ArrayPool<byte>.Shared.Return(buffer);
}
