using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace SharpImageConverter.Formats.Gif;

public class Quantizer
{
    private const int LUT_BITS = 5;              // 每通道 5bit
    private const int LUT_SIZE = 1 << LUT_BITS;  // 32
    private const int LUT_MASK = LUT_SIZE - 1;   // 31
    private const int MaxColors = 256;

    public Quantizer()
    {
    }

    /// <summary>
    /// 对图像像素进行量化，生成调色板与索引数据
    /// </summary>
    /// <param name="pixels">RGB 像素数据</param>
    /// <param name="width">宽度</param>
    /// <param name="height">高度</param>
    /// <returns>调色板与索引数组</returns>
    public (byte[] Palette, byte[] Indices) Quantize(byte[] pixels, int width, int height)
    {
        var (palette, indices) = QuantizeInternal(pixels, width, height, out _, out _, out _, false);
        return (palette, indices);
    }

    private (byte[] Palette, byte[] Indices) QuantizeInternal(byte[] pixels, int width, int height, out long ticksTraverse, out long ticksBuildPalette, out long ticksMap, bool timing)
    {
        long t0 = 0;
        long t1 = 0;
        long t2 = 0;
        long t3 = 0;
        if (timing) t0 = Stopwatch.GetTimestamp();

        int binLength = LUT_SIZE * LUT_SIZE * LUT_SIZE;
        int[] binCount = new int[binLength];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            int r = pixels[i] >> (8 - LUT_BITS);
            int g = pixels[i + 1] >> (8 - LUT_BITS);
            int b = pixels[i + 2] >> (8 - LUT_BITS);
            int idx = (r << (LUT_BITS * 2)) | (g << LUT_BITS) | b;
            binCount[idx]++;
        }

        int nonZero = 0;
        for (int i = 0; i < binLength; i++)
        {
            if (binCount[i] != 0) nonZero++;
        }

        if (nonZero == 0)
        {
            ticksTraverse = 0;
            ticksBuildPalette = 0;
            ticksMap = 0;
            return (Array.Empty<byte>(), new byte[width * height]);
        }

        int[] binR = new int[nonZero];
        int[] binG = new int[nonZero];
        int[] binB = new int[nonZero];
        int[] binW = new int[nonZero];
        int bi = 0;
        for (int i = 0; i < binLength; i++)
        {
            int w = binCount[i];
            if (w == 0) continue;
            int r5 = (i >> (LUT_BITS * 2)) & LUT_MASK;
            int g5 = (i >> LUT_BITS) & LUT_MASK;
            int b5 = i & LUT_MASK;
            int rr = (r5 << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
            int gg = (g5 << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
            int bb = (b5 << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
            binR[bi] = rr;
            binG[bi] = gg;
            binB[bi] = bb;
            binW[bi] = w;
            bi++;
        }

        int k = Math.Min(MaxColors, nonZero);
        int[] centerR = new int[k];
        int[] centerG = new int[k];
        int[] centerB = new int[k];

        long totalW = 0;
        for (int i = 0; i < nonZero; i++) totalW += binW[i];
        long step = totalW / k;
        if (step <= 0) step = 1;
        long target = step;
        long acc = 0;
        int ci = 0;
        for (int i = 0; i < nonZero && ci < k; i++)
        {
            acc += binW[i];
            if (acc >= target)
            {
                centerR[ci] = binR[i];
                centerG[ci] = binG[i];
                centerB[ci] = binB[i];
                ci++;
                target += step;
            }
        }
        for (; ci < k; ci++)
        {
            centerR[ci] = binR[nonZero - 1];
            centerG[ci] = binG[nonZero - 1];
            centerB[ci] = binB[nonZero - 1];
        }

        int maxIter = 10;
        long[] sumR = new long[k];
        long[] sumG = new long[k];
        long[] sumB = new long[k];
        long[] sumW = new long[k];

        for (int iter = 0; iter < maxIter; iter++)
        {
            Array.Clear(sumR, 0, k);
            Array.Clear(sumG, 0, k);
            Array.Clear(sumB, 0, k);
            Array.Clear(sumW, 0, k);

            for (int i = 0; i < nonZero; i++)
            {
                int rr = binR[i];
                int gg = binG[i];
                int bb = binB[i];
                int best = 0;
                int bestDist = int.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    int dr = centerR[c] - rr;
                    int dg = centerG[c] - gg;
                    int db = centerB[c] - bb;
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = c;
                        if (dist == 0) break;
                    }
                }

                int w = binW[i];
                sumR[best] += (long)rr * w;
                sumG[best] += (long)gg * w;
                sumB[best] += (long)bb * w;
                sumW[best] += w;
            }

            bool changed = false;
            for (int c = 0; c < k; c++)
            {
                if (sumW[c] == 0) continue;
                int nr = (int)(sumR[c] / sumW[c]);
                int ng = (int)(sumG[c] / sumW[c]);
                int nb = (int)(sumB[c] / sumW[c]);
                if (nr != centerR[c] || ng != centerG[c] || nb != centerB[c])
                {
                    centerR[c] = nr;
                    centerG[c] = ng;
                    centerB[c] = nb;
                    changed = true;
                }
            }

            if (!changed) break;
        }

        if (timing) t1 = Stopwatch.GetTimestamp();
        int[] order = new int[k];
        for (int i = 0; i < k; i++) order[i] = i;
        Array.Sort(order, (a, b) => sumW[b].CompareTo(sumW[a]));

        byte[] palette = new byte[k * 3];
        for (int i = 0; i < k; i++)
        {
            int c = order[i];
            int r = centerR[c];
            int g = centerG[c];
            int b = centerB[c];
            if (r < 0) r = 0;
            else if (r > 255) r = 255;
            if (g < 0) g = 0;
            else if (g > 255) g = 255;
            if (b < 0) b = 0;
            else if (b > 255) b = 255;
            int p = i * 3;
            palette[p + 0] = (byte)r;
            palette[p + 1] = (byte)g;
            palette[p + 2] = (byte)b;
        }

        if (timing) t2 = Stopwatch.GetTimestamp();

        byte[] indices = new byte[width * height];
        byte[] lut = BuildColorLut(palette, k);
        Parallel.For(0, height, y =>
        {
            int rowPixel = y * width;
            int src = rowPixel * 3;
            int dst = rowPixel;
            for (int x = 0; x < width; x++)
            {
                int s = src + x * 3;
                int r = pixels[s] >> (8 - LUT_BITS);
                int g = pixels[s + 1] >> (8 - LUT_BITS);
                int b = pixels[s + 2] >> (8 - LUT_BITS);
                int lutIndex = (r << (LUT_BITS * 2)) | (g << LUT_BITS) | b;
                indices[dst + x] = lut[lutIndex];
            }
        });

        if (timing) t3 = Stopwatch.GetTimestamp();

        ticksTraverse = timing ? t1 - t0 : 0;
        ticksBuildPalette = timing ? t2 - t1 : 0;
        ticksMap = timing ? t3 - t2 : 0;

        return (palette, indices);
    }
    private static byte[] BuildColorLut(byte[] palette, int paletteCount)
    {
        int lutLength = LUT_SIZE * LUT_SIZE * LUT_SIZE;
        byte[] lut = new byte[lutLength];
        int[] palR = new int[paletteCount];
        int[] palG = new int[paletteCount];
        int[] palB = new int[paletteCount];
        for (int i = 0; i < paletteCount; i++)
        {
            int p = i * 3;
            palR[i] = palette[p + 0];
            palG[i] = palette[p + 1];
            palB[i] = palette[p + 2];
        }

        int vecSize = Vector<int>.Count;
        int limit = paletteCount - (paletteCount % vecSize);
        int vecBlocks = limit / vecSize;
        Vector<int>[] palRVectors = new Vector<int>[vecBlocks];
        Vector<int>[] palGVectors = new Vector<int>[vecBlocks];
        Vector<int>[] palBVectors = new Vector<int>[vecBlocks];
        for (int i = 0; i < limit; i += vecSize)
        {
            int block = i / vecSize;
            palRVectors[block] = new Vector<int>(palR, i);
            palGVectors[block] = new Vector<int>(palG, i);
            palBVectors[block] = new Vector<int>(palB, i);
        }
        Parallel.For(0, LUT_SIZE, r =>
        {
            int rr = (r << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
            Vector<int> vrr = new(rr);
            for (int g = 0; g < LUT_SIZE; g++)
            {
                int gg = (g << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
                Vector<int> vgg = new(gg);
                for (int b = 0; b < LUT_SIZE; b++)
                {
                    int bb = (b << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
                    Vector<int> vbb = new(bb);

                    int minDist = int.MaxValue;
                    int bestIndex = 0;

                    int i = 0;
                    for (int block = 0; block < vecBlocks; block++)
                    {
                        var vr = palRVectors[block];
                        var vg = palGVectors[block];
                        var vb = palBVectors[block];
                        var dr = vr - vrr;
                        var dg = vg - vgg;
                        var db = vb - vbb;
                        var dist = Vector.Add(Vector.Add(Vector.Multiply(dr, dr), Vector.Multiply(dg, dg)), Vector.Multiply(db, db));
                        for (int lane = 0; lane < vecSize; lane++)
                        {
                            int d = dist[lane];
                            if (d < minDist)
                            {
                                minDist = d;
                                bestIndex = (block * vecSize) + lane;
                                if (d == 0) break;
                            }
                        }
                        if (minDist == 0) break;
                    }

                    i = limit;
                    for (; i < paletteCount; i++)
                    {
                        int dr = palR[i] - rr;
                        int dg = palG[i] - gg;
                        int db = palB[i] - bb;
                        int dist = dr * dr + dg * dg + db * db;
                        if (dist < minDist)
                        {
                            minDist = dist;
                            bestIndex = i;
                            if (dist == 0) break;
                        }
                    }

                    int index = (r << (LUT_BITS * 2)) | (g << LUT_BITS) | b;
                    lut[index] = (byte)bestIndex;
                }
            }
        });

        return lut;
    }

}
