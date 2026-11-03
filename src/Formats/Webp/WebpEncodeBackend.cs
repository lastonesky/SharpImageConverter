using System;
using SharpImageConverter.Metadata;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>WebP 编码实现选择。</summary>
    public enum WebpEncodeBackend
    {
        /// <summary>默认：无损走纯托管 VP8L，有损走 libwebp。</summary>
        Auto = 0,

        /// <summary>强制 libwebp 原生实现（既有无损/有损行为，便于对拍与回退）。</summary>
        LibWebp = 1,

        /// <summary>强制纯 C# 托管实现：无损走 VP8L，有损走 VP8（暂不支持 alpha）。</summary>
        CSharp = 2,
    }

    /// <summary>
    /// WebP 编码后端策略。
    ///
    /// 环境变量 <c>SIC_WEBP_ENCODE_BACKEND</c>：<c>auto</c>（默认）/ <c>libwebp</c>（别名 <c>native</c>）/ <c>csharp</c>。
    ///
    /// 托管编码器同时提供 VP8L 无损与 VP8 有损：<see cref="WebpEncodeBackend.CSharp"/> 下两者都走纯 C#，
    /// 有损的 alpha（ALPH 编码）尚未实现，遇到带 alpha 的请求抛 <see cref="WebpUnsupportedException"/>，
    /// 避免静默改用 libwebp 导致行为不可预期。
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
            "csharp" or "managed" or "native-csharp" => WebpEncodeBackend.CSharp,
            "libwebp" or "native" or "interop" or "off" => WebpEncodeBackend.LibWebp,
            _ => WebpEncodeBackend.Auto,
        };
    }

    /// <summary>按策略在托管 VP8L 编码器与 libwebp 之间分派。</summary>
    internal static class WebpEncodeDispatch
    {
        /// <summary>无损 RGBA 编码是否应交给托管实现。</summary>
        public static bool ShouldEncodeLosslessCSharp()
        {
            return WebpEncodePolicy.Backend switch
            {
                WebpEncodeBackend.CSharp => true,
                WebpEncodeBackend.LibWebp => false,
                _ => true,
            };
        }

        /// <summary>
        /// 有损 RGBA 编码是否应交给托管 VP8 编码器。
        /// 仅显式 <see cref="WebpEncodeBackend.CSharp"/> 为 true；Auto 与 LibWebp 保持 libwebp。
        /// </summary>
        public static bool ShouldEncodeLossyCSharp()
            => WebpEncodePolicy.Backend == WebpEncodeBackend.CSharp;

        /// <summary>按策略把 RGBA 无损编码成完整 WebP 字节，并透传元数据。</summary>
        public static byte[] EncodeLosslessRgba(ReadOnlySpan<byte> rgba, int width, int height,
            bool csharp, ImageMetadata? metadata = null)
        {
            if (csharp)
                return WebpEncoder.EncodeRgba(rgba, width, height, metadata);

            byte[] bytes = WebpCodec.EncodeLosslessRgbaForDispatch(rgba, width, height);
            return WebpMetadataWriter.Apply(bytes, width, height, metadata);
        }
    }
}
