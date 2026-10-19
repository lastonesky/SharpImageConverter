using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StreamNet;

internal enum FaultMode
{
    None,
    /// <summary>连接被重置：Read/ReadAsync 抛 IOException（典型 RST）。</summary>
    Reset,
    /// <summary>对端提前关闭：Read 返回 0（典型 FIN，不带异常）。</summary>
    EarlyEof,
}

/// <summary>
/// 网络传输条件。所有参数都作用在「一次 Read 调用」的粒度上：
/// 分片大小 = 单次调用最多交付的字节数，延迟 = 每次调用前的等待。
/// </summary>
internal sealed class NetworkProfile
{
    /// <summary>单次 Read 最多交付的字节数。</summary>
    public int ChunkBytes { get; init; } = 4096;

    /// <summary>分片大小抖动（相对 ChunkBytes 的百分比，0 表示固定）。</summary>
    public int ChunkJitterPercent { get; init; }

    /// <summary>每次 Read 前的单向延迟（毫秒），模拟限速/高 RTT。</summary>
    public double DelayMsPerRead { get; init; }

    /// <summary>建立连接的一次性延迟（毫秒）。</summary>
    public double LatencyMs { get; init; }

    /// <summary>在交付到该字节数后注入故障；-1 表示不注入。</summary>
    public long FaultAtBytes { get; init; } = -1;

    public FaultMode FaultMode { get; init; } = FaultMode.None;

    public int Seed { get; init; } = 1;

    /// <summary>每交付一批字节的回调（用于在传输中途取消）。</summary>
    public Action<long>? Progress { get; init; }

    /// <summary>模拟「只支持异步读取」的流：同步 Read 抛 NotSupportedException。</summary>
    public bool SyncReadUnsupported { get; init; }
}

internal sealed class NetworkStats
{
    public long ReadCalls;
    public long BytesServed;
    public long MaxRequested;
    public long MaxServed;
    public double InjectedDelayMs;
    public double LongestCallMs;
}

/// <summary>
/// 只读、不可 Seek 的网络流仿真：固定/抖动分片、每读延迟、连接重置、提前 EOF。
/// 不可 Seek 是关键约束——它保证被测代码走「只能顺序消费」的真实网络路径。
/// </summary>
internal sealed class SimulatedNetworkStream : Stream
{
    private readonly byte[] payload;
    private readonly NetworkProfile profile;
    private readonly Random rng;
    private readonly NetworkStats stats = new();
    private long position;
    private bool latencyApplied;

    public SimulatedNetworkStream(byte[] payload, NetworkProfile profile)
    {
        this.payload = payload;
        this.profile = profile;
        rng = new Random(profile.Seed);
    }

    public NetworkStats Stats => stats;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException("网络流长度未知");

    public override long Position
    {
        get => throw new NotSupportedException("网络流不可定位");
        set => throw new NotSupportedException("网络流不可定位");
    }

    private int NextChunk(int max)
    {
        int chunk = profile.ChunkBytes;
        if (profile.ChunkJitterPercent > 0)
        {
            int delta = chunk * profile.ChunkJitterPercent / 100;
            if (delta > 0) chunk += rng.Next(-delta, delta + 1);
        }

        if (chunk < 1) chunk = 1;
        return Math.Min(chunk, max);
    }

    private void ApplyFault()
    {
        if (profile.FaultMode == FaultMode.None || profile.FaultAtBytes < 0) return;
        if (position < profile.FaultAtBytes) return;

        if (profile.FaultMode == FaultMode.Reset)
        {
            throw new IOException("模拟网络故障：连接被对端重置 (RST)");
        }
    }

    private int Serve(Span<byte> destination)
    {
        stats.ReadCalls++;
        stats.MaxRequested = Math.Max(stats.MaxRequested, destination.Length);
        long started = Stopwatch.GetTimestamp();

        try
        {
            if (!latencyApplied)
            {
                latencyApplied = true;
                stats.InjectedDelayMs += profile.LatencyMs;
                Delay(profile.LatencyMs, countInjected: false);
            }

            stats.InjectedDelayMs += profile.DelayMsPerRead;
            Delay(profile.DelayMsPerRead, countInjected: false);

            if (destination.Length == 0) return 0;

            ApplyFault();
            if (profile.FaultMode == FaultMode.EarlyEof && profile.FaultAtBytes >= 0 && position >= profile.FaultAtBytes)
            {
                return 0; // FIN：干净关闭但没有更多数据
            }

            if (position >= payload.Length) return 0;

            int toServe = NextChunk((int)Math.Min(destination.Length, payload.Length - position));
            payload.AsSpan((int)position, toServe).CopyTo(destination);
            position += toServe;
            stats.BytesServed += toServe;
            stats.MaxServed = Math.Max(stats.MaxServed, toServe);
            profile.Progress?.Invoke(stats.BytesServed);
            return toServe;
        }
        finally
        {
            stats.LongestCallMs = Math.Max(stats.LongestCallMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (profile.SyncReadUnsupported)
        {
            throw new NotSupportedException("该流只支持异步读取（模拟）");
        }

        return Serve(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (profile.SyncReadUnsupported)
        {
            throw new NotSupportedException("该流只支持异步读取（模拟）");
        }

        return Serve(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        stats.ReadCalls++;
        stats.MaxRequested = Math.Max(stats.MaxRequested, buffer.Length);
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!latencyApplied)
            {
                latencyApplied = true;
                await DelayAsync(profile.LatencyMs, cancellationToken).ConfigureAwait(false);
            }

            if (profile.DelayMsPerRead > 0)
            {
                await DelayAsync(profile.DelayMsPerRead, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (buffer.Length == 0) return 0;

            ApplyFault();
            if (profile.FaultMode == FaultMode.EarlyEof && profile.FaultAtBytes >= 0 && position >= profile.FaultAtBytes)
            {
                return 0;
            }

            if (position >= payload.Length) return 0;

            int toServe = NextChunk(Math.Min(buffer.Length, (int)(payload.Length - position)));
            payload.AsSpan((int)position, toServe).CopyTo(buffer.Span);
            position += toServe;
            stats.BytesServed += toServe;
            stats.MaxServed = Math.Max(stats.MaxServed, toServe);
            profile.Progress?.Invoke(stats.BytesServed);
            return toServe;
        }
        finally
        {
            stats.LongestCallMs = Math.Max(stats.LongestCallMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private static void Delay(double milliseconds, bool countInjected = true)
    {
        // Windows 默认定时器粒度 ~15.6 ms，Thread.Sleep(2) 实际会睡 15 ms，
        // 短延迟必须自旋等待，否则「注入延迟」与墙钟完全对不上。
        if (milliseconds <= 0) return;
        if (milliseconds > 20)
        {
            Thread.Sleep((int)Math.Round(milliseconds));
            return;
        }

        long target = (long)(Stopwatch.Frequency * milliseconds / 1000.0);
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < target)
        {
            Thread.SpinWait(200);
        }
    }

    private async Task DelayAsync(double milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0) return;
        stats.InjectedDelayMs += milliseconds;
        if (milliseconds >= 30)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Delay(milliseconds, countInjected: false);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("网络流不可定位");
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("只读流");
}

/// <summary>
/// 只写、不可 Seek 的网络流仿真：分片写入 + 每片延迟 + 中途断链。
/// 用来验证编码器把结果写给网络响应流时的行为（HTTP 响应体典型不可 Seek）。
/// </summary>
internal sealed class ThrottledWriteStream : Stream
{
    private readonly MemoryStream sink = new();
    private readonly int chunkBytes;
    private readonly double delayMsPerWrite;
    private readonly long faultAtBytes;
    private readonly bool faultAsReset;
    private long written;

    public ThrottledWriteStream(int chunkBytes = 4096, double delayMsPerWrite = 0, long faultAtBytes = -1, bool faultAsReset = true)
    {
        this.chunkBytes = Math.Max(1, chunkBytes);
        this.delayMsPerWrite = delayMsPerWrite;
        this.faultAtBytes = faultAtBytes;
        this.faultAsReset = faultAsReset;
    }

    public long BytesWritten => written;
    public int WriteCalls { get; private set; }
    public byte[] ToArray() => sink.ToArray();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException("网络流长度未知");

    public override long Position
    {
        get => throw new NotSupportedException("网络流不可定位");
        set => throw new NotSupportedException("网络流不可定位");
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            if (faultAtBytes >= 0 && written >= faultAtBytes)
            {
                if (faultAsReset) throw new IOException("模拟网络故障：写入途中连接被重置 (RST)");
                return;
            }

            int toWrite = Math.Min(chunkBytes, buffer.Length);
            if (faultAtBytes >= 0) toWrite = (int)Math.Min(toWrite, faultAtBytes - written);
            if (toWrite <= 0) continue;

            sink.Write(buffer[..toWrite]);
            written += toWrite;
            WriteCalls++;
            buffer = buffer[toWrite..];

            if (delayMsPerWrite >= 1)
            {
                Thread.Sleep((int)Math.Round(delayMsPerWrite));
            }
            else if (delayMsPerWrite > 0)
            {
                Thread.SpinWait(2000);
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (delayMsPerWrite >= 1)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMsPerWrite), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("只写流");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("网络流不可定位");
    public override void SetLength(long value) => throw new NotSupportedException();
}
