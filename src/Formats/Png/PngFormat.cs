using System;
using System.IO;
using SharpImageConverter.Core;

namespace SharpImageConverter.Formats
{
    /// <summary>
    /// PNG 图像格式描述与探测。
    /// </summary>
    public sealed class PngFormat : IImageFormat
    {
        private static readonly string[] s_extensions = [".png"];
        /// <summary>
        /// 格式名称
        /// </summary>
        public string Name => "PNG";
        /// <summary>
        /// 支持扩展名
        /// </summary>
        public string[] Extensions => (string[])s_extensions.Clone();
        /// <summary>
        /// 判断输入流是否为 PNG（8 字节签名）
        /// </summary>
        /// <param name="s">输入流</param>
        /// <returns>匹配返回 true</returns>
        public bool IsMatch(Stream s)
        {
            long origin = 0;
            bool restorePosition = s.CanSeek;
            if (restorePosition)
            {
                origin = s.Position;
            }

            Span<byte> b = stackalloc byte[8];
            try
            {
                if (s.Read(b) != b.Length) return false;
                return b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
            }
            finally
            {
                if (restorePosition)
                {
                    s.Position = origin;
                }
            }
        }
    }
}
