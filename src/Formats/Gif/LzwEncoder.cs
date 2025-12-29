using System;
using System.IO;

namespace PictureSharp.Formats.Gif;

public class LzwEncoder
{
    private readonly Stream _stream;
    private int _codeSize;
    private int _clearCode;
    private int _endCode;
    private int _nextCode;
    private int _curMaxCode;
    private int _initCodeSize;
    
    // Hash table for dictionary
    private readonly int[] _htab = new int[HSIZE];
    private readonly int[] _codetab = new int[HSIZE];
    private const int HSIZE = 5003; // Prime number

    private int _curAccum;
    private int _curBits;
    
    // Block buffering
    private const int MAX_BLOCK_SIZE = 255;
    private readonly byte[] _packet = new byte[256];
    private int _packetSize = 0;

    public LzwEncoder(Stream stream)
    {
        _stream = stream;
    }

    public void Encode(byte[] pixels, int width, int height, int colorDepth)
    {
        _initCodeSize = Math.Max(2, colorDepth);
        _stream.WriteByte((byte)_initCodeSize); // Write initial code size

        _codeSize = _initCodeSize + 1;
        _clearCode = 1 << _initCodeSize;
        _endCode = _clearCode + 1;
        _nextCode = _clearCode + 2;
        _curMaxCode = (1 << _codeSize) - 1;

        _curAccum = 0;
        _curBits = 0;
        _packetSize = 0;

        // Initialize hash table
        for (int i = 0; i < HSIZE; i++) _htab[i] = -1;

        Output(_clearCode);

        int ent = pixels[0];

        for (int i = 1; i < pixels.Length; i++)
        {
            int c = pixels[i];
            int fcode = (c << 12) + ent;
            int h = (c << 4) ^ ent; // XOR hashing

            if (_htab[h] == fcode)
            {
                ent = _codetab[h];
                continue;
            }
            else if (_htab[h] >= 0) // Collision
            {
                int disp = HSIZE - h;
                if (h == 0) disp = 1;
                
                bool found = false;
                do
                {
                    if ((h -= disp) < 0) h += HSIZE;
                    if (_htab[h] == fcode)
                    {
                        ent = _codetab[h];
                        found = true;
                        break;
                    }
                } while (_htab[h] >= 0);

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
                // Clear table
                for (int j = 0; j < HSIZE; j++) _htab[j] = -1;
                Output(_clearCode);
                _codeSize = _initCodeSize + 1;
                _nextCode = _clearCode + 2;
                _curMaxCode = (1 << _codeSize) - 1;
            }
        }

        Output(ent);
        Output(_endCode);
        
        // Flush remaining bits
        while (_curBits > 0)
        {
            AddToPacket((byte)(_curAccum & 0xFF));
            _curAccum >>= 8;
            _curBits -= 8;
        }
        
        FlushPacket();
        _stream.WriteByte(0); // Block terminator
    }

    private void Output(int code)
    {
        _curAccum |= code << _curBits;
        _curBits += _codeSize;

        while (_curBits >= 8)
        {
            AddToPacket((byte)(_curAccum & 0xFF));
            _curAccum >>= 8;
            _curBits -= 8;
        }

        if (_nextCode > _curMaxCode && _codeSize < 12)
        {
            _codeSize++;
            _curMaxCode = (1 << _codeSize) - 1;
        }
    }

    private void AddToPacket(byte b)
    {
        _packet[_packetSize++] = b;
        if (_packetSize >= MAX_BLOCK_SIZE)
        {
            FlushPacket();
        }
    }

    private void FlushPacket()
    {
        if (_packetSize > 0)
        {
            _stream.WriteByte((byte)_packetSize);
            _stream.Write(_packet, 0, _packetSize);
            _packetSize = 0;
        }
    }
}
