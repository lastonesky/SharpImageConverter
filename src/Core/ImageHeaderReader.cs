using System;
using System.Buffers.Binary;
using System.IO;

namespace SharpImageConverter.Core
{
    /// <summary>
    /// 全库唯一的格式嗅探与文件头尺寸读取实现。
    /// </summary>
    /// <remarks>
    /// 此前同一件事散在三处：<c>Configuration</c> 的 <c>IsMatch</c> 循环、
    /// <c>ImageFrame.Load</c> 里手写的 12 字节 magic 分支、<c>ImageOptimizer.DetectFormat</c>，
    /// 而尺寸读取另有一份在 <c>MemoryGuard</c> 里。新增一种格式要改 3~4 个地方，必然漂移。
    /// 现在嗅探与尺寸读取都收敛到这里：
    /// <list type="bullet">
    /// <item><see cref="Sniff"/>：只看头部字节，给出 <see cref="ImageFormatKind"/></item>
    /// <item><see cref="TryReadInfo"/>：按格式解析头部取宽高/帧数/渐进标志，不解码像素</item>
    /// </list>
    /// </remarks>
    internal static class ImageHeaderReader
    {
        /// <summary>嗅探所需的最小头部字节数（WebP 需要 12）。</summary>
        internal const int SniffBytes = 12;

        /// <summary>
        /// 按文件头魔数判断格式。只看 <paramref name="head"/> 的前若干字节，不移动任何流位置。
        /// </summary>
        /// <param name="head">文件起始字节（至少 <see cref="SniffBytes"/> 字节才有完整判断力）</param>
        /// <returns>识别到的格式；无法识别时返回 <see cref="ImageFormatKind.Unknown"/></returns>
        internal static ImageFormatKind Sniff(ReadOnlySpan<byte> head)
        {
            if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ImageFormatKind.Jpeg;
            if (head.Length >= 8
                && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return ImageFormatKind.Png;
            if (head.Length >= 6 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F'
                && head[3] == (byte)'8' && (head[4] == (byte)'7' || head[4] == (byte)'9') && head[5] == (byte)'a') return ImageFormatKind.Gif;
            if (head.Length >= 12
                && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return ImageFormatKind.Webp;
            if (head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M') return ImageFormatKind.Bmp;
            // 以下两种只识别、不读写：识别它们的唯一用途是把「无法识别的图像格式」
            // 换成「识别到 TIFF，但当前只支持 …」的可操作提示（见 ImageInfo.UnsupportedMessage）。
            if (head.Length >= 4
                && ((head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x2A && head[3] == 0x00)   // "II*\0" 小端
                    || (head[0] == 0x4D && head[1] == 0x4D && head[2] == 0x00 && head[3] == 0x2A))) // "MM\0*" 大端
                return ImageFormatKind.Tiff;
            if (head.Length >= 4 && head[0] == 0x00 && head[1] == 0x00
                && (head[2] == 0x01 || head[2] == 0x02) && head[3] == 0x00) return ImageFormatKind.Ico;
            return ImageFormatKind.Unknown;
        }

        /// <summary>
        /// 按格式读取文件头，得到宽高、帧数与渐进标志。不解码像素。
        /// </summary>
        /// <remarks>
        /// <para>
        /// JPEG / GIF / WebP / BMP 需要跳过段、块、子块；PNG 只需顺序读 24 字节。
        /// 以往这里要求调用方保证流可 Seek，于是不可 Seek 的调用点只能把整条流读进内存再解析
        /// （143 MB 上传 = 143 MB 常驻）。现在跳过动作统一走 <see cref="SkipForward"/>：
        /// 可 Seek 就直接定位，不可 Seek 就读并丢弃，内存开销恒为常数。
        /// </para>
        /// <para>
        /// 因此**不可 Seek 的流会被读到文件头结尾**（GIF 要扫到 trailer 才能数帧、动画 WebP 要数
        /// <c>ANMF</c>），但不会整块驻留。解析失败时返回 false 并给出 <see cref="ImageInfo"/> 的默认值。
        /// </para>
        /// </remarks>
        /// <param name="stream">输入流（位置为图像起始；可 Seek 时可被任意定位）</param>
        /// <param name="kind">已嗅探出的格式</param>
        /// <param name="info">解析结果</param>
        /// <returns>成功解析宽高则为 true</returns>
        internal static bool TryReadInfo(Stream stream, ImageFormatKind kind, out ImageInfo info)
        {
            info = default;
            switch (kind)
            {
                case ImageFormatKind.Png:
                    if (!TryReadPng(stream, out int pw, out int ph, out int frames, out bool is16Bit)) return false;
                    info = new ImageInfo(kind, pw, ph, frames, isProgressive: false, is16Bit: is16Bit);
                    return true;

                case ImageFormatKind.Jpeg:
                    if (!TryReadJpeg(stream, out int jw, out int jh, out bool progressive, out _)) return false;
                    info = new ImageInfo(kind, jw, jh, 1, progressive);
                    return true;

                case ImageFormatKind.Gif:
                    if (!TryReadGif(stream, out int gw, out int gh, out int gifFrames)) return false;
                    info = new ImageInfo(kind, gw, gh, gifFrames, isProgressive: false);
                    return true;

                case ImageFormatKind.Webp:
                    if (!TryReadWebp(stream, out int ww, out int wh, out int webpFrames)) return false;
                    info = new ImageInfo(kind, ww, wh, webpFrames, isProgressive: false);
                    return true;

                case ImageFormatKind.Bmp:
                    if (!TryReadBmp(stream, out int bw, out int bh)) return false;
                    info = new ImageInfo(kind, bw, bh, 1, isProgressive: false);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 向前跳过 <paramref name="bytes"/> 字节：可 Seek 时直接定位，否则读并丢弃。
        /// </summary>
        /// <remarks>
        /// 不可 Seek 的流上这是「读掉」而不是「跳过」——需要顺序读完才能到目标位置，
        /// 但不占用与文件等量的内存。越界（剩余字节不足）返回 false。
        /// </remarks>
        /// <param name="stream">输入流</param>
        /// <param name="bytes">要跳过的字节数</param>
        /// <returns>是否跳到了目标位置</returns>
        internal static bool SkipForward(Stream stream, long bytes)
        {
            if (bytes < 0) return false;
            if (bytes == 0) return true;

            if (stream.CanSeek)
            {
                if (bytes > stream.Length - stream.Position) return false;
                stream.Seek(bytes, SeekOrigin.Current);
                return true;
            }

            Span<byte> scratch = stackalloc byte[512];
            while (bytes > 0)
            {
                int take = (int)Math.Min(bytes, scratch.Length);
                if (stream.ReadAtLeast(scratch[..take], take, throwOnEndOfStream: false) < take) return false;
                bytes -= take;
            }
            return true;
        }

        /// <summary>
        /// 读取 JPEG 的 SOF 段信息。
        /// </summary>
        /// <param name="stream">输入流（可 Seek，位置在 SOI）</param>
        /// <param name="width">宽度</param>
        /// <param name="height">高度</param>
        /// <param name="progressive">是否渐进式（SOF2/6/10/14）</param>
        /// <param name="fusedRow">是否 3 分量且采样为 4:2:0+4:4:4 或全 4:4:4（可用融合条带路径）</param>
        internal static bool TryReadJpeg(
            Stream stream,
            out int width,
            out int height,
            out bool progressive,
            out bool fusedRow)
        {
            width = height = 0;
            progressive = fusedRow = false;
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return false;

            Span<byte> buffer = stackalloc byte[4];
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return false;
                if (b != 0xFF) continue;
                int marker;
                do
                {
                    marker = stream.ReadByte();
                } while (marker == 0xFF);
                if (marker < 0) return false;

                // 无长度的独立标记
                if (marker == 0xD8 || marker == 0xD9 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
                {
                    if (marker == 0xD9) return false;
                    continue;
                }

                if (stream.ReadAtLeast(buffer[..2], 2, throwOnEndOfStream: false) < 2) return false;
                int length = (buffer[0] << 8) | buffer[1];
                if (length < 2) return false;

                bool isSof = marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
                    or 0xC5 or 0xC6 or 0xC7
                    or 0xC9 or 0xCA or 0xCB
                    or 0xCD or 0xCE or 0xCF;
                if (!isSof)
                {
                    if (!SkipForward(stream, length - 2)) return false;
                    continue;
                }

                int payload = length - 2;
                if (payload < 6) return false;
                Span<byte> sof = stackalloc byte[6 + 12];
                int want = Math.Min(payload, sof.Length);
                if (stream.ReadAtLeast(sof[..want], want, throwOnEndOfStream: false) < want) return false;

                height = (sof[1] << 8) | sof[2];
                width = (sof[3] << 8) | sof[4];
                int count = sof[5];
                progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
                if (count == 3 && payload >= 6 + 9)
                {
                    int hv0 = sof[7];
                    int hv1 = sof[10];
                    int hv2 = sof[13];
                    fusedRow = (hv0 == 0x22 && hv1 == 0x11 && hv2 == 0x11)
                        || (hv0 == 0x11 && hv1 == 0x11 && hv2 == 0x11);
                }

                return width > 0 && height > 0;
            }
        }

        /// <summary>
        /// 读 PNG/APNG 的 IHDR，并顺带取 bitDepth 与 APNG 帧数。
        /// </summary>
        /// <remarks>
        /// 帧数来自 <c>acTL</c>（规范要求它在第一个 <c>IDAT</c> 之前）：不读它的话，
        /// 动画 PNG 只能报 1 帧，<see cref="ImageInfo.IsAnimated"/> 就是错的。
        /// bitDepth 用于判断 16-bit（optimize 对 16-bit 输入必须放弃同格式重编码，
        /// 否则会静默降到 8-bit）。
        /// </remarks>
        private static bool TryReadPng(Stream stream, out int width, out int height, out int frameCount, out bool is16Bit)
        {
            width = height = 0;
            frameCount = 1;
            is16Bit = false;

            // 8 字节签名 + IHDR（4 长度 + 4 类型 + 13 数据）+ 4 CRC = 33
            Span<byte> head = stackalloc byte[33];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (Sniff(head) != ImageFormatKind.Png) return false;
            if (BinaryPrimitives.ReadUInt32BigEndian(head[8..12]) < 13
                || head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R') return false;

            uint w = BinaryPrimitives.ReadUInt32BigEndian(head[16..20]);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(head[20..24]);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
            width = (int)w;
            height = (int)h;
            is16Bit = head[24] == 16;

            // 往后扫到 IDAT 为止，只为拿 acTL 的 num_frames。
            Span<byte> chunkHeader = stackalloc byte[8];
            while (stream.ReadAtLeast(chunkHeader, 8, throwOnEndOfStream: false) == 8)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
                ReadOnlySpan<byte> type = chunkHeader[4..8];

                if (type.SequenceEqual("acTL"u8))
                {
                    Span<byte> actl = stackalloc byte[8];
                    if (length < 8 || stream.ReadAtLeast(actl, 8, throwOnEndOfStream: false) < 8)
                    {
                        // acTL 存在即说明是 APNG：即便长度不合法也要按动画处理，
                        // 否则 optimize 会把多帧 PNG 当单帧重编码，静默丢帧。
                        frameCount = 2;
                        return true;
                    }
                    uint frames = BinaryPrimitives.ReadUInt32BigEndian(actl);
                    // num_frames 理论上 ≥2；数值异常（0/1/超大）时按「至少 2 帧」上报——
                    // 「是动画」这个判断比精确帧数更重要（optimize 据此决定放弃重编码）。
                    frameCount = frames is > 1 and <= int.MaxValue ? (int)frames : 2;
                    return true;
                }

                // IDAT 之后不会再有 acTL；IEND 之后没有数据。宽度已拿到，直接成功返回。
                if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return true;

                // 其余 chunk 按长度跳过（含 CRC）。跳过越界说明文件被截断，但宽高已有效。
                if (!SkipForward(stream, (long)length + 4)) return true;
            }
            return true;
        }

        private static bool TryReadGif(Stream stream, out int width, out int height, out int frameCount)
        {
            width = height = 0;
            frameCount = 1;
            Span<byte> head = stackalloc byte[13];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (Sniff(head) != ImageFormatKind.Gif) return false;

            width = head[6] | (head[7] << 8);
            height = head[8] | (head[9] << 8);
            if (width <= 0 || height <= 0) return false;

            int packed = head[10];
            if ((packed & 0x80) != 0)
            {
                int gctSize = 3 * (1 << ((packed & 0x07) + 1));
                if (!SkipForward(stream, gctSize)) return false;
            }

            int frames = 0;
            Span<byte> block = stackalloc byte[9];
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                if (b == 0x3B) break; // trailer
                if (b == 0x21) // extension
                {
                    if (stream.ReadByte() < 0) break;
                    if (!SkipGifSubBlocks(stream)) break;
                    continue;
                }
                if (b != 0x2C) break; // unknown block

                frames++;
                if (stream.ReadAtLeast(block[..9], 9, throwOnEndOfStream: false) < 9) break;
                int imgPacked = block[8];
                if ((imgPacked & 0x80) != 0)
                {
                    int lctSize = 3 * (1 << ((imgPacked & 0x07) + 1));
                    if (!SkipForward(stream, lctSize)) break;
                }
                if (stream.ReadByte() < 0) break; // LZW minimum code size
                if (!SkipGifSubBlocks(stream)) break;
            }

            if (frames > 0) frameCount = frames;
            return true;
        }

        private static bool SkipGifSubBlocks(Stream stream)
        {
            while (true)
            {
                int len = stream.ReadByte();
                if (len < 0) return false;
                if (len == 0) return true;
                if (!SkipForward(stream, len)) return false;
            }
        }

        private static bool TryReadWebp(Stream stream, out int width, out int height, out int frameCount)
        {
            width = height = 0;
            frameCount = 1;
            Span<byte> head = stackalloc byte[12];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (Sniff(head) != ImageFormatKind.Webp) return false;

            Span<byte> chunk = stackalloc byte[8];
            Span<byte> data = stackalloc byte[10];
            while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                ReadOnlySpan<byte> fourCc = chunk[..4];
                if (fourCc.SequenceEqual("VP8X"u8))
                {
                    if (size < 10 || stream.ReadAtLeast(data, 10, throwOnEndOfStream: false) < 10) return false;
                    uint w = (uint)(data[4] | (data[5] << 8) | (data[6] << 16)) + 1;
                    uint h = (uint)(data[7] | (data[8] << 8) | (data[9] << 16)) + 1;
                    width = (int)Math.Min(w, int.MaxValue);
                    height = (int)Math.Min(h, int.MaxValue);
                    if (width <= 0 || height <= 0) return false;

                    // flags 的 bit1 是 ANIM：置位说明后面跟着 ANIM + 若干 ANMF。
                    if ((data[0] & 0x02) == 0) return true;

                    // 跳过 VP8X 剩余 payload（含奇数长度补齐位），再数 ANMF 拿帧数。
                    if (!SkipForward(stream, (long)size - 10 + (size & 1))) return true;
                    int frames = CountWebpAnimationFrames(stream);
                    // flags 已声明是动画，即使扫不到 ANMF 也至少 2 帧，避免 IsAnimated 反转成 false。
                    frameCount = frames > 1 ? frames : 2;
                    return true;
                }
                if (fourCc.SequenceEqual("VP8 "u8))
                {
                    if (size < 10 || stream.ReadAtLeast(data, 10, throwOnEndOfStream: false) < 10) return false;
                    if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A) return false;
                    width = (data[6] | (data[7] << 8)) & 0x3FFF;
                    height = (data[8] | (data[9] << 8)) & 0x3FFF;
                    return width > 0 && height > 0;
                }
                if (fourCc.SequenceEqual("VP8L"u8))
                {
                    if (size < 5 || stream.ReadAtLeast(data[..5], 5, throwOnEndOfStream: false) < 5) return false;
                    if (data[0] != 0x2F) return false;
                    uint bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                    return true;
                }

                long skip = (long)size + (size & 1);
                if (!SkipForward(stream, skip)) return false;
            }
            return false;
        }

        /// <summary>
        /// 数 WebP 动画的 <c>ANMF</c> 块数量。
        /// </summary>
        /// <remarks>
        /// 每个 ANMF 是一帧（含它的子 chunk）。块之间按「长度 + 奇数补齐」跳过，
        /// 可 Seek 的流上是纯定位、几乎零成本；不可 Seek 的流会读掉数据但内存恒定。
        /// </remarks>
        private static int CountWebpAnimationFrames(Stream stream)
        {
            int frames = 0;
            Span<byte> chunk = stackalloc byte[8];
            while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                if (chunk[..4].SequenceEqual("ANMF"u8)) frames++;
                if (!SkipForward(stream, (long)size + (size & 1))) break;
            }
            return frames;
        }

        private static bool TryReadBmp(Stream stream, out int width, out int height)
        {
            width = height = 0;
            Span<byte> head = stackalloc byte[26];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return false;
            if (Sniff(head) != ImageFormatKind.Bmp) return false;
            int w = BinaryPrimitives.ReadInt32LittleEndian(head[18..22]);
            int h = BinaryPrimitives.ReadInt32LittleEndian(head[22..26]);
            // 自下而上位图的高度为负值，取绝对值
            if (h < 0) h = -h;
            if (w <= 0 || h <= 0) return false;
            width = w;
            height = h;
            return true;
        }
    }
}
