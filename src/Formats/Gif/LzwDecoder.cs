using System;
using System.Buffers;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif
{
    internal sealed class LzwDecoder(Stream stream) : IDisposable
    {
        private readonly Stream _stream = stream;
        private readonly byte[] _blockBuffer = new byte[256];
        private int _blockLength;
        private int _blockIndex;

        private int _bitBuffer;
        private int _bitCount;

        private readonly NativeBufferOwner<int> _prefix = NativeBufferOwner<int>.Allocate(4096);
        private readonly NativeBufferOwner<byte> _suffix = NativeBufferOwner<byte>.Allocate(4096);

        /// <summary>
        /// 每个字典项对应串的长度。有了它就能在展开之前知道串长，
        /// 从而把字符直接写进输出缓冲的正确位置，省掉「压栈 + 反向拷贝」这第二遍。
        /// 根码不查此表（长度恒为 1）。
        /// </summary>
        private readonly NativeBufferOwner<int> _strLen = NativeBufferOwner<int>.Allocate(4096);

        /// <summary>
        /// 解码核心的工作量实测（2400x1800 照片，432 万像素）：
        /// 码数 100.7 万、串平均长 4.29、前缀链迭代 331 万次、位补位迭代 142 万次。
        /// 据此做过并已否决的改动（均实测变慢，勿重复尝试）：
        /// 一次补 4 字节的位缓冲（-12%）、prefix/suffix 打包成单个 int（-5%）、
        /// 二级跳转表一次展开两个像素（-5%）。
        /// </summary>
        public void Decode(Span<byte> pixels, int width, int height, int dataSize)
        {
            Span<int> prefix = _prefix.Span;
            Span<byte> suffix = _suffix.Span;
            Span<int> strLen = _strLen.Span;
            int clearCode = 1 << dataSize;
            int endCode = clearCode + 1;
            int available = clearCode + 2;
            int oldCode = -1;
            int codeSize = dataSize + 1;
            int codeMask = (1 << codeSize) - 1;

            int pixelIndex = 0;
            int pixelCount = width * height;

            // Reset buffers
            _blockLength = 0;
            _blockIndex = 0;
            _bitBuffer = 0;
            _bitCount = 0;

            // Pre-allocate and clear tables for better cache performance
            prefix.Clear();
            suffix.Clear();

            while (pixelIndex < pixelCount)
            {
                // Fill bit buffer with enough bits
                while (_bitCount < codeSize)
                {
                    if (_blockIndex >= _blockLength)
                    {
                        int len = _stream.ReadByte();
                        if (len <= 0)
                        {
                            // Unexpected end of data, but let's break and return what we have
                            return;
                        }
                        _blockLength = len;
                        int read = 0;
                        while (read < len)
                        {
                            int n = _stream.Read(_blockBuffer.AsSpan(read, len - read));
                            if (n == 0) throw new EndOfStreamException("Unexpected end of stream in GIF data block");
                            read += n;
                        }
                        _blockIndex = 0;
                    }

                    _bitBuffer |= (_blockBuffer[_blockIndex++] & 0xFF) << _bitCount;
                    _bitCount += 8;
                }

                // Extract code
                int code = _bitBuffer & codeMask;
                _bitBuffer >>= codeSize;
                _bitCount -= codeSize;

                // Handle clear code
                if (code == clearCode)
                {
                    codeSize = dataSize + 1;
                    codeMask = (1 << codeSize) - 1;
                    available = clearCode + 2;
                    oldCode = -1;
                    continue;
                }

                // Handle end code
                if (code == endCode) break;

                // 首个码必定是根码：串长 1，且不产生新的字典项
                if (oldCode == -1)
                {
                    pixels[pixelIndex++] = (byte)code;
                    oldCode = code;
                    continue;
                }

                // 串长：根码为 1；已在字典中则查表；code == available 时
                // 串为 string(oldCode) 拼接上它自己的首字符
                int codeLen;
                if (code < clearCode) codeLen = 1;
                else if (code < available) codeLen = strLen[code];
                else codeLen = oldCode < clearCode ? 2 : strLen[oldCode] + 1;

                int firstChar;
                if (codeLen <= pixelCount - pixelIndex)
                {
                    // 常规路径：串完整落在输出缓冲内，逐字符直写，不做边界判断
                    int pos = pixelIndex + codeLen;
                    int walk = code;

                    // 特殊情形：码尚未进字典，输出为 string(oldCode) + string(oldCode) 的首字符
                    if (code >= available)
                    {
                        int fc = oldCode;
                        while (fc >= clearCode) fc = prefix[fc];
                        pixels[--pos] = (byte)fc;
                        walk = oldCode;
                    }

                    while (walk >= clearCode)
                    {
                        pixels[--pos] = suffix[walk];
                        walk = prefix[walk];
                    }
                    firstChar = walk;
                    pixels[--pos] = (byte)firstChar;
                    pixelIndex += codeLen;
                }
                else
                {
                    // 数据被截断：只写前 writeLen 个字符（正常 GIF 不会走到这里）
                    int writeLen = pixelCount - pixelIndex;
                    int idx = codeLen;
                    int walk = code;
                    if (code >= available)
                    {
                        int fc = oldCode;
                        while (fc >= clearCode) fc = prefix[fc];
                        idx--;
                        if (idx < writeLen) pixels[pixelIndex + idx] = (byte)fc;
                        walk = oldCode;
                    }
                    while (walk >= clearCode)
                    {
                        idx--;
                        if (idx < writeLen) pixels[pixelIndex + idx] = suffix[walk];
                        walk = prefix[walk];
                    }
                    firstChar = walk;
                    idx--;
                    if (idx < writeLen) pixels[pixelIndex + idx] = (byte)firstChar;
                    pixelIndex += writeLen;
                }

                // Add new code to table if possible
                if (available < 4096)
                {
                    prefix[available] = oldCode;
                    suffix[available] = (byte)firstChar;
                    strLen[available] = oldCode < clearCode ? 2 : strLen[oldCode] + 1;
                    available++;
                    // Increase code size when needed
                    if ((available & codeMask) == 0 && available < 4096)
                    {
                        codeSize++;
                        codeMask = (1 << codeSize) - 1;
                    }
                }
                oldCode = code;
            }

            // Flush remaining sub-blocks
            while (true)
            {
                 int len = _stream.ReadByte();
                 if (len <= 0) break;
                 if (_stream.CanSeek)
                    _stream.Seek(len, SeekOrigin.Current);
                 else
                 {
                    byte[] skipBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(len, 256));
                    try
                    {
                        int skipped = 0;
                        while (skipped < len)
                        {
                            int chunk = Math.Min(len - skipped, skipBuffer.Length);
                            int n = _stream.Read(skipBuffer, 0, chunk);
                            if (n == 0) break;
                            skipped += n;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(skipBuffer);
                    }
                 }
            }
        }

        public void Dispose()
        {
            _prefix.Dispose();
            _suffix.Dispose();
            _strLen.Dispose();
        }
    }
}
