using System;
using System.IO;

namespace SharpImageConverter.Core
{
    public interface IImageFormat
    {
        string Name { get; }
        string[] Extensions { get; }
        bool IsMatch(Stream s);
    }

    public interface IImageDecoder
    {
        Image<Rgb24> DecodeRgb24(string path);
    }

    public interface IImageEncoder
    {
        void EncodeRgb24(string path, Image<Rgb24> image);
    }

    public interface IImageInfo
    {
        int Width { get; }
        int Height { get; }
    }
}
