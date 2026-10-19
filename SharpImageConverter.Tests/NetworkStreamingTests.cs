using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpImageConverter;
using SharpImageConverter.Formats.Jpeg;
using SharpImageConverter.Formats.Webp;
using StreamNet;
using Tests.Helpers;
using Xunit;

namespace Tests
{
    /// <summary>
    /// 网络传输流式调用的回归测试：分片、截断、连接重置、取消、写出侧断链。
    /// 仿真流实现与 <c>tools/streamnet</c> 共用同一份源码（见 csproj 的 Compile Include）。
    /// </summary>
    public class NetworkStreamingTests
    {
        private static string ExamplePath(string name)
            => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", name);

        private static byte[] ReferenceRgb(byte[] jpeg) => JpegDecoder.Decode(jpeg).ToRgb24();

        [Theory]
        [InlineData("video-001.rgb.jpeg", 3)]
        [InlineData("Amish-Noka-Dresser.jpg", 1)]
        [InlineData("Amish-Noka-Dresser.jpg", 17)]
        [InlineData("5_star_base.jpg", 4096)]
        public void Streaming_Decode_ChunkedDelivery_MatchesMemoryDecode(string file, int chunk)
        {
            byte[] data = File.ReadAllBytes(ExamplePath(file));
            byte[] reference = ReferenceRgb(data);

            using var stream = new SimulatedNetworkStream(data, new NetworkProfile { ChunkBytes = chunk });
            var result = JpegDecoder.DecodeFromStreamAsync(stream, default, false, requireEoi: true)
                .GetAwaiter().GetResult();

            Assert.Equal(reference.AsSpan().ToArray(), result.Image.ToRgb24());
        }

        [Theory]
        [InlineData("video-001.rgb.jpeg")]
        [InlineData("Amish-Noka-Dresser.jpg")]
        [InlineData("5_star_base.jpg")]
        public void Streaming_Decode_CompleteFile_PassesStrictMode(string file)
        {
            byte[] data = File.ReadAllBytes(ExamplePath(file));
            byte[] reference = ReferenceRgb(data);

            using var stream = new SimulatedNetworkStream(data, new NetworkProfile { ChunkBytes = 4096 });
            var result = JpegDecoder.DecodeFromStreamAsync(stream, default, false, requireEoi: true)
                .GetAwaiter().GetResult();

            Assert.Equal(reference.AsSpan().ToArray(), result.Image.ToRgb24());
        }

        [Fact]
        public void Streaming_Decode_TruncatedAtScanBoundary_StrictModeThrows()
        {
            byte[] data = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
            int boundary = FirstScanBoundaryAfterFirstSos(data);
            Assert.InRange(boundary, 1, data.Length - 1);

            byte[] truncated = data[..boundary];
            using var stream = new SimulatedNetworkStream(truncated, EofProfile(truncated.Length));

            Assert.Throws<InvalidDataException>(() =>
                JpegDecoder.DecodeFromStreamAsync(stream, default, false, requireEoi: true)
                    .GetAwaiter().GetResult());
        }

        [Fact]
        public void Streaming_Decode_TruncatedAtScanBoundary_LenientModeReturnsCorruptImage()
        {
            // 记录现状：宽松模式把「只解出前几个扫描」当成完整图像返回。
            // 这是 requireEoi 存在的理由，改动宽松语义前请先看 docs/NetworkStreaming.md。
            byte[] data = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
            byte[] reference = ReferenceRgb(data);
            int boundary = FirstScanBoundaryAfterFirstSos(data);

            byte[] truncated = data[..boundary];
            using var stream = new SimulatedNetworkStream(truncated, EofProfile(truncated.Length));
            byte[] rgb = JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult().Image.ToRgb24();

            Assert.Equal(reference.Length, rgb.Length);
            int diff = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                if (reference[i] != rgb[i]) diff++;
            }
            Assert.True(diff > reference.Length / 2, $"期望严重失真，实际差异 {diff}/{reference.Length}");
        }

        [Fact]
        public void Streaming_Decode_MissingEoi_StrictModeThrows()
        {
            byte[] data = File.ReadAllBytes(ExamplePath("5_star_base.jpg"));
            byte[] truncated = data[..^2]; // 去掉 EOI
            using var stream = new SimulatedNetworkStream(truncated, EofProfile(truncated.Length));

            Assert.Throws<InvalidDataException>(() =>
                JpegDecoder.DecodeFromStreamAsync(stream, default, false, requireEoi: true)
                    .GetAwaiter().GetResult());
        }

        [Fact]
        public void Streaming_Decode_ConnectionReset_PropagatesIOException()
        {
            byte[] data = File.ReadAllBytes(ExamplePath("5_star_base.jpg"));
            using var stream = new SimulatedNetworkStream(data, new NetworkProfile
            {
                ChunkBytes = 1024,
                FaultMode = FaultMode.Reset,
                FaultAtBytes = data.Length / 2,
            });

            Assert.Throws<IOException>(() =>
                JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult());
        }

        [Fact]
        public void Streaming_Decode_Cancellation_IsObserved()
        {
            byte[] data = File.ReadAllBytes(ExamplePath("5_star_base.jpg"));
            using var cts = new CancellationTokenSource();
            using var stream = new SimulatedNetworkStream(data, new NetworkProfile
            {
                ChunkBytes = 64,
                Progress = delivered =>
                {
                    if (delivered >= data.Length / 4) cts.Cancel();
                },
            });

            Assert.ThrowsAny<OperationCanceledException>(() =>
                JpegDecoder.DecodeFromStreamAsync(stream, cts.Token).GetAwaiter().GetResult());
        }

        [Fact]
        public void Streaming_Decode_AsyncOnlyStream_ThrowsNotSupported()
        {
            // 现状记录：熵解码阶段是同步读取，只实现 ReadAsync 的流会失败。
            byte[] data = File.ReadAllBytes(ExamplePath("Amish-Noka-Dresser.jpg"));
            using var stream = new SimulatedNetworkStream(data, new NetworkProfile
            {
                ChunkBytes = 8192,
                SyncReadUnsupported = true,
            });

            Assert.Throws<NotSupportedException>(() =>
                JpegDecoder.DecodeFromStreamAsync(stream).GetAwaiter().GetResult());
        }

        [Fact]
        public void Webp_Encode_WriteFailure_PropagatesIOException()
        {
            var image = TestImageFactory.CreateGradient(64, 64);
            var broken = new ThrottledWriteStream(chunkBytes: 16, faultAtBytes: 32);

            var encoder = new WebpEncoderAdapter();
            Assert.Throws<IOException>(() => encoder.EncodeRgb24(broken, image));
        }

        [Fact]
        public void Webp_Decode_TruncatedData_ThrowsInvalidData()
        {
            var image = TestImageFactory.CreateGradient(32, 32);
            using var memory = new MemoryStream();
            new WebpEncoderAdapter().EncodeRgb24(memory, image);
            byte[] data = memory.ToArray();
            byte[] truncated = data[..(data.Length / 2)];

            using var stream = new MemoryStream(truncated);
            Assert.Throws<InvalidDataException>(() => new WebpDecoderAdapter().DecodeRgb24(stream));
        }

        private static NetworkProfile EofProfile(long length) => new()
        {
            ChunkBytes = 4096,
            FaultMode = FaultMode.EarlyEof,
            FaultAtBytes = length,
        };

        /// <summary>找到第一个 SOS 之后的第一个 DHT 标记偏移（渐进式 JPEG 的扫描边界）。</summary>
        private static int FirstScanBoundaryAfterFirstSos(byte[] data)
        {
            bool sawSos = false;
            for (int i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] != 0xFF) continue;
                byte marker = data[i + 1];
                if (marker == 0xDA) { sawSos = true; continue; }
                if (!sawSos) continue;
                if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC4 or 0xDB or 0xDD) return i;
            }
            throw new InvalidOperationException("未找到扫描边界");
        }
    }
}
