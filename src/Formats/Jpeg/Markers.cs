namespace SharpImageConverter.Formats.Jpeg;

internal enum JpegMarker : byte
{
    SOF0 = 0xC0,
    SOF2 = 0xC2,
    DHT = 0xC4,
    DNL = 0xDC,
    DRI = 0xDD,
    SOS = 0xDA,
    APP0 = 0xE0,
    APP1 = 0xE1,
    APP14 = 0xEE,
    COM = 0xFE,
    SOI = 0xD8,
    EOI = 0xD9,
    DQT = 0xDB,
    RST0 = 0xD0,
    RST1 = 0xD1,
    RST2 = 0xD2,
    RST3 = 0xD3,
    RST4 = 0xD4,
    RST5 = 0xD5,
    RST6 = 0xD6,
    RST7 = 0xD7
}

