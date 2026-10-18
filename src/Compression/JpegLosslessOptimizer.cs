using System;
using System.Collections.Generic;
using System.IO;
using SharpImageConverter.Formats.Jpeg;

namespace SharpImageConverter.Compression
{
    /// <summary>
    /// JPEG 无损重封装：只删 APP1（EXIF/XMP）、APP13（IPTC）、COM，保留 ICC、SOF/SOS/DQT/DHT
    /// 与熵编码数据，不碰任何系数，因此像素逐字节不变。
    /// </summary>
    /// <remarks>
    /// 这是 P1-4a 的第一阶段。尚未实现 Huffman 表重排（jpegtran -optimize），
    /// 那需要解析并重写熵编码段，收益约 5%~10%，留作后续。
    /// </remarks>
    internal static class JpegLosslessOptimizer
    {
        /// <summary>
        /// 尝试无损剥除元数据；没有可删段或源带非 1 的 EXIF Orientation 时返回 null。
        /// </summary>
        /// <param name="jpeg">完整 JPEG 字节</param>
        internal static byte[]? StripMetadata(byte[] jpeg)
        {
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            {
                return null;
            }

            // 第一遍只找 EXIF Orientation：非 1 时不能删 APP1，否则显示方向会变。
            int orientation = 1;
            byte[]? exifRaw = null;
            bool keepApp1 = false;
            foreach (var segment in EnumerateSegments(jpeg))
            {
                if (segment.Marker == 0xE1)
                {
                    JpegDecoder.TryStoreExifApp1(
                        jpeg.AsSpan(segment.DataStart, segment.DataLength), ref exifRaw, ref orientation);
                    if (orientation != 1) keepApp1 = true;
                }
            }

            bool removed = false;
            bool wroteEoi = false;
            using var output = new MemoryStream(jpeg.Length);
            output.WriteByte(0xFF);
            output.WriteByte(0xD8);

            int pos = 2;
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }

                int markerPos = pos;
                int m = pos + 1;
                while (m < jpeg.Length && jpeg[m] == 0xFF) m++;
                if (m >= jpeg.Length) break;
                byte marker = jpeg[m];

                if (marker == 0xD8) // 重复 SOI
                {
                    pos = m + 1;
                    continue;
                }
                if (marker == 0xD9) // EOI
                {
                    output.WriteByte(0xFF);
                    output.WriteByte(0xD9);
                    wroteEoi = true;
                    break;
                }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) // TEM / RST
                {
                    output.Write(jpeg, markerPos, m - markerPos + 1);
                    pos = m + 1;
                    continue;
                }
                if (m + 2 >= jpeg.Length) break;

                int length = (jpeg[m + 1] << 8) | jpeg[m + 2];
                int end = m + 1 + length;
                if (length < 2 || end > jpeg.Length) break;

                bool drop = marker == 0xFE // COM
                    || marker == 0xED // APP13 (IPTC / Photoshop)
                    || (marker == 0xE1 && !keepApp1); // EXIF / XMP
                if (drop)
                {
                    removed = true;
                }
                else
                {
                    output.Write(jpeg, markerPos, end - markerPos);
                }

                pos = end;
                if (marker == 0xDA) // SOS：把熵编码数据原样拷到下一个 marker
                {
                    int entropyStart = pos;
                    while (pos + 1 < jpeg.Length)
                    {
                        if (jpeg[pos] != 0xFF)
                        {
                            pos++;
                            continue;
                        }
                        byte next = jpeg[pos + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
                        {
                            pos += 2; // 填充字节或 restart marker
                            continue;
                        }
                        if (next == 0xFF)
                        {
                            pos++;
                            continue;
                        }
                        break;
                    }
                    output.Write(jpeg, entropyStart, pos - entropyStart);
                }
            }

            if (!wroteEoi)
            {
                output.WriteByte(0xFF);
                output.WriteByte(0xD9);
            }
            return removed ? output.ToArray() : null;
        }

        private readonly record struct Segment(byte Marker, int DataStart, int DataLength);

        /// <summary>
        /// 枚举带长度字段的段（不进入熵编码数据，只用于第一遍找 APP1）。
        /// </summary>
        private static IEnumerable<Segment> EnumerateSegments(byte[] jpeg)
        {
            int pos = 2;
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF)
                {
                    pos++;
                    continue;
                }
                int m = pos + 1;
                while (m < jpeg.Length && jpeg[m] == 0xFF) m++;
                if (m >= jpeg.Length) yield break;
                byte marker = jpeg[m];
                if (marker == 0xD9) yield break;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    pos = m + 1;
                    continue;
                }
                if (m + 2 >= jpeg.Length) yield break;
                int length = (jpeg[m + 1] << 8) | jpeg[m + 2];
                int end = m + 1 + length;
                if (length < 2 || end > jpeg.Length) yield break;

                yield return new Segment(marker, m + 3, length - 2);
                pos = end;
                if (marker == 0xDA)
                {
                    while (pos + 1 < jpeg.Length)
                    {
                        if (jpeg[pos] != 0xFF) { pos++; continue; }
                        byte next = jpeg[pos + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) { pos += 2; continue; }
                        if (next == 0xFF) { pos++; continue; }
                        break;
                    }
                }
            }
        }
    }
}
