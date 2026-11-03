using System;
using System.Buffers;
using System.IO;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>WebP 解码实现选择。</summary>
    public enum WebpDecodeBackend
    {
        /// <summary>默认：纯 C# 托管实现。</summary>
        Auto = 0,

        /// <summary>强制纯 C# 托管实现；不支持的特性直接抛异常，不静默回退。</summary>
        CSharp = 1,
    }

    /// <summary>
    /// WebP 解码后端策略。
    ///
    /// 环境变量 <c>SIC_WEBP_BACKEND</c>：<c>auto</c>（默认）/ <c>csharp</c>。
    /// 纯 C# 是唯一实现，两个取值行为一致，保留枚举只为兼容既有调用方。
    /// </summary>
    public static class WebpDecodePolicy
    {
        static WebpDecodePolicy()
        {
            Backend = Parse(Environment.GetEnvironmentVariable("SIC_WEBP_BACKEND"));
        }

        /// <summary>当前生效的后端（测试可覆盖；进程内全局，非线程本地）。</summary>
        public static WebpDecodeBackend Backend { get; set; }

        private static WebpDecodeBackend Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "csharp" or "managed" or "native-csharp" => WebpDecodeBackend.CSharp,
            _ => WebpDecodeBackend.Auto,
        };
    }

    /// <summary>纯 C# 解码入口（保留原分派层签名，便于适配器平滑迁移）。</summary>
    internal static class WebpDecodeDispatch
    {
        /// <summary>把 WebP 流解码为 RGBA 字节（每像素 R,G,B,A）。</summary>
        public static byte[] DecodeRgba(Stream stream, out int width, out int height)
            => DecodeRgba(stream, out width, out height, out _);

        /// <summary>
        /// 把 WebP 流解码为 RGBA 字节，同时从容器原样取出 ICCP / EXIF / XMP。
        /// </summary>
        public static byte[] DecodeRgba(Stream stream, out int width, out int height, out ImageMetadata? metadata)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (stream.CanSeek)
            {
                byte[] data = ReadSeekable(stream);
                var rgba = DecodeRgba(data, out width, out height);
                metadata = ExtractMetadata(data);
                return rgba;
            }

            // 不可 Seek（网络流）：用池化缓冲收完，解码期间直接在该缓冲上做，避免 ToArray 再拷一份。
            byte[] poolBuf = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                using var ms = new PooledMemoryStream(64 * 1024);
                int n;
                while ((n = stream.Read(poolBuf, 0, poolBuf.Length)) > 0)
                    ms.Write(poolBuf, 0, n);

                var seg = ms.GetBuffer();
                if (seg.Count <= 0) throw new InvalidDataException("WebP 流数据为空");
                byte[] data = seg.Array!.AsSpan(seg.Offset, seg.Count).ToArray();
                var rgba = DecodeRgba(data, out width, out height);
                metadata = ExtractMetadata(data);
                return rgba;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuf);
            }
        }

        /// <summary>
        /// 从完整 WebP 字节中取出元数据 chunk（只透传原始字节，不解析内容）。
        /// 容器结构异常时不影响解码结果，返回 null。
        /// </summary>
        internal static ImageMetadata? ExtractMetadata(byte[] data)
        {
            WebpContainer container;
            try
            {
                container = WebpContainer.Load(data);
            }
            catch (WebpContainerException)
            {
                return null;
            }

            if (!container.HasIccProfile && !container.HasExif && !container.HasXmp) return null;

            var metadata = new ImageMetadata();
            if (container.HasIccProfile) metadata.IccProfile = container.IccProfile.ToArray();
            if (container.HasExif) metadata.ExifRaw = container.Exif.ToArray();
            if (container.HasXmp) metadata.XmpRaw = container.Xmp.ToArray();
            return metadata;
        }

        /// <summary>把 WebP 字节解码为 RGBA 字节（每像素 R,G,B,A）。</summary>
        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
        {
            ArgumentNullException.ThrowIfNull(data);

            // 单图解码入口保持既有语义：动图不走这里，避免调用方拿到“最后一帧”而误以为是静态图。
            if (LooksAnimated(data))
                throw new WebpUnsupportedException("单图解码入口不支持动图 WebP");

            return WebpDecoder.DecodeRgba(data, out width, out height);
        }

        /// <summary>
        /// 快速判断是否为动图：只读 RIFF/WEBP 头与 VP8X 的 flags 字节（偏移 20，bit1 = ANIMATION）。
        /// </summary>
        private static bool LooksAnimated(ReadOnlySpan<byte> data)
        {
            if (data.Length < 21) return false;
            if (data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F') return false;
            if (data[8] != (byte)'W' || data[9] != (byte)'E' || data[10] != (byte)'B' || data[11] != (byte)'P') return false;
            if (data[12] != (byte)'V' || data[13] != (byte)'P' || data[14] != (byte)'8' || data[15] != (byte)'X') return false;
            return (data[20] & 0x02) != 0;
        }

        private static byte[] ReadSeekable(Stream stream)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining <= 0) throw new InvalidDataException("WebP 流数据为空");
            if (remaining > int.MaxValue) throw new InvalidDataException("WebP 流数据过大");

            var data = new byte[remaining];
            int offset = 0;
            while (offset < data.Length)
            {
                int read = stream.Read(data, offset, data.Length - offset);
                if (read == 0) throw new EndOfStreamException("WebP 流数据不完整");
                offset += read;
            }
            return data;
        }
    }
}
