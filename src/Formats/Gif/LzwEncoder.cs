using System;
using System.IO;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// LZW 编码器，用于 GIF 图像数据压缩。
/// </summary>
public class LzwEncoder(Stream stream)
{
    private readonly Stream _stream = stream;
    private int _codeSize;
    private int _clearCode;
    private int _endCode;
    private int _nextCode;
    private int _curMaxCode;
    private int _initCodeSize;
    
    // Hash table for dictionary: 4096 entries max, use 8191 (prime) for efficiency
    private const int HSIZE = 8191; 
    private readonly int[] _htab = new int[HSIZE];
    private readonly int[] _codetab = new int[HSIZE];

    private long _curAccum;
    private int _curBits;
    
    private const int MAX_BLOCK_SIZE = 255;
    private readonly byte[] _packet = new byte[256];
    private int _packetSize = 0;

    public void Encode(byte[] pixels, int width, int height, int colorDepth)
    {
        _initCodeSize = Math.Max(2, colorDepth);
        _stream.WriteByte((byte)_initCodeSize);

        _codeSize = _initCodeSize + 1;
        _clearCode = 1 << _initCodeSize;
        _endCode = _clearCode + 1;
        _nextCode = _clearCode + 2;
        _curMaxCode = (1 << _codeSize) - 1;

        _curAccum = 0;
        _curBits = 0;
        _packetSize = 0;

        for (int i = 0; i < HSIZE; i++) _htab[i] = -1;

        Output(_clearCode);

        int ent = pixels[0];

        for (int i = 1; i < pixels.Length; i++)
        {
            int c = pixels[i];
            int fcode = (c << 12) | ent;
            int h = (c << 4) ^ ent; // Basic XOR hash

            if (_htab[h] == fcode)
            {
                ent = _codetab[h];
                continue;
            }
            
            if (_htab[h] >= 0) // Collision
            {
                int disp = HSIZE - h;
                if (h == 0) disp = 1;
                
                bool found = false;
                while (true)
                {
                    h -= disp;
                    if (h < 0) h += HSIZE;
                    if (_htab[h] == fcode)
                    {
                        ent = _codetab[h];
                        found = true;
                        break;
                    }
                    if (_htab[h] < 0) break;
                }
                if (found) continue;
            }

            Output(ent);
            ent = c;

            if (_nextCode < 4096)
            {
                _codetab[h] = _nextCode++;
                _htab[h] = fcode;
            }
            else
            {
                ClearTable();
            }
        }

        Output(ent);
        Output(_endCode);
        FlushBits();
        _stream.WriteByte(0); // Block terminator
    }

    private void ClearTable()
    {
        for (int i = 0; i < HSIZE; i++) _htab[i] = -1;
        Output(_clearCode);
        _codeSize = _initCodeSize + 1;
        _nextCode = _clearCode + 2;
        _curMaxCode = (1 << _codeSize) - 1;
    }

    private void Output(int code)
    {
        _curAccum |= (long)code << _curBits;
        _curBits += _codeSize;

        while (_curBits >= 8)
        {
            _packet[++_packetSize] = (byte)(_curAccum & 0xFF);
            _curAccum >>= 8;
            _curBits -= 8;
            if (_packetSize >= MAX_BLOCK_SIZE) FlushPacket();
        }

        if (_nextCode > _curMaxCode && _codeSize < 12)
        {
            _codeSize++;
            _curMaxCode = (1 << _codeSize) - 1;
        }
    }

    private void FlushBits()
    {
        while (_curBits > 0)
        {
            _packet[++_packetSize] = (byte)(_curAccum & 0xFF);
            _curAccum >>= 8;
            _curBits -= 8;
            if (_packetSize >= MAX_BLOCK_SIZE) FlushPacket();
        }
        FlushPacket();
    }

    private void FlushPacket()
    {
        if (_packetSize > 0)
        {
            _packet[0] = (byte)_packetSize;
            _stream.Write(_packet, 0, _packetSize + 1);
            _packetSize = 0;
        }
    }
}
