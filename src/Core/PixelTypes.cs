using System;

namespace PictureSharp.Core
{
    public interface IPixel
    {
        int BytesPerPixel { get; }
    }

    public struct Rgb24 : IPixel
    {
        public byte R;
        public byte G;
        public byte B;
        public int BytesPerPixel => 3;
    }

    public struct Rgba32 : IPixel
    {
        public byte R;
        public byte G;
        public byte B;
        public byte A;
        public int BytesPerPixel => 4;
    }

    public struct Gray8 : IPixel
    {
        public byte V;
        public int BytesPerPixel => 1;
    }
}
