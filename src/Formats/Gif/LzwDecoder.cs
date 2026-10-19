using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
        private readonly NativeBufferOwner<byte> _pixelStack = NativeBufferOwner<byte>.Allocate(4097);

        /// <summary>
        /// 解码一帧 LZW 数据。
        /// </summary>
        /// <param name="pixels">输出索引缓冲（长度须 ≥ width*height）</param>
        /// <param name="width">帧宽</param>
        /// <param name="height">帧高</param>
        /// <param name="dataSize">LZW 最小码长</param>
        /// <param name="requireComplete">
        /// 严格模式：数据在像素解完之前结束（流被截断或提前出现终止块）时抛
        /// <see cref="InvalidDataException"/>。默认关闭，保持历史行为——尽力返回已解出的像素，
        /// 未解出的部分留在调用方的画布缓冲里（网络输入下这会表现为「不报错但图像残缺」）。
        /// </param>
        public void Decode(Span<byte> pixels, int width, int height, int dataSize, bool requireComplete = false)
        {
            Span<int> prefix = _prefix.Span;
            Span<byte> suffix = _suffix.Span;
            Span<byte> pixelStack = _pixelStack.Span;
            int clearCode = 1 << dataSize;
            int endCode = clearCode + 1;
            int available = clearCode + 2;
            int oldCode = -1;
            int codeSize = dataSize + 1;
            int codeMask = (1 << codeSize) - 1;

            int top = 0;
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
                            // len == 0 是数据块终止符（其后像素仍未解完说明数据短了），
                            // len == -1 是流被截断。历史行为是「尽力返回已解出的像素」，
                            // 但网络输入下这会静默产出一张残缺图像，故严格模式直接拒绝。
                            if (requireComplete)
                            {
                                throw new InvalidDataException(
                                    $"GIF 图像数据不完整：数据块在像素解完前结束（已解 {pixelIndex}/{pixelCount}）");
                            }

                            // 未解出的部分必须清零：调用方的索引缓冲来自 ArrayPool，
                            // 残留的是上一个使用者（可能是别的图片/别的请求）的数据，
                            // 不清零等于把无关内存渲染进输出，且结果随池内容变化而不确定。
                            pixels[pixelIndex..pixelCount].Clear();
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
                if (code == endCode)
                {
                    // 循环条件是 pixelIndex < pixelCount，所以这里出现 endCode 就意味着像素没解完。
                    if (requireComplete)
                    {
                        throw new InvalidDataException(
                            $"GIF 图像数据不完整：提前遇到结束码（已解 {pixelIndex}/{pixelCount}）");
                    }

                    pixels[pixelIndex..pixelCount].Clear();
                    break;
                }

                // First code case
                if (oldCode == -1)
                {
                    pixelStack[top++] = (byte)code; // code < clearCode implies suffix is code
                    oldCode = code;
                }
                else
                {
                    int inCode = code;
                    int firstChar;

                    // Special case: Code is not in table yet
                    if (code >= available)
                    {
                        // Output is OldString + OldString[0]
                        int temp = oldCode;
                        while (temp >= clearCode)
                        {
                            temp = prefix[temp];
                        }
                        firstChar = temp;
                        pixelStack[top++] = (byte)firstChar;
                        code = oldCode;
                    }

                    // Expand code into pixel stack
                    while (code >= clearCode)
                    {
                        pixelStack[top++] = suffix[code];
                        code = prefix[code];
                    }
                    firstChar = code;
                    pixelStack[top++] = (byte)firstChar;

                    // Add new code to table if possible
                    if (available < 4096)
                    {
                        prefix[available] = oldCode;
                        suffix[available] = (byte)firstChar;
                        available++;
                        // Increase code size when needed
                        if ((available & codeMask) == 0 && available < 4096)
                        {
                            codeSize++;
                            codeMask = (1 << codeSize) - 1;
                        }
                    }
                    oldCode = inCode;
                }

                // 栈顶到栈底就是这段串的正向输出：短串直接内联回写，长串走反序向量拷贝
                int runLength = Math.Min(top, pixelCount - pixelIndex);
                if (runLength > 16)
                {
                    ReverseCopy(pixels.Slice(pixelIndex, runLength), pixelStack.Slice(top - runLength, runLength));
                }
                else
                {
                    for (int k = 0; k < runLength; k++)
                    {
                        pixels[pixelIndex + k] = pixelStack[top - 1 - k];
                    }
                }
                pixelIndex += runLength;
                top = 0;
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

        /// <summary>
        /// 把 src 逆序写入 dst（要求 dst.Length == src.Length，且两块内存不重叠）。
        /// 有字节查表能力时（x86 pshufb / ARM NEON tbl）每 16 字节反转一次，
        /// 替代逐字节回写。
        /// </summary>
        private static void ReverseCopy(Span<byte> dst, ReadOnlySpan<byte> src)
        {
            int n = dst.Length;
            int i = 0;
            if (SimdCompat.ByteShuffleSupported && n >= 16)
            {
                ref byte dstRef = ref MemoryMarshal.GetReference(dst);
                ref byte srcRef = ref MemoryMarshal.GetReference(src);
                Vector128<byte> reverse = Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
                int limit = n - 16;
                for (; i <= limit; i += 16)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref srcRef, (nuint)(n - i - 16));
                    SimdCompat.StoreBytes(SimdCompat.ShuffleBytes(v, reverse), ref dstRef, (nuint)i);
                }
            }
            for (; i < n; i++)
            {
                dst[i] = src[n - 1 - i];
            }
        }

        public void Dispose()
        {
            _prefix.Dispose();
            _suffix.Dispose();
            _pixelStack.Dispose();
        }
    }
}
