using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Metadata;
using SharpImageConverter.Tests.Helpers;
using Xunit;
using SharpImageConverter.Formats;


namespace SharpImageConverter.Tests
{
    /// <summary>
    /// CLI 进程边界的端到端转换。
    /// </summary>
    public partial class FormatConversionTests
    {
        /// <summary>
        /// CLI 端到端：PNG → JPEG 走完整进程边界（参数解析 + 文件读写）。
        /// </summary>
        /// <remarks>
        /// 原先用 <c>dotnet run --project Cli</c>，实测 3.0 s，其中 2.6 s 花在 dotnet run 自身的
        /// 求值/还原上，占整个测试套件的 17%。改为执行已构建的 CLI dll（0.4 s），
        /// 构建顺序由 csproj 里的 <c>ReferenceOutputAssembly=false</c> 项目引用保证。
        /// </remarks>
        [Fact]
        public void Cli_EndToEnd_Convert_Png_To_Jpeg()
        {
            var img = TestImageFactory.CreateGradient(128, 128);
            string input = NewTemp(".png");
            string output = NewTemp(".jpg");
            Image.Save(img, input);
            try
            {
                string cliDll = LocateCliAssembly();
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{cliDll}\" \"{input}\" \"{output}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi)!;
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.True(proc.ExitCode == 0, $"CLI 退出码 {proc.ExitCode}: {stderr}");
                Assert.True(File.Exists(output), $"CLI 未产出 {output}");
                var loaded = Image.Load(output);
                Assert.Equal(img.Width, loaded.Width);
                Assert.Equal(img.Height, loaded.Height);
            }
            finally
            {
                File.Delete(input);
                if (File.Exists(output)) File.Delete(output);
            }
        }
    }
}
