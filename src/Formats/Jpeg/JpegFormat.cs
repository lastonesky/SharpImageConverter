using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{
    /// <summary>
    /// JPEG 图像格式描述与探测。
    /// </summary>
    public sealed class JpegFormat : IImageFormat
    {
        /// <summary>
        /// 格式名称
        /// </summary>
        public string Name => "JPEG";
        /// <summary>
        /// 支持扩展名
        /// </summary>
        public string[] Extensions => new[] { ".jpg", ".jpeg" };
        /// <summary>
        /// 判断输入流是否为 JPEG（FF D8 头）
        /// </summary>
        /// <param name="s">输入流</param>
        /// <returns>匹配返回 true</returns>
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            if (s.Read(b) != b.Length) return false;
            return b[0] == 0xFF && b[1] == 0xD8;
        }
    }
}
