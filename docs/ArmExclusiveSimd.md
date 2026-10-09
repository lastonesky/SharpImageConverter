# ARM 专属加速指令候选清单（M4 实测校准版）

> 状态：**仅调研与记录，未改动任何生产代码**。
> 本文列出的全部 .NET API 名称，是用反射直接读本机 .NET 10 程序集
> （`System.Runtime.Intrinsics.Arm.*`）枚举出来的，不是凭记忆写的；
> 落点（文件:行）来自 `6047811`，并已在 HEAD `c931bec` 上复核仍然有效。
>
> **2026-10-09 已在 Apple M4（arm64，.NET 10.0.101）上跑完探测并校准本表**，
> 逐条结果见 `.perf/results/verify-ArmExclusiveSimd-20261009.md`。
> 校准改动了四处：**§3.1 CRC 前提纠错 + 定级与门槛重判**、**§3.3 SQRDMLAH 语义更正**、
> **§3.2 指令数由 18 条更正为 27 条**、**§5-2 准入门槛由单一 7% 改为双判据**。
> 凡标注「✅ 实测」的都是本机跑出来的数字，其余仍是预估或引用值。
>
> **2026-10-09 落地结果（第二轮实测，已改写结论）**：按 §6 顺序实做三条，
> 逐条结果见 `.perf/results/arm-optimization-20261009.md`。**三条里只有一条成立**：
>
> | 条目 | 原判| ✅ 实测结论 | 处置 |
> |---|---|---|---|
> | §3.2 LD3 解交织 | P0，估GIF 编码 +10~30% | **否决**：M4 上 LD3 比 tbl **慢 9.4%** | 不落地，保留 tbl |
> | §3.1 CRC 硬件化 | P1，估内核 2.88x | **成立**：实测 **2.42~3.00x**，端到端 encode 全线正向 | 已落地 |
> | §3.3 RDM | P0，估定点段 1.2~1.5x | **否决**：与现有 Q13 内核**不一致率 100%**，最大偏差 3785 | 不落地，保留原路径 |
>
> **最重要的方法论教训**：§3.2 的"27 条 → 1 条"推理**只数指令条数、没算单条成本**。
> M4 的 `tbl` 吞吐远高于 `LD3`（多周期结构化 load），因此条数少 27 倍的那条路反而更慢。
> 这类"指令数比"必须配一个实测吞吐比才能立项。

---

## 0. 上机第一步（已完成）

```bash
cd SharpImageConverter
dotnet run .perf/arm-api/probe.cs      # 约 30 秒
```

脚本在 `.perf/arm-api/probe.cs`（纯探测 + 微基准，不引用、不修改 `src/`）：

| 输出段 | 回答的问题 | M4 实测结果 |
|---|---|---|
| §1 能力探测 | M4 上有哪些扩展可用；**尤其是 SVE 到底支不支持** | **Sve=False、Sve2=False**；其余全部 True |
| §2 CRC 多项式判定 | 硬件 `CRC32` 是否就是 PNG 要的 IEEE 多项式（用 `"123456789" → 0xCBF43926` 标准检验值判定） | IEEE `0xCBF43926` **一致**；Crc32C `0xE3069283` |
| §3 CRC 吞吐 | 硬件 `crc32x` vs 现有 slice-by-8 表驱动，各多少 GB/s | slice-by-8 **0.76** GB/s、硬件 **2.19** GB/s（**2.88x**） |
| §4 LD3 解交织 | 正确性小样 + 吞吐（48 字节/次） | R/G/B 与期望逐字节一致；**10.17 GB/s** |
| §5 SQRDMLAH | 语义抽样 | 实测为**取高 16 位**，非 `2ab+acc`（详见 §3.3） |

---

## 1. 能力基线

`docs/ArmPerfBaseline.md §1.1` 记录过一份，下面是 2026-10-09 在 M4 上的完整复测
（含 `ArmPerfBaseline` 未列的 `Sve/Sve2/Sha*`，由 `probe.cs` §1 输出）：

```
AdvSimd=True  AdvSimd.Arm64=True  Crc32=True  Crc32.Arm64=True  Aes=True
Dp=True  Rdm=True  Sha1=True  Sha256=True
Sve=False  Sve2=False
Vector<byte>.Count=16
```

**原先待确认的两条，现已定论**：

| 项 | ✅ 实测 | 影响 |
|---|---|---|
| `Sve` / `Sve2` | **False**（`.NET 10` 已暴露这两个类，需 `#pragma warning disable SYSLIB5003`，但 M4 不实现） | 确认为 `False` ⇒ `OctreeQuantizer` 那个 32768 项 LUT 的 gather **无解**（见 §4），不必再议 |
| SME（矩阵引擎） | M4 硬件有，但 **.NET 未暴露任何 SME API** | 无用，别调研 |

---

## 2. 候选清单总表

"x86 对应"列是判断"是否 ARM 专属"的关键：**空白 = x86 上没有等价指令**。

> ✅ **2026-10-09 已在本机运行时程序集**（`System.Private.CoreLib` 10.0.1，
> 即 `/usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.1`）上逐个反射核验下表 API，
> **全部存在**（`Crc32.ComputeCrc32` 3 重载、`Load2x/3x/4xVector128AndUnzip` 各 10 重载、
> `StoreVectorAndZip` 30/21 重载、`UnzipEven/Odd` 与 `TransposeEven/Odd` 各 17 重载、
> `AddAcross` 系各 10~11 重载、`AbsoluteDifferenceAdd` 12 重载、`BitwiseSelect/Clear/OrNot`
> 各 20 重载、`ShiftLeft/RightAndInsert` 各 14 重载、`ReverseElementBits`/`PopCount` 各 4 重载、
> `PolynomialMultiplyWideningLower/Upper` 各 2 重载）。
> 反射时注意：`AdvSimd.Arm64` 是**嵌套类**，全名要写 `AdvSimd+Arm64`，写成 `AdvSimd.Arm64` 会查不到类型。

| # | 指令 | .NET API（已枚举确认） | x86 对应 | 本库落点 | 预估 | 优先级 |
|---|---|---|---|---|---|---|
| 1 | CRC32B/H/W/X | `Crc32.ComputeCrc32(uint, byte/ushort/uint)` | `Sse42.Crc32` 是 **Castagnoli**，算不了 IEEE | `Crc32.cs` → `PngWriter.cs:291`、`PngDecoder.cs:375/420` | ✅**已落地**：实测 **2.42~3.00x**；端到端 encode 全线正向 +0.6~6.1% | ✅ **完成** |
| 2 | LD2/LD3/LD4 解交织加载 | `AdvSimd.Arm64.Load3xVector128AndUnzip(byte*)`、`Load2x/4xVector128AndUnzip` | **无** | `OctreeQuantizer.cs:614-619`、`:771-773`、JPEG `RgbToYCbCr`/`FillMcu420`、`BmpReader.cs:485` | ❌**已实测否决**：M4 上比 tbl **慢 9.4%**（§3.2） | **撤销** |
| 3 | ST2/ST3/ST4 交织存储 | `AdvSimd.Arm64.StoreVectorAndZip`、`AdvSimd.StoreVectorAndZip` | **无** | JPEG 解码交织写出、`gray8→rgb24`、`rgba32→rgb24` | 与 #2 配套 | **P0** |
| 4 | SQRDMLAH/SQRDMLSH | `Rdm.MultiplyRoundedDoublingAndAddSaturateHigh`、`MultiplyRoundedDoublingBySelectedScalarAndAddSaturateHigh`（注意 `BySelectedScalar` 在名字**中间**，不是后缀） | **无**（需 4~5 条拼） | `FastIDCT.cs`、`SimdJpegPipeline.cs`、`SimdJpegEncodePipeline.cs` | ❌**已实测否决**：本库是 Q13/Q16/Q0，非 Q15，不一致率 100%（§3.3.1） | **撤销** |
| 5 | UZP1/2、TRN1/2 | `AdvSimd.Arm64.UnzipEven/Odd`、`TransposeEven/Odd` | **无**（punpck 是 zip 方向） | RGB→planar 第二级、4:2:0 色度抽取、`SimdHelper` 灰度/通道转换 | 中 | P1 |
| 6 | ADDV/SMAXV/SMINV/SADALP | `AdvSimd.Arm64.AddAcross/MaxAcross/MinAcross/AddAcrossWidening` | **无**单指令跨 lane 归约（只有 `phminposuw` 特例） | `Adler32`（已用）、Area/box 缩放横向求和、直方图 | 小 | P1 |
| 7 | SABA/UABA/UABAL | `AdvSimd.AbsoluteDifferenceAdd`、`AbsoluteDifferenceWideningLowerAndAdd` | `psadbw` 只能按 8 字节分组求和 | `Quantizer.cs:340-345` 最近色搜索（Wu 量化器，非默认） | 仅 Wu 路径 | P1 |
| 8 | BSL/BIC/ORN | `AdvSimd.BitwiseSelect`、`BitwiseClear`、`OrNot` | 需 `pand/pandn/por` 三条 / 额外 `not` | 所有 mask+blend（resize clamp、量化饱和、PNG 反滤波） | <3%，零风险 | P2 |
| 9 | SLI/SRI 位域插入 | `AdvSimd.ShiftLeftAndInsert`、`ShiftRightAndInsert` | **无** | `LzwEncoder.cs:266` 变长码打包、`LzwDecoder.cs:75` | 低（变长码串行） | P2 |
| 10 | RBIT 向量位反转 | `AdvSimd.Arm64.ReverseElementBits` | **无**（连标量位反转都没有） | LZW 码流位序；**PNG 侧无落点**（本库只实现 8/16-bit） | 低 | P2 |
| 11 | CNT 向量 popcount | `AdvSimd.PopCount` | **无**（本机 `AVX512F=False`，也无 VPOPCNTDQ） | 库内暂无落点（无二值形态学/位平面算子） | — | 记录 |
| 12 | PMULL（无进位乘） | `Aes.PolynomialMultiplyWideningLower/Upper` | `Pclmulqdq` 只有 2×64 位 | x86 侧 CLMUL 版 CRC（#1 在 ARM 上已可用，此条仅供 x86 复刻时用） | 备选 | 记录 |

---

## 3. 分条说明

### 3.1 【P1，值得做】硬件 CRC32 —— 内核 2.88x，但端到端拿不到 7%

**为什么是 ARM 专属**（✅ 实测成立）：PNG 用 CRC-32/ISO-HDLC（IEEE，多项式 `0xEDB88320`）。
x86 的 `SSE4.2 CRC32` 实现的是 **Castagnoli（CRC-32C）**多项式，**算不出 PNG 要的值**；
x86 想加速只能手写 CLMUL + Barrett 归约（代码量大、要对齐 16 字节块）。
ARM 的 `crc32b/h/w/x` 正是 IEEE 多项式，`crc32cb/…` 才是 Castagnoli —— 两档都有。

probe §2 实测：`"123456789"` → 硬件 IEEE = `0xCBF43926`（与参考实现一致 ✓），
硬件 Crc32C = `0xE3069283`（即 Castagnoli，正是 x86 唯一有的那一档）。
⇒ **"可直接替换"在多项式层面确认无误**。

**落点**：
- `src/Formats/Png/Crc32.cs` — 当前 slice-by-8 表驱动（`Crc32Optimized`）
- 编码：`PngWriter.cs:291-292`，每个 chunk 的 `type + data`
- 解码：`PngDecoder.cs:375 / 420`，IDAT 校验

**❌ 原稿在此处的前提错了**：原稿写"无参构造路径默认开启校验"，实际

```csharp
// PngDecoder.cs:51
public bool ValidateChunkCrc { get; set; }        // 无初始值 ⇒ 默认 false
// ImageFrame.cs:370
var decoder = new PngDecoder();                   // 默认路径不开校验
```

⇒ **解码侧默认根本不执行 CRC**，原稿"解码 ~10%、可能过门槛"的推算落空。

**✅ 实测吞吐（probe §3，64 MB）**：slice-by-8 **0.76 GB/s**（原稿按 x86 外推成 1.0 GB/s，
绝对值偏低约 2 倍）、硬件 `crc32x` **2.19 GB/s** ⇒ 加速 **2.88x**。

**关于门槛（判定已修正）**：原稿自设"吞吐 ≥ 3× 才做"，实测 2.88× 差一点点。
经复核认为**这个门槛本身设得过严**：CRC 是纯吞吐型内核，无精度、无舍入、无饱和语义差异，
只要多项式对就**必然逐位一致**，2× 以上已经是数量级级别的改善，没必要卡到 3×。
⇒ §5-2 的准入门槛改为**双判据**：`吞吐 ≥ 2×` **或** `端到端 ≥ 7%`，**满足其一即准入**。
本条走的是第一条。

按 `large.png` 实际压缩后 7.70 MB 重算端到端（**注意这是另一把尺子**）：

| 侧 | CRC 是否执行 | 现有 | 换硬件后 | 节省 | 占总耗时 | 端到端 7% |
|---|---|---:|---:|---:|---:|:--:|
| 编码 `encode/png@large`（≈225 ms） | **是**（`PngWriter` 无条件） | 10.1 ms | 3.5 ms | 6.6 ms | **2.9%** | ✗ |
| 解码 `decode/png@large`（≈51 ms） | **否**（默认关闭） | 0 ms | 0 ms | 0 ms | 0% | ✗ |

⇒ **结论：做，但验收口径要选对。**

- 判据用「吞吐量 ≥2×」（probe §3 复测即可），**不要拿端到端 A/B 当判据** —— 端到端只有 2.9%，
  会被 §5-2 的 7% 噪声地板盖住，得出"没效果"的错误结论。
- 改动成本极低：只动 `Crc32.cs` 一个文件，加一条 `Crc32.IsSupported` 分支即可，
  其余调用方（`PngWriter` / `PngDecoder`）**一行都不用改**。
- 想让解码侧也吃到这块收益（顺带获得损坏文件检测能力），前置决策是把 `ValidateChunkCrc`
  默认改为 `true` —— 那是**行为变更**（默认开始校验、会拒绝损坏文件），需单独评估，与本条优化解耦。

**✅ 落地实测（2026-10-09，已入库）**：

正确性（隔离 harness，非端到端反推）：

```
长度 0..4096 全部一致 = True（表驱动 vs 硬件，0 处不一致）
标准检验值 = 0xCBF43926   ✓
分块续算（4×1000 字节 Update）== 一次性（4000 字节）= True   ✓
```

吞吐（slice-by-8 vs 硬件，7 轮取中位数）：

| 尺寸 | slice-by-8 | 硬件 | 加速 |
|---:|---:|---:|---:|
| 8 MB | 2.17 GB/s | 5.79 GB/s | **2.67x** |
| 1 MB | 2.11 GB/s | 5.63 GB/s | **2.67x** |
| 64 KB | 1.69 GB/s | 4.10 GB/s | **2.42x** |
| 1 KB | 1.14 GB/s | 3.41 GB/s | **3.00x** |

⇒ **过「吞吐 ≥2×」判据**（各尺寸均 ≥2.4x）。

端到端 A/B（`encode/png`，3 轮全量迭代跨轮中位数，**产物 hash 17/17 全部一致**）：

| 项 | 基线 | 改后 | 加速 |
|---|---:|---:|---:|
| `encode-gray/png8@medium` | 12.40 | 11.69 | **1.061x** |
| `encode/png@medium` | 33.18 | 31.86 | **1.041x** |
| `encode/png@tiny` | 1.39 | 1.34 | **1.031x** |
| `encode/png@huge` | 2019.70 | 2004.41 | 1.008x |
| `encode/png@large` | 216.48 | 215.19 | 1.006x |

encode 侧**全部正向**（+0.6% ~ +6.1%），与 §3.1 预估的"约 2.9%"量级吻合。
decode 侧在 ±5% 内正负混杂，属噪声 —— 默认 `ValidateChunkCrc = false`，本就不执行 CRC。

**⚠️ 三个必须写进代码注释的坑（都已在 `Crc32.cs` 中处理）**：

1. **守卫必须用 `AdvSimd.Arm64.IsSupported`，绝不能用 `Crc32.IsSupported`**。
   后者在 x86 上也为 `true`，但映射到 `SSE4.2` 的 `crc32` 指令 = **Castagnoli**
   （检验值 `0xE3069283`），算不出 PNG 要的 IEEE 值。用错守卫会静默产出错误 CRC。
2. **.NET 没有 `crc32x`（ulong）重载**。`Crc32.ComputeCrc32` 只有
   `(uint, byte)` / `(uint, ushort)` / `(uint, uint)` 三个宽度 ⇒ 8字节要用两次 `crc32w`。
   （文档早期版本写"crc32x 吞吐最高"，实测发现 API 层根本没暴露这一档。）
3. **命名冲突**：本库自己的 `SharpImageConverter.Crc32` 会遮蔽
   `System.Runtime.Intrinsics.Arm.Crc32`，必须 `using ArmCrc32 = ...Crc32;` 起别名，
   否则 `Crc32.ComputeCrc32` 会解析到自己的类上（CS0117）。

**改动面**：只改 `Crc32.cs` 一个文件，新增 `Crc32Hardware` 类 + `IsHardwareSupported` 属性。
`PngWriter` / `PngDecoder` **一行未改**。

### 3.2 【❌ 已否决】LD3 解交织加载 —— 27 条比1 条快，但**慢 9.4%**

> **本节结论已被实测推翻，保留原文是为了记录"为什么错"。**
> 原稿的立论是"LD3 一条指令替换掉 27 条 ALU + 3 次加载，收益比估计更大"。
> **这个推理漏了单条成本**：M4 上 `tbl` 的吞吐很高，而 `LD3` 是一条多周期的
> 结构化 load，**字节/周期吞吐低于「3×ldr + 3×tbl + 2×orr」**。

**✅ 微基准实测（仅测解交织本身，48 字节/次，取中位数）**：

| stride | LD3 GB/s | TBL GB/s | LD3/TBL |
|---:|---:|---:|---:|
| 48（对齐） | 63.17 | **86.07** | **0.734** |
| 51（典型 RGB24 行尾错位） | 67.83 | 81.67 | 0.830 |
| 45 | 65.99 | 86.59 | 0.762 |
| 33 | 72.17 | 97.98 | 0.737 |

**✅ 端到端隔离 A/B**（同进程同二进制交替测，唯一变量是 `SimdCompat.DisableUnzipLoad`，
4096x3072，40 次预热踢掉 tier-0，9 轮取中位数）：

```
产物逐字节一致 = True (12,582,912 字节)
pshufb(旧) 中位 4.689 ms   全部: 4.533 4.606 4.642 4.654 4.689 4.864 4.929 4.946 4.969
LD3   (新) 中位 5.131 ms   全部: 5.029 5.064 5.119 5.123 5.131 5.194 5.254 5.436 5.491
⇒ 0.914x，即慢 9.4%
```

**跨构建 A/B 也印证**：`encode/gif@huge` 612.76→605.90ms（+1.1%，在噪声内），
`encode/gif@large` −1.3% —— 即端到端层面 LD3 没有可测收益。

**处置**：**不落地**。`OctreeQuantizer` 的两处解交织仍走 `tbl`/`pshufb`，
仅把代码收拢进 `SimdCompat.LoadRgb24Unzip3` 以便统一维护与复测。
`DisableUnzipLoad` 开关保留在库里（默认 `false`），它不是"预留功能"，
而是**这条否决结论的可复现凭据**——置 `true` 即可在本机重跑上面的 A/B。

**⚠️ 移植性提醒**：LD3/TBL 的吞吐比是**微架构相关**的。Neoverse（Graviton）
与 Cortex-A 系列上 LD3 的实现与 M 系不同，比例可能反转。若将来在那些平台上
重新评估，须重跑上表，而不是沿用 M4 的结论。

<details>
<summary>原稿全文（已失效，保留以备查）</summary>

**现状**（`OctreeQuantizer.cs:614-619`，一次 16 像素 = 48 字节）：

```
3 × LoadBytesPtr
+ 9 × ShuffleBytes（3 个平面 × 3 个源向量）
+ 6 × OrBytes（orr）
= 18 条（x86 口径）
```

**⚠️ ARM 上实际是 27 条，不是 18 条**：`SimdCompat.ShuffleBytes` 为了与 `pshufb` 逐位等价，
每条 shuffle 都要先做一次掩码归一化 `mask & 0x8F`（见 `SimdCompat.cs:119-127`），
于是 NEON 上每处 shuffle 是 **`tbl` + `and` 两条**：

```
3 × ldr  +  9 × tbl  +  9 × and  +  6 × orr  =  27 条
```

⇒ `LD3` 一条指令替换掉的是 **24 条 ALU + 3 次加载**，收益比原稿估计的更大。

**✅ 语义已实测**（probe §4）：`Load3xVector128AndUnzip` 一次读入 48 字节，
给出的 R/G/B 三平面与期望值逐字节一致（`R = 00 03 06 … 2D`），吞吐 **10.17 GB/s**。

**改法**：`AdvSimd.Arm64.Load3xVector128AndUnzip(byte*)` —— **一条指令**读入 48 字节并直接给出 3 个解交织后的 16 字节平面。
步长正好是 48 字节，与现有循环天然对齐。

**为什么 x86 做不到**：x86 没有解交织加载（`punpck*` 是交织方向），只能用 `pshufb` + `por` 拼。
顺带一提，M4 上 `tbl` 吞吐只有 2/cycle、`orr` 3/cycle，这 27 条指令在 ARM 上比在 x86 上更亏。

**同类落点**：JPEG 编码 `RgbToYCbCr` / `FillMcu420`（RGB24 → Y/Cb/Cr 三个平面）、
`BmpReader.cs:485` 的 BGR→RGB、`SimdHelper` 的通道转换。

**预估**：GIF 量化在 huge 档是 571 ms 的大头（x86 实测量化占 GIF 编码 73%），
解交织段指令数减半以上 ⇒ 整体 GIF 编码有望 +10~30%。

**风险**：尾部（<48 字节）处理要重做；`_mapLut` 查表逻辑不变，产物理论上逐字节一致。

### 3.3 【P0】RDM：SQRDMLAH / SQRDMLSH

**❌ 原稿语义写错了，✅ 实测更正如下。**

原稿写的是 `acc = saturate(acc + 2·a·b)`。**这是错的**。probe §5 实测
（a = 1000/2000/−3000/4000/100/−100/5/−5，b = 3/−3/2/−2/300/300/9/−9，acc = 7）：

```
SQRDMLAH                    = 7 7 7 7 8 6 7 7
手工 2ab+acc（无饱和/舍入）  = 6007 -11993 -11993 -15993 60007 -59993 97 97
```

两者差两个数量级。逐 lane 反推可确认真实语义是——**取 32 位乘积的高 16 位**：

```
SQRDMLAH(acc, a, b) = sat16( acc + ((2·a·b + 0x8000) >> 16) )
```

| a, b | 2·a·b | (2ab + 0x8000) >> 16 | +acc=7 |
|---|---:|---:|---:|
| 1000, 3 | 6000 | 0 | **7** ✓ |
| 100, 300 | 60000 | 1 | **8** ✓ |
| −100, 300 | −60000 | −1 | **6** ✓ |

即它是 **Q15 分数乘法**（doubling + 固定 +0.5 舍入常量 + 饱和，返回高半部分），
.NET 方法名里的 `High` 已经提示了这一点。x86 要用
`pmullw + pmulhw + 移位 + padd + packss` 四到五条拼，**且舍入语义不同**。

**落点**：`FastIDCT.cs`（`Fix_0_541196100` 一类定点常量）、`SimdJpegPipeline.cs` / `SimdJpegEncodePipeline.cs`
的 SIMD IDCT / FDCT、色彩转换的定点矩阵。这些都是纯乘加密集型内核。

**⚠️ 最大的坑（结论不变，理由更正）**：SQRDMLAH 的舍入与饱和语义与现有定点内核
**不保证逐位一致**，先对拍；若不一致，**保留原路径**，不要为了用上指令而改变产物
（与 §3.1 CRC 不同：CRC 只要多项式对就一定逐位一致，RDM 不一定）。

> 更正带来的正面信息：这个"取高 16 位"的语义**恰好就是定点 IDCT/FDCT 想要的**——
> 乘完直接是 Q15 结果、省一次移位。所以先判一件事即可：
> **现有定点常量是否按 Q15 存放**。是则很可能逐位对齐、收益可期；不是就直接放弃。
> 比原稿描述的"舍入语义不同"更有希望，值得优先做这一次对拍。

### 3.3.1 【❌ 已否决】判据结果：**现有常量不是 Q15，直接放弃**

按上面定的单一判据去查源码，答案是明确的：

| 内核 | 文件 | 定点格式 | 是否 Q15 |
|---|---|---|:--:|
| SIMD IDCT | `SimdJpegPipeline.cs:11` | `ConstBits = 13`（如 `Fix_0_541196100 = 4433` = 0.5412×**8192**） | ❌ Q13 |
| 标量 IDCT | `FastIDCT.cs:7` | 同上，`ConstBits = 13` | ❌ Q13 |
| YCbCr→RGB | `SimdJpegPipeline.cs:32` | `ColorShift = 16` | ❌ Q16 |
| RGB→YCbCr | `SimdJpegEncodePipeline.cs` | 纯整数系数（77/150/29/…），无小数位 | ❌ Q0 |

**三条路径全都不是 Q15 ⇒ 直接放弃**，不需要再做逐位对拍。

即便强行把常量重标为 Q15（`4433 << 2 = 17732`）来试，**实测也不一致**：

```
样本 20000（反量化后int32 系数，范围 ±2048）
与 (Q13 乘 >> 13) 不一致 19996 次 (100.0%)，最大偏差 3785
```

根因有三层，任一层都足以否决：
1. **每步都多一次舍入**。现有内核把全部乘加做完，最后统一 `Descale` 一次；
   SQRDMLAH 是**每个乘加点各舍入一次**，误差无法与"最后统一移位"对齐。
2. **中间值饱和到 int16**。现有 pass1 的中间和是 32 位（`(v2+v6) * 4433`
   量级可达 10^7），而 SQRDMLAH 输出被 sat 到 int16，量程差两个数量级。
3. **Q13→Q15 重标本身改变数值**。`<<2` 是精确的，但与之配套的
   `ConstBits`/`Pass1Shift`/`Pass2Shift` 全部要改，等于重写整个内核的定标。

**处置**：**保留原路径**。这与文档 §5-3「产物hash 必须逐字节一致」的约束一致 ——
不能为了用上一条指令而改变 JPEG 产物。RDM 这条**在本库的定点格式下不成立**。

### 3.4 【P1/P2】其余

- **UZP1/2 + TRN1/2**（`UnzipEven/Odd`、`TransposeEven/Odd`）：常与 LD3 配套做第二级拆分
  （例如从 R/G/B 平面再抽 4:2:0 色度）。单独用收益中等。
- **ADDV/SMAXV/SMINV/SADALP**：Adler32 已用上；剩余落点是 Area/box 缩放的横向求和与直方图统计，收益小但零风险。
- **SABA/UABA**（`AbsoluteDifferenceAdd`）：只救 `Quantizer.cs:340-345` 的 Wu 最近色搜索。
  默认量化器是 `OctreeBayer`（`GifAdapter.cs:18`），**默认路径不受益** —— 先确认要不要救 Wu 再动手。
- **BSL/BIC/ORN**：把 mask+blend 的三条并成一条，零风险、可批量替换，但整体 <3%，顺手做。
- **SLI/SRI**：LZW 变长码打包（`LzwEncoder.cs:266`）。LZW 有串行字典依赖，向量化困难，优先级最低。
- **RBIT**：本库 PNG 只实现 8/16-bit（`PngDecoder` 里只判 `BitDepth == 16`），**没有 1/2/4-bit 子字节展开**，
  所以 PNG 侧无落点；只剩 LZW 位序一处可能，低。

---

## 4. 明确排除（别浪费时间）

| 项 | 结论 |
|---|---|
| **SVE / SVE2** | ✅ **已实测 `False`**（2026-10-09，M4）。.NET 10 虽已暴露这两个类（实验性 API），但 M4 不实现 ⇒ 彻底排除。`OctreeQuantizer.cs:593` 注释里"AVX2 下无解"的 32768 项 LUT gather 也就确定无解了 |
| **SME** | M4 硬件有，.NET 无 API ⇒ 不可用 |
| **UDOT/SDOT 用于 resize** | `docs/VnniResizeFindings.md` 已证明：x86 上 VNNI 量化 resize **比浮点慢 4x**，瓶颈在逐像素 gather 与内存带宽，不在点积。`Dp.DotProduct` 是同一类指令 ⇒ **单独上 UDOT 大概率同样不划算**；原稿设想的"配合 LD3/UZP 把 gather 批量化"这条后路，其**前提 LD3 已在 M4 上被证伪**（§3.2，慢 9.4%）⇒ 这条路在 M4 上也随之封死 |
| **PNG 编码主体 / WebP** | 瓶颈是 BCL deflate 与原生 libwebp，不是本库代码 |
| **Adler32** | 已 NEON 化到 7.81 GB/s，无剩余空间 |
| **32768 项 `_mapLut` 的向量 gather** | `OctreeQuantizer.cs:593-594` 的注释已写明 AVX2 无解；NEON 的 `TBL` 最多只有 4 张表 = 64 字节（✅ 已由反射确认 `VectorTableLookup` 只有 2/3/4 表共 8 个重载），**同样无解**。而 SVE 已实测不可用 ⇒ 这条路彻底封死 |

补充说明：`AdvSimd.Arm64.VectorTableLookup` 的 2/3/4 表形式（TBL，最多 64 字节表）
确实比 x86 的 `pshufb`（最多 16 字节）强，但**本库目前没有落在 16~64 字节区间内的查表热路径**
（Bayer 抖动用的是 `offTab[y & 3]` 的 4 KB 表，已被 SIMD 加载覆盖），所以这条先记着，不排期。

---

## 5. 落地时的硬性约束（今天 x86 复测得出的）

1. **x86 分支必须保持 1:1**：任何进 `SimdCompat.cs` 的封装，x86 侧必须仍是原来的
   `Sse2./Ssse3./Sse41./Avx2.` 内在函数（今天已逐条核对，当前所有封装都满足）。
2. **准入门槛（2026-10-09 修正为双判据，满足其一即可）**、取**跨轮中位数**、
   同构建 A/B（用 `SimdCompat.ForceScalar` 或静态开关切）：
   - **吞吐 ≥ 2×**：适用于纯吞吐型、语义无差异的内核（CRC、Adler32 这一类）。
     原设 3× 过严，已放宽 —— 这类改动零风险且必然逐位一致，2× 即数量级改善。
   - **端到端 ≥ 7%**：适用于整个管线级别的改动（LD3、RDM 这一类）。
     7% 是本机 A/B 的噪声地板，低于它分不出信号。

   ⚠️ 两条判据**不能混用**：走"吞吐"判据的项（如 CRC）端到端往往只有 2~3%，
   拿端到端去判会误杀；反之亦然。立项时就先定好用哪一把尺子。
3. **123 项产物 hash 逐字节一致**（已知豁免只有 5 项 JPEG 解码，见 `ArmPerfBaseline.md §6.1`）。
4. **改完必须重跑 x86 复测**：现成脚本已就绪 ——
   `python .perf/x86-ab.py`（5 轮全量，双口径），参考值：几何平均 **1.008x / 1.006x**，噪声地板 12%。
   只要 x86 几何平均仍在 1.00 附近的噪声带内，就算 ARM 侧改动没有波及 x86。

---

## 6. 动手顺序（2026-10-09 实做后的最终状态）

原计划的三条已全部实做，**只有 CRC 一条成立**。当前状态：

| # | 条目 | 状态 | 说明 |
|---|---|---|---|
| 1 | ~~§3.2 LD3 解交织~~ | ❌ **已否决** | M4 上慢 9.4%（§3.2），代码收拢进 `SimdCompat.LoadRgb24Unzip3` 但走 tbl |
| 2 | §3.1 CRC 硬件化 | ✅ **已落地** | 内核 2.42~3.00x，encode 端到端全线正向，产物 hash 17/17 一致 |
| 3 | ~~§3.3 RDM~~ | ❌ **已否决** | 常量是 Q13/Q16/Q0 而非 Q15，不一致率 100%（§3.3.1） |

**如果还要继续做，剩下值得排期的是**：

1. **`BSL`/`BIC`/`ORN`**（`AdvSimd.BitwiseSelect`/`BitwiseClear`/`OrNot`）——
   把 mask+blend 的三条并成一条，零风险、可批量替换，但整体 <3%，属"顺手做"级别。
   本次未做是因为它需要扫全部 clamp/饱和点，收益低于改动面。
2. **`SLI`/`SRI`**（LZW 变长码打包）—— LZW 有串行字典依赖，向量化困难，最低优先级。
3. **`SABA`/`UABA`**（`AbsoluteDifferenceAdd`）—— 只救 Wu 量化器，而默认量化器
   是 `OctreeBayer`（`GifAdapter.cs:18`），**默认路径不受益**。先确认要不要救 Wu。
4. **`ADDV`/`SMAXV`/`SMINV`** —— Area/box 缩放横向求和与直方图，收益小但零风险。

**明确不要再评估的**（本轮已用硬证据排除）：

- **任何以 LD3/LD4 为前提的方案**（包括 §4 里"配合 LD3/UZP 批量化 gather"那条）——
  前提在 M4 上已被证伪，除非换平台并重测。
- **UDOT/SDOT 用于 resize** —— 维持 `docs/VnniResizeFindings.md` 的原判。
- **SVE / SVE2 / SME** —— 实测不可用。

**方法论沉淀（本轮最大收获）**：

1. **"指令数比"不能当立项依据**。§3.2 算出 27:1 的悬殊比，实测是 0.91:1。
   必须配一条**吞吐微基准**（见 §3.2 的表）才谈得上收益。
2. **跨构建 A/B 分不出 10% 量级的信号**。Apple Silicon 的 P/E 核调度漂移能造出
   ±26% 的假变化（同一条未改动的行在不同轮里差 2.1 倍，脚本注释里已记录）。
   本轮能定性的靠的是**同进程同二进制交替测**（唯一变量是一个静态开关），
   噪声地板压到 1% 以内。
3. **隔离 harness 要复刻真实内核，不能用简化版**。CRC 用真实 API、LD3 用
   `OctreeQuantizer.MapDirectSimd` 的完整内核（去掉 LUT gather 以聚焦解交织段），
   才拿到可信数字。

**2026-10-09 第二轮已验证/已推翻的部分**：LD3 vs TBL 吞吐比（§3.2，已推翻 LD3）、
RDM 语义与定点格式匹配性（§3.3.1，已否决）、CRC 内核吞吐与端到端（§3.1，已落地）。

**仍属预估/引用的部分**：SME 是否真的无 .NET API（未穷举全程序集）、
§4「UDOT/SDOT 用于 resize 不划算」（需实现后 A/B，`Dp.IsSupported=True` 指令可用）、
§4「Adler32 已 7.81 GB/s」（引自 `ArmPerfBaseline.md`）。

**已被实测取代的旧数字**：`tbl` 2/cycle 与 `orr` 3/cycle 出自原稿的引用值，
与 §3.2 实测的"tbl 路径 86 GB/s、显著快于 LD3 的 63 GB/s"方向一致但量级不同，
**以 §3.2 的实测为准**。
