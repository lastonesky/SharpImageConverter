using System;
using System.IO;
using SharpImageConverter.Core;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    public class TransparencyTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        [Fact]
        public void Png_Rgba32_Roundtrip_Preserves_Alpha()
        {
            int w = 4, h = 4;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool alt = ((x + y) % 2) == 0;
                    rgba[o + 0] = alt ? (byte)10 : (byte)200;
                    rgba[o + 1] = alt ? (byte)20 : (byte)210;
                    rgba[o + 2] = alt ? (byte)30 : (byte)220;
                    rgba[o + 3] = alt ? (byte)0 : (byte)255;
                }
            }
            var img = new Image<Rgba32>(w, h, rgba);
            string path = NewTemp(".png");
            Image.Save(img, path);
            var loaded = Image.LoadRgba32(path);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
            int transparentCount = 0, opaqueCount = 0;
            for (int i = 3; i < loaded.Buffer.Length; i += 4)
            {
                if (loaded.Buffer[i] == 0) transparentCount++;
                else if (loaded.Buffer[i] == 255) opaqueCount++;
            }
            Assert.True(transparentCount > 0 && opaqueCount > 0);
            File.Delete(path);
        }

        [Fact]
        public void Gif_Rgba32_Roundtrip_Preserves_AlphaMask()
        {
            int w = 8, h = 8;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool transparent = (x < w / 2);
                    rgba[o + 0] = transparent ? (byte)0 : (byte)180;
                    rgba[o + 1] = transparent ? (byte)0 : (byte)40;
                    rgba[o + 2] = transparent ? (byte)0 : (byte)200;
                    rgba[o + 3] = transparent ? (byte)0 : (byte)255;
                }
            }
            var img = new Image<Rgba32>(w, h, rgba);
            string path = NewTemp(".gif");
            Image.Save(img, path);
            var loaded = Image.LoadRgba32(path);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    byte a = loaded.Buffer[o + 3];
                    if (x < w / 2) Assert.Equal(0, a);
                    else Assert.Equal(255, a);
                }
            }
            File.Delete(path);
        }
    }
}
