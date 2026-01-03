using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats
{
    internal static partial class WebpCodec
    {
        // 使用 ReadOnlySpan 直接映射输入缓冲区，LibraryImport 会自动处理 pinning
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPGetInfo(ReadOnlySpan<byte> data, nuint data_size, out int width, out int height);

        // 使用 Span 映射输出缓冲区
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr WebPDecodeRGBAInto(ReadOnlySpan<byte> data, nuint data_size, Span<byte> output_buffer, int output_buffer_size, int output_stride);

        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial nuint WebPEncodeRGBA(ReadOnlySpan<byte> rgba, int width, int height, int stride, float quality_factor, out IntPtr output);

        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void WebPFree(IntPtr ptr);

        // --- 逻辑实现 ---

        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
        {
            // .NET 10 现代写法：不再需要 GCHandle
            if (WebPGetInfo(data, (nuint)data.Length, out width, out height) == 0)
                throw new InvalidOperationException("WebP 解析失败");

            var buffer = new byte[width * height * 4];
            
            // 直接传递 Span 给 P/Invoke，无需手动锁定内存
            IntPtr res = WebPDecodeRGBAInto(data, (nuint)data.Length, buffer, buffer.Length, width * 4);
            
            if (res == IntPtr.Zero) throw new InvalidOperationException("WebP 解码失败");
            return buffer;
        }

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, float quality)
        {
            // 编码时也直接使用 Span
            nuint size = WebPEncodeRGBA(rgba, width, height, width * 4, quality, out IntPtr output);
            
            int len = checked((int)size);
            if (len <= 0 || output == IntPtr.Zero) throw new InvalidOperationException("WebP 编码失败");

            try
            {
                // 使用更现代的 Span 拷贝方式，替代 Marshal.Copy
                unsafe
                {
                    var result = new byte[len];
                    // 将外部 C 指针转换为 ReadOnlySpan，然后直接拷贝给 byte[]
                    new ReadOnlySpan<byte>((void*)output, len).CopyTo(result);
                    return result;
                }
            }
            finally
            {
                WebPFree(output); // 确保释放 C 库分配的内存
            }
        }
    }
}
