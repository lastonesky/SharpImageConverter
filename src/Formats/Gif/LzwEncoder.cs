using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpImageConverter.Formats.Gif;

/// <summary>
/// LZW 编码器，用于 GIF 图像数据压缩。
/// </summary>
/// <remarks>
/// 字典查找采用开放寻址哈希表，单个槽位打包为 64 位：
/// <c>((fcode + 1) &lt;&lt; 12) | code</c>，其中 fcode = (c &lt;&lt; 12) | ent 是 20 位键，
/// code 是 12 位字典编号。这样键与值落在同一条 cache line 上，一次探测只需一次加载。
///
/// 槽位为 0 表示空。键存储为 fcode + 1，因此插入项恒非零，
/// 不会与空标记冲突（若直接存 fcode，则 (c=0, ent=0) 的键为 0 会被误判成命中）。
///
/// 槽位数远大于字典上限 4096（装载因子约 0.12），用于压低线性探测链长度。
/// 实测瓶颈在探测次数而非缓存：槽位 4096/8192/16384/32768/65536/131072 对应
/// 编码耗时 32.6/24.9/22.0/21.0/21.6/23.9 ms，故取 32768。
/// 该尺寸超过 LOH 阈值，因此从 ArrayPool 租用，避免动画逐帧分配触发大对象 GC。
/// </remarks>
public class LzwEncoder(Stream stream) : IDisposable
{
    private readonly Stream _stream = stream;
    private int _codeSize;
    private int _clearCode;
    private int _endCode;
    private int _nextCode;
    private int _curMaxCode;
    private int _initCodeSize;

    /// <summary>哈希表槽位数，取 2 的幂以便用掩码代替取模。</summary>
    private const int HSIZE = 32768;
    private const int HSIZE_MASK = HSIZE - 1;

    /// <summary>
    /// 奇数 64 位常数，用于乘法散列（Fibonacci hashing）。
    /// 取乘积的高位作为槽位下标，分布远优于原来的 (c &lt;&lt; 4) ^ ent
    /// —— 后者因 c&lt;=255、ent&lt;4096 恒落在 [0, 4095]，8191 个槽位只用到一半，
    /// 装载因子实际为 1.0，探测链严重退化。
    /// </summary>
    private const ulong HashMul = 0x9E3779B97F4A7C15UL;

    /// <summary>从 64 位乘积中取出高 15 位作为槽位下标（HSIZE = 2^15）。</summary>
    private const int HashShift = 64 - 15;

    /// <summary>字典哈希表。槽位 0 表示空，插入项见类注释的打包方式。</summary>
    private ulong[]? _tab = ArrayPool<ulong>.Shared.Rent(HSIZE);

    private long _curAccum;
    private int _curBits;

    private const int MAX_BLOCK_SIZE = 255;
    private readonly byte[] _packet = new byte[256];
    private int _packetSize = 0;

    public void Encode(byte[] pixels, int width, int height, int colorDepth)
    {
        _initCodeSize = Math.Max(2, colorDepth);
        _stream.WriteByte((byte)_initCodeSize);

        _codeSize = _initCodeSize + 1;
        _clearCode = 1 << _initCodeSize;
        _endCode = _clearCode + 1;
        _nextCode = _clearCode + 2;
        _curMaxCode = (1 << _codeSize) - 1;

        _curAccum = 0;
        _curBits = 0;
        _packetSize = 0;

        ulong[] tab = _tab!;
        Array.Clear(tab);
        ref ulong tabRef = ref MemoryMarshal.GetArrayDataReference(tab);

        Output(_clearCode);

        int ent = pixels[0];

        for (int i = 1; i < pixels.Length; i++)
        {
            int c = pixels[i];
            int fcode = (c << 12) | ent;
            int key = fcode + 1;
            int h = (int)(((ulong)fcode * HashMul) >> HashShift);

            ulong slot = Unsafe.Add(ref tabRef, h);
            if ((int)(slot >> 12) != key)
            {
                // 未命中：非空槽说明发生了冲突，线性探测直到命中或遇到空槽
                if (slot != 0)
                {
                    bool found = false;
                    while (true)
                    {
                        h = (h + 1) & HSIZE_MASK;
                        slot = Unsafe.Add(ref tabRef, h);
                        if ((int)(slot >> 12) == key) { found = true; break; }
                        if (slot == 0) break; // 空槽：字典中没有该串，插入位置就是这里
                    }
                    if (found)
                    {
                        ent = (int)(slot & 0xFFF);
                        continue;
                    }
                }
            }
            else
            {
                ent = (int)(slot & 0xFFF);
                continue;
            }

            Output(ent);
            ent = c;

            if (_nextCode < 4096)
            {
                Unsafe.Add(ref tabRef, h) = ((ulong)key << 12) | (uint)_nextCode++;
            }
            else
            {
                ClearTable();
            }
        }

        Output(ent);
        Output(_endCode);
        FlushBits();
        _stream.WriteByte(0); // Block terminator
    }

    private void ClearTable()
    {
        Array.Clear(_tab!);
        Output(_clearCode);
        _codeSize = _initCodeSize + 1;
        _nextCode = _clearCode + 2;
        _curMaxCode = (1 << _codeSize) - 1;
    }

    private void Output(int code)
    {
        _curAccum |= (long)code << _curBits;
        _curBits += _codeSize;

        while (_curBits >= 8)
        {
            _packet[++_packetSize] = (byte)(_curAccum & 0xFF);
            _curAccum >>= 8;
            _curBits -= 8;
            if (_packetSize >= MAX_BLOCK_SIZE) FlushPacket();
        }

        if (_nextCode > _curMaxCode && _codeSize < 12)
        {
            _codeSize++;
            _curMaxCode = (1 << _codeSize) - 1;
        }
    }

    private void FlushBits()
    {
        while (_curBits > 0)
        {
            _packet[++_packetSize] = (byte)(_curAccum & 0xFF);
            _curAccum >>= 8;
            _curBits -= 8;
            if (_packetSize >= MAX_BLOCK_SIZE) FlushPacket();
        }
        FlushPacket();
    }

    public void Dispose()
    {
        FlushPacket();
        ulong[]? tab = _tab;
        if (tab != null)
        {
            _tab = null;
            ArrayPool<ulong>.Shared.Return(tab);
        }
    }

    private void FlushPacket()
    {
        if (_packetSize > 0)
        {
            _packet[0] = (byte)_packetSize;
            _stream.Write(_packet, 0, _packetSize + 1);
            _packetSize = 0;
        }
    }
}
