using System;
using System.IO;

namespace PictureSharp.Core
{
    public sealed class Image<TPixel> where TPixel : struct, IPixel
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Buffer { get; private set; }

        public Image(int width, int height, byte[] buffer)
        {
            Width = width;
            Height = height;
            Buffer = buffer;
        }

        public void Update(int width, int height, byte[] buffer)
        {
            Width = width;
            Height = height;
            Buffer = buffer;
        }
    }

    public static class Image
    {
        public static Image<Rgb24> Load(string path)
        {
            return Configuration.Default.LoadRgb24(path);
        }

        public static void Save(Image<Rgb24> image, string path)
        {
            Configuration.Default.SaveRgb24(image, path);
        }
    }
}
