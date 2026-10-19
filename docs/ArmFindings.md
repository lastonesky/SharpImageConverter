# ARM（Apple M4）性能基线与专属指令实测

> 本文件合并了「调优前基线 / 调优后 A/B」与「ARM 专属指令候选清单 + 实测判决」。
> 结论一句话：ARM 上原本**整条向量化路径静默失效**，把 `Sse2/Ssse3/Sse41/Avx2` 换成 NEON 后
> JPEG 解码拿到 3~6x；而 ARM 专属的 LD3 / RDM 两条指令**实测都是负收益或语义不符**，
> 唯一成立的是硬件 CRC32。
>
> 判定取**中位数**，产物哈希必须与基线逐字节一致（已知例外见 §6.1）。

## 1. 测试环境与能力探测

| 项 | 值 |
|---|---|
| 机型 | Apple M4（Mac mini），10 逻辑核 |
| OS / .NET | macOS 26.5.2 / .NET 10.0.1（SDK 10.0.101） |
| GC | workstation，非并发，LatencyMode=Batch |

```
simd-x86     : SSE2=False SSSE3=False SSE41=False AVX=False AVX2=False AVX512F=False AVXVNNI=False
simd-arm     : AdvSimd=True AdvSimd.Arm64=True Crc32=True Crc32.Arm64=True Aes=True Dp=True Rdm=True
               Sha1=True Sha256=True   Sve=False  Sve2=False
simd-generic : Vector.IsHardwareAccelerated=True Vector<byte>.Count=16
```

**这是全部问题的根因**：库的向量化实现原本 100% 建立在 `System.Runtime.Intrinsics.X86` 上，
arm64 上这些自检全为 `false`，所有 `if (Ssse3.IsSupported)` 守卫整条失效，程序静默退标量。

两条已定论的边界：

- `Vector<byte>.Count == 16` ⇒ **基于 `Vector<T>` 的代码在 ARM 上有效**（走 NEON），
  失效的只有显式写 `Sse2.` / `Ssse3.` / `Avx2.` 的路径。
- `Sve/Sve2` **实测为 False**（.NET 10 已暴露这两个类，M4 不实现）⇒ 32768 项 LUT 的 gather 无解；
  SME 硬件有但 .NET 无 API，不必调研。

## 2. ARM 上失效的 x86 SIMD 路径

| 文件 | x86 调用数 | 守卫条件 | ARM 上是否生效 |
|---|---:|---|---|
| `Formats/Jpeg/SimdJpegPipeline.cs` | 48 | 无守卫（由调用方守卫） | ✗ 完全不执行 |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs` | 52 | `Sse2` / `Ssse3` | ✗ 完全不执行 |
| `Processing/Processing.cs` | 160 | `Ssse3 && Sse41`、`Avx2 && AvxVnni` | ✗ 全部退标量 |
| `Formats/Gif/OctreeQuantizer.cs` | 104 | `Ssse3 && Sse2` | ✗ 完全不执行 |
| `Formats/Png/PngDecoder.cs` | 51 | `Sse2` | ✗ 反滤波退标量 |
| `Core/SimdHelper.cs` | 46 | `Ssse3`（仅 `AddBytesInPlace` 有 AdvSimd） | △ 部分失效 |
| `Formats/Png/Adler32.cs` | 9 | `Sse2` | ✗ **热在 PNG 编码**（见下） |
| `Formats/Gif/GifDecoder.cs` | 7 | `Avx2` | ✗ |
| `Formats/Bmp/BmpWriter.cs` / `BmpReader.cs` | 3 + 3 | `Ssse3` | ✗ RGB↔BGR 退标量 |
| `Formats/Gif/LzwDecoder.cs` | 2 | `Ssse3` | ✗ pshufb 反序退标量 |
| `Formats/Jpeg/JpegReconstruct.cs` / `JpegFrameState.cs` | 2 + 2 | `Sse2 && Ssse3` | ✗ |
| `Formats/Jpeg/JpegEncoder.cs` | 1 | `Sse2 \|\| AdvSimd` | ✓ 已有 ARM 分支 |

**Adler32 的定位纠错**（早期判断错两次）：它**不**在解码热路径，也**不是**「不在热路径」。
解码走 `DecompressTo(Stream, Span<byte>)` → BCL `ZLibStream`（Adler 在 BCL 内部校验，不经本库）；
编码走 `CompressRaw` → `Adler32Stream.Write` → 逐字节 `Adler32.Update`。
该标量循环有 `s1 += b[i]; s2 += s1;` 的跨迭代依赖链，实测 1.62 GB/s（NEON 版 7.81 GB/s）。

### 2.1 影响最大的三条链路

- **JPEG 解码**：`JpegFrameState` 的 `Sse2.IsSupported` 门控使 `TryDecodeInterleavedYCbCrSimd`
  永不进入 ⇒ 退回「平面 IDCT + 标量交织 + 双线性上采样」，**按 MCU 行的 `Parallel.For` 也一并失效**。
  x86 上这段是 decode total −49%。
- **JPEG 编码**：`FdctSupported = Sse2.IsSupported`、`ColorSupported = Ssse3.IsSupported` 均为 false
  ⇒ 丢掉色彩转换（x86 上占编码 total 64.5%）。
- **缩放**：Bilinear / Bicubic / Area 全部退标量 ⇒ x86 的 2.17~2.30x / 1.31x / 1.18~1.35x 全部归零。

## 3. 基线数字（123 项，中位数 ms）

### 3.1 huge 143.0 MP

| 操作 | 基线 ms | 备注 |
|---|---:|---|
| decode JPEG（渐进式） | **1213.27** | SIMD 重建 + 并行均失效 |
| grayscale rgb24→gray | 37.45 | 内存带宽受限 |
| resize 缩小 25% / 50%（Area） | 64.26 / 54.81 | 标量 |
| encode JPEG q=75 | 356.92 | 色彩转换退标量 |
| encode PNG | 2111.18 | 94% 是 deflate（BCL） |
| encode BMP | 77.41 | RGB→BGR 退标量 |
| encode WebP q=75 | 3552.40 | 原生 libwebp（上游已含 NEON） |
| encode GIF | 571.45 | 量化器 104 处 SIMD 全失效 |

### 3.2 large 12.58 MP / medium 1.92 MP

| 操作 | large | medium |
|---|---:|---:|
| decode JPEG | 96.71 | 19.50 |
| decode PNG | 48.52 | — |
| decode GIF / BMP / WebP | 44.00 / 5.81 / 38.38 | — |
| encode JPEG q75 | 32.24 | 5.01 |
| encode PNG | 227.08 | 33.30 |
| encode GIF | 55.40 | — |
| encode WebP | 335.28 | — |
| grayscale | 4.11 | — |
| resize 缩小 50% | 6.50 | 2.45（Bilinear） |
| resize 放大 200%（auto→Bicubic） | — | 18.87 |
| resize 放大 200%（**Bilinear**） | — | **33.49** |

> `upscale-200%-bilinear` 比 `auto` 慢 77% 是 ARM 退化最直观的旁证：x86 上 Bilinear 是最快的一档
> （比 Bicubic 快约 2x），ARM 上因 `Ssse3 && Sse41` 不成立反成最慢。

## 4. 调优优先级（依据绝对耗时 × SIMD 可恢复比例）

| 优先级 | 目标 | 依据 |
|---|---|---|
| **P0** | JPEG 解码 SIMD 交织重建 + MCU 行并行 | huge 1213 ms；x86 该项 −49% |
| **P0** | GIF 量化器（`OctreeQuantizer`） | huge 571 ms，实测量化占编码 73% |
| **P1** | JPEG 编码色彩转换 + FDCT | 色彩转换占编码 total 64.5% |
| **P1** | 缩放（Bilinear / Bicubic / Area） | x86 2.2x / 1.3x / 1.2–1.35x |
| **P2** | `SimdHelper` 色彩转换与灰度 | Gray8→Rgb24 4.44x 等 |
| **P2** | PNG 解码反滤波 | −17% ~ −35% |
| **P3** | BMP RGB↔BGR | 绝对值小 |
| **—** | PNG 编码 / WebP | 瓶颈是 deflate / 原生 libwebp |

语义不能想当然的几处：`Sse2.PackUnsignedSaturate`（→ UQXTN/UQXTN2）、
`Sse2.UnpackLow/High`（→ ZIP1/ZIP2）、`Sse41.MultiplyLow`（→ NEON `mul`）、
`Sse41.Blend`、`Sse2.ShiftRightLogical` 的 16 位语义、`Avx2` 256 位路径（ARM 无对应，保持 128 位）。

## 5. NEON `tbl` 与 x86 `pshufb` 语义等价性

| 掩码字节 m | x86 `pshufb` | NEON `tbl` |
|---|---|---|
| 0..15 | `table[m]` | `table[m]` |
| **16..127** | **`table[m & 0x0F]`**（低 4 位折叠） | **0**（越界归零） |
| 128..255 | 0（bit7 置位） | 0（索引 ≥ 16） |

`mask & 0x8F` 一步即可让 `tbl` 完全等价于 `pshufb`：bit7 保留 ⇒ 归零语义，
bit4..6 清零 ⇒ 等价于 `& 0x0F`。代价是每次查表多一条 `and`；对库中现有全部掩码
（字节只落在 `0..15` 或 `0x80`）是**恒等变换**，不改变任何既有产物。
实现见 `Core/SimdCompat.ShuffleBytes`；`ShuffleBytesRaw` 是零开销版，调用方须自证掩码合法。

**`EXT` 方向**（写反过一次，导致 PNG 去滤波产物错乱、6 个测试失败）：

| 表达式 | 等价指令 |
|---|---|
| `ExtractVector128(v, zero, n)` | `psrldq(v, n)` |
| `ExtractVector128(zero, v, 16-n)` | `pslldq(v, n)` |

`n = 0` 时 `16-0 = 16` 超界会抛异常，而 `pslldq x, 0` 是合法恒等操作，必须单独处理。

## 6. 验收口径

1. 取**中位数**，不取单次、不取 min/max。
2. **产物一致性**：123 项测试的 `hash` 与 `.perf/results/arm-baseline.csv` 逐字节一致（例外见 §6.1）。
3. **同构建内 A/B**：细分量级比较用 `static readonly` 开关切在同一份二进制里。
4. **不许改 x86 结果**：ARM 优化不得改变 x86 的行为与产物（复测见 §10）。

### 6.1 产物一致性的已知例外（5 项 JPEG 解码，且不成立是正确行为）

`decode jpeg @ tiny/small/medium/large` 与 `decode jpeg-progressive @ huge` 共 5 项的产物与 ARM 标量基线不同。
根因是 SIMD 交织重建路径用「色度复制」上采样、标量路径用「双线性」——这在引入该层的 x86 上就存在，
ARM 基线之所以不同，恰恰是因为这段 SIMD 被整条跳过。移植后 ARM 走 SIMD ⇒ **产物向 x86 收敛**。

隔离验证：`SimdCompat.ForceScalar` 强制标量 ⇒ 4 个尺寸精确复现基线 hash；放开 ⇒ 全部不同（含 tiny）。
4:4:4 样本差异 48,159 B / maxAbs 5，自造 4:2:0 样本 794,239 B / maxAbs 110（4:4:4 无色度上采样，差一个量级）。

### 6.2 热机口径：关掉分层编译，而不是靠「多热身几百次」

固定热身 40 次的实测：tier-0→tier-1 阈值落在 **80~150 次**之间，40 次远远不够。
而且这条偏差**专门打击优化后的构建**：向量路径 tier-0 代码体量大（大量 `AggressiveInlining` 包装），
实测同一算子基线 tier-0 3.19 ms → 稳态 0.94 ms（3.4x），优化后 9.04 ms → 0.57 ms（**15.9x**）。
于是 40 次窗口里基线已接近稳态、优化后仍停在 tier-0，tiny/small 全项被误判为变慢
（测出 `upscale-200%@tiny 0.271x` 这类物理不可能的数字）。

**修法**：在 `SicBench.csproj` 设 `<TieredCompilation>false</TieredCompilation>`，
热身固定 8 次（上限 800 ms）即可。实测与「热身 150~1000 次」得到的 0.55~0.60 ms 完全一致，
整轮 A/B 从 ~15 min 降到 3 min 58 s。边界：它比的是稳态吞吐，短生命周期进程里向量路径 tier-0 更慢是真实代价。

## 7. 调优后结果（A/B，2026-10-08）

`tools/perf/run-ab.sh 4fd37f3 2 1.0`，两侧各 2 轮交替、跨轮取中位数。

| 加速比区间 | 项数 | 占比 |
|---|---:|---:|
| ≥ 1.5x | 30 | 24.4% |
| 1.07 ~ 1.5x | 28 | 22.8% |
| 0.93 ~ 1.07x（含未改动项） | 62 | 50.4% |
| < 0.93x | 3 | 2.4% |

| 链路 | 项 | 基线 | 调优后 | 加速 |
|---|---|---:|---:|---:|
| JPEG 解码 | `decode/jpeg@large` | 102.48 | 17.33 | **5.91x** |
| JPEG 解码 | `decode/jpeg@medium` | 16.77 | 3.59 | **4.68x** |
| JPEG 解码（渐进式） | `@huge` | 1329.97 | 356.62 | **3.73x** |
| JPEG 解码 | `@small` | 4.48 | 1.44 | **3.11x** |
| RGB→灰度 | `@tiny` | 0.07 | 0.02 | 3.03x |
| WebP 解码 | `@tiny` | 0.66 | 0.26 | 2.50x |
| BMP 编码 | `@medium` | 1.05 | 0.49 | 2.14x |
| 双线性放大 200% | `@medium` | 30.91 | 14.97 | **2.06x** |
| JPEG 编码 q75 | `@huge` | 343.05 | 174.91 | **1.96x** |
| 灰度缩放 200% | `@medium` | 20.25 | 11.02 | 1.84x |
| 双三次放大 200% | `@small` | 4.84 | 3.01 | 1.61x |
| PNG 编码（Adler32） | `@large` / `@medium` / `@huge` | — | — | 1.084x / 1.096x / 1.093x |
| GIF 编解码 | `encode@small` / `decode@medium` | — | — | 1.100x / 1.073x |

结构性说明：

- JPEG 解码 3~6x 是最大单项收益，等于把 x86 上整条 SIMD 重建 + 并行找回来。
- PNG 编码 +8~10% **全部来自 Adler32**；PNG 解码无收益（走 BCL `ZLibStream`）。
- GIF 只有 +3%~+12%：ARM 上的标量回退本就是查表实现，效率不差，天花板低，不如实夸大。
- 产物：118 项逐字节一致，5 项为 §6.1 的继承差异。

**三个 < 0.93x 项的取证**（诚实记录）：

| 项 | 判定 |
|---|---|
| `resize/resizefit-1024@medium` | 两轮方向相反，r1 的 min 与基线完全相同 ⇒ 双峰噪声 |
| `decode-gray/png8@medium` | 两侧 `min_ms` 几乎相同（4.641 vs 4.616）⇒ 单轮离群值拉高中位数 |
| `encode/jpeg-q90@medium` | 两轮同向偏慢 9%，但同路径 q75 为 1.185x ⇒ 存疑，**保留观察**，不宣称已解释 |

## 8. ARM 专属指令候选清单（M4 实测判决）

下表全部 .NET API 名称是用反射读本机 .NET 10 程序集枚举出来的，不是凭记忆写的
（注意 `AdvSimd.Arm64` 是嵌套类，反射全名要写 `AdvSimd+Arm64`）。

| # | 指令 | .NET API | x86 对应 | 本库落点 | 判决 |
|---|---|---|---|---|---|
| 1 | CRC32B/H/W/X | `Crc32.ComputeCrc32(uint, byte/ushort/uint)` | `Sse42.Crc32` 是 **Castagnoli**，算不了 IEEE | `Crc32.cs` | ✅ **已落地**：内核 2.42~3.00x |
| 2 | LD2/3/4 解交织 | `AdvSimd.Arm64.Load3xVector128AndUnzip` 等 | 无 | `OctreeQuantizer`、JPEG `RgbToYCbCr`/`FillMcu420`、`BmpReader` | ❌ 实测**慢 9.4%**（§8.2） |
| 3 | ST2/3/4 交织存储 | `StoreVectorAndZip` | 无 | JPEG 解码交织写、`gray8→rgb24` | 与 #2 配套，已随 #2 失去前提 |
| 4 | SQRDMLAH/SQRDMLSH | `Rdm.MultiplyRoundedDoublingAndAddSaturateHigh` | 无（需 4~5 条拼） | `FastIDCT`、`SimdJpeg*Pipeline` | ❌ 现有常量非 Q15，不一致率 100%（§8.3） |
| 5 | UZP1/2、TRN1/2 | `UnzipEven/Odd`、`TransposeEven/Odd` | 无（punpck 是 zip 方向） | RGB→planar 第二级、4:2:0 色度抽取 | P1，中等收益 |
| 6 | ADDV/SMAXV/SMINV/SADALP | `AddAcross` / `MaxAcross` / `AddAcrossWidening` | 无单指令跨 lane 归约 | Adler32（已用）、Area/box 横向求和、直方图 | P1，收益小但零风险 |
| 7 | SABA/UABA | `AbsoluteDifferenceAdd` | `psadbw` 只能按 8 字节分组求和 | `Quantizer.cs` 最近色搜索（**非默认路径**） | P1，仅救 Wu 量化器 |
| 8 | BSL/BIC/ORN | `BitwiseSelect` / `BitwiseClear` / `OrNot` | 需 `pand/pandn/por` 三条 | 所有 mask+blend（clamp、饱和、反滤波） | P2，<3%，顺手做 |
| 9 | SLI/SRI | `ShiftLeftAndInsert` / `ShiftRightAndInsert` | 无 | `LzwEncoder` 变长码打包、`LzwDecoder` | P2，变长码串行，最低 |
| 10 | RBIT | `ReverseElementBits` | 无 | 只剩 LZW 位序（PNG 只实现 8/16-bit，无子字节展开） | 记录 |
| 11 | CNT popcount | `AdvSimd.PopCount` | 无（本机无 VPOPCNTDQ） | 库内暂无落点 | 记录 |
| 12 | PMULL | `Aes.PolynomialMultiplyWidening*` | `Pclmulqdq` 只有 2×64 位 | 仅供 x86 复刻 CLMUL 版 CRC 时参考 | 备选 |

### 8.1 CRC32 硬件化（唯一成立的条目）

**为什么是 ARM 专属**：PNG 用 CRC-32/ISO-HDLC（IEEE，`0xEDB88320`）。x86 的 `SSE4.2 CRC32`
实现的是 Castagnoli（CRC-32C），**算不出 PNG 要的值**；x86 想加速只能手写 CLMUL + Barrett 归约。
ARM 的 `crc32b/h/w/x` 正是 IEEE 多项式。

实测：`"123456789"` → `0xCBF43926`（IEEE ✓），Crc32C → `0xE3069283`（即 x86 唯一有的那档）。
内核吞吐（7 轮中位）：8 MB 表驱动 2.17 → 硬件 5.79 GB/s（2.67x）、64 KB 1.69 → 4.10（2.42x）、
1 KB 1.14 → 3.41（3.00x）。端到端 `encode/png` 全部正向 +0.6%~+6.1%（与「CRC 占编码 2.9%」量级吻合），
产物 hash 17/17 一致。

**解码侧默认不执行 CRC**（`PngDecoder.ValidateChunkCrc` 默认 false，`ImageFrame` 默认路径不开校验），
所以「解码也有收益」的推算是落空的。想让解码也吃到，前置决策是把 `ValidateChunkCrc` 默认改 true
——那是**行为变更**（会拒绝损坏文件），与本条优化解耦。

**三个必须写进代码注释的坑**（已在 `Crc32.cs` 处理）：

1. 守卫必须用 `AdvSimd.Arm64.IsSupported`，**绝不能用 `Crc32.IsSupported`**——后者在 x86 上也是
   `true`，映射到 `SSE4.2 crc32` = Castagnoli，会静默产出错误 CRC。
2. .NET **没有 `crc32x`（ulong）重载**，只有 byte/ushort/uint 三个宽度 ⇒ 8 字节要用两次 `crc32w`。
3. 命名冲突：本库自己的 `SharpImageConverter.Crc32` 会遮蔽 Arm 的同名类，必须 `using` 起别名。

### 8.2 LD3 解交织：27 条比 1 条快，但实测慢 9.4%

> 保留错误推理是为了记住教训：**只数指令条数、没算单条成本**。

原稿立论：NEON 上解交织一次 16 像素（48 字节）要 `3 ldr + 9 tbl + 9 and + 6 orr = 27` 条
（`and` 是 `pshufb` 掩码归一化带来的），`LD3` 一条就能替换，收益应远大于估计。
**漏了单条成本**：M4 的 `tbl` 吞吐很高，而 `LD3` 是多周期结构化 load。

微基准（48 字节/次，中位数）：

| stride | LD3 GB/s | TBL GB/s | LD3/TBL |
|---:|---:|---:|---:|
| 48（对齐） | 63.17 | **86.07** | 0.734 |
| 51（典型 RGB24 行尾错位） | 67.83 | 81.67 | 0.830 |
| 45 | 65.99 | 86.59 | 0.762 |
| 33 | 72.17 | 97.98 | 0.737 |

端到端隔离 A/B（同进程同二进制，唯一变量 `SimdCompat.DisableUnzipLoad`，4096×3072，9 轮中位）：
`pshufb` 4.689 ms vs LD3 5.131 ms ⇒ **0.914x**，产物逐字节一致。
跨构建 A/B 也印证：`encode/gif@huge` +1.1%（噪声内）。

**处置**：不落地。解交织仍走 `tbl`/`pshufb`，代码收拢进 `SimdCompat.LoadRgb24Unzip3` 便于统一维护；
`DisableUnzipLoad` 开关保留在库里（默认 false），它是这条否决结论的**可复现凭据**。

**移植性提醒**：LD3/TBL 的吞吐比是**微架构相关**的。Neoverse（Graviton）与 Cortex-A 上的实现与 M 系不同，
比例可能反转 —— 换平台须重跑上表，不能沿用 M4 结论。

### 8.3 RDM（SQRDMLAH）：语义是 Q15 取高半，本库常量不是 Q15

原稿写 `acc = saturate(acc + 2·a·b)`，**这是错的**。实测（a/b/acc 取多组值）反推出真实语义是
**取 32 位乘积的高 16 位**：

```
SQRDMLAH(acc, a, b) = sat16( acc + ((2·a·b + 0x8000) >> 16) )
```

即 **Q15 分数乘法**（doubling + 固定 +0.5 舍入 + 饱和，返回高半）。.NET 方法名里的 `High` 已提示。

于是判据只剩一条：**现有定点常量是不是 Q15**。查源码：SIMD IDCT 与标量 IDCT 都是 `ConstBits = 13`
（`Fix_0_541196100 = 4433 = 0.5412 × 8192`），YCbCr→RGB 是 `ColorShift = 16`，RGB→YCbCr 是纯整数系数（Q0）。
**三条路径全不是 Q15 ⇒ 直接放弃，无需逐位对拍。**

即便强行重标为 Q15 也实测不一致：20000 个样本与 `(Q13 乘 >> 13)` **不一致 19996 次（100%）**，最大偏差 3785。
三层根因任一层即足以否决：

1. 每个乘加点各舍入一次，而现有内核全部乘加做完只 `Descale` 一次；
2. 中间值被 sat 到 int16，现有 pass1 的中间和是 32 位（可达 10^7），量程差两个数量级；
3. Q13→Q15 重标要连带改 `ConstBits`/`Pass1Shift`/`Pass2Shift`，等于重写整个内核定标。

**处置**：保留原路径。不能为了用上指令而改变 JPEG 产物。

### 8.4 其余条目

- **UZP/TRN**：常与 LD3 配套做第二级拆分（从 R/G/B 平面再抽 4:2:0 色度），单独用收益中等。
- **ADDV/SMAXV**：Adler32 已用上；剩余落点是 Area 缩放横向求和与直方图。
- **SABA/UABA**：只救 `Quantizer.cs` 的 Wu 最近色搜索；默认量化器是 `OctreeBayer`，**默认路径不受益**，
  先确认要不要救 Wu 再动手。
- **BSL/BIC/ORN**：把 mask+blend 三条并成一条，零风险可批量替换，但整体 <3%，属「顺手做」。
- **RBIT**：本库 PNG 只实现 8/16-bit，没有 1/2/4-bit 子字节展开，PNG 侧无落点。

## 9. 明确排除（别浪费时间）

| 项 | 结论 |
|---|---|
| **SVE / SVE2** | 实测 False ⇒ 32768 项 `_mapLut` 的 gather 彻底无解 |
| **SME** | 硬件有，.NET 无 API |
| **UDOT/SDOT 用于 resize** | 与 x86 AVX-VNNI 同类（都是整型点积）。VNNI 量化 resize 实测比浮点**慢 4 倍**（见 PerfReport.md §4），且原设想「配合 LD3/UZP 批量化 gather」的后路，其前提 LD3 已在 M4 被证伪 |
| **32768 项 `_mapLut` 向量 gather** | AVX2 无可用指令；NEON `TBL` 最多 4 张表 = 64 字节（`VectorTableLookup` 只有 2/3/4 表共 8 个重载），同样无解 |
| **PNG 编码主体 / WebP** | 瓶颈是 BCL deflate 与原生 libwebp |
| **Adler32** | 已 NEON 化到 7.81 GB/s，无剩余空间 |

## 10. 方法论教训

1. **「指令数比」不能当立项依据**。算出 27:1 的悬殊比，实测是 0.91:1，必须配一条吞吐微基准。
2. **跨构建 A/B 分不出细分量级的信号**。Apple Silicon 的 P/E 核调度漂移能造出 ±26% 的假变化
   （同一条未改动的行在不同轮里差 2.1 倍）；能定性的靠**同进程同二进制交替测**，噪声地板 <1%。
3. **隔离 harness 要复刻真实内核**，不能用简化版：CRC 用真实 API、LD3 用 `OctreeQuantizer.MapDirectSimd`
   的完整内核（去掉 LUT gather 以聚焦解交织段）。
4. **x86 侧必须保持 1:1**：任何进 `SimdCompat.cs` 的封装，x86 分支仍是原来的内在函数；
   改完重跑 `python .perf/x86-ab.py`（5 轮全量），参考值几何平均 1.008x / 1.006x、噪声地板 12%。
   只要仍在 1.00 附近的噪声带内，就算 ARM 改动没有波及 x86。
