using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace SharpImageConverter.Formats.Webp
{
    internal static unsafe partial class WebpCodec
    {
        private static readonly object EncodeLock = new object();
        private sealed class WebPWriterState
        {
            public WebPWriterState(Stream stream)
            {
                Stream = stream;
            }

            public Stream Stream { get; }
            public Exception? Error { get; set; }
        }
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPGetInfo(ReadOnlySpan<byte> data, nuint data_size, out int width, out int height);
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr WebPDecodeRGBAInto(ReadOnlySpan<byte> data, nuint data_size, Span<byte> output_buffer, int output_buffer_size, int output_stride);
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr WebPINewDecoder(IntPtr output_buffer);
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void WebPIDelete(IntPtr idec);
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial VP8StatusCode WebPIAppend(IntPtr idec, ReadOnlySpan<byte> data, nuint data_size);
        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial IntPtr WebPIDecGetRGB(IntPtr idec, out int last_y, out int width, out int height, out int stride);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial nuint WebPEncodeRGBA(ReadOnlySpan<byte> rgba, int width, int height, int stride, float quality_factor, out IntPtr output);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial nuint WebPEncodeRGB(ReadOnlySpan<byte> rgb, int width, int height, int stride, float quality_factor, out IntPtr output);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void WebPFree(IntPtr ptr);
        [LibraryImport("libwebp", EntryPoint = "WebPConfigInitInternal")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPConfigInitInternal(ref WebPConfig config, WebPPreset preset, float quality, int version);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPValidateConfig(ref WebPConfig config);
        [LibraryImport("libwebp", EntryPoint = "WebPPictureInitInternal")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPPictureInitInternal(ref WebPPicture picture, int version);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial void WebPPictureFree(ref WebPPicture picture);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPPictureImportRGB(ref WebPPicture picture, ReadOnlySpan<byte> rgb, int rgb_stride);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPPictureImportRGBA(ref WebPPicture picture, ReadOnlySpan<byte> rgba, int rgba_stride);
        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        private static partial int WebPEncode(ref WebPConfig config, ref WebPPicture picture);
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
        private static readonly int[] EncoderAbiVersionsToTry = [0x020F, 0x020E, 0x020D, 0x020C, 0x020B, 0x020A, 0x0209, 0x0208, 0x0207, 0x0206, 0x0205, 0x0204, 0x0203, 0x0202, 0x0201, 0x0200];
        private static readonly WebPWriterFunction WriterCallback = WriteToManagedStream;
        private static readonly IntPtr WriterCallbackPtr = Marshal.GetFunctionPointerForDelegate(WriterCallback);

        private static IntPtr CreateEmptyMux()
        {
            for (int i = 0; i < MuxAbiVersionsToTry.Length; i++)
            {
                IntPtr mux = WebPNewInternal(MuxAbiVersionsToTry[i]);
                if (mux != IntPtr.Zero) return mux;
            }

            throw new InvalidOperationException("WebPNewInternal 创建失败（ABI 版本不匹配或库不兼容）");
        }

        private static void InitConfig(ref WebPConfig config, float quality)
        {
            for (int i = 0; i < EncoderAbiVersionsToTry.Length; i++)
            {
                if (WebPConfigInitInternal(ref config, WebPPreset.WEBP_PRESET_DEFAULT, quality, EncoderAbiVersionsToTry[i]) != 0)
                {
                    config.quality = quality;
                    config.method = 4;
                    if (WebPValidateConfig(ref config) == 0) throw new InvalidOperationException("WebP 编码参数无效");
                    return;
                }
            }
            throw new InvalidOperationException("WebPConfigInitInternal 创建失败（ABI 版本不匹配或库不兼容）");
        }

        private static void InitPicture(ref WebPPicture picture)
        {
            for (int i = 0; i < EncoderAbiVersionsToTry.Length; i++)
            {
                if (WebPPictureInitInternal(ref picture, EncoderAbiVersionsToTry[i]) != 0) return;
            }
            throw new InvalidOperationException("WebPPictureInitInternal 创建失败（ABI 版本不匹配或库不兼容）");
        }

        private static byte[] EncodeWithConfig(ReadOnlySpan<byte> pixels, int width, int height, int stride, bool hasAlpha, float quality)
        {
            using var ms = new MemoryStream();
            EncodeWithConfigToStream(ms, pixels, width, height, stride, hasAlpha, quality);
            return ms.ToArray();
        }

        private static void EncodeWithConfigToStream(Stream stream, ReadOnlySpan<byte> pixels, int width, int height, int stride, bool hasAlpha, float quality)
        {
            WebPConfig config = default;
            WebPPicture picture = default;
            GCHandle handle = default;
            WebPWriterState? state = null;
            try
            {
                InitConfig(ref config, quality);
                InitPicture(ref picture);
                picture.width = width;
                picture.height = height;
                picture.writer = WriterCallbackPtr;
                state = new WebPWriterState(stream);
                handle = GCHandle.Alloc(state);
                picture.custom_ptr = GCHandle.ToIntPtr(handle);
                int ok = hasAlpha
                    ? WebPPictureImportRGBA(ref picture, pixels, stride)
                    : WebPPictureImportRGB(ref picture, pixels, stride);
                if (ok == 0) throw new InvalidOperationException("WebP 图像导入失败");
                if (WebPEncode(ref config, ref picture) == 0) throw new InvalidOperationException($"WebP 编码失败: {(WebPEncodingError)picture.error_code}");
                if (state.Error != null) throw new InvalidOperationException($"WebP 写入失败: {state.Error.GetType().Name}: {state.Error.Message}", state.Error);
            }
            finally
            {
                WebPPictureFree(ref picture);
                if (handle.IsAllocated) handle.Free();
            }
        }

        private static void ExecuteWithConcurrency(WebpConcurrencyStrategy strategy, Action action)
        {
            if (strategy == WebpConcurrencyStrategy.Parallel)
            {
                action();
                return;
            }
            lock (EncodeLock)
            {
                action();
            }
        }

        private unsafe static int WriteToManagedStream(byte* data, nuint data_size, IntPtr picture)
        {
            try
            {
                if (data_size == 0) return 1;
                var pic = (WebPPicture*)picture;
                var handle = GCHandle.FromIntPtr(pic->custom_ptr);
                var state = (WebPWriterState)handle.Target!;
                var stream = state.Stream;
                nuint remaining = data_size;
                byte* p = data;
                while (remaining > 0)
                {
                    int chunk = remaining > int.MaxValue ? int.MaxValue : (int)remaining;
                    stream.Write(new ReadOnlySpan<byte>(p, chunk));
                    p += chunk;
                    remaining -= (nuint)chunk;
                }
                return 1;
            }
            catch (Exception ex)
            {
                try
                {
                    var pic = (WebPPicture*)picture;
                    var handle = GCHandle.FromIntPtr(pic->custom_ptr);
                    if (handle.Target is WebPWriterState state) state.Error = ex;
                }
                catch
                {
                }
                return 0;
            }
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

        public static byte[] DecodeRgbaFromStream(Stream stream, out int width, out int height)
        {
            ArgumentNullException.ThrowIfNull(stream);

            try
            {
                if (stream.CanSeek)
                {
                    long remainingLong = stream.Length - stream.Position;
                    if (remainingLong <= 0) throw new InvalidDataException("WebP 流数据为空");
                    if (remainingLong > int.MaxValue) throw new InvalidDataException("WebP 流数据过大");
                    int remaining = (int)remainingLong;
                    byte[] data = new byte[remaining];
                    int offset = 0;
                    while (offset < remaining)
                    {
                        int n = stream.Read(data, offset, remaining - offset);
                        if (n == 0) throw new EndOfStreamException("WebP 流数据不完整");
                        offset += n;
                    }
                    return DecodeRgba(data, out width, out height);
                }

                var poolBuf = ArrayPool<byte>.Shared.Rent(32 * 1024);
                try
                {
                    using var ms = new SharpImageConverter.Formats.Png.PooledMemoryStream(64 * 1024);
                    int n;
                    while ((n = stream.Read(poolBuf, 0, poolBuf.Length)) > 0)
                    {
                        ms.Write(poolBuf, 0, n);
                    }
                    var seg = ms.GetBuffer();
                    if (seg.Count <= 0) throw new InvalidDataException("WebP 流数据为空");
                    byte[] data = new byte[seg.Count];
                    Buffer.BlockCopy(seg.Array!, seg.Offset, data, 0, seg.Count);
                    return DecodeRgba(data, out width, out height);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(poolBuf);
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

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, float quality)
        {
            try
            {
                byte[]? result = null;
                ExecuteWithConcurrency(WebpConcurrencyStrategy.Auto, () =>
                {
                    result = EncodeWithConfig(rgba, width, height, width * 4, true, quality);
                });
                return result!;
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

        public static byte[] EncodeRgb(byte[] rgb, int width, int height, float quality)
        {
            try
            {
                byte[]? result = null;
                ExecuteWithConcurrency(WebpConcurrencyStrategy.Auto, () =>
                {
                    result = EncodeWithConfig(rgb, width, height, width * 3, false, quality);
                });
                return result!;
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
            try
            {
                byte[]? result = null;
                ExecuteWithConcurrency(options.ConcurrencyStrategy, () =>
                {
                    result = EncodeWithConfig(rgba, width, height, width * 4, true, options.Quality);
                });
                return result!;
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

        public static byte[] EncodeRgb(byte[] rgb, int width, int height, WebpEncoderOptions options)
        {
            try
            {
                byte[]? result = null;
                ExecuteWithConcurrency(options.ConcurrencyStrategy, () =>
                {
                    result = EncodeWithConfig(rgb, width, height, width * 3, false, options.Quality);
                });
                return result!;
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

        public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height, float quality)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA 像素长度不匹配", nameof(rgba));
            try
            {
                lock (EncodeLock)
                {
                    EncodeWithConfigToStream(stream, rgba, width, height, width * 4, true, quality);
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

        public static void EncodeRgbToStream(Stream stream, ReadOnlySpan<byte> rgb, int width, int height, float quality)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rgb.Length != checked(width * height * 3)) throw new ArgumentException("RGB 像素长度不匹配", nameof(rgb));
            try
            {
                lock (EncodeLock)
                {
                    EncodeWithConfigToStream(stream, rgb, width, height, width * 3, false, quality);
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

        public static void EncodeRgbaToStream(Stream stream, ReadOnlySpan<byte> rgba, int width, int height, WebpEncoderOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("RGBA 像素长度不匹配", nameof(rgba));
            try
            {
                if (options.ConcurrencyStrategy == WebpConcurrencyStrategy.Parallel)
                {
                    EncodeWithConfigToStream(stream, rgba, width, height, width * 4, true, options.Quality);
                }
                else
                {
                    lock (EncodeLock)
                    {
                        EncodeWithConfigToStream(stream, rgba, width, height, width * 4, true, options.Quality);
                    }
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

        public static void EncodeRgbToStream(Stream stream, ReadOnlySpan<byte> rgb, int width, int height, WebpEncoderOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (rgb.Length != checked(width * height * 3)) throw new ArgumentException("RGB 像素长度不匹配", nameof(rgb));
            try
            {
                if (options.ConcurrencyStrategy == WebpConcurrencyStrategy.Parallel)
                {
                    EncodeWithConfigToStream(stream, rgb, width, height, width * 3, false, options.Quality);
                }
                else
                {
                    lock (EncodeLock)
                    {
                        EncodeWithConfigToStream(stream, rgb, width, height, width * 3, false, options.Quality);
                    }
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

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, float quality)
        {
            return EncodeAnimatedRgba(rgbaFrames, width, height, frameDurationsMs, loopCount, new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto));
        }

        public static byte[] EncodeAnimatedRgba(byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, WebpEncoderOptions options)
        {
            ArgumentNullException.ThrowIfNull(rgbaFrames, nameof(rgbaFrames));
            ArgumentNullException.ThrowIfNull(frameDurationsMs, nameof(frameDurationsMs));
            if (rgbaFrames.Length == 0) throw new ArgumentOutOfRangeException(nameof(rgbaFrames));
            if (rgbaFrames.Length != frameDurationsMs.Length) throw new ArgumentException("帧与时长数量不一致");
            if (loopCount < 0) loopCount = 0;

            try
            {
                byte[]? result = null;
                ExecuteWithConcurrency(options.ConcurrencyStrategy, () =>
                {
                    if (rgbaFrames.Length == 1)
                    {
                        result = EncodeRgba(rgbaFrames[0], width, height, options);
                        return;
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

                            byte[] encoded = EncodeRgba(rgba, width, height, options);

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
                                var bytes = new byte[len];
                                new ReadOnlySpan<byte>((void*)assembled.bytes, len).CopyTo(bytes);
                                result = bytes;
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
                });
                return result!;
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

        public static void EncodeAnimatedRgbaToStream(Stream stream, byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, float quality)
        {
            EncodeAnimatedRgbaToStream(stream, rgbaFrames, width, height, frameDurationsMs, loopCount, new WebpEncoderOptions(quality, WebpConcurrencyStrategy.Auto));
        }

        public static void EncodeAnimatedRgbaToStream(Stream stream, byte[][] rgbaFrames, int width, int height, int[] frameDurationsMs, int loopCount, WebpEncoderOptions options)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(rgbaFrames, nameof(rgbaFrames));
            ArgumentNullException.ThrowIfNull(frameDurationsMs, nameof(frameDurationsMs));
            if (rgbaFrames.Length == 0) throw new ArgumentOutOfRangeException(nameof(rgbaFrames));
            if (rgbaFrames.Length != frameDurationsMs.Length) throw new ArgumentException("帧与时长数量不一致");
            if (loopCount < 0) loopCount = 0;

            try
            {
                ExecuteWithConcurrency(options.ConcurrencyStrategy, () =>
                {
                    if (rgbaFrames.Length == 1)
                    {
                        EncodeRgbaToStream(stream, rgbaFrames[0], width, height, options);
                        return;
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

                            byte[] encoded = EncodeRgba(rgba, width, height, options);

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
                                stream.Write(new ReadOnlySpan<byte>((void*)assembled.bytes, len));
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
                });
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

    internal enum VP8StatusCode : int
    {
        VP8_STATUS_OK = 0,
        VP8_STATUS_OUT_OF_MEMORY = 1,
        VP8_STATUS_INVALID_PARAM = 2,
        VP8_STATUS_BITSTREAM_ERROR = 3,
        VP8_STATUS_UNSUPPORTED_FEATURE = 4,
        VP8_STATUS_SUSPENDED = 5,
        VP8_STATUS_USER_ABORT = 6,
        VP8_STATUS_NOT_ENOUGH_DATA = 7
    }

    internal enum WebPPreset : int
    {
        WEBP_PRESET_DEFAULT = 0,
        WEBP_PRESET_PICTURE = 1,
        WEBP_PRESET_PHOTO = 2,
        WEBP_PRESET_DRAWING = 3,
        WEBP_PRESET_ICON = 4,
        WEBP_PRESET_TEXT = 5
    }

    internal enum WebPEncCSP : int
    {
        WEBP_YUV420 = 0,
        WEBP_YUV420A = 4,
        WEBP_CSP_UV_MASK = 3,
        WEBP_CSP_ALPHA_BIT = 4
    }

    internal enum WebPEncodingError : int
    {
        VP8_ENC_OK = 0,
        VP8_ENC_ERROR_OUT_OF_MEMORY = 1,
        VP8_ENC_ERROR_BITSTREAM_OUT_OF_MEMORY = 2,
        VP8_ENC_ERROR_NULL_PARAMETER = 3,
        VP8_ENC_ERROR_INVALID_CONFIGURATION = 4,
        VP8_ENC_ERROR_BAD_DIMENSION = 5,
        VP8_ENC_ERROR_PARTITION0_OVERFLOW = 6,
        VP8_ENC_ERROR_PARTITION_OVERFLOW = 7,
        VP8_ENC_ERROR_BAD_WRITE = 8,
        VP8_ENC_ERROR_FILE_TOO_BIG = 9,
        VP8_ENC_ERROR_USER_ABORT = 10,
        VP8_ENC_ERROR_LAST = 11
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

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct WebPConfig
    {
        public int lossless;
        public float quality;
        public int method;
        public int image_hint;
        public int target_size;
        public float target_PSNR;
        public int segments;
        public int sns_strength;
        public int filter_strength;
        public int filter_sharpness;
        public int filter_type;
        public int autofilter;
        public int alpha_compression;
        public int alpha_filtering;
        public int alpha_quality;
        public int pass;
        public int show_compressed;
        public int preprocessing;
        public int partitions;
        public int partition_limit;
        public int emulate_jpeg_size;
        public int thread_level;
        public int low_memory;
        public int near_lossless;
        public int exact;
        public int use_delta_palette;
        public int use_sharp_yuv;
        public int qmin;
        public int qmax;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal unsafe struct WebPPicture
    {
        public int use_argb;
        public WebPEncCSP colorspace;
        public int width;
        public int height;
        public byte* y;
        public byte* u;
        public byte* v;
        public int y_stride;
        public int uv_stride;
        public byte* a;
        public int a_stride;
        public fixed uint pad1[2];
        public uint* argb;
        public int argb_stride;
        public fixed uint pad2[3];
        public IntPtr writer;
        public IntPtr custom_ptr;
        public int extra_info_type;
        public byte* extra_info;
        public IntPtr stats;
        public WebPEncodingError error_code;
        public IntPtr progress_hook;
        public IntPtr user_data;
        public fixed uint pad3[3];
        public byte* pad4;
        public byte* pad5;
        public fixed uint pad6[8];
        public IntPtr memory_;
        public IntPtr memory_argb_;
        public IntPtr pad7_0;
        public IntPtr pad7_1;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate int WebPWriterFunction(byte* data, nuint data_size, IntPtr picture);
}
