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
/// <para>
/// 字典查找分两级：<b>一级是直接映射的过滤缓存</b>（<see cref="CSIZE"/> 个槽），
/// <b>二级是开放寻址哈希表</b>（<see cref="HSIZE"/> 个槽，线性探测）。
/// 两级都把「键 + 字典编号」打包进同一个 32 位槽位：<c>(fcode &lt;&lt; 12) | code</c>，
/// 其中 <c>fcode = (c &lt;&lt; 12) | ent</c> 是 20 位键（c 是 8 位像素值，ent 最多 12 位）。
/// </para>
/// <para>
/// <b>为什么要一级缓存</b>：编码循环是一条「算散列 → 载入槽位 → 比较 → 更新 ent」的串行依赖链，
/// 每像素一次，既无法向量化也无法软件流水。二级表 256 KB 必然落在 L2 上，
/// 那次加载的十余周期延迟就是整条链的主体。一级缓存只有 128 KB，
/// 且下标取 <c>fcode &amp; (CSIZE-1)</c>（低 12 位就是 ent，等价于每个前缀 8 个槽），
/// 访问局部性远好于散列表，实测 87.5% 的查找能在这里命中。
/// </para>
/// <para>
/// 三个已实测的关键点，改动前请先读完：
/// <list type="bullet">
/// <item><b>两级加载必须并行发射</b>。先算好两级下标，把 <c>tab[h]</c> 与 <c>cache[ci]</c>
/// 连续发出，再判缓存；缓存命中时大表那次加载白做，但被乱序执行重叠掉了。
/// 若写成「先判缓存、未命中再查大表」，未命中要串行付两次加载延迟，
/// 实测比完全不用缓存还慢 4 ms。</item>
/// <item><b>缓存下标不能依赖散列值</b>。试过用主表的 <c>h</c> 当缓存下标，
/// 噪声图命中率确实从 53% 升到 61%，但下标要等 imul 算完才能发出，
/// 缓存加载被串行到大表之后，progressive.jpg 从 425 ms 退到 686 ms。
/// 多花一次 AND 换「立即发射」是划算的。</item>
/// <item><b>缓存必须参与插入</b>。试过「只在大表命中时回填缓存、新插入的条目不写缓存」，
/// 本意是避免一次性条目挤出热项，结果三张图全部变慢（progressive 431 → 456 ms）。</item>
/// </list>
/// </para>
/// <para>
/// <b>命中判定</b>不直接比较键，而是取差值 <c>d = slot - (fcode &lt;&lt; 12)</c>：
/// 命中时 d 恰好等于字典编号，一次减法同时完成「比较键」和「取出编号」。
/// 空槽（slot == 0）在 fcode == 0 时给出 d == 0，与任何合法编号（恒 ≥ clearCode + 2 ≥ 6）
/// 都不同，因此不必额外判断空槽就能把「空槽」与「键为 0 的合法项」区分开。
/// 判定写成 <c>d - 1u &lt; 4095u</c>：d ∈ [1,4095] 才算命中，d == 0（空槽）与
/// d ≥ 4096（键不同）被同一个无符号比较排除。
/// 早期版本直接用 <c>(slot &gt;&gt; 12) == fcode</c>，空槽 0 会被误判成 (c=0, ent=0) 的命中，
/// 少输出一个码字且少插一条 —— 表面上快了 5%，实际产物 md5 已不一致。
/// </para>
/// <para>
/// <b>实测</b>（<c>--gif-bench 5</c> 取中位数，LZW 阶段，对比改动前）：
/// <list type="bullet">
/// <item>槽位 64 位 → 32 位：progressive.jpg（143 MP）564 → 537 ms（−5%），
/// 同样 256 KB 容量下槽位数翻倍，装载因子 0.125 → 0.0625。</item>
/// <item>再加一级缓存：537 → 420 ms（再 −22%）。端到端编码 total 622 → 483 ms（−22%）。</item>
/// <item>缓存槽位数 2^12/2^13/2^14/2^15/2^16/2^17 对应 544/501/458/430/429/443 ms，
/// 峰值在 2^15（128 KB），再大就装不进缓存层级，收益被自身足迹吃掉。</item>
/// <item>二级表槽位数在有了一级缓存后仍取 2^16：2^14/2^15/2^16 对应 436/418/414 ms。</item>
/// </list>
/// </para>
/// <para>
/// <b>已知代价</b>：收益取决于缓存命中率，命中率低于约 60% 的图会略微变慢。
/// examples/5_star_base.jpg（1.2 MP，噪声多，字典重置密度是 progressive 的 3.3 倍，
/// 命中率仅 53%）LZW 6.2 → 6.9 ms（+9%）；同目录 car.png（命中率 81.6%）是 −18%。
/// 清零缓存不是原因（消融实验显示只占 1.9%），是低命中率下「多一次加载 + 比较」白付了。
/// </para>
/// <para>
/// 两张表都超过 LOH 阈值，因此从 ArrayPool 租用，避免动画逐帧分配触发大对象 GC。
/// </para>
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

    /// <summary>二级哈希表槽位数，取 2 的幂以便用掩码代替取模。</summary>
    private const int HSIZE = 65536;
    private const int HSIZE_MASK = HSIZE - 1;

    /// <summary>一级过滤缓存槽位数，取 2 的幂；直接映射，冲突即回落到二级表。</summary>
    private const int CSIZE = 32768;
    private const int CSIZE_MASK = CSIZE - 1;

    /// <summary>
    /// 奇数 64 位常数，用于乘法散列（Fibonacci hashing）。
    /// 取乘积的高位作为槽位下标，分布远优于原来的 (c &lt;&lt; 4) ^ ent
    /// —— 后者因 c&lt;=255、ent&lt;4096 恒落在 [0, 4095]，8191 个槽位只用到一半，
    /// 装载因子实际为 1.0，探测链严重退化。
    /// </summary>
    private const ulong HashMul = 0x9E3779B97F4A7C15UL;

    /// <summary>从 64 位乘积中取出高 16 位作为二级表下标（HSIZE = 2^16）。</summary>
    private const int HashShift = 64 - 16;

    /// <summary>
    /// 二级哈希表。槽位为 <c>(fcode &lt;&lt; 12) | code</c>，槽位 0 表示空：
    /// 合法字典编号恒 ≥ clearCode + 2 ≥ 6，所以任何真实条目都不可能取值为 0。
    /// </summary>
    private uint[]? _tab = ArrayPool<uint>.Shared.Rent(HSIZE);

    /// <summary>
    /// 一级过滤缓存，槽位格式与二级表一致，下标为 <c>fcode &amp; CSIZE_MASK</c>。
    /// 字典重置时必须一并清空，否则会把上一代的编号当成当前代的命中。
    /// </summary>
    private uint[]? _cache = ArrayPool<uint>.Shared.Rent(CSIZE);

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

        uint[] tab = _tab!;
        uint[] cache = _cache!;
        Array.Clear(tab);
        Array.Clear(cache);
        ref uint tabRef = ref MemoryMarshal.GetArrayDataReference(tab);
        ref uint cacheRef = ref MemoryMarshal.GetArrayDataReference(cache);

        Output(_clearCode);

        int ent = pixels[0];

        for (int i = 1; i < pixels.Length; i++)
        {
            int c = pixels[i];
            // 键 (c, ent) 打包成 20 位，再左移 12 位留出编号字段
            int fcode = (c << 12) | ent;
            uint probeKey = (uint)fcode << 12;
            int h = (int)(((ulong)fcode * HashMul) >> HashShift);

            // 两级加载并行发射：缓存下标只依赖 fcode，能立刻发出；
            // 缓存命中时大表那次加载白做，但被乱序执行重叠掉了
            uint slot = Unsafe.Add(ref tabRef, h);
            uint cd = Unsafe.Add(ref cacheRef, fcode & CSIZE_MASK) - probeKey;
            if (cd - 1u < 4095u)
            {
                ent = (int)cd;
                continue;
            }

            // 缓存未命中，走二级表。命中时 d 恰为字典编号；未命中时 d 为 0（空槽）或 ≥ 4096（键不同）
            uint d = slot - probeKey;
            if (d - 1u >= 4095u)
            {
                // 非空槽说明发生了冲突，线性探测直到命中或遇到空槽
                if (slot != 0)
                {
                    bool found = false;
                    while (true)
                    {
                        h = (h + 1) & HSIZE_MASK;
                        slot = Unsafe.Add(ref tabRef, h);
                        d = slot - probeKey;
                        if (d - 1u < 4095u) { found = true; break; }
                        if (slot == 0) break; // 空槽：字典中没有该串，插入位置就是这里
                    }
                    if (found)
                    {
                        ent = (int)d;
                        Unsafe.Add(ref cacheRef, fcode & CSIZE_MASK) = probeKey | (uint)d;
                        continue;
                    }
                }
            }
            else
            {
                ent = (int)d;
                Unsafe.Add(ref cacheRef, fcode & CSIZE_MASK) = probeKey | (uint)d;
                continue;
            }

            Output(ent);
            ent = c;

            if (_nextCode < 4096)
            {
                Unsafe.Add(ref tabRef, h) = probeKey | (uint)_nextCode;
                Unsafe.Add(ref cacheRef, fcode & CSIZE_MASK) = probeKey | (uint)_nextCode;
                _nextCode++;
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
        Array.Clear(_cache!);
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
        uint[]? tab = _tab;
        if (tab != null)
        {
            _tab = null;
            ArrayPool<uint>.Shared.Return(tab);
        }
        uint[]? cache = _cache;
        if (cache != null)
        {
            _cache = null;
            ArrayPool<uint>.Shared.Return(cache);
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
