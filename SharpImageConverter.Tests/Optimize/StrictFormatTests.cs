using System;
using System.IO;
using SharpImageConverter.Compression;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Png;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// <c>--strict-format</c> / <see cref="OptimizeOptions.RequireFormatMatch"/>：
    /// 扩展名与文件头内容不符时拒绝隐式转换。
    /// </summary>
    /// <remarks>
    /// 默认口径是「读进来按内容、写出去按扩展名」（见 §3.5）：把 PNG 存成 <c>a.jpg</c> 再 optimize
    /// 会被默默转成真 JPEG。严格模式把这件事变成显式失败，避免批量流水线在文件名撒谎时改掉格式。
    /// </remarks>
    public class StrictFormatTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        /// <summary>把 PNG 内容写进指定扩展名的文件，返回路径。</summary>
        private static string WritePngAs(string ext)
        {
            var img = Helpers.TestImageFactory.CreateChecker(24, 24, (10, 20, 30), (200, 210, 220), 3);
            string path = NewTemp(ext);
            PngWriter.Write(path, img.Width, img.Height, img.Buffer, img.Metadata);
            return path;
        }

        /// <summary>
        /// 基线行为（严格模式关闭）：PNG 内容 + <c>.jpg</c> 名 → 隐式转成真 JPEG。
        /// 这条测试是给 §3.5 留证据的，不能因为加了严格模式而改变默认行为。
        /// </summary>
        [Fact]
        public void Default_Lying_Extension_Is_Still_Converted_To_Jpeg()
        {
            string input = WritePngAs(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                var result = ImageOptimizer.Optimize(input, output);

                Assert.True(result.Converted, $"预期走「转换即最优」，实际：{result.Reason ?? result.Method}");
                byte[] bytes = File.ReadAllBytes(output);
                Assert.True(bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8, "输出不是 JPEG");
            }
            finally
            {
                foreach (string f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        /// <summary>
        /// 严格模式：扩展名说 JPEG、内容是 PNG → 抛 <see cref="InvalidDataException"/>，
        /// 且不产出任何文件（不能先写再报错）。
        /// </summary>
        [Fact]
        public void RequireFormatMatch_Rejects_Lying_Extension()
        {
            string input = WritePngAs(".jpg");
            string output = NewTemp(".jpg");
            try
            {
                var options = new OptimizeOptions { RequireFormatMatch = true };
                var ex = Assert.Throws<InvalidDataException>(() => ImageOptimizer.Optimize(input, output, options));

                Assert.Contains("不符", ex.Message, StringComparison.Ordinal);
                Assert.Contains("JPEG", ex.Message, StringComparison.Ordinal);
                Assert.Contains("PNG", ex.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(output), "严格模式失败后不应留下输出文件");
            }
            finally
            {
                foreach (string f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        /// <summary>
        /// 严格模式下扩展名与内容一致时照常工作（不能把正常文件也拦掉）。
        /// </summary>
        [Fact]
        public void RequireFormatMatch_Allows_Matching_Extension()
        {
            string input = WritePngAs(".png");
            string output = NewTemp(".png");
            try
            {
                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { RequireFormatMatch = true });

                Assert.False(result.Converted, "同格式压缩不该被标记为跨格式转换");
                Assert.True(File.Exists(output));
            }
            finally
            {
                foreach (string f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        /// <summary>
        /// 扩展名无法识别（<c>.bin</c>）时无从比对，严格模式不应拦——否则管道/临时文件场景全废。
        /// </summary>
        [Fact]
        public void RequireFormatMatch_Ignores_Unknown_Extension()
        {
            string input = WritePngAs(".bin");
            string output = NewTemp(".bin");
            try
            {
                var result = ImageOptimizer.Optimize(input, output, new OptimizeOptions { RequireFormatMatch = true });

                Assert.Null(result.Reason);
                Assert.True(File.Exists(output));
            }
            finally
            {
                foreach (string f in new[] { input, output })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        /// <summary>
        /// <see cref="ImageOptimizer.EnsureFormatMatches"/> 是唯一判定点：转换路径与 optimize 路径共用，
        /// 直接调用时的行为也要与 optimize 内一致。
        /// </summary>
        [Fact]
        public void EnsureFormatMatches_Is_The_Single_Check()
        {
            string input = WritePngAs(".jpg");
            try
            {
                var kind = ImageOptimizer.DetectFormat(input);
                Assert.Equal(ImageFormatKind.Png, kind);
                Assert.Throws<InvalidDataException>(() => ImageOptimizer.EnsureFormatMatches(input, kind));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
            }
        }
    }
}
