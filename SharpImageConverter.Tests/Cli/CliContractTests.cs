using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace SharpImageConverter.Tests
{
    /// <summary>
    /// CLI 的契约面：退出码、--help/--version、未知参数不得被静默吞掉。
    /// </summary>
    /// <remarks>
    /// 这些行为此前全都不成立：<c>Main</c> 是 <c>void</c>（永远 exit 0）、
    /// <c>sic --help</c> 会报"输入文件不存在: --help"、
    /// <c>sic in.png out.png --打错的选项 value</c> 会把 value 当成输出路径。
    /// </remarks>
    public class CliContractTests
    {
        private static string LocateCliAssembly()
        {
            // 与 FormatConversionTests.Cli_EndToEnd 相同的定位方式：直接用已构建的 CLI dll，
            // 避免 dotnet run 的求值开销
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "Cli", "bin", "Release", "net10.0", "SharpImageConverter.Cli.dll");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "Cli", "bin", "Debug", "net10.0", "SharpImageConverter.Cli.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("找不到已构建的 CLI 程序集");
        }

        private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(LocateCliAssembly());
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }

        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }

        /// <summary>
        /// --help 必须退出 0 并打印用法；不能把 --help 当输入路径。
        /// </summary>
        [Fact]
        public void Help_Exits_Zero_And_Prints_Usage()
        {
            var (code, stdout, stderr) = RunCli("--help");
            Assert.Equal(0, code);
            Assert.Contains("用法", stdout, StringComparison.Ordinal);
            Assert.Contains("--optimize", stdout, StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr);
        }

        /// <summary>
        /// --version 打印库版本号（当前在 1.x 区间），退出 0。
        /// </summary>
        [Fact]
        public void Version_Exits_Zero_And_Prints_Version()
        {
            var (code, stdout, _) = RunCli("--version");
            Assert.Equal(0, code);
            Assert.StartsWith("SharpImageConverter ", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("+", stdout); // SourceLink 的 commit 后缀被截掉
            Assert.Matches(@"\d+\.\d+\.\d+", stdout);
        }

        /// <summary>
        /// 无参数 → 用法错误（2），并打印用法。
        /// </summary>
        [Fact]
        public void No_Arguments_Returns_Usage_Error()
        {
            var (code, stdout, _) = RunCli();
            Assert.Equal(2, code);
            Assert.Contains("用法", stdout, StringComparison.Ordinal);
        }

        /// <summary>
        /// 输入不存在 → 用法错误（2）。
        /// </summary>
        [Fact]
        public void Missing_Input_Returns_Usage_Error()
        {
            var (code, _, stderr) = RunCli(NewTemp(".png"));
            Assert.Equal(2, code);
            Assert.Contains("输入文件不存在", stderr, StringComparison.Ordinal);
        }

        /// <summary>
        /// 未知选项必须是用法错误，且**不得**把它的值当输出路径去写文件。
        /// </summary>
        [Fact]
        public void Unknown_Option_Is_Rejected_And_Writes_Nothing()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".png");
            try
            {
                Helpers.TestImageFactory.CreateGradient(16, 16);
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(16, 16), input);

                var (code, _, stderr) = RunCli(input, output, "--definitely-not-an-option");
                Assert.Equal(2, code);
                Assert.Contains("未知参数", stderr, StringComparison.Ordinal);
                Assert.False(File.Exists(output), "未知选项被接受，并且产出了文件");
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// 未知选项后面跟一个值（最容易被误当成输出路径的情形）。
        /// </summary>
        [Fact]
        public void Unknown_Option_Does_Not_Swallow_Its_Value()
        {
            string input = NewTemp(".png");
            string stray = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(16, 16), input);

                // 输入合法、有 --bogus，后面的值不能被当成输出路径
                var (code, _, _) = RunCli(input, "--bogus", stray);
                Assert.Equal(2, code);
                Assert.False(File.Exists(stray), "未知选项的值被当成了输出路径");
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(stray)) File.Delete(stray);
            }
        }

        /// <summary>
        /// 正常转换 → 退出 0。
        /// </summary>
        [Fact]
        public void Successful_Convert_Returns_Zero()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(32, 32), input);
                var (code, _, stderr) = RunCli(input, output);
                Assert.True(code == 0, $"退出码 {code}，stderr: {stderr}");
                Assert.True(File.Exists(output));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// 目录批量里有坏文件时：其他文件照常处理，但退出码必须是 1（此前永远 0）。
        /// </summary>
        [Fact]
        public void Directory_Batch_With_Broken_File_Returns_One()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sic_cli_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string outDir = Path.Combine(dir, "out");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(16, 16), Path.Combine(dir, "good.png"));
                File.WriteAllBytes(Path.Combine(dir, "broken.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

                var (code, stdout, _) = RunCli(dir, outDir);
                Assert.Equal(1, code);
                Assert.Contains("失败 1", stdout, StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(outDir, "good.png")), "坏文件不应妨碍好文件");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
