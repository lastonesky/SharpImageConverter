namespace SharpImageConverter.Formats.Webp.Managed;

/// <summary>
/// WebP 纯 C# 解码实现抛出的异常基类。
/// </summary>
public class WebpException : Exception
{
    public WebpException() { }

    public WebpException(string message) : base(message) { }

    public WebpException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// RIFF/WEBP 容器或 chunk 解析异常。
/// </summary>
public class WebpContainerException : WebpException
{
    public WebpContainerException() { }

    public WebpContainerException(string message) : base(message) { }
}

/// <summary>
/// VP8/VP8L 位流解码异常（截断、损坏、超出规格）。
/// </summary>
public class WebpBitstreamException : WebpException
{
    public WebpBitstreamException() { }

    public WebpBitstreamException(string message) : base(message) { }
}

/// <summary>
/// 遇到当前实现不支持的 WebP 特性时抛出。
/// </summary>
public class WebpUnsupportedException : WebpException
{
    public WebpUnsupportedException() { }

    public WebpUnsupportedException(string message) : base(message) { }
}
