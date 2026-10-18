using System;
using System.Collections.Generic;

namespace SharpImageConverter.Formats.Jpeg;

internal sealed partial class JpegFrameState
{
    internal QuantizationTable[] QuantTables => quantTables;
    internal int RestartIntervalValue => restartInterval;
    internal JpegColorSpace ColorSpace => JpegDecoder.DetermineColorSpace(components, hasJfif, hasAdobe, adobeTransform);

    /// <summary>基线行级解码的 restart 状态（跨 MCU 行保持）。</summary>
    internal struct BaselineRestartState
    {
        public int ExpectedRst;
        public int UnitsUntilRestart;
        public int EobRun;
    }

    /// <summary>
    /// 为逐 MCU 行基线解码做准备：校验扫描/表并绑定 Huffman 表，但**不分配整幅系数缓冲**。
    /// </summary>
    internal ComponentState[] PrepareBaselineRowScan(in ScanHeader scan)
    {
        if (isProgressive)
        {
            throw new NotSupportedException("Progressive JPEG cannot be decoded row-by-row without keeping full coefficients");
        }
        if (scan.Components.Length != components.Length)
        {
            throw new NotSupportedException("Only interleaved baseline JPEG is supported for fused row decoding");
        }
        if (scan.Ss != 0 || scan.Se != 63 || scan.Ah != 0 || scan.Al != 0)
        {
            throw new NotSupportedException("Unsupported baseline scan parameters");
        }

        var result = new ComponentState[scan.Components.Length];
        for (int i = 0; i < scan.Components.Length; i++)
        {
            ScanComponent sc = scan.Components[i];
            ComponentState comp = FindComponent(sc.ComponentId);
            if (!quantTableDefined[comp.QuantTableId])
            {
                ThrowHelper.ThrowInvalidData("Missing quantization table for component.");
            }
            if (!dcTableDefined[sc.DcTableId])
            {
                ThrowHelper.ThrowInvalidData("Missing DC Huffman table.");
            }
            if (!acTableDefined[sc.AcTableId])
            {
                ThrowHelper.ThrowInvalidData("Missing AC Huffman table.");
            }
            comp.AssignTables(sc.DcTableId, sc.AcTableId);
            comp.ResetPredictors();
            result[i] = comp;
        }
        return result;
    }

    /// <summary>
    /// 解码一个 MCU 行的系数到调用方提供的块数组（每块 64 short，按分量/块顺序紧凑排列）。
    /// </summary>
    internal void DecodeBaselineRowBlocks(
        in ScanHeader scan,
        ComponentState[] scanComponents,
        ref JpegBitReader reader,
        int mcuRow,
        Span<short> blocks,
        ref BaselineRestartState restart)
    {
        int offset = 0;
        for (int mx = 0; mx < frame.McuX; mx++)
        {
            for (int ci = 0; ci < scanComponents.Length; ci++)
            {
                ComponentState comp = scanComponents[ci];
                for (int v = 0; v < comp.V; v++)
                {
                    for (int h = 0; h < comp.H; h++)
                    {
                        Span<short> block = blocks.Slice(offset, 64);
                        block.Clear();
                        try
                        {
                            DecodeBaselineBlock(ref reader, comp, block);
                        }
                        catch (InvalidDataException ex)
                        {
                            ThrowHelper.ThrowInvalidData(
                                $"Row scan decode failed (mx={mx}, my={mcuRow}, componentId={comp.Id}, " +
                                $"bytesConsumed={reader.BytesConsumed}, bitCount={reader.BitCount}, pendingMarker={reader.PendingMarker}). {ex.Message}");
                        }
                        offset += 64;
                    }
                }
            }

            if (restartInterval != 0 && --restart.UnitsUntilRestart == 0)
            {
                ProcessRestart(ref reader, ref restart.ExpectedRst, scan.Components, ref restart.EobRun);
                restart.UnitsUntilRestart = restartInterval;
            }
        }
    }

    internal int CountBlocksPerMcuRow(ComponentState[] scanComponents)
    {
        int count = 0;
        foreach (ComponentState comp in scanComponents)
        {
            count += comp.H * comp.V;
        }
        return count;
    }
}
