using System;
using SharpImageConverter.Formats.Webp.Managed;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>WebP 编码实现选择。</summary>
    public enum WebpEncodeBackend
    {
        /// <summary>默认：无损走纯托管 VP8L，有损走 libwebp（托管尚未实现有损编码）。</summary>
        Auto = 0,

        /// <summary>强制 libwebp 原生实现（既有无损/有损行为，便于对拍与回退）。</summary>
        LibWebp = 1,

        /// <summary>强制纯 C# 托管实现；目前只支持无损，有损直接抛异常，不静默回退。</summary>
        Managed = 2,
    }

    /// <summary>
    /// WebP 编码后端策略。
    ///
    /// 环境变量 <c>SIC_WEBP_ENCODE_BACKEND</c>：<c>auto</c>（默认）/ <c>libwebp</c>（别名 <c>native</c>）/ <c>managed</c>。
    ///
    /// 托管编码器只实现 VP8L 无损：<see cref="WebpEncodeBackend.Managed"/> 下遇到有损请求会抛
    /// <see cref="WebpUnsupportedException"/>，避免静默改用 libwebp 导致行为不可预期。
    /// </summary>
    public static class WebpEncodePolicy
    {
        static WebpEncodePolicy()
        {
            Backend = Parse(Environment.GetEnvironmentVariable("SIC_WEBP_ENCODE_BACKEND"));
        }

        /// <summary>当前生效的后端（测试可覆盖；进程内全局，非线程本地）。</summary>
        public static WebpEncodeBackend Backend { get; set; }

        private static WebpEncodeBackend Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "managed" or "csharp" or "native-csharp" => WebpEncodeBackend.Managed,
            "libwebp" or "native" or "interop" or "off" => WebpEncodeBackend.LibWebp,
            _ => WebpEncodeBackend.Auto,
        };
    }

    /// <summary>按策略在托管 VP8L 编码器与 libwebp 之间分派。</summary>
    internal static class WebpEncodeDispatch
    {
        /// <summary>无损 RGBA 编码是否应交给托管实现。</summary>
        public static bool ShouldEncodeLosslessManaged()
        {
            return WebpEncodePolicy.Backend switch
            {
                WebpEncodeBackend.Managed => true,
                WebpEncodeBackend.LibWebp => false,
                _ => true,
            };
        }

        /// <summary>
        /// 若策略要求托管后端且请求为有损，抛出 <see cref="WebpUnsupportedException"/>。
        /// Auto 与 LibWebp 下有损请求仍走 libwebp。
        /// </summary>
        public static void EnsureSupportsLossy()
        {
            if (WebpEncodePolicy.Backend == WebpEncodeBackend.Managed)
                throw new WebpUnsupportedException(
                    "托管 WebP 编码器目前只支持无损（VP8L），有损编码需要 libwebp 后端");
        }

        /// <summary>按策略把 RGBA 无损编码成完整 WebP 字节。</summary>
        public static byte[] EncodeLosslessRgba(ReadOnlySpan<byte> rgba, int width, int height, bool managed)
        {
            return managed
                ? ManagedWebpEncoder.EncodeRgba(rgba, width, height)
                : WebpCodec.EncodeLosslessRgbaForDispatch(rgba, width, height);
        }
    }
}
