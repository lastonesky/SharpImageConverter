using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats.Bmp
{
    /// <summary>
    /// BMP 图像格式描述与探测。
    /// </summary>
    public sealed class BmpFormat : IImageFormat
    {
        /// <summary>
        /// 格式名称
        /// </summary>
        public string Name => "BMP";
        /// <summary>
        /// 支持扩展名
        /// </summary>
        public string[] Extensions => new[] { ".bmp" };
        /// <summary>
        /// 判断输入流是否为 BMP（BM 头）
        /// </summary>
        /// <param name="s">输入流</param>
        /// <returns>匹配返回 true</returns>
        public bool IsMatch(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            if (s.Read(b) != b.Length) return false;
            return b[0] == (byte)'B' && b[1] == (byte)'M';
        }
    }
}
