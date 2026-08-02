using System.Buffers;
using System.Runtime.Intrinsics.X86;

namespace SharpImageConverter.Formats.Jpeg;

public static partial class JpegDecoder
{
    // ---------- 快速路径重建 ----------

    internal static bool TryReconstructGrayDirect(FrameHeader frame, ComponentState component, QuantizationTable[] quantTables, bool useFloatingPointIdct, byte[] output)
    {
        int width = frame.Width;
        int height = frame.Height;
        int w = frame.McuX * component.H * 8;
        int h = frame.McuY * component.V * 8;
        if (w < width || h < height)
        {
            return false;
        }

        if (w == width && h == height)
        {
            component.DecodeSpatial(output.AsSpan(0, w * h), w, h, w, quantTables[component.QuantTableId].Table, useFloatingPointIdct);
            return true;
        }

        byte[] plane = ArrayPool<byte>.Shared.Rent(w * h);
        try
        {
            component.DecodeSpatial(plane.AsSpan(0, w * h), w, h, w, quantTables[component.QuantTableId].Table, useFloatingPointIdct);
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(plane, y * w, output, y * width, width);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plane);
        }

        return true;
    }

    internal static bool TryReconstructRgbDirect(FrameHeader frame, ComponentState[] components, QuantizationTable[] quantTables, ReadOnlySpan<int> componentOrder, bool useFloatingPointIdct, byte[] output)
    {
        int maxH = frame.MaxH;
        int maxV = frame.MaxV;
        for (int channel = 0; channel < 3; channel++)
        {
            ComponentState component = components[componentOrder[channel]];
            if (component.H != maxH || component.V != maxV)
            {
                return false;
            }
        }

        int width = frame.Width;
        int height = frame.Height;
        int fullWidth = frame.McuX * maxH * 8;
        int fullHeight = frame.McuY * maxV * 8;
        for (int channel = 0; channel < 3; channel++)
        {
            ComponentState component = components[componentOrder[channel]];
            byte[] plane = ArrayPool<byte>.Shared.Rent(fullWidth * fullHeight);
            try
            {
                component.DecodeSpatial(plane.AsSpan(0, fullWidth * fullHeight), fullWidth, fullHeight, fullWidth, quantTables[component.QuantTableId].Table, useFloatingPointIdct);
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * fullWidth;
                    int dstRow = y * width * 3 + channel;
                    for (int x = 0; x < width; x++)
                    {
                        output[dstRow + x * 3] = plane[srcRow + x];
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(plane);
            }
        }

        return true;
    }

    internal static bool TryDecodeInterleavedYCbCrSimd(ComponentState[] components, byte[] output, int width, int height, int fullWidth, int fullHeight, int[] componentOrder, QuantizationTable[] quantTables, bool useFloatingPointIdct, FrameHeader frame)
    {
        if (!Sse2.IsSupported || useFloatingPointIdct) return false;
        if (componentOrder.Length != 3) return false;
        int yIdx = componentOrder[0];
        int cbIdx = componentOrder[1];
        int crIdx = componentOrder[2];

        ComponentState yComp = components[yIdx];
        ComponentState cbComp = components[cbIdx];
        ComponentState crComp = components[crIdx];

        bool is444 = yComp.H == 1 && yComp.V == 1 && cbComp.H == 1 && cbComp.V == 1 && crComp.H == 1 && crComp.V == 1;
        bool is420 = yComp.H == 2 && yComp.V == 2 && cbComp.H == 1 && cbComp.V == 1 && crComp.H == 1 && crComp.V == 1;

        if (!is444 && !is420) return false;

        int mcuX = frame.McuX;
        int mcuY = frame.McuY;
        ushort[] yQuant = quantTables[yComp.QuantTableId].Table;
        ushort[] cbQuant = quantTables[cbComp.QuantTableId].Table;
        ushort[] crQuant = quantTables[crComp.QuantTableId].Table;

        for (int my = 0; my < mcuY; my++)
        {
            for (int mx = 0; mx < mcuX; mx++)
            {
                if (is444)
                {
                    DecodeMcu444Simd(mx, my, yComp, cbComp, crComp, yQuant, cbQuant, crQuant, output, width, height);
                }
                else if (is420)
                {
                    DecodeMcu420Simd(mx, my, yComp, cbComp, crComp, yQuant, cbQuant, crQuant, output, width, height);
                }
            }
        }

        return true;
    }

    private static void DecodeMcu444Simd(int mx, int my, ComponentState yComp, ComponentState cbComp, ComponentState crComp,
        ushort[] yQuant, ushort[] cbQuant, ushort[] crQuant, byte[] output, int width, int height)
    {
        int px = mx * 8;
        int py = my * 8;
        if (px >= width || py >= height) return;

        ReadOnlySpan<short> yBlock = yComp.GetBlockSpan(my * yComp.BlocksX + mx);
        ReadOnlySpan<short> cbBlock = cbComp.GetBlockSpan(my * cbComp.BlocksX + mx);
        ReadOnlySpan<short> crBlock = crComp.GetBlockSpan(my * crComp.BlocksX + mx);

        int stride = width * 3;
        if (px + 8 <= width && py + 8 <= height)
        {
            SimdJpegPipeline.TransformAndConvertYCbCr8x8(yBlock, yQuant, cbBlock, cbQuant, crBlock, crQuant, output.AsSpan(py * stride + px * 3), stride);
            return;
        }

        Span<byte> temp = stackalloc byte[8 * 8 * 3];
        SimdJpegPipeline.TransformAndConvertYCbCr8x8(yBlock, yQuant, cbBlock, cbQuant, crBlock, crQuant, temp, 8 * 3);
        int copyW = Math.Min(8, width - px);
        int copyH = Math.Min(8, height - py);
        for (int y = 0; y < copyH; y++)
        {
            int src = y * 8 * 3;
            int dst = (py + y) * stride + px * 3;
            temp.Slice(src, copyW * 3).CopyTo(output.AsSpan(dst));
        }
    }

    private static void DecodeMcu420Simd(int mx, int my, ComponentState yComp, ComponentState cbComp, ComponentState crComp,
        ushort[] yQuant, ushort[] cbQuant, ushort[] crQuant, byte[] output, int width, int height)
    {
        int px = mx * 16;
        int py = my * 16;
        if (px >= width || py >= height) return;

        int blocksX = yComp.BlocksX;
        ReadOnlySpan<short> y0 = yComp.GetBlockSpan((my * 2 + 0) * blocksX + (mx * 2 + 0));
        ReadOnlySpan<short> y1 = yComp.GetBlockSpan((my * 2 + 0) * blocksX + (mx * 2 + 1));
        ReadOnlySpan<short> y2 = yComp.GetBlockSpan((my * 2 + 1) * blocksX + (mx * 2 + 0));
        ReadOnlySpan<short> y3 = yComp.GetBlockSpan((my * 2 + 1) * blocksX + (mx * 2 + 1));
        ReadOnlySpan<short> cb = cbComp.GetBlockSpan(my * cbComp.BlocksX + mx);
        ReadOnlySpan<short> cr = crComp.GetBlockSpan(my * crComp.BlocksX + mx);

        int stride = width * 3;
        if (px + 16 <= width && py + 16 <= height)
        {
            SimdJpegPipeline.TransformAndConvertYCbCr420(y0, y1, y2, y3, yQuant, cb, cbQuant, cr, crQuant, output.AsSpan(py * stride + px * 3), stride);
            return;
        }

        Span<byte> temp = stackalloc byte[16 * 16 * 3];
        SimdJpegPipeline.TransformAndConvertYCbCr420(y0, y1, y2, y3, yQuant, cb, cbQuant, cr, crQuant, temp, 16 * 3);
        int copyW = Math.Min(16, width - px);
        int copyH = Math.Min(16, height - py);
        for (int y = 0; y < copyH; y++)
        {
            int src = y * 16 * 3;
            int dst = (py + y) * stride + px * 3;
            temp.Slice(src, copyW * 3).CopyTo(output.AsSpan(dst));
        }
    }

    // ---------- 色彩空间判定与分量排序 ----------

    internal static JpegColorSpace DetermineColorSpace(ComponentState[] components, bool hasJfif, bool hasAdobe, byte adobeTransform)
    {
        int componentCount = components.Length;
        if (componentCount == 1)
        {
            return JpegColorSpace.Gray;
        }

        if (componentCount == 3)
        {
            if (hasAdobe)
            {
                if (adobeTransform == 0)
                {
                    return JpegColorSpace.Rgb;
                }

                if (adobeTransform == 1)
                {
                    return JpegColorSpace.YCbCr;
                }
            }

            if (HasComponentIds(components, [(byte)'R', (byte)'G', (byte)'B']))
            {
                return JpegColorSpace.Rgb;
            }

            if (hasJfif || HasComponentIds(components, [1, 2, 3]))
            {
                return JpegColorSpace.YCbCr;
            }

            return JpegColorSpace.YCbCr;
        }

        if (componentCount == 4)
        {
            if (hasAdobe)
            {
                if (adobeTransform == 0)
                {
                    return JpegColorSpace.Cmyk;
                }

                if (adobeTransform == 2)
                {
                    return JpegColorSpace.Ycck;
                }
            }

            if (HasComponentIds(components, [(byte)'C', (byte)'M', (byte)'Y', (byte)'K']))
            {
                return JpegColorSpace.Cmyk;
            }

            if (HasComponentIds(components, [1, 2, 3, 4]))
            {
                // Default 4-component is often YCbCrK (YCCK) in many JPEG libraries if no Adobe APP14
                return JpegColorSpace.Ycck;
            }

            return JpegColorSpace.Unknown4;
        }

        return JpegColorSpace.Unknown4;
    }

    internal static JpegPixelFormat PixelFormatFromColorSpace(JpegColorSpace colorSpace)
    {
        return colorSpace switch
        {
            JpegColorSpace.Gray => JpegPixelFormat.Gray8,
            JpegColorSpace.Rgb => JpegPixelFormat.Rgb24,
            JpegColorSpace.YCbCr => JpegPixelFormat.YCbCr24,
            JpegColorSpace.Cmyk => JpegPixelFormat.Cmyk32,
            JpegColorSpace.Ycck => JpegPixelFormat.Ycck32,
            JpegColorSpace.Unknown4 => JpegPixelFormat.Unknown32,
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace))
        };
    }

    internal static int[] BuildComponentOrder(ComponentState[] components, JpegColorSpace colorSpace)
    {
        return colorSpace switch
        {
            JpegColorSpace.Gray => BuildComponentOrder(components, [1]),
            JpegColorSpace.Rgb => BuildComponentOrderWithFallback(components, [1, 2, 3], [(byte)'R', (byte)'G', (byte)'B']),
            JpegColorSpace.YCbCr => BuildComponentOrder(components, [1, 2, 3]),
            JpegColorSpace.Cmyk => BuildComponentOrderWithFallback(components, [1, 2, 3, 4], [(byte)'C', (byte)'M', (byte)'Y', (byte)'K']),
            JpegColorSpace.Ycck => BuildComponentOrder(components, [1, 2, 3, 4]),
            JpegColorSpace.Unknown4 => BuildSequentialOrder(components.Length),
            _ => BuildSequentialOrder(components.Length)
        };
    }

    private static int[] BuildComponentOrder(ComponentState[] components, ReadOnlySpan<byte> expectedIds)
    {
        if (TryBuildComponentOrder(components, expectedIds, out int[] order))
        {
            return order;
        }

        return BuildSequentialOrder(components.Length);
    }

    private static int[] BuildSequentialOrder(int count)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        return order;
    }

    private static int[] BuildComponentOrderWithFallback(ComponentState[] components, ReadOnlySpan<byte> expectedIds, ReadOnlySpan<byte> fallbackIds)
    {
        if (TryBuildComponentOrder(components, expectedIds, out int[] order))
        {
            return order;
        }

        if (TryBuildComponentOrder(components, fallbackIds, out int[] fallback))
        {
            return fallback;
        }

        return BuildSequentialOrder(components.Length);
    }

    private static bool TryBuildComponentOrder(ComponentState[] components, ReadOnlySpan<byte> expectedIds, out int[] order)
    {
        order = new int[expectedIds.Length];
        for (int i = 0; i < expectedIds.Length; i++)
        {
            int index = FindComponentIndex(components, expectedIds[i]);
            if (index < 0)
            {
                order = Array.Empty<int>();
                return false;
            }

            order[i] = index;
        }

        return true;
    }

    internal static int FindComponentIndex(ComponentState[] components, byte id)
    {
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool HasComponentIds(ComponentState[] components, ReadOnlySpan<byte> expectedIds)
    {
        if (components.Length != expectedIds.Length)
        {
            return false;
        }

        for (int i = 0; i < expectedIds.Length; i++)
        {
            if (FindComponentIndex(components, expectedIds[i]) < 0)
            {
                return false;
            }
        }

        return true;
    }

    // ---------- 平面交织与上采样 ----------

    internal static void InterleaveComponents(
        byte[][] planes,
        int[] planeStrides,
        int[] planeWidths,
        int[] planeHeights,
        int fullWidth,
        int fullHeight,
        int width,
        int height,
        int[] componentOrder,
        byte[] output)
    {
        int channels = componentOrder.Length;
        int outStride = width * channels;

        bool directSample = true;
        for (int c = 0; c < channels; c++)
        {
            int planeIndex = componentOrder[c];
            if (planeWidths[planeIndex] != fullWidth || planeHeights[planeIndex] != fullHeight)
            {
                directSample = false;
                break;
            }
        }

        if (directSample)
        {
            Span<byte> outputSpan = output;
            for (int y = 0; y < height; y++)
            {
                int rowOut = y * outStride;
                for (int c = 0; c < channels; c++)
                {
                    int planeIndex = componentOrder[c];
                    int srcRow = y * planeStrides[planeIndex];
                    ReadOnlySpan<byte> plane = planes[planeIndex];
                    int outIndex = rowOut + c;
                    for (int x = 0; x < width; x++)
                    {
                        outputSpan[outIndex] = plane[srcRow + x];
                        outIndex += channels;
                    }
                }
            }

            return;
        }

        int mapLength = width * channels;
        int[] x0Map = ArrayPool<int>.Shared.Rent(mapLength);
        int[] x1Map = ArrayPool<int>.Shared.Rent(mapLength);
        byte[] xWeightMap = ArrayPool<byte>.Shared.Rent(mapLength);
        try
        {
            for (int c = 0; c < channels; c++)
            {
                int planeIndex = componentOrder[c];
                int planeW = planeWidths[planeIndex];
                int baseIndex = c * width;
                for (int x = 0; x < width; x++)
                {
                    ComputeLinearSample(x, width, planeW, fullWidth, out int sx0, out int sx1, out byte xWeight);
                    x0Map[baseIndex + x] = sx0;
                    x1Map[baseIndex + x] = sx1;
                    xWeightMap[baseIndex + x] = xWeight;
                }
            }

            for (int y = 0; y < height; y++)
            {
                int rowOut = y * outStride;
                Span<byte> outputRow = output.AsSpan(rowOut, outStride);
                for (int c = 0; c < channels; c++)
                {
                    int planeIndex = componentOrder[c];
                    int planeH = planeHeights[planeIndex];
                    ComputeLinearSample(y, height, planeH, fullHeight, out int sy0, out int sy1, out byte yWeight);
                    int srcRow0 = sy0 * planeStrides[planeIndex];
                    int srcRow1 = sy1 * planeStrides[planeIndex];
                    ReadOnlySpan<byte> plane = planes[planeIndex];
                    int mapBase = c * width;
                    int outIndex = c;
                    for (int x = 0; x < width; x++)
                    {
                        int mapIndex = mapBase + x;
                        int sx0 = x0Map[mapIndex];
                        int sx1 = x1Map[mapIndex];
                        int wx = xWeightMap[mapIndex];
                        int top = ((plane[srcRow0 + sx0] * (256 - wx)) + (plane[srcRow0 + sx1] * wx) + 128) >> 8;
                        int bottom = ((plane[srcRow1 + sx0] * (256 - wx)) + (plane[srcRow1 + sx1] * wx) + 128) >> 8;
                        int value = ((top * (256 - yWeight)) + (bottom * yWeight) + 128) >> 8;
                        outputRow[outIndex] = (byte)value;
                        outIndex += channels;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(x0Map);
            ArrayPool<int>.Shared.Return(x1Map);
            ArrayPool<byte>.Shared.Return(xWeightMap);
        }
    }

    private static void ComputeLinearSample(int dstIndex, int dstLength, int srcLength, int fullLength, out int index0, out int index1, out byte weight1)
    {
        if (srcLength <= 1 || fullLength <= 0 || dstLength <= 1)
        {
            index0 = 0;
            index1 = 0;
            weight1 = 0;
            return;
        }

        float srcPos = (((dstIndex + 0.5f) * srcLength) / fullLength) - 0.5f;
        int i0 = (int)MathF.Floor(srcPos);
        float frac = srcPos - i0;

        if (i0 < 0)
        {
            i0 = 0;
            frac = 0f;
        }
        else if (i0 >= srcLength - 1)
        {
            i0 = srcLength - 1;
            frac = 0f;
        }

        index0 = i0;
        index1 = i0 < srcLength - 1 ? i0 + 1 : i0;
        int w = (int)(frac * 256f + 0.5f);
        if (w < 0) w = 0;
        if (w > 255) w = 255;
        weight1 = (byte)w;
    }

    // ---------- EXIF ----------

    internal static void TryStoreExifApp1(ReadOnlySpan<byte> segment, ref byte[]? exifRaw, ref int exifOrientation)
    {
        if (segment.Length < 6)
        {
            return;
        }

        if (segment[0] != (byte)'E' ||
            segment[1] != (byte)'x' ||
            segment[2] != (byte)'i' ||
            segment[3] != (byte)'f' ||
            segment[4] != 0 ||
            segment[5] != 0)
        {
            return;
        }

        exifRaw ??= segment.ToArray();

        if (exifOrientation == 1)
        {
            int orientation = ParseExifOrientation(segment);
            if (orientation >= 1 && orientation <= 8)
            {
                exifOrientation = orientation;
            }
        }
    }

    private static int ParseExifOrientation(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) return 1;
        if (data[0] != (byte)'E' || data[1] != (byte)'x' || data[2] != (byte)'i' || data[3] != (byte)'f' || data[4] != 0 || data[5] != 0)
            return 1;

        int tiffBase = 6;
        bool littleEndian;
        if (data[tiffBase + 0] == (byte)'I' && data[tiffBase + 1] == (byte)'I') littleEndian = true;
        else if (data[tiffBase + 0] == (byte)'M' && data[tiffBase + 1] == (byte)'M') littleEndian = false;
        else return 1;

        if (ReadU16(data, tiffBase + 2, littleEndian) != 42) return 1;
        uint ifdOffset = ReadU32(data, tiffBase + 4, littleEndian);
        if (ifdOffset == 0) return 1;

        int p = tiffBase + (int)ifdOffset;
        ushort entryCount = ReadU16(data, p, littleEndian);
        p += 2;

        for (int i = 0; i < entryCount; i++)
        {
            ushort tag = ReadU16(data, p, littleEndian);
            if (tag == 0x0112) // Orientation
            {
                ushort type = ReadU16(data, p + 2, littleEndian);
                uint count = ReadU32(data, p + 4, littleEndian);
                if (type == 3 && count == 1) // SHORT
                {
                    return ReadU16(data, p + 8, littleEndian);
                }
            }

            p += 12;
        }

        return 1;

        static ushort ReadU16(ReadOnlySpan<byte> s, int offset, bool le)
        {
            if (offset < 0 || offset + 2 > s.Length) return 0;
            return le ? (ushort)(s[offset] | (s[offset + 1] << 8)) : (ushort)((s[offset] << 8) | s[offset + 1]);
        }

        static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool le)
        {
            if (offset < 0 || offset + 4 > s.Length) return 0;
            return le
                ? (uint)(s[offset] | (s[offset + 1] << 8) | (s[offset + 2] << 16) | (s[offset + 3] << 24))
                : (uint)((s[offset] << 24) | (s[offset + 1] << 16) | (s[offset + 2] << 8) | s[offset + 3]);
        }
    }
}
