using System;

public class JpegSegment
{
    public ushort Marker { get; }
    public int Offset { get; }
    public int Length { get; }

    public JpegSegment(ushort marker, int offset, int length)
    {
        Marker = marker;
        Offset = offset;
        Length = length;
    }

    public override string ToString()
        => $"Marker=0x{Marker:X4}, Offset={Offset}, Length={Length}";
}
