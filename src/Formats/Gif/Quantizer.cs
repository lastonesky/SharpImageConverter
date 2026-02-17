using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// 颜色量化器，使用八叉树算法将真彩色图像减少到指定数量的颜色。
/// </summary>
public class Quantizer
{
    private const int LUT_BITS = 5;              // 每通道 5bit
    private const int LUT_SIZE = 1 << LUT_BITS;  // 32
    private const int LUT_MASK = LUT_SIZE - 1;   // 31

    private class Node
    {
        public Node?[] Children = new Node?[8];
        public bool IsLeaf;
        public int PixelCount;
        public long RedSum;
        public long GreenSum;
        public long BlueSum;
        public int PaletteIndex;
        public int Level;

        public Node(int level)
        {
            Level = level;
        }

        private static int GetIndex(byte r, byte g, byte b, int level)
        {
            int shift = 7 - level;
            int index = 0;
            if ((r & (1 << shift)) != 0) index |= 4;
            if ((g & (1 << shift)) != 0) index |= 2;
            if ((b & (1 << shift)) != 0) index |= 1;
            return index;
        }

        public IEnumerable<Node> GetLeaves()
        {
            if (IsLeaf)
            {
                yield return this;
            }
            else
            {
                foreach (var child in Children)
                {
                    if (child != null)
                    {
                        foreach (var leaf in child.GetLeaves())
                        {
                            yield return leaf;
                        }
                    }
                }
            }
        }
    }

    private Node _root;
    private List<Node>[] _levels;
    private const int MaxColors = 256;

    /// <summary>
    /// 初始化量化器
    /// </summary>
    public Quantizer()
    {
        _root = new Node(0);
        _levels = new List<Node>[8];
        for (int i = 0; i < 8; i++) _levels[i] = new List<Node>();
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

        _root = new Node(0);
        _levels = new List<Node>[8];
        for (int i = 0; i < 8; i++) _levels[i] = new List<Node>();

        for (int i = 0; i < pixels.Length; i += 3)
        {
            InsertColor(pixels[i], pixels[i + 1], pixels[i + 2]);
            while (_leafCount > MaxColors)
            {
                Reduce();
            }
        }

        if (timing) t1 = Stopwatch.GetTimestamp();

        var leaves = _root.GetLeaves().ToList();
        leaves.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));

        int paletteSize = Math.Min(leaves.Count, MaxColors);
        byte[] palette = new byte[paletteSize * 3];

        for (int i = 0; i < paletteSize; i++)
        {
            var node = leaves[i];
            node.PaletteIndex = i;
            if (node.PixelCount > 0)
            {
                palette[i * 3 + 0] = (byte)(node.RedSum / node.PixelCount);
                palette[i * 3 + 1] = (byte)(node.GreenSum / node.PixelCount);
                palette[i * 3 + 2] = (byte)(node.BlueSum / node.PixelCount);
            }
        }

        if (timing) t2 = Stopwatch.GetTimestamp();

        byte[] indices = new byte[width * height];
        int pIdx = 0;
        byte[] lut = BuildColorLut(palette, paletteSize);
        for (int i = 0; i < pixels.Length; i += 3)
        {
            int r = pixels[i] >> (8 - LUT_BITS);
            int g = pixels[i + 1] >> (8 - LUT_BITS);
            int b = pixels[i + 2] >> (8 - LUT_BITS);

            int lutIndex = (r << (LUT_BITS * 2)) | (g << LUT_BITS) | b;

            indices[pIdx++] = lut[lutIndex];
        }

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

        for (int r = 0; r < LUT_SIZE; r++)
        {
            for (int g = 0; g < LUT_SIZE; g++)
            {
                for (int b = 0; b < LUT_SIZE; b++)
                {
                    // 恢复到 0~255 范围（取桶中心值）
                    int rr = (r << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
                    int gg = (g << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));
                    int bb = (b << (8 - LUT_BITS)) + (1 << (7 - LUT_BITS));

                    int minDist = int.MaxValue;
                    int bestIndex = 0;

                    for (int i = 0; i < paletteCount; i++)
                    {
                        int pr = palette[i * 3 + 0];
                        int pg = palette[i * 3 + 1];
                        int pb = palette[i * 3 + 2];

                        int dr = pr - rr;
                        int dg = pg - gg;
                        int db = pb - bb;

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
        }

        return lut;
    }

    private int _leafCount = 0;

    private void InsertColor(byte r, byte g, byte b)
    {
        Node node = _root;
        for (int level = 0; level < 8; level++)
        {
            if (node.IsLeaf)
            {
                node.PixelCount++;
                node.RedSum += r;
                node.GreenSum += g;
                node.BlueSum += b;
                return;
            }

            int idx = GetIndex(r, g, b, level);
            if (node.Children[idx] == null)
            {
                node.Children[idx] = new Node(level + 1);
                if (level == 7) // Max depth
                {
                    node.Children[idx]!.IsLeaf = true;
                    _leafCount++;
                }
                else
                {
                    // Only add to reducible list if this is the first child (avoid duplicates)
                    bool isFirstChild = true;
                    for (int k = 0; k < 8; k++)
                    {
                        if (k != idx && node.Children[k] != null)
                        {
                            isFirstChild = false;
                            break;
                        }
                    }
                    if (isFirstChild)
                    {
                        _levels[level].Add(node);
                    }
                }
            }
            node = node.Children[idx]!;
        }
        // At level 8
        node.PixelCount++;
        node.RedSum += r;
        node.GreenSum += g;
        node.BlueSum += b;
    }

    private void Reduce()
    {
        // Find deepest level with reducible nodes
        int level = 6; // Max level to reduce is 6 (merging children at 7)
        while (level >= 0 && _levels[level].Count == 0)
        {
            level--;
        }
        if (level < 0) return; // Cannot reduce further

        // Pick a node to reduce
        // Ideally pick one with least pixel count or just last added
        Node node = _levels[level][_levels[level].Count - 1];
        _levels[level].RemoveAt(_levels[level].Count - 1);

        // Merge children
        long rSum = 0, gSum = 0, bSum = 0;
        int pCount = 0;
        int childrenRemoved = 0;

        for (int i = 0; i < 8; i++)
        {
            if (node.Children[i] != null)
            {
                rSum += node.Children[i]!.RedSum;
                gSum += node.Children[i]!.GreenSum;
                bSum += node.Children[i]!.BlueSum;
                pCount += node.Children[i]!.PixelCount;
                node.Children[i] = null;
                childrenRemoved++;
            }
        }

        node.IsLeaf = true;
        node.RedSum = rSum;
        node.GreenSum = gSum;
        node.BlueSum = bSum;
        node.PixelCount = pCount;

        _leafCount -= (childrenRemoved - 1);
    }

    private static int GetIndex(byte r, byte g, byte b, int level)
    {
        int shift = 7 - level;
        int index = 0;
        if ((r & (1 << shift)) != 0) index |= 4;
        if ((g & (1 << shift)) != 0) index |= 2;
        if ((b & (1 << shift)) != 0) index |= 1;
        return index;
    }
}
