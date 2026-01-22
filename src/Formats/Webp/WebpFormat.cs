using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>
    /// WebP 格式探测器与元信息
    /// </summary>
    public sealed class WebpFormat : IImageFormat
    {
        /// <summary>
        /// 格式名称
        /// </summary>
        public string Name => "WebP";
        /// <summary>
        /// 支持的文件扩展名
        /// </summary>
        public string[] Extensions => [".webp"];
        /// <summary>
        /// 判断流是否为 WebP 文件（RIFF/WEBP 头）
        /// </summary>
        /// <param name="s">输入流</param>
        /// <returns>是 WebP 则为 true，否则为 false</returns>
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[12];
            if (s.Read(b) != b.Length) return false;
            return b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
                && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
        }
    }
}
