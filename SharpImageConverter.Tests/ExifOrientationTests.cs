using System;
using SharpImageConverter;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    /// <summary>
    /// ApplyExifOrientation 改成分支外提 + 分块转置之后，逐像素结果必须与原实现完全一致。
    /// 这里保留一份"原样参考实现"（switch 在双层循环内、无分块）做全量对照。
    /// </summary>
    public class ExifOrientationTests
    {
        private static byte[] Reference(byte[] src, int width, int height, int orientation)
        {
            int newW, newH;
            switch (orientation)
            {
                case 1: return src;
                case 2:
                case 3:
                case 4:
                    newW = width; newH = height; break;
                case 5:
                case 6:
                case 7:
                case 8:
                    newW = height; newH = width; break;
                default: return src;
            }

            var dst = new byte[newW * newH * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int dx, dy;
                    switch (orientation)
                    {
                        case 2: dx = (width - 1 - x); dy = y; break;
                        case 3: dx = (width - 1 - x); dy = (height - 1 - y); break;
                        case 4: dx = x; dy = (height - 1 - y); break;
                        case 5: dx = y; dy = x; break;
                        case 6: dx = (height - 1 - y); dy = x; break;
                        case 7: dx = (height - 1 - y); dy = (width - 1 - x); break;
                        default: dx = y; dy = (width - 1 - x); break;
                    }
                    int srcIdx = (y * width + x) * 3;
                    int dstIdx = (dy * newW + dx) * 3;
                    dst[dstIdx + 0] = src[srcIdx + 0];
                    dst[dstIdx + 1] = src[srcIdx + 1];
                    dst[dstIdx + 2] = src[srcIdx + 2];
                }
            }
            return dst;
        }

        private static byte[] MakeData(int length, int seed)
        {
            var data = new byte[length];
            uint x = (uint)seed;
            for (int i = 0; i < length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] = (byte)(x >> 24);
            }
            return data;
        }

        public static TheoryData<int, int> SizeCases =>
            new()
            {
                { 1, 1 },
                { 2, 3 },
                { 3, 2 },
                { 7, 5 },
                { 32, 32 },   // 正好一个分块
                { 33, 31 },   // 跨一个分块
                { 64, 65 },
                { 70, 33 },   // 两个方向都跨多个分块
            };

        [Theory]
        [MemberData(nameof(SizeCases))]
        public void ApplyExifOrientation_MatchesReferenceExactly(int width, int height)
        {
            var buf = MakeData(width * height * 3, width * 13 + height);

            for (int orientation = 1; orientation <= 8; orientation++)
            {
                var expected = Reference(buf, width, height, orientation);
                var frame = new ImageFrame(width, height, buf);
                var actual = frame.ApplyExifOrientation(orientation);

                int expectedLen = expected.Length;
                Assert.Equal(expectedLen, actual.Pixels.Length);
                Assert.Equal(expected, actual.Pixels);
            }
        }
    }
}
