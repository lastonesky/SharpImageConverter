using System;
using System.IO;

namespace SharpImageConverter.Core;

/// <summary>
/// 「先写同目录临时文件，成功后原子替换」的文件写入。
/// </summary>
/// <remarks>
/// 编码失败（不支持的参数、截断输入、取消）时不能留下 0 字节或半截的目标文件。
/// 临时文件与目标同目录，保证 <see cref="File.Move(string, string, bool)"/> 不会跨卷；
/// 任何异常都会清理临时文件后原样抛出。
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// 写入 <paramref name="path"/>：<paramref name="write"/> 写入的是临时文件，
    /// 正常返回后才替换目标文件。
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="write">向临时文件写入内容的委托</param>
    internal static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        string tempPath = Path.Combine(
            directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                write(fs);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}