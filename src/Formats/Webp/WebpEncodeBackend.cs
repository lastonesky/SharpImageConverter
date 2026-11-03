using System;

namespace SharpImageConverter.Formats.Webp
{
    /// <summary>WebP 编码实现选择。</summary>
    public enum WebpEncodeBackend
    {
        /// <summary>默认：纯 C# 实现（无损 VP8L，有损 VP8 + ALPH）。</summary>
        Auto = 0,

        /// <summary>强制纯 C# 实现；与 <see cref="Auto"/> 行为一致，保留只为兼容既有调用方。</summary>
        CSharp = 1,
    }

    /// <summary>
    /// WebP 编码后端策略。
    ///
    /// 环境变量 <c>SIC_WEBP_ENCODE_BACKEND</c>：<c>auto</c>（默认）/ <c>csharp</c>。
    /// 纯 C# 是唯一实现，两个取值行为一致。
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
            _ => WebpEncodeBackend.Auto,
        };
    }
}
