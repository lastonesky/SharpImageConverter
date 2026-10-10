# ARM 性能记录（调优前基线 + 调优后结果）

本文件是 **ARM 架构性能调优的记录**，用于回答三个问题：

1. 调优前 ARM 上到底有多快？（客观数字，123 项测试）
2. 为什么慢？（哪些 x86 SIMD 路径在 ARM 上变成了死代码）
3. 调优后拿到了多少？（A/B 复测，见 **§7**）

§1~§6 记录的是**未做任何 ARM 优化**的状态（提交 `4fd37f3`），即对照基线；
§7 给出移植完成后的实测结果。本文档写作时项目尚用「提升不足 7% 算无效提升」的性能准入阈值，
判定取**中位数**，且必须确认产物哈希与基线一致（已知例外见 §6.1）；
**该阈值已于 2026-10-10 废止**（见 `CHANGELOG.md` → 未发布 → 规则），本文件相关文字作为当时记录保留。
（「产物哈希必须一致」「取中位数」是测量纪律，与阈值无关，继续有效。）

---

## 1. 测试环境

| 项 | 值 |
|---|---|
| 机型 | Apple M4（Mac mini） |
| 架构 | `arm64` / `Arm64` |
| OS | macOS 26.5.2（Darwin 25.5.0） |
| .NET | 10.0.1（SDK 10.0.101） |
| 逻辑核 | 10 |
| GC | workstation，非并发，LatencyMode=Batch |

### 1.1 SIMD 能力探测（关键）

```
simd-x86         : SSE2=False SSSE3=False SSE41=False AVX=False AVX2=False AVX512F=False AVXVNNI=False
simd-arm         : AdvSimd=True AdvSimd.Arm64=True Crc32=True Aes=True Dp=True Rdm=True
simd-generic     : Vector.IsHardwareAccelerated=True Vector<byte>.Count=16 Vector128<byte>.Count=16
```

**这是全部问题的根因**：本库的向量化实现 100% 建立在
`System.Runtime.Intrinsics.X86`（SSE2 / SSSE3 / SSE4.1 / AVX2 / AVX-VNNI）之上，
而这些自检在 arm64 上**全部为 `false`**。库中所有 `if (Ssse3.IsSupported)` 形式的
守卫因此整条失效，程序静默退回到标量实现。

> 注意 `Vector<byte>.Count = 16`：`System.Numerics.Vector<T>` 在 arm64 上走 NEON，
> 所以**基于 `Vector<T>` 泛型的代码在 ARM 上是有效的**（例如 `SimdHelper.AddBytesInPlace`
> 的最后一档分派）。失效的只有显式写 `Sse2.` / `Ssse3.` / `Avx2.` 的那些路径。

### 1.2 语料

由 `SicBench --mode gen` 从 `examples/progressive.jpg`（10650×13426 = 143.0 MP）生成：

| tag | 尺寸 | 像素 |
|---|---|---|
| tiny | 320×240 | 0.08 MP |
| small | 800×600 | 0.48 MP |
| medium | 1600×1200 | 1.92 MP |
| large | 4096×3072 | 12.58 MP |
| huge | 10650×13426 | 143.0 MP |

### 1.3 复现方式

```bash
dotnet build tools/perf/SicBench/SicBench.csproj -c Release -o .perf/bin/current
./.perf/bin/current/SicBench --mode gen   --corpus .perf/corpus --examples examples
./.perf/bin/current/SicBench --mode bench --corpus .perf/corpus \
    --tag arm-baseline --out .perf/results/arm-baseline.csv
```

产物：`.perf/results/arm-baseline.csv`（123 行）、`.perf/results/arm-baseline.log`。
本轮 123 项测试全部通过，**产物哈希全部稳定（unstable-output 0）**，可作为后续
逐字节一致性校验的基准。

---

## 2. ARM 上失效的 SIMD 路径清单（根因分析）

逐文件统计 `System.Runtime.Intrinsics.X86` 调用点与守卫条件：

| 文件 | x86 调用数 | 守卫条件 | ARM 上是否生效 |
|---|---:|---|---|
| `Formats/Jpeg/SimdJpegPipeline.cs` | 48 | 无守卫（由调用方守卫） | ✗ 完全不执行 |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs` | 52 | `Sse2.IsSupported` / `Ssse3.IsSupported` | ✗ 完全不执行 |
| `Processing/Processing.cs` | 160 | `Ssse3 && Sse41`、`Avx2 && AvxVnni` 等 | ✗ 全部退标量 |
| `Formats/Gif/OctreeQuantizer.cs` | 104 | `Ssse3 && Sse2` | ✗ 完全不执行 |
| `Formats/Png/PngDecoder.cs` | 51 | `Sse2.IsSupported` | ✗ 反滤波退标量 |
| `Core/SimdHelper.cs` | 46 | `Ssse3.IsSupported`（仅 `AddBytesInPlace` 有 AdvSimd 分支） | △ 部分失效 |
| `Formats/Png/Adler32.cs` | 9 | `Sse2.IsSupported` | ✗ **在热路径上**（见下方更正） |
| `Formats/Gif/GifDecoder.cs` | 7 | `Avx2.IsSupported` | ✗ |
| `Formats/Bmp/BmpWriter.cs` | 3 | `Ssse3.IsSupported` | ✗ RGB→BGR 退标量 |
| `Formats/Bmp/BmpReader.cs` | 3 | `Ssse3.IsSupported` | ✗ BGR→RGB 退标量 |
| `Formats/Gif/LzwDecoder.cs` | 2 | `Ssse3.IsSupported` | ✗ pshufb 反序退标量 |
| `Formats/Jpeg/JpegReconstruct.cs` | 2 | `Sse2 && Ssse3` 整体门控 | ✗ 见下 |
| `Formats/Jpeg/JpegFrameState.cs` | 2 | `Sse2.IsSupported` | ✗ 见下 |
| `Formats/Jpeg/JpegEncoder.cs` | 1 | `Sse2 \|\| AdvSimd` | ✓ 已有 ARM 分支 |

只有 **1 处**（`JpegEncoder.cs:2044`）已经写了 `AdvSimd` 分支。

> **更正（2026-10-08，两轮实测修正）**：上表把 `Adler32.cs` 标为"不在热路径"，**这是错的**；
> 但按"解码热路径"去理解它同样是错的。实测结论是——**它热在 PNG 编码，不在解码**：
>
> | 侧 | 调用的 `ZlibHelper` 重载 | 是否调用 `Adler32.Update` |
> |---|---|---|
> | 解码 | `DecompressTo(Stream, Span<byte>)`（`PngDecoder.cs:114/235`） | ✗ 走 BCL `ZLibStream`，Adler 在 BCL 内部校验 |
> | 编码 | `CompressRaw` → `Adler32Stream.Write`（`ZlibHelper.cs:243`） | ✓ 逐字节调用 |
>
> 该标量循环存在 `s1 += b[i]; s2 += s1;` 的**跨迭代依赖链**，吞吐被锁在约 2 周期/字节，
> 实测仅 **1.62 GB/s**（NEON 版 **7.81 GB/s**）。
> 折算到实测：PNG 编码 large 为 36 MB 解压量，标量 Adler 约 23 ms / 总 246 ms，
> 换成 NEON 后理论省 18.5 ms（+7.5%），**实测 +8.4%**（见 §7.2），吻合。
> 而 PNG 解码因此**完全没有收益**（+1%~2%，落在噪声内）——这一条已被 A/B 证实。
> 定级：P1，收益记在**编码侧**。

### 2.1 影响最大的三条链路

**(a) JPEG 解码：整条 SIMD 交织重建被跳过**

`JpegFrameState.cs:813`

```csharp
if (!handled && colorSpace == JpegColorSpace.YCbCr && !useFloatingPointIdct && Sse2.IsSupported)
    // → JpegDecoder.TryDecodeInterleavedYCbCrSimd(...)
```

`Sse2.IsSupported == false` ⇒ 该分支永不进入 ⇒ 后续 `TryDecodeInterleavedYCbCrSimd`
内部 `if (!Sse2.IsSupported || !Ssse3.IsSupported || ...) return false;` 同样直接返回。
结果：解码退回到 **平面 IDCT + 标量色彩空间交织/线性上采样**（`InterleaveComponents`
里逐像素 `outputRow[outIndex] = (byte)value`）。

x86 侧这项优化的实测收益是 **decode total −49%（440→223 ms）**，即 ARM 上同样量级的
收益被整体丢弃。同时 `Parallel.For(0, mcuY, ...)` 的按 MCU 行并行也一并失效——
**解码现在连并行都没有**。

**(b) JPEG 编码：色彩转换与 FDCT 双失效**

```csharp
internal static bool FdctSupported  { get; set; } = Sse2.IsSupported;   // ARM: false
internal static bool ColorSupported { get; set; } = Ssse3.IsSupported;  // ARM: false
```

x86 实测：色彩转换（`RgbToYCbCr`，`FillMcu420`）占编码 total 的 **64.5%**；FDCT 与
量化融合后不在关键路径、收益小。⇒ ARM 上编码至少丢掉色彩转换那一大块。

**(c) 缩放：Bilinear / Bicubic / Area 全部退标量**

x86 实测收益：`ResizeBilinear` **2.17–2.30x**（放大缩水都稳定）、`ResizeArea` 1.18–1.35x、
`Bicubic` 1.30x、`Gray8→Rgb24` **4.44x**、`Rgba32→Rgb24` **3.71x**、`Rgb24→Rgba32` **4.77x**。
这些在 ARM 上全部为 1.00x（走标量）。

---

## 3. 基线数字（123 项，中位数 ms）

### 3.1 huge 143.0 MP（最有代表性）

| 操作 | 基线 ms | Mpx/s | 备注 |
|---|---:|---:|---|
| decode JPEG（渐进式） | **1213.27** | 117.85 | SIMD 重建 + 并行均失效 |
| grayscale rgb24→gray | 37.45 | 3818.37 | 内存带宽受限 |
| resize 缩小 25%（Area） | 64.26 | 2225.04 | 标量 |
| resize 缩小 50%（Area） | 54.81 | 2608.77 | 标量 |
| encode JPEG q=75 | 356.92 | 400.61 | 色彩转换退标量 |
| encode PNG | 2111.18 | 67.73 | 94% 是 deflate（BCL，非本库代码） |
| encode BMP | 77.41 | 1847.02 | RGB→BGR 退标量 |
| encode WebP q=75 | 3552.40 | 40.25 | 原生 libwebp（上游已含 NEON） |
| encode GIF | 571.45 | 250.22 | 量化器 104 处 SIMD 全失效 |

### 3.2 large 4096×3072 = 12.58 MP

| 操作 | 基线 ms | 操作 | 基线 ms |
|---|---:|---|---:|
| decode JPEG | 96.71 | encode JPEG q75 | 32.24 |
| decode PNG | 48.52 | encode PNG | 227.08 |
| decode GIF | 44.00 | encode GIF | 55.40 |
| decode BMP | 5.81 | encode BMP | 6.38 |
| decode WebP | 38.38 | encode WebP | 335.28 |
| decode-gray PNG8 | 26.73 | encode-gray PNG8 | 91.65 |
| grayscale | 4.11 | resize 缩小 50% | 6.50 |
| resize 缩小 25% | 5.81 | resizefit-1024 | 5.77 |

### 3.3 medium 1600×1200 = 1.92 MP（缩放差异最明显）

| 操作 | 基线 ms | 备注 |
|---|---:|---|
| resize 放大 200%（auto→Bicubic） | 18.87 | |
| resize 放大 200%（**Bilinear**） | **33.49** | 比 auto 慢 77% —— 标量回退的直接证据 |
| resize 放大 200%（Bicubic） | 19.10 | |
| resize 缩小 50%（Bilinear） | 2.45 | |
| decode JPEG | 19.50 | |
| encode JPEG q75 | 5.01 | |
| encode PNG | 33.30 | |

> `upscale-200%-bilinear`（33.49 ms）明显慢于 `upscale-200%-auto`（18.87 ms）是
> **ARM 退化最重要的一条旁证**：x86 上 Bilinear 是全家最快的（比 Bicubic 快约 2x），
> ARM 上却因为 `Ssse3 && Sse41` 不成立而成为最慢的一档。

### 3.4 small / tiny

| 操作 | small (0.48MP) | tiny (0.08MP) |
|---|---:|---:|
| decode JPEG | 8.35 | 1.33 |
| decode PNG | 2.58 | 0.49 |
| encode JPEG q75 | 4.96 | 1.31 |
| encode PNG | 12.95 | 2.05 |
| grayscale | 0.55 | 0.09 |

---

## 4. 调优优先级（依据绝对耗时 × SIMD 可恢复比例）

| 优先级 | 目标 | 依据 | 预期空间 |
|---|---|---|---|
| **P0** | JPEG 解码 SIMD 交织重建 + MCU 行并行 | huge 1213 ms；x86 该项 −49% 且并行被一并禁用 | 大（2x 量级） |
| **P0** | GIF 量化器（`OctreeQuantizer`） | huge 571 ms，x86 实测量化占 GIF 编码 73% | 大 |
| **P1** | JPEG 编码色彩转换 + FDCT | x86 实测色彩转换占编码 total 64.5% | 中~大 |
| **P1** | 缩放（Bilinear/Bicubic/Area） | x86 实测 Bilinear 2.2x、Bicubic 1.3x、Area 1.2–1.35x | 中 |
| **P2** | `SimdHelper` 色彩转换与灰度 | x86 实测 Gray8→Rgb24 4.44x、Rgba→Rgb 3.71x、Rgb→Rgba 4.77x、灰度 1.55x | 中 |
| **P2** | PNG 解码反滤波 | x86 实测 −17% ~ −35% | 中 |
| **P3** | BMP RGB↔BGR | 绝对值小（6–77 ms） | 小 |
| **—** | PNG 编码 / WebP | 瓶颈是 deflate / 原生 libwebp，非本库可向量化部分 | 不动 |

### 4.1 技术前提（已验证）

- **`AdvSimd.Arm64.VectorTableLookup(Vector128<byte> table, Vector128<byte> indices)`
  与 `Ssse3.Shuffle` 语义<ins>并不完全等价</ins>**：两者对掩码字节 **16..127** 的处理不同
  （详见表下说明）。已用独立程序实测确认（见 §5）。⇒ 库中大量 `pshufb` 掩码可以机械移植，
  但**必须先做掩码归一化**（`mask & 0x8F`）才能保证逐位等价。
- `Vector<byte>.Count == 16` ⇒ 基于 `Vector<T>` 的分派在 ARM 上有效，可继续保留。
- 需要逐条核对语义的非 1:1 映射（不能想当然）：
  `Sse2.PackUnsignedSaturate`（→ UQXTN/UQXTN2）、`Sse2.UnpackLow/High`（→ ZIP1/ZIP2）、
  `Sse41.MultiplyLow` int32（→ NEON `mul`，注意 M4 上吞吐远好于 Alder Lake 的 pmulld）、
  `Sse41.Blend`、`Sse2.ShiftRightLogical` 的 16 位语义、`Avx2` 的 256 位路径（ARM 无对应，
  保持 128 位即可）。

---

## 5. NEON `TBL` 与 x86 `pshufb` 语义等价性验证

### 5.1 相同之处

两者都把掩码字节的最高位当作「该输出字节归零」的标志，且索引 0..15 时都取
`table[index]`。归一化后（见 5.2）逐位等价，因此库中大量 `pshufb` 掩码可以机械移植。

### 5.2 关键差异：掩码字节落在 16..127 时

| 掩码字节 m | x86 `pshufb` | NEON `tbl` |
|---|---|---|
| 0..15 | `table[m]` | `table[m]` |
| **16..127** | **`table[m & 0x0F]`**（取低 4 位） | **0**（越界归零） |
| 128..255 | 0（bit7 置位） | 0（索引 ≥ 16） |

> 修订说明：本文件早期版本写的「`tbl` 与 `pshufb` 语义完全等价，索引 ≥ 16 都返回 0」
> **是错的**——`pshufb` 并不归零，而是按低 4 位折叠。Intel SDM 的 PSHUFB 伪码为
> `IF (mask[7] = 1) THEN 0 ELSE table[mask[3:0]]`。

### 5.3 归一化方法

`mask & 0x8F` 一步即可让 NEON `tbl` 完全等价于 `pshufb`：

- bit7 保留 ⇒ `0x80..0x8F` 作为索引 ≥ 16 会归零，正是 pshufb 的归零语义；
- bit4..6 清零 ⇒ 等价于 pshufb 的 `& 0x0F`。

代价是每次查表多一条 NEON `and`。对库中现有全部掩码（字节只落在 `0..15` 或 `0x80`）
这是恒等变换，不改变任何既有产物。实现见 `Core/SimdCompat.ShuffleBytes`；
`ShuffleBytesRaw` 是零开销版本，调用方须自行保证掩码合法。

### 5.4 已实测确认的 NEON `EXT` 方向

`AdvSimd.ExtractVector128` 用于合成整寄存器按字节移位，方向极易写反（开发中确实写反过一次，
导致 PNG 去滤波产物错乱、6 个测试失败）。实测结论：

| 表达式 | 等价指令 |
|---|---|
| `ExtractVector128(v, zero, n)` | `psrldq(v, n)` |
| `ExtractVector128(zero, v, 16-n)` | `pslldq(v, n)` |

注意 `n = 0` 时 `16-0 = 16` 超出 `ExtractVector128` 允许的 0..15 会抛异常，
而 x86 的 `pslldq x, 0` 是合法恒等操作——必须单独处理。

---

## 6. 验收口径（沿用项目既有约定）

1. ~~**准入阈值 7%**：提升不足 7% 的改动不保留、不提交。~~ **已于 2026-10-10 废止**
   （见本文开头注与 `CHANGELOG.md`）；其余四条继续有效。
2. **取中位数**，不取单次、不取 min/max。
3. **产物一致性**：所有改动必须使 123 项测试的 `hash` 列与
   `.perf/results/arm-baseline.csv` 逐字节一致；同时通过
   `SharpImageConverter.Tests` 全部单测。
4. **同构建内 A/B**：涉及 <10% 量级的比较，探针须用 `static readonly` 环境开关
   切在同一份二进制里，避免跨进程 8% 量级的漂移。
5. **不许改 x86 结果**：ARM 优化不得改变 x86 上的行为与产物。

### 6.1 产物一致性的已知例外（2026-10-08 实测确认）

第 3 条"123 项 hash 全部逐字节一致"对 **5 项 JPEG 解码**不成立，且**不成立是正确行为**：

| 项 | 差异 |
|---|---|
| `decode jpeg/jpeg @ tiny / small / medium / large` | 4 项 |
| `decode jpeg-progressive/jpeg @ huge` | 1 项 |

**根因**：`JpegFrameState` 的 SIMD 交织重建路径（`TryDecodeInterleavedYCbCrSimd`）与标量回退
在**色度上采样算法上本来就不同**——SIMD 路径用「色度复制」，标量路径用「双线性」。
这是在引入本层之前就存在的 x86 行为。ARM 基线之所以与 x86 产物不同，恰恰是因为
**ARM 上这段 SIMD 整条被跳过**（`Sse2.IsSupported == false`）而走了标量双线性。

因此：移植后 ARM 走上与 x86 相同的 SIMD 分支 ⇒ **ARM 产物向 x86 收敛**，与"ARM 标量基线"
必然不同。隔离验证：

- 用 `SimdCompat.ForceScalar` 强制标量 ⇒ 4 个尺寸**全部精确复现基线 hash**；
- 放开（走 NEON）⇒ 4 个尺寸**全部不同，包括 tiny**，与"任何尺寸都会走不同上采样"一致；
- 用 4:4:4 样本隔离色度因素：`examples/5_star_base.jpg` 差异 48 159 B / maxAbs 5，
  自造 4:2:0 样本差异 794 239 B / maxAbs 110（4:4:4 无色度上采样，故差异小一个量级）；
- `git diff` 核对 `SimdJpegPipeline.cs` 的 44 处改动**全部**是 x86 内在函数的 1:1 替换，
  **零算法逻辑改动**。

⇒ 判定：这 5 项属于**继承自 x86 的既有行为差异**，不是本次移植引入的回归；
验收时按"逐字节一致"豁免，改为人工核对"差异仅来自色度上采样"。

### 6.2 热机口径：关掉分层编译，而不是靠"多热身几百次"

`SicBench.Measure` 原先固定热身 40 次。用环境变量扫描同一算子
（`resize/upscale-200%-auto/rgb24@tiny`，同一份二进制）得到：

| 热机次数 | 40 | 80 | 150 | 300 | 1000 |
|---|---:|---:|---:|---:|---:|
| 中位数 ms | **7.72** | **8.08** | 0.59 | 0.60 | 0.55 |

即 tier-0→tier-1 的实际阈值落在 **80~150 次**之间，40 次远远不够。

**为什么这条偏差专门打击"优化后"的构建**：向量路径的 tier-0 代码体量远大于标量路径
（大量 `AggressiveInlining` 的 `SimdCompat` 包装 + 内在函数在 tier-0 不被优化）。
实测同一算子的 tier-0/tier-1 落差：

| 构建 | tier-0（第 0~19 次均值） | 稳态 | 落差 |
|---|---:|---:|---:|
| 基线（标量） | 3.19 ms | 0.94 ms | 3.4x |
| 优化后（NEON） | **9.04 ms** | 0.57 ms | **15.9x** |

于是 40 次热机窗口中，基线已接近稳态、优化后仍停在 tier-0，**tiny/small 全项被误判为变慢**，
测出 `upscale-200%@tiny 0.271x` 这类物理不可能的数字（输出 0.9 MB 的项比输出 5.6 MB 的项还慢 2.4 倍）。

**最终修法（不靠大次数重复）**：在 `SicBench.csproj` 里设
`<TieredCompilation>false</TieredCompilation>`，让运行时**根本不存在 tier-0 平台期**——
方法第一次 JIT 就是完全优化版。于是热身只需覆盖"首次调用"，固定 **8 次**（上限 800 ms 保护巨型算子）即可，
不再需要 150~600 次的扫描或收敛判定，也不再需要任何环境变量开关。

实测同一项（`resize/upscale-200%-auto/rgb24@tiny`，`w=8`）：**0.58 / 0.77 / 0.62 ms**，
与"热身 150~1000 次"得到的 0.55~0.60 ms 完全一致。整轮 A/B 因此从 ~15 min 降到 **3 min 58 s**。

> 注意这条修法也有其边界：它比较的是**稳态吞吐**。对"短生命周期进程只调用几次"的场景，
> 向量路径的 tier-0 更慢是真实代价——该差异不在本文件口径内，另行记录。
> 两侧（基线/优化）用同一份 `SicBench.csproj`，因此对比仍然公平。

---

## 7. 调优后结果（A/B，2026-10-08）

复现命令：

```bash
tools/perf/run-ab.sh 4fd37f3 2 1.0      # 基线 commit / 2 轮 / 阈值 1.0 / 全量
```

两侧各 2 轮交替、跨轮取中位数；产物哈希逐字节比对。
原始数据：`.perf/results/{baseline,current}-final-20261008-154535.csv`，
报告 `.perf/results/report-20261008-154535.md`。

### 7.1 全局分布（123 项）

| 加速比区间 | 项数 | 占比 |
|---|---:|---:|
| ≥ 1.5x | 30 | 24.4% |
| 1.07 ~ 1.5x | 28 | 22.8% |
| ±7% 以内（含未改动项） | 62 | 50.4% |
| < 0.93x | 3 | 2.4% |

即 **58 项（47.2%）超过当时项目的 7% 准入阈值**（该阈值已于 2026-10-10 废止），且全部集中在原本被跳过 SIMD 的路径上。

### 7.2 主要收益

| 链路 | 项 | 基线 ms | 调优后 ms | 加速 |
|---|---|---:|---:|---:|
| JPEG 解码 | `decode/jpeg@large` | 102.48 | 17.33 | **5.91x** |
| JPEG 解码 | `decode/jpeg@medium` | 16.77 | 3.59 | **4.68x** |
| JPEG 解码（渐进式） | `decode/jpeg-progressive@huge` | 1329.97 | 356.62 | **3.73x** |
| JPEG 解码 | `decode/jpeg@small` | 4.48 | 1.44 | **3.11x** |
| RGB→灰度 | `grayscale/rgb24->gray@tiny` | 0.07 | 0.02 | **3.03x** |
| WebP 解码 | `decode/webp@tiny` | 0.66 | 0.26 | **2.50x** |
| RGB→灰度 | `grayscale/rgb24->gray@medium` | 0.42 | 0.19 | **2.18x** |
| BMP 编码 | `encode/bmp@medium` | 1.05 | 0.49 | **2.14x** |
| 双线性缩放 200% | `resize/upscale-200%-bilinear@medium` | 30.91 | 14.97 | **2.06x** |
| 双线性缩小 50% | `resize/downscale-50%-bilinear@medium` | 2.25 | 1.09 | **2.06x** |
| 双线性缩放 200% | `resize/upscale-200%-bilinear@small` | 8.03 | 3.96 | **2.03x** |
| JPEG 编码 q75 | `encode/jpeg-q75@huge` | 343.05 | 174.91 | **1.96x** |
| 双三次缩放 200% | `resize/upscale-200%-bicubic@small` | 4.84 | 3.01 | **1.61x** |
| 灰度缩放 200% | `gray-resize/upscale-200%@medium` | 20.25 | 11.02 | **1.84x** |
| BMP 编码 | `encode/bmp@huge` | 68.85 | 41.62 | **1.65x** |
| **PNG 编码**（Adler32） | `encode/png@large` | 246.44 | 227.31 | **1.084x** |
| **PNG 编码**（Adler32） | `encode/png@medium` | 36.87 | 33.65 | **1.096x** |
| **PNG 编码**（Adler32） | `encode/png@huge` | 2260.21 | 2067.08 | **1.093x** |
| GIF 编解码 | `encode/gif@small` | 4.10 | 3.73 | 1.100x |
| GIF 编解码 | `decode/gif@medium` | 8.07 | 7.52 | 1.073x |

**结构性说明**：

- **JPEG 解码 3~6x** 是本次最大单项收益：原 x86 SIMD 交织重建 + IDCT 整条被跳过，
  ARM 上是纯标量，移植后逐条恢复。
- **PNG 编码 +8~10% 全部来自 Adler32**，与 §2 的推算吻合（36 MB ×(1/1.62−1/7.81) ≈ 18.5 ms / 246 ms）。
  **PNG 解码无收益**（+1~2%，噪声内）——因为解码走 BCL `ZLibStream`，根本不经我们这段代码。
- **GIF 只有 +3%~+12%**，因为 ARM 上的标量回退本就是查表实现，效率不差；
  这类路径的收益天花板低，属如实体现，不作夸大。
- **`resize/downscale-25%-auto`、`webp` 编码、`bmp` 解码**等未改动项稳定在 ±2% 内，
  说明测量口径本身没有系统性漂移。

### 7.3 产物一致性

123 项中 **5 项哈希不一致**，全部是 JPEG 解码（`decode/jpeg` 4 项 + `jpeg-progressive@huge`），
即 §6.1 已认定并豁免的"SIMD 路径用色度复制上采样、标量路径用双线性"的**继承自 x86 的既有差异**，
非本次引入。其余 **118 项逐字节一致**。

### 7.4 三个 <0.93x 项的逐项取证

| 项 | 基线（两轮 med / min） | 调优后（两轮 med / min） | 判定 |
|---|---|---|---|
| `resize/resizefit-1024@medium` | 1.598/1.454、1.578/1.498 | **1.479**/1.454、**2.124**/1.775 | 两轮方向相反（r1 更快、r2 更慢），且 r1 的 min 与基线完全相同 ⇒ **双峰噪声** |
| `decode-gray/png8@medium` | 4.779/4.641、4.698/4.658 | 5.662/**4.616**、4.785/4.705 | `min_ms` 两侧几乎相同（4.641 vs 4.616）⇒ **同一轮内的离群值拉高中位数**，非回归 |
| `encode/jpeg-q90@medium` | 5.002/4.873、4.896/4.822 | 5.715/5.550、5.202/5.010 | 两轮同向偏慢 9%。但同一代码路径的 q75@medium 为 **1.185x**、q90@large 为 **1.135x**，q90 与 q75 只差量化表 ⇒ 存疑，**保留观察**，不宣称已解释 |

> 诚实声明：前两项已用原始数据证明是噪声；第三项尚未给出机制解释，既不认定为回归、
> 也不掩饰。若要定论需要单独复测（增加轮次或改用 `--filter` 隔离该行）。

