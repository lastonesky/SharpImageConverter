using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SharpImageConverter.Tests.Helpers;

/// <summary>
/// libwebpdemux 的 WebPAnimDecoder 封装（测试期黑盒对拍用）：
/// 输出画布尺寸、循环次数、每帧时间戳与逐帧合成后的完整画布。
/// </summary>
internal static class LibWebpAnimationDecoder
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WebPDataNative
    {
        public IntPtr Bytes;
        public nuint Size;
    }

    [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderNewInternal", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NewInternal(ref WebPDataNative data, IntPtr options, int abiVersion);

    [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderGetInfo", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetInfo(IntPtr dec, IntPtr info);

    [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderHasMoreFrames", CallingConvention = CallingConvention.Cdecl)]
    private static extern int HasMoreFrames(IntPtr dec);

    [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderGetNext", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetNext(IntPtr dec, out IntPtr buffer, out int timestamp);

    [DllImport("libwebpdemux", EntryPoint = "WebPAnimDecoderDelete", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Delete(IntPtr dec);

    /// <summary>
    /// 解码动图。成功返回 true；timestamps 为 libwebp 给出的累计时间戳（毫秒），
    /// 帧 i 的时长 = timestamps[i] - timestamps[i-1]。
    /// </summary>
    public static bool TryDecode(byte[] webp, out LibWebpAnimation result)
    {
        if (webp.Length == 0)
        {
            result = default!;
            return false;
        }

        var pin = GCHandle.Alloc(webp, GCHandleType.Pinned);
        IntPtr info = Marshal.AllocHGlobal(64);
        IntPtr dec = IntPtr.Zero;
        try
        {
            var data = new WebPDataNative { Bytes = pin.AddrOfPinnedObject(), Size = (nuint)webp.Length };
            for (int abi = 0x0107; abi >= 0x0100 && dec == IntPtr.Zero; abi--)
                dec = NewInternal(ref data, IntPtr.Zero, abi);
            if (dec == IntPtr.Zero)
            {
                result = default!;
                return false;
            }

            Marshal.Copy(new byte[64], 0, info, 64);
            if (GetInfo(dec, info) == 0)
            {
                result = default!;
                return false;
            }

            // WebPAnimInfo: canvas_width(0), canvas_height(4), loop_count(8), bgcolor(12), frame_count(16)
            int width = Marshal.ReadInt32(info, 0);
            int height = Marshal.ReadInt32(info, 4);
            int loopCount = Marshal.ReadInt32(info, 8);
            int frameCount = Marshal.ReadInt32(info, 16);

            var frames = new List<byte[]>(frameCount);
            var timestamps = new List<int>(frameCount);
            while (HasMoreFrames(dec) != 0)
            {
                if (GetNext(dec, out IntPtr buffer, out int timestamp) == 0)
                {
                    result = default!;
                    return false;
                }
                var canvas = new byte[width * height * 4];
                Marshal.Copy(buffer, canvas, 0, canvas.Length);
                frames.Add(canvas);
                timestamps.Add(timestamp);
            }

            result = new LibWebpAnimation(width, height, loopCount, frames.ToArray(), timestamps.ToArray());
            return frameCount > 0 && frames.Count == frameCount;
        }
        finally
        {
            if (dec != IntPtr.Zero) Delete(dec);
            Marshal.FreeHGlobal(info);
            pin.Free();
        }
    }
}

internal sealed record LibWebpAnimation(
    int Width, int Height, int LoopCount, byte[][] Frames, int[] Timestamps);
