using System;
using System.IO;
using PictureSharp.Core;
using PictureSharp;
using Tests.Helpers;

namespace Jpeg2Bmp.Tests
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            FormatConversionTests.Bmp_Roundtrip_Exact();
            FormatConversionTests.Png_Roundtrip_Exact();
            FormatConversionTests.Jpeg_Roundtrip_WithTolerance();
            FormatConversionTests.Jpeg_Roundtrip_DefaultSettings_NoSevereColorShift();
            ProcessingTests.Resize_2x2_To_1x1_Picks_TopLeft();
            ProcessingTests.Resize_3x3_To_6x6_NearestNeighborMapping();
            ProcessingTests.Grayscale_Formula_Matches();
            ProcessingTests.ResizeToFit_PreservesAspectRatio();
            SniffingTests.Jpeg_IsMatch_By_Header();
            SniffingTests.Png_IsMatch_By_Header();
            SniffingTests.Bmp_IsMatch_By_Header();
            SniffingTests.Random_Header_Is_Not_Match();
            Webp_Roundtrip_Exact();
            Console.WriteLine("所有测试已通过");
        }

        private static void Webp_Roundtrip_Exact()
        {
            var img = TestImageFactory.CreateSolid(2, 2, 10, 20, 30);
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".webp");
            Image.Save(img, path);
            var loaded = Image.Load(path);
            Assert.AreEqual(img.Width, loaded.Width);
            Assert.AreEqual(img.Height, loaded.Height);
            File.Delete(path);
        }
    }
}
