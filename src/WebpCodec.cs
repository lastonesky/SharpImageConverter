using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PictureSharp.Formats
{
    internal static partial class WebpCodec
    {
        static WebpCodec()
        {
            //NativeLibrary.SetDllImportResolver(typeof(WebpCodec).Assembly, Resolve);
            //TryPreloadDependencies();
            //AddRuntimesToPath();
        }

        private static void TryPreloadDependencies()
        {
            string baseDir = AppContext.BaseDirectory;
            if (OperatingSystem.IsWindows())
            {
                string dir = Path.Combine(baseDir, "runtimes", "win-x64");
                TryLoad(Path.Combine(dir, "libsharpyuv.dll"));
                TryLoad(Path.Combine(dir, "libwebpdecoder.dll"));
                TryLoad(Path.Combine(dir, "libwebp.dll"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                string dir = Path.Combine(baseDir, "runtimes", "osx-arm64", "native");
                TryLoad(Path.Combine(dir, "libsharpyuv.0.1.2.dylib"));
                TryLoad(Path.Combine(dir, "libwebp.7.2.0.dylib"));
                TryLoad(Path.Combine(dir, "libwebpdecoder.3.2.0.dylib"));
            }
            else
            {
                string dir = Path.Combine(baseDir, "runtimes", "linux-x64");
                TryLoad(Path.Combine(dir, "libsharpyuv.so.0.1.2"));
                TryLoad(Path.Combine(dir, "libwebp.so.7.2.0"));
            }
        }

        private static void TryLoad(string path)
        {
            if (!File.Exists(path)) return;
            NativeLibrary.TryLoad(path, out _);
        }

        private static void AddRuntimesToPath()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string dir;
                string sep;
                if (OperatingSystem.IsWindows())
                {
                    dir = Path.Combine(baseDir, "runtimes", "win-x64");
                    sep = ";";
                }
                else if (OperatingSystem.IsMacOS())
                {
                    dir = Path.Combine(baseDir, "runtimes", "osx-arm64", "native");
                    sep = ":";
                }
                else
                {
                    dir = Path.Combine(baseDir, "runtimes", "linux-x64");
                    sep = ":";
                }

                if (Directory.Exists(dir))
                {
                    string? old = Environment.GetEnvironmentVariable("PATH");
                    if (old == null || !old.Contains(dir, StringComparison.OrdinalIgnoreCase))
                    {
                        Environment.SetEnvironmentVariable("PATH", dir + sep + (old ?? ""));
                    }
                }
            }
            catch { }
        }

        private static IntPtr Resolve(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
        {
            string baseDir = AppContext.BaseDirectory;
            if (OperatingSystem.IsWindows())
            {
                if (name == "libsharpyuv") return SafeLoad(Path.Combine(baseDir, "runtimes", "win-x64", "libsharpyuv.dll"));
                if (name == "libwebp") return SafeLoad(Path.Combine(baseDir, "runtimes", "win-x64", "libwebp.dll"));
                if (name == "libwebpdecoder") return SafeLoad(Path.Combine(baseDir, "runtimes", "win-x64", "libwebpdecoder.dll"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                string dir = Path.Combine(baseDir, "runtimes", "osx-arm64", "native");
                if (name == "libsharpyuv") return SafeLoad(Path.Combine(dir, "libsharpyuv.0.1.2.dylib"));
                if (name == "libwebp") return SafeLoad(Path.Combine(dir, "libwebp.7.2.0.dylib"));
                if (name == "libwebpdecoder") return SafeLoad(Path.Combine(dir, "libwebpdecoder.3.2.0.dylib"));
            }
            else
            {
                if (name == "libsharpyuv") return SafeLoad(Path.Combine(baseDir, "runtimes", "linux-x64", "libsharpyuv.so.0.1.2"));
                if (name == "libwebp" || name == "libwebpdecoder") return SafeLoad(Path.Combine(baseDir, "runtimes", "linux-x64", "libwebp.so.7.2.0"));
            }
            return IntPtr.Zero;
        }

        private static IntPtr SafeLoad(string path)
        {
            if (!File.Exists(path)) return IntPtr.Zero;
            return NativeLibrary.Load(path);
        }

        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial int WebPGetInfo(IntPtr data, UIntPtr data_size, out int width, out int height);

        [LibraryImport("libwebpdecoder")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial IntPtr WebPDecodeRGBAInto(IntPtr data, UIntPtr data_size, IntPtr output_buffer, int output_buffer_size, int output_stride);

        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial UIntPtr WebPEncodeRGBA(IntPtr rgba, int width, int height, int stride, float quality_factor, out IntPtr output);

        [LibraryImport("libwebp")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial void WebPFree(IntPtr ptr);

        public static byte[] DecodeRgba(byte[] data, out int width, out int height)
        {
            var hData = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                int w, h;
                int ok = WebPGetInfo(hData.AddrOfPinnedObject(), (UIntPtr)data.Length, out w, out h);
                if (ok == 0) throw new InvalidOperationException("WebP 解析失败");
                width = w;
                height = h;
                var buffer = new byte[w * h * 4];
                var hBuf = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    IntPtr res = WebPDecodeRGBAInto(hData.AddrOfPinnedObject(), (UIntPtr)data.Length, hBuf.AddrOfPinnedObject(), buffer.Length, w * 4);
                    if (res == IntPtr.Zero) throw new InvalidOperationException("WebP 解码失败");
                    return buffer;
                }
                finally
                {
                    hBuf.Free();
                }
            }
            finally
            {
                hData.Free();
            }
        }

        public static byte[] EncodeRgba(byte[] rgba, int width, int height, float quality)
        {
            var hRgba = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                UIntPtr size = WebPEncodeRGBA(hRgba.AddrOfPinnedObject(), width, height, width * 4, quality, out IntPtr output);
                int len = checked((int)size);
                if (len <= 0 || output == IntPtr.Zero) throw new InvalidOperationException("WebP 编码失败");
                var result = new byte[len];
                Marshal.Copy(output, result, 0, len);
                WebPFree(output);
                return result;
            }
            finally
            {
                hRgba.Free();
            }
        }
    }
}
