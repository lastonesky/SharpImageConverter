using SharpImageConverter.Core;

namespace Tests.Helpers
{
    public static class TestImageFactory
    {
        public static Image<Rgb24> CreateSolid(int width, int height, byte r, byte g, byte b)
        {
            var buf = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 3;
                    buf[o + 0] = r;
                    buf[o + 1] = g;
                    buf[o + 2] = b;
                }
            }
            return new Image<Rgb24>(width, height, buf);
        }

        public static Image<Rgb24> CreateChecker(int width, int height, (byte r, byte g, byte b) c1, (byte r, byte g, byte b) c2, int blockSize = 1)
        {
            var buf = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int bx = x / blockSize;
                    int by = y / blockSize;
                    var c = ((bx + by) % 2 == 0) ? c1 : c2;
                    int o = (y * width + x) * 3;
                    buf[o + 0] = c.r;
                    buf[o + 1] = c.g;
                    buf[o + 2] = c.b;
                }
            }
            return new Image<Rgb24>(width, height, buf);
        }

        public static Image<Rgb24> CreateGradient(int width, int height)
        {
            var buf = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = (byte)(width <= 1 ? 0 : (x * 255) / (width - 1));
                    byte g = (byte)(height <= 1 ? 0 : (y * 255) / (height - 1));
                    byte b = (byte)((r + g) / 2);
                    int o = (y * width + x) * 3;
                    buf[o + 0] = r;
                    buf[o + 1] = g;
                    buf[o + 2] = b;
                }
            }
            return new Image<Rgb24>(width, height, buf);
        }
    }
}
