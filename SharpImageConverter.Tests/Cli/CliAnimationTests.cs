using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Webp;
using Xunit;

namespace SharpImageConverter.Tests.Cli;

/// <summary>
/// CLI 层的动图通路：WebP 动图的输入与输出、GIF → WebP 动图的 alpha 保留。
/// </summary>
/// <remarks>
/// 这些行为此前全都不成立：
/// <list type="bullet">
/// <item>WebP 动图作为输入时，转换路径直接抛 <c>WebpUnsupportedException</c>
/// （单图解码入口拒绝动图），只有 <c>--optimize</c> 走「跳过压缩、原样拷贝」；</item>
/// <item>GIF → WebP 动图走的是 Rgb24 路径，透明通道被整个丢掉
/// （实测整幅 1024 像素全部变成 alpha=255）；</item>
/// <item>GIF 编码器在「颜色丰富 + 含透明」时调色板需要 257 个槽位，超出 GIF 的 256 项上限，
/// 写调色板时数组越界（<c>Destination array was not long enough</c>）。</item>
/// </list>
/// </remarks>
public class CliAnimationTests
{
    private static string LocateCliAssembly()
    {
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
        string path = Path.Combine(Path.GetTempPath(), $"sic_anim_{Guid.NewGuid():N}{ext}");
        return path;
    }

    /// <summary>
    /// 造一个 3 帧 WebP 动图：每帧一个不透明方块，方块外全透明。
    /// </summary>
    private static byte[] BuildWebpAnimation(int width, int height, int frameCount, out int[] durations)
    {
        var frames = new List<WebpAnimationFrameInput>();
        durations = new int[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            var rgba = new byte[width * height * 4];
            int boxSize = height / 2;
            int x0 = i * 4, y0 = i * 4;
            for (int y = y0; y < y0 + boxSize && y < height; y++)
                for (int x = x0; x < x0 + boxSize && x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    rgba[o] = 255;
                    rgba[o + 1] = (byte)(i * 80);
                    rgba[o + 2] = 40;
                    rgba[o + 3] = 255;
                }
            durations[i] = 100 + i * 20;
            frames.Add(new WebpAnimationFrameInput(rgba, durations[i]));
        }

        return WebpAnimationWriter.Encode(frames, width, height, loopCount: 3,
            new WebpAnimationEncodeOptions(Lossless: true));
    }

    /// <summary>
    /// CLI：WebP 动图 → WebP 动图，帧数、时长、循环次数都要保留。
    /// </summary>
    [Fact]
    public void WebpAnimation_To_WebpAnimation_PreservesFramesAndTiming()
    {
        const int w = 48, h = 40;
        byte[] source = BuildWebpAnimation(w, h, 4, out int[] durations);

        string input = NewTemp(".webp");
        string output = NewTemp(".webp");
        File.WriteAllBytes(input, source);
        try
        {
            var (exit, stdout, stderr) = RunCli(input, output, "--to", "webp");
            Assert.True(exit == 0, $"CLI 退出码 {exit}: {stderr}");
            Assert.True(File.Exists(output), "CLI 未产出 WebP 动图");

            var anim = WebpDecoder.DecodeAnimation(File.ReadAllBytes(output));
            Assert.Equal(4, anim.Frames.Length);
            Assert.Equal(w, anim.CanvasWidth);
            Assert.Equal(h, anim.CanvasHeight);
            Assert.Equal(3, anim.LoopCount);
            for (int i = 0; i < durations.Length; i++)
            {
                Assert.Equal(durations[i], anim.Frames[i].DurationMs);
            }
        }
        finally
        {
            File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// CLI：WebP 动图 → GIF 动图，帧数与时长保留。
    /// </summary>
    [Fact]
    public void WebpAnimation_To_GifAnimation_PreservesFramesAndTiming()
    {
        const int w = 48, h = 40;
        byte[] source = BuildWebpAnimation(w, h, 4, out int[] durations);

        string input = NewTemp(".webp");
        string output = NewTemp(".gif");
        File.WriteAllBytes(input, source);
        try
        {
            var (exit, stdout, stderr) = RunCli(input, output, "--to", "gif");
            Assert.True(exit == 0, $"CLI 退出码 {exit}: {stderr}");
            Assert.True(File.Exists(output), "CLI 未产出 GIF 动图");

            using var fs = File.OpenRead(output);
            var anim = new GifDecoder().DecodeAnimationRgba32(fs);
            Assert.Equal(4, anim.Frames.Count);
            for (int i = 0; i < durations.Length; i++)
            {
                Assert.Equal(durations[i], anim.FrameDurationsMs[i]);
            }
        }
        finally
        {
            File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// CLI：WebP 动图 → 静态格式，取首帧且必须给出提示（不能静默丢帧）。
    /// </summary>
    [Fact]
    public void WebpAnimation_To_StillImage_TakesFirstFrame_AndWarns()
    {
        const int w = 48, h = 40;
        byte[] source = BuildWebpAnimation(w, h, 4, out _);

        string input = NewTemp(".webp");
        string output = NewTemp(".png");
        File.WriteAllBytes(input, source);
        try
        {
            var (exit, stdout, stderr) = RunCli(input, output, "--to", "png");
            Assert.True(exit == 0, $"CLI 退出码 {exit}: {stderr}");
            Assert.True(File.Exists(output), "CLI 未产出 PNG");

            // 提示必须出现在 stdout 或 stderr 里
            string all = stdout + stderr;
            Assert.Contains("首帧", all, StringComparison.Ordinal);

            var loaded = SharpImageConverter.Core.Image.LoadRgba32(output);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
        }
        finally
        {
            File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// CLI：GIF 动图 → WebP 动图，透明通道必须保留。
    /// </summary>
    /// <remarks>
    /// 此前 CLI 走 Rgb24 路径，GIF 的透明索引信息全部丢失（整幅都变成不透明）。
    /// </remarks>
    [Fact]
    public void GifAnimation_To_WebpAnimation_PreservesAlpha()
    {
        const int w = 32, h = 32;

        // 造一个带透明背景的 GIF 动图
        var frames = new List<byte[]>();
        for (int f = 0; f < 3; f++)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    bool inBox = x >= f * 4 && x < f * 4 + 16 && y >= f * 4 && y < f * 4 + 16;
                    if (inBox)
                    {
                        rgba[o] = 255; rgba[o + 1] = (byte)(f * 60); rgba[o + 2] = 0; rgba[o + 3] = 255;
                    }
                    else
                    {
                        rgba[o + 3] = 0; // 透明背景
                    }
                }
            frames.Add(rgba);
        }

        string gifPath = NewTemp(".gif");
        int[] gifDurations = [100, 100, 100];
        using (var fs = File.Create(gifPath))
        {
            new GifEncoder().EncodeAnimationRgba(w, h, frames, gifDurations, 0, fs);
        }

        string output = NewTemp(".webp");
        try
        {
            var (exit, stdout, stderr) = RunCli(gifPath, output, "--to", "webp");
            Assert.True(exit == 0, $"CLI 退出码 {exit}: {stderr}");
            Assert.True(File.Exists(output), "CLI 未产出 WebP 动图");

            var anim = WebpDecoder.DecodeAnimation(File.ReadAllBytes(output));
            Assert.Equal(3, anim.Frames.Length);

            // 首帧应有透明像素
            var frame = anim.Frames[0];
            int transparent = 0;
            for (int p = 0; p < w * h; p++)
            {
                if (frame.Rgba[p * 4 + 3] != 255) transparent++;
            }
            Assert.True(transparent > 0,
                $"GIF 的透明背景应在 WebP 里保留，实际首帧 0 个透明像素（alpha 全被丢弃）");
        }
        finally
        {
            File.Delete(gifPath);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// CLI：--anim-frames 把 WebP 动图拆成单帧序列。
    /// </summary>
    [Fact]
    public void WebpAnimation_AnimFrames_ExportsFrameSequence()
    {
        const int w = 40, h = 32;
        byte[] source = BuildWebpAnimation(w, h, 5, out _);

        string input = NewTemp(".webp");
        string dir = Path.Combine(Path.GetTempPath(), $"sic_anim_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(input, source);
        try
        {
            string output = Path.Combine(dir, "frame.png");
            var (exit, stdout, stderr) = RunCli(input, output, "--anim-frames");
            Assert.True(exit == 0, $"CLI 退出码 {exit}: {stderr}");

            var files = Directory.GetFiles(dir, "frame_*.png");
            Assert.Equal(5, files.Length);

            // 抽出的帧尺寸应等于画布尺寸
            var loaded = SharpImageConverter.Core.Image.LoadRgba32(files[0]);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
        }
        finally
        {
            File.Delete(input);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
