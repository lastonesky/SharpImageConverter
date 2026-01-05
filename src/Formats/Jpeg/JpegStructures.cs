using System;

namespace SharpImageConverter;

internal class Component
{
    public int Id { get; set; }
    public int HFactor { get; set; }
    public int VFactor { get; set; }
    public int QuantTableId { get; set; }
    public int DcTableId { get; set; }
    public int AcTableId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WidthInBlocks { get; set; }
    public int HeightInBlocks { get; set; }
    public int[] Coeffs { get; set; }
    public int DcPred;
}

internal class FrameHeader
{
    public int Precision { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }
    public int ComponentsCount { get; set; }
    public Component[] Components { get; set; }
    public bool IsProgressive { get; set; }
    public int McuWidth { get; set; }
    public int McuHeight { get; set; }
    public int McuCols { get; set; }
    public int McuRows { get; set; }
}

internal class ScanHeader
{
    public int ComponentsCount { get; set; }
    public ScanComponent[] Components { get; set; }
    public int StartSpectralSelection { get; set; }
    public int EndSpectralSelection { get; set; }
    public int SuccessiveApproximationBitHigh { get; set; }
    public int SuccessiveApproximationBitLow { get; set; }
}

internal class ScanComponent
{
    public int ComponentId { get; set; }
    public int DcTableId { get; set; }
    public int AcTableId { get; set; }
}
