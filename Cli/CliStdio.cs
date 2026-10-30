using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter;

/// <summary>
/// <c>-</c>（标准输入/标准输出）的落地：把管道两端映射成临时文件。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接把编解码器接到 <see cref="Stream"/>：CLI 的转换路径需要**路径**才能表达
/// 目标格式（<c>Path.GetExtension</c> 派发 GIF 动画、WebP 自适应、JPEG 渐进式转码等十余处分叉），
/// 而且解码器要求可 <see cref="Stream.CanSeek"/>。把管道桥接成「临时文件 + 正确扩展名」，
/// 走的是与真实文件**完全相同**的代码路径，产物逐字节一致，也不会因为新增格式而漏改。
/// </para>
/// <para>
/// 代价是管道模式多一次磁盘往返（顺序读写）。相对一次编码（4 MP 图 JPEG 单遍就已 12 ms 起）
/// 可以忽略，换来的是零分叉。
/// </para>
/// </remarks>
internal sealed class CliStdio : IDisposable
{
    /// <summary>标准输入/输出的占位参数。</summary>
    public const string Dash = "-";

    private string? _tempInput;
    private string? _tempOutput;
    private bool _stdoutPending;

    /// <summary>参数是否代表标准输入/输出。</summary>
    public static bool IsDash(string? value) => value == Dash;

    /// <summary>本次运行是否把结果写到标准输出（决定人读信息是否改走 stderr）。</summary>
    public bool WritingStdout => _stdoutPending;

    /// <summary>本次运行的输入是否来自标准输入（决定报告里显示 <c>-</c> 还是临时路径）。</summary>
    public bool ReadingStdin => _tempInput is not null;

    /// <summary>
    /// 把标准输入读进临时文件，返回可直接交给后续管线的路径。
    /// </summary>
    /// <remarks>
    /// 扩展名由内容嗅探决定（<see cref="Image.Identify(Stream)"/>），而不是靠用户声明：
    /// 管道的另一端不会带文件名。无法识别的格式直接抛出，报错文案与处理普通文件时一致。
    /// </remarks>
    public string StageInput()
    {
        string temp = NewTempPath(".part");
        bool staged = false;
        try
        {
            using (Stream stdin = Console.OpenStandardInput())
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                stdin.CopyTo(file);
            }

            if (new FileInfo(temp).Length == 0)
            {
                throw new InvalidDataException("标准输入为空");
            }

            ImageFormatKind kind;
            using (var probe = File.OpenRead(temp))
            {
                kind = Image.Identify(probe).Format;
            }

            string ext = kind switch
            {
                ImageFormatKind.Jpeg => ".jpg",
                ImageFormatKind.Png => ".png",
                ImageFormatKind.Gif => ".gif",
                ImageFormatKind.Webp => ".webp",
                ImageFormatKind.Bmp => ".bmp",
                _ => string.Empty,
            };
            if (ext.Length == 0)
            {
                throw new NotSupportedException($"标准输入不是可解码的图像格式（{ImageInfo.SupportedFormatList}）");
            }

            string path = Path.ChangeExtension(temp, ext);
            File.Move(temp, path);
            _tempInput = path;
            staged = true;
            return path;
        }
        finally
        {
            // 失败路径（嗅探抛异常等）也要清掉半截文件
            if (!staged) TryDelete(temp);
        }
    }

    /// <summary>
    /// 若 <see cref="CliOptions.OutputPath"/> 是 <c>-</c>，改写成临时文件路径并登记待输出。
    /// </summary>
    /// <param name="options">已解析的选项（会被就地改写的只有 <see cref="CliOptions.OutputPath"/>）</param>
    /// <param name="exitCode">校验失败时的退出码</param>
    /// <returns>是否继续执行；false 表示已经报错</returns>
    public bool TryPrepareOutput(CliOptions options, out int exitCode)
    {
        exitCode = 0;
        if (!IsDash(options.OutputPath)) return true;

        if (options.IsDirectoryInput)
        {
            Console.Error.WriteLine("输出到标准输出时不支持目录输入（批量结果无法放进一条流）");
            exitCode = 2;
            return false;
        }
        if (options.GifFrames)
        {
            Console.Error.WriteLine("输出到标准输出时不支持 --gif-frames（多帧无法放进一条流）");
            exitCode = 2;
            return false;
        }
        if (options.Json)
        {
            // JSON 报告本身要写 stdout，再叠一份二进制就是不可解析的流
            Console.Error.WriteLine("--json 与输出到标准输出不能同时使用");
            exitCode = 2;
            return false;
        }
        if (options.OutputExtension is null)
        {
            // 管道没有文件名，只能靠 --to 指定格式
            Console.Error.WriteLine("输出到标准输出时必须用 --to <格式> 指定输出格式（如 --to png）");
            exitCode = 2;
            return false;
        }

        string path = NewTempPath(options.OutputExtension);
        _tempOutput = path;
        _stdoutPending = true;
        options.OutputPath = path;
        return true;
    }

    /// <summary>
    /// 把暂存的结果复制到标准输出。只在处理成功（退出码 0）时调用：
    /// 失败时往管道里写半截数据，比什么都不写更糟。
    /// </summary>
    public void FlushStdout()
    {
        if (!_stdoutPending || _tempOutput is null) return;

        using (var file = File.OpenRead(_tempOutput))
        using (Stream stdout = Console.OpenStandardOutput())
        {
            file.CopyTo(stdout);
            stdout.Flush();
        }
        _stdoutPending = false;
    }

    /// <summary>删除本次运行创建的临时文件。</summary>
    public void Dispose()
    {
        TryDelete(_tempInput);
        TryDelete(_tempOutput);
        _tempInput = null;
        _tempOutput = null;
    }

    private static string NewTempPath(string extension)
    {
        string name = "sic-" + Guid.NewGuid().ToString("N") + extension;
        return Path.Combine(Path.GetTempPath(), name);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 清理失败不影响退出码：临时目录由系统回收，这里不掩盖主流程的结果
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
