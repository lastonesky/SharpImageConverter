using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Gif
{
    /// <summary>
    /// GIF 图像格式描述与探测。
    /// </summary>
    public sealed class GifFormat : IImageFormat
    {
        /// <summary>
        /// 格式名称
        /// </summary>
        public string Name => "GIF";
        /// <summary>
        /// 支持扩展名
        /// </summary>
        public string[] Extensions => new[] { ".gif" };
        /// <summary>
        /// 判断输入流是否为 GIF（GIF 头）
        /// </summary>
        /// <param name="s">输入流</param>
        /// <returns>匹配返回 true</returns>
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[3];
            if (s.Read(b) != b.Length) return false;
            return b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F';
        }
    }
}
