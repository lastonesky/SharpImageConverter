using System;
using System.IO;

namespace SharpImageConverter;

internal class JpegBitReader
{
    private readonly Stream _stream;
    private int _bitPos = 0;
    private int _currentByte = 0;
    private bool _hitMarker = false;
    private byte _marker = 0;

    public JpegBitReader(Stream stream)
    {
        _stream = stream;
    }

    public long BytePosition => _stream.Position;

    public void ResetBits()
    {
        _bitPos = 0;
    }

    public void SetPosition(long pos)
    {
        _stream.Position = pos;
        _bitPos = 0;
        _hitMarker = false;
        _marker = 0;
    }

    public int ReadBit()
    {
        if (_bitPos == 0)
        {
            NextByte();
            if (_hitMarker) return -1;
        }

        int bit = (_currentByte >> (--_bitPos)) & 1;
        return bit;
    }

    public int ReadBits(int n)
    {
        int result = 0;
        for (int i = 0; i < n; i++)
        {
            int bit = ReadBit();
            if (bit == -1) return -1;
            result = (result << 1) | bit;
        }
        return result;
    }

    private void NextByte()
    {
        int b = _stream.ReadByte();

        if (b == -1)
        {
            _hitMarker = true;
            _marker = 0;
            _currentByte = 0;
            _bitPos = 0;
            return;
        }

        if (b == 0xFF)
        {
            int b2 = _stream.ReadByte();
            if (b2 == -1)
            {
                // EOF after FF
                _currentByte = 0xFF;
                _bitPos = 8;
                return;
            }

            if (b2 == 0x00)
            {
                _currentByte = 0xFF;
            }
            else
            {
                _hitMarker = true;
                _marker = (byte)b2;
                _currentByte = 0;
                _bitPos = 0;
                // We consumed the marker byte. Position is now after marker.
                // The original code:
                // if b2 != 0x00 -> hit marker.
                // _bytePos was incremented twice (for FF and b2).
                // But in ConsumeRestartMarker, it says:
                // if hit marker, marker is b2.
                return;
            }
        }
        else
        {
            _currentByte = b;
        }

        _bitPos = 8;
    }

    public void AlignToByte()
    {
        _bitPos = 0;
    }

    public bool HitMarker => _hitMarker;
    public byte Marker => _marker;

    public bool ConsumeRestartMarker()
    {
        if (!_hitMarker)
        {
            // Try to read next byte to see if we hit marker
            // If _bitPos is not 0, we might have partial bits left, but restart marker should be byte aligned.
            // AlignToByte() should be called before this.
            
            // Force read next byte
            NextByte();
        }

        if (_hitMarker && JpegMarkers.IsRST(_marker))
        {
            _hitMarker = false;
            _marker = 0;
            _bitPos = 0;
            return true;
        }
        return false;
    }
}
