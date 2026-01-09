using System;
using System.Diagnostics;
using System.IO;
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
using Tests.Helpers;
using Xunit;

namespace Jpeg2Bmp.Tests
{
    public class BicubicBenchmarkTests
    {
        [Fact]
        public void BicubicOptimized_Benchmarks_On_Progressive_Jpeg()
        {
            string path = FindProgressiveJpeg();
            if (!File.Exists(path))
            {
                return;
            }

            var image = Image.Load(path);
            int w = image.Width / 2;
            int h = image.Height / 2;
            int iterations = 5;

            var img1 = new Image<Rgb24>(image.Width, image.Height, (byte[])image.Buffer.Clone());
            var sw1 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                ImageExtensions.Mutate(img1, ctx => ctx.ResizeBicubic(w, h));
            }
            sw1.Stop();

            var img2 = new Image<Rgb24>(image.Width, image.Height, (byte[])image.Buffer.Clone());
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                ImageExtensions.Mutate(img2, ctx => ctx.ResizeBicubicOptimized(w, h));
            }
            sw2.Stop();

            Console.WriteLine($"Bicubic 原始实现: {sw1.ElapsedMilliseconds} ms (迭代 {iterations} 次)");
            Console.WriteLine($"Bicubic 优化实现: {sw2.ElapsedMilliseconds} ms (迭代 {iterations} 次)");
        }

        static string FindProgressiveJpeg()
        {
            string baseDir = AppContext.BaseDirectory;
            string path1 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "progressive.jpg"));
            if (File.Exists(path1)) return path1;
            string path2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "progressive.jpg"));
            if (File.Exists(path2)) return path2;
            string path3 = Path.Combine(Directory.GetCurrentDirectory(), "progressive.jpg");
            return path3;
        }
    }
}

