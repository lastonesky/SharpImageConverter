using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SharpImageConverter;
using SharpImageConverter.Core;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Gif;
using SharpImageConverter.Formats.Png;
using SharpImageConverter.Formats.Webp;
using SharpImageConverter.Formats.Bmp;
using SharpImageConverter.Metadata;
using SharpImageConverter.Tests.Helpers;
using Xunit;
using SharpImageConverter.Formats;

namespace SharpImageConverter.Tests
{
    /// <summary>跨格式往返 / 校验 / 模糊 / 语料 / CLI 的共享夹具。</summary>
    [Collection(JpegSimdStateCollection.Name)]
    public partial class FormatConversionTests
    {
        private static string NewTemp(string ext)
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        }
















        /// <summary>
        /// 变异输入只允许两种结局：正常解码，或抛 <see cref="InvalidDataException"/>（受控失败）。
        /// </summary>
        /// <remarks>
        /// 原先两个 fuzz 测试用 <c>catch { }</c> 吞掉所有异常，NullReferenceException /
        /// IndexOutOfRangeException 这类**解析器自身缺陷**会被当成"健壮"而静默通过 —— 恰恰是 fuzz
        /// 最该抓的东西。这里收窄到受控异常，其余一律失败，并报告是第几个变异体。
        /// </remarks>
        private static void AssertMutatedInputsFailOnlyWithInvalidData(byte[] original, int seed, Action<byte[]> decode)
        {
            var rng = new Random(seed);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++)
            {
                byte[] mutated = MutateBytes(original, rng, 3);
                try
                {
                    decode(mutated);
                }
                catch (InvalidDataException)
                {
                    // 受控失败：格式层面拒绝即可
                }
                catch (NotSupportedException)
                {
                    // 受控失败：变异体造出了合法但当前实现不支持的特性（例如未知 interlace 方法）
                }
                catch (Exception ex)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"变异体 #{i} 抛出非受控异常 {ex.GetType().Name}: {ex.Message}");
                }
            }

            // 防死循环（不是性能门槛）：40 个 64x64 变异体的正常耗时在百毫秒量级。
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"变异解码耗时 {sw.Elapsed}，疑似死循环");
        }





        /// <summary>
        /// 定位已构建的 CLI 程序集：测试输出目录在 <c>&lt;repo&gt;/SharpImageConverter.Tests/bin/&lt;cfg&gt;/net10.0/</c>。
        /// </summary>
        private static string LocateCliAssembly()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SharpImageConvert.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}")
                ? "Debug"
                : "Release";
            string dll = Path.Combine(dir!.FullName, "Cli", "bin", config, "net10.0", "SharpImageConverter.Cli.dll");
            Assert.True(File.Exists(dll), $"找不到 CLI 程序集: {dll}（csproj 的 ProjectReference 应保证它已构建）");
            return dll;
        }


        private static int IndexOfAscii(byte[] data, string needle)
        {
            if (data.Length == 0) return -1;
            if (string.IsNullOrEmpty(needle)) return 0;

            byte[] n = System.Text.Encoding.ASCII.GetBytes(needle);
            for (int i = 0; i <= data.Length - n.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < n.Length; j++)
                {
                    if (data[i + j] != n[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        private static byte[] CreateTinyAnimatedGif1x1()
        {
            return
            [
                0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
                0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
                0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,

                0x21, 0xFF, 0x0B,
                0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30,
                0x03, 0x01, 0x00, 0x00, 0x00,

                0x21, 0xF9, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00,
                0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0x02, 0x02, 0x44, 0x01, 0x00,

                0x21, 0xF9, 0x04, 0x00, 0x02, 0x00, 0x00, 0x00,
                0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                0x02, 0x02, 0x4C, 0x01, 0x00,

                0x3B
            ];
        }

        private static byte[] CreateFakeIccProfile(int size)
        {
            var data = new byte[size];
            uint x = 123456789u;
            for (int i = 0; i < data.Length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] = (byte)x;
            }
            return data;
        }

        private static byte[] MutateBytes(byte[] data, Random rng, int flips)
        {
            var buf = new byte[data.Length];
            Buffer.BlockCopy(data, 0, buf, 0, data.Length);
            for (int i = 0; i < flips; i++)
            {
                int idx = rng.Next(0, buf.Length);
                int bit = 1 << rng.Next(0, 8);
                buf[idx] = (byte)(buf[idx] ^ bit);
            }
            return buf;
        }

        private static byte[] Hash(byte[] data)
        {
            return SHA256.HashData(data);
        }

        private sealed class ChunkedReadOnlyStream : Stream
        {
            private readonly byte[] data;
            private readonly int maxChunk;
            private int position;

            public ChunkedReadOnlyStream(byte[] data, int maxChunk)
            {
                this.data = data;
                this.maxChunk = maxChunk > 0 ? maxChunk : 1;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (position >= data.Length) return 0;
                int remaining = data.Length - position;
                int take = Math.Min(count, Math.Min(maxChunk, remaining));
                Buffer.BlockCopy(data, position, buffer, offset, take);
                position += take;
                return take;
            }

            public override int Read(Span<byte> buffer)
            {
                if (position >= data.Length) return 0;
                int remaining = data.Length - position;
                int take = Math.Min(buffer.Length, Math.Min(maxChunk, remaining));
                data.AsSpan(position, take).CopyTo(buffer);
                position += take;
                return take;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
