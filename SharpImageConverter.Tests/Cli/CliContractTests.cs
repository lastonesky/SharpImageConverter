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

        /// <summary>
        /// 跑一次 CLI，向标准输入喂字节、把标准输出按**原始字节**收回来。
        /// </summary>
        /// <remarks>
        /// 不能用 <c>StandardOutput.ReadToEnd()</c>：它会按 UTF-8 解码，二进制会被替换字符破坏，
        /// 断言就变成测编码而不是测产物。
        /// </remarks>
        private static (int ExitCode, byte[] StdOut, string StdErr) RunCliWithStdin(byte[]? stdin, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(LocateCliAssembly());
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            using (Stream childIn = proc.StandardInput.BaseStream)
            {
                if (stdin is { Length: > 0 }) childIn.Write(stdin, 0, stdin.Length);
            }

            using var buffer = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(buffer);
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, buffer.ToArray(), stderr);
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

        /// <summary>
        /// <c>--json</c>：stdout 必须是一个可解析的 JSON 对象，且不再夹带人看的进度行。
        /// </summary>
        [Fact]
        public void Json_Output_Is_Single_Parseable_Object()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(32, 32), input);
                var (code, stdout, stderr) = RunCli(input, output, "--json");
                Assert.Equal(0, code);
                Assert.Equal(string.Empty, stderr);

                using var doc = System.Text.Json.JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.Equal("convert", root.GetProperty("Mode").GetString());
                Assert.Equal(1, root.GetProperty("TotalFiles").GetInt32());
                Assert.Equal(0, root.GetProperty("Failed").GetInt32());
                Assert.True(root.GetProperty("Files").GetArrayLength() == 1);
                // 关键：不能出现 emoji/耗时这类人看的进度行（会破坏脚本解析）
                Assert.DoesNotContain("⏱️", stdout, StringComparison.Ordinal);
                Assert.DoesNotContain("✅", stdout, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// <c>--json</c> 的输出必须是 UTF-8：Windows 控制台默认代码页是 GBK，
        /// 中文（方法名/画质摘要）会被写成解析不了的字节。
        /// </summary>
        [Fact]
        public void Json_Output_Is_Valid_Utf8()
        {
            string input = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(48, 48), input);
                var (code, stdout, _) = RunCli(input, "--optimize", "--json");
                Assert.Equal(0, code);

                // 若代码页不对，这一行会抛（实测 GBK 字节无法按 UTF-8 解码）
                using var doc = System.Text.Json.JsonDocument.Parse(stdout);
                var file = doc.RootElement.GetProperty("Files")[0];
                string method = file.GetProperty("Method").GetString() ?? string.Empty;
                Assert.Contains("JPEG", method, StringComparison.Ordinal);
                Assert.False(method.Contains('�'));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                string min = Path.ChangeExtension(input, ".min.jpg");
                if (File.Exists(min)) File.Delete(min);
            }
        }

        /// <summary>
        /// <c>--quiet</c>：成功时不打印任何东西，但文件照产、退出码照旧。
        /// </summary>
        [Fact]
        public void Quiet_Succeeds_Silently()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(32, 32), input);
                var (code, stdout, stderr) = RunCli(input, output, "--quiet");
                Assert.Equal(0, code);
                Assert.Equal(string.Empty, stdout);
                Assert.Equal(string.Empty, stderr);
                Assert.True(File.Exists(output));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// <c>--quiet</c> 不吞错误：失败时 stderr 仍要给出文件名与原因，否则脚本只知道"失败了"。
        /// </summary>
        [Fact]
        public void Quiet_Still_Prints_Errors()
        {
            string input = NewTemp(".png");
            try
            {
                File.WriteAllBytes(input, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
                var (code, stdout, stderr) = RunCli(input, NewTemp(".jpg"), "--quiet");
                Assert.Equal(1, code);
                Assert.Equal(string.Empty, stdout);
                Assert.Contains("处理失败", stderr, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
            }
        }

        /// <summary>
        /// <c>sic in.png - --to jpg</c> 写出的字节必须与 <c>sic in.png out.jpg</c> 完全一致：
        /// 管道桥接走临时文件，不得改变编码结果，也不得被进度行污染。
        /// </summary>
        [Fact]
        public void Stdout_Output_Is_Byte_Identical_To_File_Output()
        {
            string input = NewTemp(".png");
            string fileOut = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(48, 32), input);
                Assert.Equal(0, RunCli(input, fileOut).ExitCode);

                var piped = RunCliWithStdin(null, input, "-", "--to", "jpg");
                Assert.Equal(0, piped.ExitCode);
                Assert.Equal(File.ReadAllBytes(fileOut), piped.StdOut);
                Assert.Contains("写入完成: -", piped.StdErr, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(fileOut)) File.Delete(fileOut);
            }
        }

        /// <summary>
        /// <c>sic - out.jpg</c>：输入侧的格式靠内容嗅探，不靠扩展名（管道没有文件名）。
        /// </summary>
        [Fact]
        public void Stdin_Input_Is_Sniffed_And_Matches_File_Output()
        {
            string input = NewTemp(".png");
            string fileOut = NewTemp(".jpg");
            string stdinOut = NewTemp(".jpg");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(48, 32), input);
                Assert.Equal(0, RunCli(input, fileOut).ExitCode);

                var piped = RunCliWithStdin(File.ReadAllBytes(input), "-", stdinOut);
                Assert.Equal(0, piped.ExitCode);
                Assert.Equal(File.ReadAllBytes(fileOut), File.ReadAllBytes(stdinOut));
            }
            finally
            {
                foreach (string f in new[] { input, fileOut, stdinOut })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }

        /// <summary>
        /// 输出到 stdout 时没有扩展名可用，必须显式给 <c>--to</c>；缺了就是用法错误。
        /// </summary>
        [Fact]
        public void Stdout_Without_Target_Format_Is_Usage_Error()
        {
            string input = NewTemp(".png");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(16, 16), input);
                var (code, stdout, stderr) = RunCliWithStdin(null, input, "-");
                Assert.Equal(2, code);
                Assert.Empty(stdout);
                Assert.Contains("--to", stderr, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
            }
        }

        /// <summary>
        /// <c>--json</c> 与 stdout 输出互斥：同一根流不能既放报告又放图片字节。
        /// </summary>
        [Fact]
        public void Stdout_With_Json_Is_Usage_Error()
        {
            string input = NewTemp(".png");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(16, 16), input);
                var (code, stdout, stderr) = RunCliWithStdin(null, input, "-", "--to", "png", "--json");
                Assert.Equal(2, code);
                Assert.Empty(stdout);
                Assert.Contains("--json", stderr, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
            }
        }

        /// <summary>
        /// 空的标准输入要明确失败（退出码 1），不能静默产出 0 字节文件。
        /// </summary>
        [Fact]
        public void Empty_Stdin_Fails_Without_Writing_Output()
        {
            string output = NewTemp(".png");
            try
            {
                var (code, _, stderr) = RunCliWithStdin([], "-", output);
                Assert.Equal(1, code);
                Assert.False(File.Exists(output));
                Assert.Contains("标准输入", stderr, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// 已移除的选项要报用法错误并说明原因，不能"警告后继续"——静默忽略会让旧脚本
        /// 以为参数还在生效。
        /// </summary>
        [Fact]
        public void Removed_Option_Fails_With_Reason()
        {
            string input = NewTemp(".png");
            string output = NewTemp(".webp");
            try
            {
                Core.Image.Save(Helpers.TestImageFactory.CreateGradient(16, 16), input);
                var (code, _, stderr) = RunCli(input, output, "--opt-webp-params");
                Assert.Equal(2, code);
                Assert.Contains("1.3.0 移除", stderr, StringComparison.Ordinal);
                Assert.False(File.Exists(output));
            }
            finally
            {
                if (File.Exists(input)) File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }

        /// <summary>
        /// <c>--strict-format</c>：PNG 内容存成 <c>.jpg</c> 名 → 退出码 1、stderr 说明不符、不产出文件；
        /// 不带开关时保持既有的隐式转格式（退出码 0），两条路径（转换 / optimize）都要拦得住。
        /// </summary>
        [Fact]
        public void StrictFormat_Rejects_Lying_Extension()
        {
            string input = NewTemp(".jpg");
            string converted = NewTemp(".jpg");
            string optimized = NewTemp(".jpg");
            try
            {
                // 用 PNG 编码器写出真实 PNG 字节，再存成 .jpg 名字
                using (var ms = new MemoryStream())
                {
                    var img = Helpers.TestImageFactory.CreateGradient(24, 24);
                    SharpImageConverter.Formats.Png.PngWriter.Write(ms, img.Width, img.Height, img.Buffer, img.Metadata);
                    File.WriteAllBytes(input, ms.ToArray());                }

                // 默认口径：仍被隐式转成 JPEG
                var plain = RunCli(input, converted);
                Assert.True(plain.ExitCode == 0, $"默认口径应成功，stderr: {plain.StdErr}");
                Assert.True(File.Exists(converted));

                var convert = RunCli(input, converted + ".strict.jpg", "--strict-format");
                Assert.Equal(1, convert.ExitCode);
                Assert.Contains("不符", convert.StdErr, StringComparison.Ordinal);
                Assert.False(File.Exists(converted + ".strict.jpg"), "严格模式失败后不应留下输出");

                var optimize = RunCli(input, optimized, "--optimize", "--strict-format");
                Assert.Equal(1, optimize.ExitCode);
                Assert.Contains("不符", optimize.StdErr, StringComparison.Ordinal);
                Assert.False(File.Exists(optimized), "严格模式失败后不应留下输出");
            }
            finally
            {
                foreach (string f in new[] { input, converted, converted + ".strict.jpg", optimized })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
        }
    }
}
