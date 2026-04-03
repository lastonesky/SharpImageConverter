using System;
using System.IO;

namespace SharpImageConverter.Formats.Gif
{
    internal sealed class LzwDecoder(Stream stream)
    {
        private readonly Stream _stream = stream;
        private readonly byte[] _blockBuffer = new byte[256];
        private int _blockLength;
        private int _blockIndex;
        
        private int _bitBuffer;
        private int _bitCount;

        private readonly int[] _prefix = new int[4096];
        private readonly byte[] _suffix = new byte[4096];
        private readonly byte[] _pixelStack = new byte[4096 + 1];

        public void Decode(Span<byte> pixels, int width, int height, int dataSize)
        {
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
            Array.Clear(_prefix, 0, _prefix.Length);
            Array.Clear(_suffix, 0, _suffix.Length);

            while (pixelIndex < pixelCount)
            {
                if (top == 0)
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

                    // First code case
                    if (oldCode == -1)
                    {
                        _pixelStack[top++] = (byte)code; // code < clearCode implies suffix is code
                        oldCode = code;
                        continue;
                    }

                    int inCode = code;
                    int firstChar;

                    // Special case: Code is not in table yet
                    if (code >= available)
                    {
                        // Output is OldString + OldString[0]
                        int temp = oldCode;
                        while (temp >= clearCode)
                        {
                            temp = _prefix[temp];
                        }
                        firstChar = temp;
                        _pixelStack[top++] = (byte)firstChar;
                        code = oldCode;
                    }

                    // Expand code into pixel stack
                    while (code >= clearCode)
                    {
                        _pixelStack[top++] = _suffix[code];
                        code = _prefix[code];
                    }
                    firstChar = code;
                    _pixelStack[top++] = (byte)firstChar;

                    // Add new code to table if possible
                    if (available < 4096)
                    {
                        _prefix[available] = oldCode;
                        _suffix[available] = (byte)firstChar;
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

                // Pop stack and write pixel
                top--;
                pixels[pixelIndex++] = _pixelStack[top];
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
                    // Skip bytes efficiently
                    byte[] skipBuffer = new byte[len];
                    int skipped = 0;
                    while (skipped < len)
                    {
                        int n = _stream.Read(skipBuffer, skipped, len - skipped);
                        if (n == 0) break;
                        skipped += n;
                    }
                 }
            }
        }
    }
}
