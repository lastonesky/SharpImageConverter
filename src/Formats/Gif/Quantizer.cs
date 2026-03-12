using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// 高质量颜色量化器，实现 Wu's Color Quantizer 算法并支持极限优化后的 Floyd-Steinberg 抖动。
/// </summary>
public class Quantizer
{
    private const int BITS = 5;
    private const int SIZE = 33; // 2^5 + 1
    private const int MaxColors = 256;

    private readonly long[] _vwt = new long[SIZE * SIZE * SIZE];
    private readonly long[] _vmr = new long[SIZE * SIZE * SIZE];
    private readonly long[] _vmg = new long[SIZE * SIZE * SIZE];
    private readonly long[] _vmb = new long[SIZE * SIZE * SIZE];
    private readonly double[] _m2 = new double[SIZE * SIZE * SIZE];

    private struct Box
    {
        public int r0, r1;
        public int g0, g1;
        public int b0, b1;
        public int vol;
    }

    /// <summary>
    /// 对图像进行高质量量化，生成 256 色调色板并支持抖动处理。
    /// </summary>
    /// <param name="pixels">原始 RGB 像素数据</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <param name="enableDithering">是否启用 Floyd-Steinberg 抖动</param>
    /// <returns>调色板与像素索引</returns>
    public static (byte[] Palette, byte[] Indices) Quantize(byte[] pixels, int width, int height, bool enableDithering = true)
    {
        var q = new Quantizer();
        return q.QuantizeInternal(pixels, width, height, enableDithering);
    }

    private (byte[] Palette, byte[] Indices) QuantizeInternal(byte[] pixels, int width, int height, bool enableDithering)
    {
        Stopwatch sw = Stopwatch.StartNew();
        
        BuildHistogramParallel(pixels);
        long tHistogram = sw.ElapsedMilliseconds;
        
        CalculateMoments();
        long tMoments = sw.ElapsedMilliseconds - tHistogram;

        Box[] cube = new Box[MaxColors];
        cube[0].r0 = cube[0].g0 = cube[0].b0 = 0;
        cube[0].r1 = cube[0].g1 = cube[0].b1 = SIZE - 1;
        
        double[] vv = new double[MaxColors];
        int next = 0;
        int actualK = 1;
        for (int i = 1; i < MaxColors; i++)
        {
            if (Split(ref cube[next], ref cube[i]))
            {
                vv[next] = cube[next].vol > 1 ? Variance(ref cube[next]) : 0;
                vv[i] = cube[i].vol > 1 ? Variance(ref cube[i]) : 0;
            }
            else
            {
                vv[next] = 0;
                i--;
            }
            next = 0;
            double temp = vv[0];
            for (int j = 1; j <= i; j++)
            {
                if (vv[j] > temp)
                {
                    temp = vv[j];
                    next = j;
                }
            }
            if (temp <= 0) { actualK = i + 1; break; }
            actualK = i + 1;
        }
        long tSplitting = sw.ElapsedMilliseconds - tHistogram - tMoments;

        byte[] palette = new byte[actualK * 3];
        for (int i = 0; i < actualK; i++)
        {
            long weight = Vol(ref cube[i], _vwt);
            if (weight > 0)
            {
                palette[i * 3 + 0] = (byte)(Vol(ref cube[i], _vmr) / weight);
                palette[i * 3 + 1] = (byte)(Vol(ref cube[i], _vmg) / weight);
                palette[i * 3 + 2] = (byte)(Vol(ref cube[i], _vmb) / weight);
            }
        }
        long tPalette = sw.ElapsedMilliseconds - tHistogram - tMoments - tSplitting;

        byte[] mappingLut = BuildMappingLut(palette);
        long tLut = sw.ElapsedMilliseconds - tHistogram - tMoments - tSplitting - tPalette;

        byte[] indices;
        if (enableDithering)
        {
            indices = ApplyDitheringWithLut(pixels, width, height, palette, mappingLut);
        }
        else
        {
            indices = ApplyMappingOnly(pixels, width, height, mappingLut);
        }
        long tMapping = sw.ElapsedMilliseconds - tHistogram - tMoments - tSplitting - tPalette - tLut;

        Console.WriteLine($"[Quantizer] Perf: Dithering={enableDithering}, Hist={tHistogram}ms, Moments={tMoments}ms, Split={tSplitting}ms, Palette={tPalette}ms, LUT={tLut}ms, Map={tMapping}ms, Total={sw.ElapsedMilliseconds}ms");
        
        return (palette, indices);
    }

    private void BuildHistogramParallel(byte[] pixels)
    {
        Array.Clear(_vwt, 0, _vwt.Length);
        Array.Clear(_vmr, 0, _vmr.Length);
        Array.Clear(_vmg, 0, _vmg.Length);
        Array.Clear(_vmb, 0, _vmb.Length);
        Array.Clear(_m2, 0, _m2.Length);

        int threadCount = Environment.ProcessorCount;
        int len = pixels.Length / 3;
        int blockSize = len / threadCount;

        Parallel.For(0, threadCount, t =>
        {
            long[] tVwt = new long[SIZE * SIZE * SIZE];
            long[] tVmr = new long[SIZE * SIZE * SIZE];
            long[] tVmg = new long[SIZE * SIZE * SIZE];
            long[] tVmb = new long[SIZE * SIZE * SIZE];
            double[] tM2 = new double[SIZE * SIZE * SIZE];

            int start = t * blockSize;
            int end = (t == threadCount - 1) ? len : (t + 1) * blockSize;

            for (int i = start; i < end; i++)
            {
                int baseIdx = i * 3;
                int r = (pixels[baseIdx] >> 3) + 1;
                int g = (pixels[baseIdx + 1] >> 3) + 1;
                int b = (pixels[baseIdx + 2] >> 3) + 1;
                int idx = (r * SIZE * SIZE) + (g * SIZE) + b;
                tVwt[idx]++;
                tVmr[idx] += pixels[baseIdx];
                tVmg[idx] += pixels[baseIdx + 1];
                tVmb[idx] += pixels[baseIdx + 2];
                tM2[idx] += (double)pixels[baseIdx] * pixels[baseIdx] + (double)pixels[baseIdx + 1] * pixels[baseIdx + 1] + (double)pixels[baseIdx + 2] * pixels[baseIdx + 2];
            }

            lock (_vwt)
            {
                for (int i = 0; i < _vwt.Length; i++)
                {
                    _vwt[i] += tVwt[i];
                    _vmr[i] += tVmr[i];
                    _vmg[i] += tVmg[i];
                    _vmb[i] += tVmb[i];
                    _m2[i] += tM2[i];
                }
            }
        });
    }

    private void CalculateMoments()
    {
        for (int r = 1; r < SIZE; r++)
        {
            long[] areaWt = new long[SIZE], areaMr = new long[SIZE], areaMg = new long[SIZE], areaMb = new long[SIZE];
            double[] areaM2 = new double[SIZE];
            for (int g = 1; g < SIZE; g++)
            {
                long lineWt = 0, lineMr = 0, lineMg = 0, lineMb = 0;
                double lineM2 = 0;
                for (int b = 1; b < SIZE; b++)
                {
                    int idx = (r * SIZE * SIZE) + (g * SIZE) + b;
                    lineWt += _vwt[idx]; lineMr += _vmr[idx]; lineMg += _vmg[idx]; lineMb += _vmb[idx]; lineM2 += _m2[idx];
                    areaWt[b] += lineWt; areaMr[b] += lineMr; areaMg[b] += lineMg; areaMb[b] += lineMb; areaM2[b] += lineM2;
                    int prevR = ((r - 1) * SIZE * SIZE) + (g * SIZE) + b;
                    _vwt[idx] = _vwt[prevR] + areaWt[b]; _vmr[idx] = _vmr[prevR] + areaMr[b]; _vmg[idx] = _vmg[prevR] + areaMg[b]; _vmb[idx] = _vmb[prevR] + areaMb[b]; _m2[idx] = _m2[prevR] + areaM2[b];
                }
            }
        }
    }

    private static long Vol(ref Box cube, long[] m)
    {
        return m[(cube.r1 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b1] - m[(cube.r0 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b1] - m[(cube.r1 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b1] + m[(cube.r0 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b1] - m[(cube.r1 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b0] + m[(cube.r0 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b0] + m[(cube.r1 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b0] - m[(cube.r0 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b0];
    }

    private static long Top(ref Box cube, int dir, int pos, long[] m)
    {
        switch (dir)
        {
            case 0: return m[(pos * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b1] - m[(pos * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b1] - m[(pos * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b0] + m[(pos * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b0];
            case 1: return m[(cube.r1 * SIZE * SIZE) + (pos * SIZE) + cube.b1] - m[(cube.r0 * SIZE * SIZE) + (pos * SIZE) + cube.b1] - m[(cube.r1 * SIZE * SIZE) + (pos * SIZE) + cube.b0] + m[(cube.r0 * SIZE * SIZE) + (pos * SIZE) + cube.b0];
            case 2: return m[(cube.r1 * SIZE * SIZE) + (cube.g1 * SIZE) + pos] - m[(cube.r0 * SIZE * SIZE) + (cube.g1 * SIZE) + pos] - m[(cube.r1 * SIZE * SIZE) + (cube.g0 * SIZE) + pos] + m[(cube.r0 * SIZE * SIZE) + (cube.g0 * SIZE) + pos];
            default: return 0;
        }
    }

    private double Variance(ref Box cube)
    {
        double dr = Vol(ref cube, _vmr), dg = Vol(ref cube, _vmg), db = Vol(ref cube, _vmb), wt = Vol(ref cube, _vwt);
        if (wt <= 0) return 0;
        return Vol2(ref cube, _m2) - (dr * dr + dg * dg + db * db) / wt;
    }

    private static double Vol2(ref Box cube, double[] m)
    {
        return m[(cube.r1 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b1] - m[(cube.r0 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b1] - m[(cube.r1 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b1] + m[(cube.r0 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b1] - m[(cube.r1 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b0] + m[(cube.r0 * SIZE * SIZE) + (cube.g1 * SIZE) + cube.b0] + m[(cube.r1 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b0] - m[(cube.r0 * SIZE * SIZE) + (cube.g0 * SIZE) + cube.b0];
    }

    private bool Split(ref Box b1, ref Box b2)
    {
        double maxVariance = 0; int bestDir = -1, bestPos = -1;
        for (int dir = 0; dir < 3; dir++)
        {
            int start = dir == 0 ? b1.r0 + 1 : (dir == 1 ? b1.g0 + 1 : b1.b0 + 1);
            int end = dir == 0 ? b1.r1 : (dir == 1 ? b1.g1 : b1.b1);
            long totalWt = Vol(ref b1, _vwt), totalMr = Vol(ref b1, _vmr), totalMg = Vol(ref b1, _vmg), totalMb = Vol(ref b1, _vmb);
            long currentWt = 0, currentMr = 0, currentMg = 0, currentMb = 0;
            for (int pos = start; pos < end; pos++)
            {
                currentWt += Top(ref b1, dir, pos, _vwt); currentMr += Top(ref b1, dir, pos, _vmr); currentMg += Top(ref b1, dir, pos, _vmg); currentMb += Top(ref b1, dir, pos, _vmb);
                if (currentWt == 0 || currentWt == totalWt) continue;
                double var = (double)(currentMr * currentMr + currentMg * currentMg + currentMb * currentMb) / currentWt + (double)((totalMr - currentMr) * (totalMr - currentMr) + (totalMg - currentMg) * (totalMg - currentMg) + (totalMb - currentMb) * (totalMb - currentMb)) / (totalWt - currentWt);
                if (var > maxVariance) { maxVariance = var; bestDir = dir; bestPos = pos; }
            }
        }
        if (bestDir == -1) return false;
        b2 = b1;
        if (bestDir == 0) b1.r1 = b2.r0 = bestPos; else if (bestDir == 1) b1.g1 = b2.g0 = bestPos; else b1.b1 = b2.b0 = bestPos;
        b1.vol = (b1.r1 - b1.r0) * (b1.g1 - b1.g0) * (b1.b1 - b1.b0);
        b2.vol = (b2.r1 - b2.r0) * (b2.g1 - b2.g0) * (b2.b1 - b2.b0);
        return true;
    }

    private byte[] BuildMappingLut(byte[] palette)
    {
        int paletteCount = palette.Length / 3;
        byte[] lut = new byte[SIZE * SIZE * SIZE];
        Parallel.For(0, SIZE, r =>
        {
            float fr = (r == 0) ? 0 : (r - 0.5f) * 8;
            for (int g = 0; g < SIZE; g++)
            {
                float fg = (g == 0) ? 0 : (g - 0.5f) * 8;
                for (int b = 0; b < SIZE; b++)
                {
                    float fb = (b == 0) ? 0 : (b - 0.5f) * 8;
                    int bestIndex = 0; float minSqDist = float.MaxValue;
                    for (int i = 0; i < paletteCount; i++)
                    {
                        float dr = fr - palette[i * 3], dg = fg - palette[i * 3 + 1], db = fb - palette[i * 3 + 2];
                        float dist = dr * dr + dg * dg + db * db;
                        if (dist < minSqDist) { minSqDist = dist; bestIndex = i; if (dist == 0) break; }
                    }
                    lut[(r * SIZE * SIZE) + (g * SIZE) + b] = (byte)bestIndex;
                }
            }
        });
        return lut;
    }

    private byte[] ApplyDitheringWithLut(byte[] pixels, int width, int height, byte[] palette, byte[] lut)
    {
        byte[] indices = new byte[width * height];
        float[] currentRowError = new float[(width + 2) * 3], nextRowError = new float[(width + 2) * 3];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                int pxOff = rowOffset + x * 3;
                float r = Math.Clamp(pixels[pxOff] + currentRowError[(x + 1) * 3], 0, 255);
                float g = Math.Clamp(pixels[pxOff + 1] + currentRowError[(x + 1) * 3 + 1], 0, 255);
                float b = Math.Clamp(pixels[pxOff + 2] + currentRowError[(x + 1) * 3 + 2], 0, 255);
                int bestIndex = lut[(((int)r >> 3) + 1) * SIZE * SIZE + (((int)g >> 3) + 1) * SIZE + (((int)b >> 3) + 1)];
                indices[y * width + x] = (byte)bestIndex;
                int palOff = bestIndex * 3;
                float er = (r - palette[palOff]) / 16f, eg = (g - palette[palOff + 1]) / 16f, eb = (b - palette[palOff + 2]) / 16f;
                currentRowError[(x + 2) * 3] += er * 7; currentRowError[(x + 2) * 3 + 1] += eg * 7; currentRowError[(x + 2) * 3 + 2] += eb * 7;
                nextRowError[x * 3] += er * 3; nextRowError[x * 3 + 1] += eg * 3; nextRowError[x * 3 + 2] += eb * 3;
                nextRowError[(x + 1) * 3] += er * 5; nextRowError[(x + 1) * 3 + 1] += eg * 5; nextRowError[(x + 1) * 3 + 2] += eb * 5;
                nextRowError[(x + 2) * 3] += er; nextRowError[(x + 2) * 3 + 1] += eg; nextRowError[(x + 2) * 3 + 2] += eb;
            }
            Array.Copy(nextRowError, currentRowError, nextRowError.Length); Array.Clear(nextRowError, 0, nextRowError.Length);
        }
        return indices;
    }

    private byte[] ApplyMappingOnly(byte[] pixels, int width, int height, byte[] lut)
    {
        byte[] indices = new byte[width * height];
        Parallel.For(0, height, y =>
        {
            int rowPixels = y * width, rowSrc = rowPixels * 3;
            for (int x = 0; x < width; x++)
            {
                int s = rowSrc + x * 3;
                int r = (pixels[s] >> 3) + 1, g = (pixels[s + 1] >> 3) + 1, b = (pixels[s + 2] >> 3) + 1;
                indices[rowPixels + x] = lut[(r * SIZE * SIZE) + (g * SIZE) + b];
            }
        });
        return indices;
    }
}
