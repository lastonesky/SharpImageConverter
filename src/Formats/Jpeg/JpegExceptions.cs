using System;
using System.IO;

namespace SharpImageConverter;

public sealed class JpegHeaderException : IOException
{
    public JpegHeaderException(string message) : base(message) { }
    public JpegHeaderException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class JpegScanException : IOException
{
    public JpegScanException(string message) : base(message) { }
    public JpegScanException(string message, Exception innerException) : base(message, innerException) { }
}
