using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats
{
    internal static partial class WebpCodec
    {
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPGetInfo(ReadOnlySpan<byte> data, nuint data_size, out int width, out int height);

        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr WebPDecodeRGBAInto(ReadOnlySpan<byte> data, nuint data_size, Span<byte> output_buffer, int output_buffer_size, int output_stride);

        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial nuint WebPEncodeRGBA(ReadOnlySpan<byte> rgba, int width, int height, int stride, float quality_factor, out IntPtr output);

        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void WebPFree(IntPtr ptr);

        [LibraryImport("libwebpmux", EntryPoint = "WebPNewInternal")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr WebPNewInternal(int version);

        [LibraryImport("libwebpmux")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void WebPMuxDelete(IntPtr mux);

        [LibraryImport("libwebpmux")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial WebPMuxError WebPMuxSetCanvasSize(IntPtr mux, int width, int height);

        [LibraryImport("libwebpmux")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial WebPMuxError WebPMuxSetAnimationParams(IntPtr mux, ref WebPMuxAnimParams anim_params);

        [LibraryImport("libwebpmux")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial WebPMuxError WebPMuxPushFrame(IntPtr mux, ref WebPMuxFrameInfo frame, int copy_data);

        [LibraryImport("libwebpmux")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial WebPMuxError WebPMuxAssemble(IntPtr mux, ref WebPData assembled_data);

        private static readonly int[] MuxAbiVersionsToTry = [0x0109, 0x0108, 0x0107, 0x0106, 0x0105, 0x0104, 0x0103, 0x0102, 0x0101, 0x0100];

        private static IntPtr CreateEmptyMux()
        {
            for (int i = 0; i < MuxAbiVersionsToTry.Length; i++)
            {
                IntPtr mux = WebPNewInternal(MuxAbiVersionsToTry[i]);
                if (mux != IntPtr.Zero) return mux;
            }

            throw new InvalidOperationException("WebPNewInternal 创建失败（ABI 版本不匹配或库不兼容）");
        }

        public static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height)
        {
            try
            {
                if (WebPGetInfo(data, (nuint)data.Length, out width, out height) == 0)
                    throw new InvalidOperationException("WebP 解析失败");

                int bufferLength = checked(width * height * 4);
                var buffer = new byte[bufferLength];
                IntPtr res = WebPDecodeRGBAInto(data, (nuint)data.Length, buffer, buffer.Length, width * 4);
                if (res == IntPtr.Zero) throw new InvalidOperationException("WebP 解码失败");
                return buffer;
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("未能加载 WebP 原生库，请检查 runtimes 目录或平台匹配。", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException("WebP 原生库版本不兼容，缺少所需入口点。", ex);
            }
        }

        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
        {
            return DecodeRgba(data.AsSpan(), out width, out height);
        }

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, float quality)
        {
            try
            {
                nuint size = WebPEncodeRGBA(rgba, width, height, width * 4, quality, out IntPtr output);

                int len = checked((int)size);
                if (len <= 0 || output == IntPtr.Zero) throw new InvalidOperationException("WebP 编码失败");

                try
                {
                    unsafe
                    {
                        var result = new byte[len];
                        new ReadOnlySpan<byte>((void*)output, len).CopyTo(result);
                        return result;
                    }
                }
                finally
                {
                    WebPFree(output);
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("未能加载 WebP 原生库，请检查 runtimes 目录或平台匹配。", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException("WebP 原生库版本不兼容，缺少所需入口点。", ex);
            }
        }

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, WebpEncoderOptions options)
        {
            return EncodeRgba(rgba, width, height, options.Quality);
        }

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, float quality)
        {
            ArgumentNullException.ThrowIfNull(rgbaFrames, nameof(rgbaFrames));
            ArgumentNullException.ThrowIfNull(frameDurationsMs, nameof(frameDurationsMs));
            if (rgbaFrames.Length == 0) throw new ArgumentOutOfRangeException(nameof(rgbaFrames));
            if (rgbaFrames.Length != frameDurationsMs.Length) throw new ArgumentException("帧与时长数量不一致");
            if (loopCount < 0) loopCount = 0;

            try
            {
                if (rgbaFrames.Length == 1)
                {
                    return EncodeRgba(rgbaFrames[0], width, height, quality);
                }

                IntPtr mux = CreateEmptyMux();
                if (mux == IntPtr.Zero) throw new InvalidOperationException("WebPMux 创建失败");

                try
                {
                    var err = WebPMuxSetCanvasSize(mux, width, height);
                    if (err != WebPMuxError.WEBP_MUX_OK) throw new InvalidOperationException($"WebPMuxSetCanvasSize 失败: {err}");

                    var animParams = new WebPMuxAnimParams
                    {
                        bgcolor = 0u,
                        loop_count = loopCount
                    };
                    err = WebPMuxSetAnimationParams(mux, ref animParams);
                    if (err != WebPMuxError.WEBP_MUX_OK) throw new InvalidOperationException($"WebPMuxSetAnimationParams 失败: {err}");

                    for (int i = 0; i < rgbaFrames.Length; i++)
                    {
                        var rgba = rgbaFrames[i];
                        if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA 帧尺寸不一致");

                        int duration = frameDurationsMs[i];
                        if (duration < 10) duration = 10;

                        byte[] encoded = EncodeRgba(rgba, width, height, quality);

                        unsafe
                        {
                            fixed (byte* p = encoded)
                            {
                                var frame = new WebPMuxFrameInfo
                                {
                                    bitstream = new WebPData { bytes = (IntPtr)p, size = (nuint)encoded.Length },
                                    x_offset = 0,
                                    y_offset = 0,
                                    duration = duration,
                                    id = WebPChunkId.WEBP_CHUNK_ANMF,
                                    dispose_method = WebPMuxAnimDispose.WEBP_MUX_DISPOSE_BACKGROUND,
                                    blend_method = WebPMuxAnimBlend.WEBP_MUX_NO_BLEND,
                                    pad = 0u
                                };

                                err = WebPMuxPushFrame(mux, ref frame, copy_data: 1);
                                if (err != WebPMuxError.WEBP_MUX_OK) throw new InvalidOperationException($"WebPMuxPushFrame 失败: {err}");
                            }
                        }
                    }

                    var assembled = new WebPData { bytes = IntPtr.Zero, size = 0 };
                    err = WebPMuxAssemble(mux, ref assembled);
                    if (err != WebPMuxError.WEBP_MUX_OK) throw new InvalidOperationException($"WebPMuxAssemble 失败: {err}");
                    if (assembled.bytes == IntPtr.Zero || assembled.size == 0) throw new InvalidOperationException("WebPMuxAssemble 返回空数据");

                    try
                    {
                        int len = checked((int)assembled.size);
                        unsafe
                        {
                            var result = new byte[len];
                            new ReadOnlySpan<byte>((void*)assembled.bytes, len).CopyTo(result);
                            return result;
                        }
                    }
                    finally
                    {
                        if (assembled.bytes != IntPtr.Zero)
                        {
                            WebPFree(assembled.bytes);
                            assembled.bytes = IntPtr.Zero;
                            assembled.size = 0;
                        }
                    }
                }
                finally
                {
                    WebPMuxDelete(mux);
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException("未能加载 WebP 原生库，请检查 runtimes 目录或平台匹配。", ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new InvalidOperationException("WebP 原生库版本不兼容，缺少所需入口点。", ex);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WebPData
    {
        public IntPtr bytes;
        public nuint size;
    }

    internal enum WebPMuxError : int
    {
        WEBP_MUX_NOT_FOUND = 0,
        WEBP_MUX_OK = 1,
        WEBP_MUX_INVALID_ARGUMENT = -1,
        WEBP_MUX_BAD_DATA = -2,
        WEBP_MUX_MEMORY_ERROR = -3,
        WEBP_MUX_NOT_ENOUGH_DATA = -4
    }

    internal enum WebPChunkId : int
    {
        WEBP_CHUNK_VP8X = 0,
        WEBP_CHUNK_ICCP = 1,
        WEBP_CHUNK_ANIM = 2,
        WEBP_CHUNK_ANMF = 3,
        WEBP_CHUNK_DEPRECATED = 4,
        WEBP_CHUNK_ALPHA = 5,
        WEBP_CHUNK_IMAGE = 6,
        WEBP_CHUNK_EXIF = 7,
        WEBP_CHUNK_XMP = 8,
        WEBP_CHUNK_UNKNOWN = 9
    }

    internal enum WebPMuxAnimDispose : int
    {
        WEBP_MUX_DISPOSE_NONE = 0,
        WEBP_MUX_DISPOSE_BACKGROUND = 1
    }

    internal enum WebPMuxAnimBlend : int
    {
        WEBP_MUX_BLEND = 0,
        WEBP_MUX_NO_BLEND = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WebPMuxAnimParams
    {
        public uint bgcolor;
        public int loop_count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WebPMuxFrameInfo
    {
        public WebPData bitstream;
        public int x_offset;
        public int y_offset;
        public int duration;
        public WebPChunkId id;
        public WebPMuxAnimDispose dispose_method;
        public WebPMuxAnimBlend blend_method;
        public uint pad;
    }
}
