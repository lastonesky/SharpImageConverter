using System.Runtime.CompilerServices;

namespace SharpImageConverter;

internal static class JpegMarkers
{
    public const byte SOI = 0xD8;
    public const byte EOI = 0xD9;
    public const byte SOS = 0xDA;
    public const byte DQT = 0xDB;
    public const byte DNL = 0xDC;
    public const byte DRI = 0xDD;
    public const byte DHP = 0xDE;
    public const byte EXP = 0xDF;

    public const byte APP0 = 0xE0;
    public const byte APP1 = 0xE1;
    public const byte APP2 = 0xE2;
    public const byte APP3 = 0xE3;
    public const byte APP4 = 0xE4;
    public const byte APP5 = 0xE5;
    public const byte APP6 = 0xE6;
    public const byte APP7 = 0xE7;
    public const byte APP8 = 0xE8;
    public const byte APP9 = 0xE9;
    public const byte APP10 = 0xEA;
    public const byte APP11 = 0xEB;
    public const byte APP12 = 0xEC;
    public const byte APP13 = 0xED;
    public const byte APP14 = 0xEE;
    public const byte APP15 = 0xEF;

    public const byte JPG0 = 0xF0;
    public const byte JPG13 = 0xFD;
    public const byte COM = 0xFE;
    public const byte TEM = 0x01;

    public const byte SOF0 = 0xC0;
    public const byte SOF1 = 0xC1;
    public const byte SOF2 = 0xC2;
    public const byte SOF3 = 0xC3;

    public const byte SOF5 = 0xC5;
    public const byte SOF6 = 0xC6;
    public const byte SOF7 = 0xC7;

    public const byte SOF9 = 0xC9;
    public const byte SOF10 = 0xCA;
    public const byte SOF11 = 0xCB;

    public const byte SOF13 = 0xCD;
    public const byte SOF14 = 0xCE;
    public const byte SOF15 = 0xCF;

    public const byte DHT = 0xC4;
    public const byte DAC = 0xCC;

    public const byte RST0 = 0xD0;
    public const byte RST1 = 0xD1;
    public const byte RST2 = 0xD2;
    public const byte RST3 = 0xD3;
    public const byte RST4 = 0xD4;
    public const byte RST5 = 0xD5;
    public const byte RST6 = 0xD6;
    public const byte RST7 = 0xD7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRST(byte marker) => marker >= RST0 && marker <= RST7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSOF(byte marker) => marker == SOF0 || marker == SOF1 || marker == SOF2 || marker == SOF3 ||
                                             marker == SOF9 || marker == SOF10 || marker == SOF11 ||
                                             marker == SOF5 || marker == SOF6 || marker == SOF7 ||
                                             marker == SOF13 || marker == SOF14 || marker == SOF15;
}
