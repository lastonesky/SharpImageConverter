using System;
using System.Buffers;
using System.IO;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp.Managed;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>WebP 解码实现选择。</summary>
    public enum WebpDecodeBackend
    {
        /// <summary>默认：能用纯托管就用（无损/VP8L），不支持的特性自动回退 libwebp。</summary>
        Auto = 0,

        /// <summary>强制 libwebp 原生实现（既有行为）。</summary>
        LibWebp = 1,

        /// <summary>强制纯 C# 托管实现；不支持的特性直接抛异常，不静默回退。</summary>
        Managed = 2,
    }

    /// <summary>
    /// WebP 解码后端策略。
    ///
    /// 环境变量 <c>SIC_WEBP_BACKEND</c>：<c>auto</c>（默认）/ <c>libwebp</c> / <c>managed</c>。
    /// 环境变量 <c>SIC_WEBP_TRACE=1</c>：托管解码失败并回退 libwebp 时向 stderr 打一行原因，便于线上观测。
    ///
    /// 动图在 <see cref="WebpDecodeBackend.Auto"/> 下始终走 libwebp：托管实现会把全部帧解成
    /// 整幅画布快照（内存与时序都与既有单图解码不同），不适合作为透明替换。
    /// </summary>
    public static class WebpDecodePolicy
    {
        private static readonly bool s_trace;

        static WebpDecodePolicy()
        {
            Backend = Parse(Environment.GetEnvironmentVariable("SIC_WEBP_BACKEND"));
            s_trace = Environment.GetEnvironmentVariable("SIC_WEBP_TRACE") == "1";
        }

        /// <summary>当前生效的后端（测试可覆盖；进程内全局，非线程本地）。</summary>
        public static WebpDecodeBackend Backend { get; set; }

        /// <summary>回退时是否输出诊断信息。</summary>
        internal static bool TraceEnabled => s_trace;

        private static WebpDecodeBackend Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "managed" or "csharp" or "native-csharp" => WebpDecodeBackend.Managed,
            "libwebp" or "interop" or "off" => WebpDecodeBackend.LibWebp,
            _ => WebpDecodeBackend.Auto,
        };

        internal static void TraceFallback(string reason)
        {
            if (!s_trace) return;
            Console.Error.WriteLine($"[sic:webp] 托管解码不可用，回退 libwebp：{reason}");
        }
    }

    /// <summary>按策略在托管实现与 libwebp 之间分派解码。</summary>
    internal static class WebpDecodeDispatch
    {
        /// <summary>把 WebP 流解码为 RGBA 字节（每像素 R,G,B,A）。</summary>
        public static byte[] DecodeRgba(Stream stream, out int width, out int height)
        {
            ArgumentNullException.ThrowIfNull(stream);

            if (stream.CanSeek)
                return DecodeRgba(ReadSeekable(stream), out width, out height);

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
                return DecodeRgba(seg.Array!.AsSpan(seg.Offset, seg.Count), out width, out height);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(poolBuf);
            }
        }

        /// <summary>把 WebP 字节解码为 RGBA 字节（每像素 R,G,B,A）。</summary>
        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
        {
            ArgumentNullException.ThrowIfNull(data);

            switch (WebpDecodePolicy.Backend)
            {
                case WebpDecodeBackend.LibWebp:
                    return WebpCodec.DecodeRgba(data, out width, out height);
                case WebpDecodeBackend.Managed:
                    return ManagedWebpDecoder.DecodeRgba(data, out width, out height);
                default:
                    return DecodeAuto(data, out width, out height);
            }
        }

        private static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height)
        {
            switch (WebpDecodePolicy.Backend)
            {
                case WebpDecodeBackend.LibWebp:
                    return WebpCodec.DecodeRgba(data, out width, out height);
                case WebpDecodeBackend.Managed:
                    return ManagedWebpDecoder.DecodeRgba(data, out width, out height);
                default:
                    // 动图：既有单图解码语义来自 libwebp（首帧），托管实现返回最后一帧，故不参与自动分派。
                    if (LooksAnimated(data))
                        return WebpCodec.DecodeRgba(data, out width, out height);
                    try
                    {
                        return ManagedWebpDecoder.DecodeRgba(data, out width, out height);
                    }
                    catch (WebpException ex)
                    {
                        WebpDecodePolicy.TraceFallback(ex.Message);
                        return WebpCodec.DecodeRgba(data, out width, out height);
                    }
            }
        }

        private static byte[] DecodeAuto(byte[] data, out int width, out int height)
        {
            // 动图：同上，保持 libwebp 的既有语义。
            if (LooksAnimated(data))
                return WebpCodec.DecodeRgba(data, out width, out height);

            try
            {
                return ManagedWebpDecoder.DecodeRgba(data, out width, out height);
            }
            catch (WebpException ex)
            {
                WebpDecodePolicy.TraceFallback(ex.Message);
                return WebpCodec.DecodeRgba(data, out width, out height);
            }
        }

        /// <summary>
        /// 快速判断是否为动图：只读 RIFF/WEBP 头与 VP8X 的 flags 字节（偏移 20，bit1 = ANIMATION）。
        /// 比完整解析容器便宜，且 Auto 分派不必为判类型额外解析一遍。
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
