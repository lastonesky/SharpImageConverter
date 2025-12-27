using System;

namespace Jpeg2Bmp.Tests
{
    class Program
    {
        static int Main(string[] args)
        {
            int passed = 0, failed = 0;
            void Run(string name, Action a)
            {
                try
                {
                    a();
                    Console.WriteLine($"[PASS] {name}");
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAIL] {name}: {ex.Message}");
                    failed++;
                }
            }
            Run("FormatConversionTests.Bmp_Roundtrip_Exact", FormatConversionTests.Bmp_Roundtrip_Exact);
            Run("FormatConversionTests.Png_Roundtrip_Exact", FormatConversionTests.Png_Roundtrip_Exact);
            Run("FormatConversionTests.Jpeg_Roundtrip_WithTolerance", FormatConversionTests.Jpeg_Roundtrip_WithTolerance);
            Run("FormatConversionTests.Jpeg_Roundtrip_DefaultSettings_NoSevereColorShift", FormatConversionTests.Jpeg_Roundtrip_DefaultSettings_NoSevereColorShift);
            Run("ProcessingTests.Resize_2x2_To_1x1_Picks_TopLeft", ProcessingTests.Resize_2x2_To_1x1_Picks_TopLeft);
            Run("ProcessingTests.Resize_3x3_To_6x6_NearestNeighborMapping", ProcessingTests.Resize_3x3_To_6x6_NearestNeighborMapping);
            Run("ProcessingTests.Grayscale_Formula_Matches", ProcessingTests.Grayscale_Formula_Matches);
            Run("ProcessingTests.ResizeToFit_PreservesAspectRatio", ProcessingTests.ResizeToFit_PreservesAspectRatio);
            Run("ImageFrameTests.ImageFrame_Save_And_Load_Bmp_Png_Jpeg", ImageFrameTests.ImageFrame_Save_And_Load_Bmp_Png_Jpeg);
            Run("ImageFrameTests.ApplyExifOrientation_All_1_To_8", ImageFrameTests.ApplyExifOrientation_All_1_To_8);
            Run("BmpPaddingTests.Bmp_RowPadding_Roundtrip_Exact", BmpPaddingTests.Bmp_RowPadding_Roundtrip_Exact);
            Run("SniffingTests.Jpeg_IsMatch_By_Header", SniffingTests.Jpeg_IsMatch_By_Header);
            Run("SniffingTests.Png_IsMatch_By_Header", SniffingTests.Png_IsMatch_By_Header);
            Run("SniffingTests.Bmp_IsMatch_By_Header", SniffingTests.Bmp_IsMatch_By_Header);
            Run("SniffingTests.Random_Header_Is_Not_Match", SniffingTests.Random_Header_Is_Not_Match);
            Console.WriteLine($"总计: 通过 {passed}, 失败 {failed}");
            return failed == 0 ? 0 : 1;
        }
    }
}
