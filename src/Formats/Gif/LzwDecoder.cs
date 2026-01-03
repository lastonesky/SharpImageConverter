using System;
using System.IO;

namespace SharpImageConverter.Formats.Gif
{
    internal sealed class LzwDecoder
    {
        private readonly Stream _stream;
        private readonly byte[] _blockBuffer = new byte[256];
        private int _blockLength;
        private int _blockIndex;
        
        private int _bitBuffer;
        private int _bitCount;

        private readonly int[] _prefix = new int[4096];
        private readonly byte[] _suffix = new byte[4096];
        private readonly byte[] _pixelStack = new byte[4096 + 1];

        public LzwDecoder(Stream stream)
        {
            _stream = stream;
        }

        public void Decode(byte[] pixels, int width, int height, int dataSize)
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

            while (pixelIndex < pixelCount)
            {
                if (top == 0)
                {
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
                                int n = _stream.Read(_blockBuffer, read, len - read);
                                if (n == 0) throw new EndOfStreamException("Unexpected end of stream in GIF data block");
                                read += n;
                            }
                            _blockIndex = 0;
                        }

                        _bitBuffer |= (_blockBuffer[_blockIndex++] & 0xFF) << _bitCount;
                        _bitCount += 8;
                    }

                    int code = _bitBuffer & codeMask;
                    _bitBuffer >>= codeSize;
                    _bitCount -= codeSize;

                    if (code == clearCode)
                    {
                        codeSize = dataSize + 1;
                        codeMask = (1 << codeSize) - 1;
                        available = clearCode + 2;
                        oldCode = -1;
                        continue;
                    }

                    if (code == endCode) break;

                    if (oldCode == -1)
                    {
                        _pixelStack[top++] = (byte)code; // code < clearCode implies suffix is code
                        oldCode = code;
                        continue;
                    }

                    int inCode = code;
                    int firstChar;

                    if (code >= available)
                    {
                        // Special case: Code is not in table yet (it's the one we are about to add)
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

                    while (code >= clearCode)
                    {
                        _pixelStack[top++] = _suffix[code];
                        code = _prefix[code];
                    }
                    firstChar = code;
                    _pixelStack[top++] = (byte)firstChar;

                    if (available < 4096)
                    {
                        _prefix[available] = oldCode;
                        _suffix[available] = (byte)firstChar;
                        available++;
                        if ((available & codeMask) == 0 && available < 4096)
                        {
                            codeSize++;
                            codeMask += available;
                        }
                    }
                    oldCode = inCode;
                }

                // Pop stack
                top--;
                pixels[pixelIndex++] = _pixelStack[top];
            }

            // Flush remaining sub-blocks
            while (true)
            {
                 int len = _stream.ReadByte();
                 if (len <= 0) break;
                 _stream.Seek(len, SeekOrigin.Current);
            }
        }
    }
}
