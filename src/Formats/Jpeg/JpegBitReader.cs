using System;
using System.Runtime.CompilerServices;

namespace SharpImageConverter;

internal unsafe class JpegBitReader
{
    private readonly byte* _data;
    private readonly int _length;
    private int _byteOffset;
    private ulong _bitBuffer;
    private int _bitCount;
    private bool _hitMarker;
    private byte _marker;
    private int _markerOffset;

    public JpegBitReader(byte* data, int length)
    {
        _data = data;
        _length = length;
        _byteOffset = 0;
        _bitBuffer = 0;
        _bitCount = 0;
        _hitMarker = false;
        _marker = 0;
        _markerOffset = -1;
    }

    public int ByteOffset => _byteOffset;
    public int MarkerOffset => _markerOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetBits()
    {
        _bitBuffer = 0;
        _bitCount = 0;
        _hitMarker = false;
        _marker = 0;
        _markerOffset = -1;
        _byteOffset = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        return ReadBits(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBits(int n)
    {
        if (n <= 0) return 0;
        int bits = PeekBits(n);
        if (bits == -1) return -1;
        SkipBits(n);
        return bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekBits(int n)
    {
        FillBits(n);
        if (_bitCount < n) return -1;
        int shift = _bitCount - n;
        ulong mask = n == 64 ? ulong.MaxValue : ((1UL << n) - 1);
        return (int)((_bitBuffer >> shift) & mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipBits(int n)
    {
        if (n <= 0) return;
        if (n > _bitCount) n = _bitCount;
        _bitCount -= n;
        if (_bitCount == 0)
        {
            _bitBuffer = 0;
            return;
        }
        if (_bitCount < 64)
        {
            _bitBuffer &= (1UL << _bitCount) - 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillBits(int minBits)
    {
        while (!_hitMarker && _bitCount < minBits && _byteOffset < _length)
        {
            byte b = _data[_byteOffset++];

            if (b == 0xFF)
            {
                if (_byteOffset >= _length)
                {
                    _hitMarker = true;
                    _marker = 0;
                    _markerOffset = _byteOffset - 1;
                    return;
                }

                byte b2 = _data[_byteOffset++];
                if (b2 == 0x00)
                {
                    AppendByte(0xFF);
                    continue;
                }

                _markerOffset = _byteOffset - 2;
                _hitMarker = true;
                _marker = b2;
                return;
            }

            AppendByte(b);
        }

        if (!_hitMarker && _byteOffset >= _length && _bitCount < minBits)
        {
            _hitMarker = true;
            _marker = 0;
            _markerOffset = _length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendByte(byte b)
    {
        if (_bitCount > 56) throw new InvalidOperationException("Bit buffer overflow");
        _bitBuffer = (_bitBuffer << 8) | b;
        _bitCount += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AlignToByte()
    {
        int mod = _bitCount & 7;
        if (mod != 0) SkipBits(mod);
    }

    public bool HitMarker => _hitMarker;
    public byte Marker => _marker;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConsumeRestartMarker()
    {
        AlignToByte();
        if (!_hitMarker) FillBits(8);
        if (!_hitMarker) return false;
        if (!JpegMarkers.IsRST(_marker)) return false;

        _hitMarker = false;
        _marker = 0;
        _bitBuffer = 0;
        _bitCount = 0;
        return true;
    }
}
