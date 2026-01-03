using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// 颜色量化器，使用八叉树算法将真彩色图像减少到指定数量的颜色。
/// </summary>
public class Quantizer
{
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
        // 1. Build Octree
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

        // 3. Build Palette
        var leaves = _root.GetLeaves().ToList();
        // Sort by pixel count for better ordering (optional)
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

        // 4. Map Pixels
        byte[] indices = new byte[width * height];
        int pIdx = 0;
        
        // Use Nearest Neighbor search instead of Tree traversal for better quality and error handling
        for (int i = 0; i < pixels.Length; i += 3)
        {
            indices[pIdx++] = GetNearestColorIndex(palette, pixels[i], pixels[i + 1], pixels[i + 2], paletteSize);
        }

        return (palette, indices);
    }

    private static byte GetNearestColorIndex(byte[] palette, byte r, byte g, byte b, int paletteCount)
    {
        int minDist = int.MaxValue;
        int bestIndex = 0;

        for (int i = 0; i < paletteCount; i++)
        {
            int pr = palette[i * 3 + 0];
            int pg = palette[i * 3 + 1];
            int pb = palette[i * 3 + 2];

            int dr = pr - r;
            int dg = pg - g;
            int db = pb - b;

            int dist = dr * dr + dg * dg + db * db;
            if (dist < minDist)
            {
                minDist = dist;
                bestIndex = i;
                if (minDist == 0) break; // Exact match
            }
        }
        return (byte)bestIndex;
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
