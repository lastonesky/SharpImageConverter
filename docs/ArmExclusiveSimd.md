# ARM 专属加速指令候选清单（待 Mac mini 实测）

> 状态：**仅调研与记录，未改动任何生产代码**。
> 本文列出的全部 .NET API 名称，是用反射直接读本机 .NET 10 程序集
> （`System.Runtime.Intrinsics.Arm.*`）枚举出来的，不是凭记忆写的；
> 落点（文件:行）来自当前 `6047811`。
> 明天的目标不是"把清单全部实现"，而是**先用探测脚本拿到数字，再决定动哪几条**。

---

## 0. 明天上机第一步

```bash
cd SharpImageConverter
dotnet run .perf/arm-api/probe.cs
```

脚本已写好（`.perf/arm-api/probe.cs`，纯探测 + 微基准，不引用、不修改 `src/`）：

| 输出段 | 回答的问题 |
|---|---|
| §1 能力探测 | M4 上有哪些扩展可用；**尤其是 SVE 到底支不支持** |
| §2 CRC 多项式判定 | 硬件 `CRC32` 是否就是 PNG 要的 IEEE 多项式（用 `"123456789" → 0xCBF43926` 标准检验值判定） |
| §3 CRC 吞吐 | 硬件 `crc32x` vs 现有 slice-by-8 表驱动，各多少 GB/s |
| §4 LD3 解交织 | 正确性小样 + 吞吐（48 字节/次） |
| §5 SQRDMLAH | 语义抽样，确认与"2ab+acc"的差别（doubling + rounding + saturate） |

本机（x86）已验证脚本可编译可运行，参考实现算出的检验值 `0xCBF43926` 正确；
ARM 段会因 `IsSupported=false` 自动跳过，到 Mac 上才会真正执行。

---

## 1. 能力基线

`docs/ArmPerfBaseline.md §1.1` 已在 M4 上实测：

```
AdvSimd=True  AdvSimd.Arm64=True  Crc32=True  Aes=True  Dp=True  Rdm=True
```

**待明天确认的两条**：

| 项 | 现状 | 影响 |
|---|---|---|
| `Sve` / `Sve2` | .NET 10 **已暴露**这两个类（实验性 API，需 `#pragma warning disable SYSLIB5003`），但 Apple M4 是否实现 SVE 存疑（Apple 至今只上了 SME，未上 SVE） | 若为 `True`，`OctreeQuantizer` 那个 32768 项 LUT 的 gather 才有解（见 §4） |
| SME（矩阵引擎） | M4 硬件有，但 **.NET 未暴露任何 SME API** | 无用，别调研 |

---

## 2. 候选清单总表

"x86 对应"列是判断"是否 ARM 专属"的关键：**空白 = x86 上没有等价指令**。

| # | 指令 | .NET API（已枚举确认） | x86 对应 | 本库落点 | 预估 | 优先级 |
|---|---|---|---|---|---|---|
| 1 | CRC32B/H/W/X | `Crc32.ComputeCrc32(uint, byte/ushort/uint)` | `Sse42.Crc32` 是 **Castagnoli**，算不了 IEEE | `Crc32.cs` → `PngWriter.cs:291`、`PngDecoder.cs:375/420` | 解码侧可能 +5~10% | **P0** |
| 2 | LD2/LD3/LD4 解交织加载 | `AdvSimd.Arm64.Load3xVector128AndUnzip(byte*)`、`Load2x/4xVector128AndUnzip` | **无** | `OctreeQuantizer.cs:614-619`、`:771-773`、JPEG `RgbToYCbCr`/`FillMcu420`、`BmpReader.cs:485` | 解交织段 18 条→1 条 | **P0** |
| 3 | ST2/ST3/ST4 交织存储 | `AdvSimd.Arm64.StoreVectorAndZip`、`AdvSimd.StoreVectorAndZip` | **无** | JPEG 解码交织写出、`gray8→rgb24`、`rgba32→rgb24` | 与 #2 配套 | **P0** |
| 4 | SQRDMLAH/SQRDMLSH | `Rdm.MultiplyRoundedDoublingAndAddSaturateHigh`、`…BySelectedScalar…` | **无**（需 4~5 条拼） | `FastIDCT.cs`、`SimdJpegPipeline.cs`、`SimdJpegEncodePipeline.cs` | 定点段 1.2~1.5x | **P0** |
| 5 | UZP1/2、TRN1/2 | `AdvSimd.Arm64.UnzipEven/Odd`、`TransposeEven/Odd` | **无**（punpck 是 zip 方向） | RGB→planar 第二级、4:2:0 色度抽取、`SimdHelper` 灰度/通道转换 | 中 | P1 |
| 6 | ADDV/SMAXV/SMINV/SADALP | `AdvSimd.Arm64.AddAcross/MaxAcross/MinAcross/AddAcrossWidening` | **无**单指令跨 lane 归约（只有 `phminposuw` 特例） | `Adler32`（已用）、Area/box 缩放横向求和、直方图 | 小 | P1 |
| 7 | SABA/UABA/UABAL | `AdvSimd.AbsoluteDifferenceAdd`、`AbsoluteDifferenceWideningLowerAndAdd` | `psadbw` 只能按 8 字节分组求和 | `Quantizer.cs:340-345` 最近色搜索（Wu 量化器，非默认） | 仅 Wu 路径 | P1 |
| 8 | BSL/BIC/ORN | `AdvSimd.BitwiseSelect`、`BitwiseClear`、`OrNot` | 需 `pand/pandn/por` 三条 / 额外 `not` | 所有 mask+blend（resize clamp、量化饱和、PNG 反滤波） | <3%，零风险 | P2 |
| 9 | SLI/SRI 位域插入 | `AdvSimd.ShiftLeftAndInsert`、`ShiftRightAndInsert` | **无** | `LzwEncoder.cs:266` 变长码打包、`LzwDecoder.cs:75` | 低（变长码串行） | P2 |
| 10 | RBIT 向量位反转 | `AdvSimd.Arm64.ReverseElementBits` | **无**（连标量位反转都没有） | LZW 码流位序；**PNG 侧无落点**（本库只实现 8/16-bit） | 低 | P2 |
| 11 | CNT 向量 popcount | `AdvSimd.PopCount` | **无**（本机 `AVX512F=False`，也无 VPOPCNTDQ） | 库内暂无落点（无二值形态学/位平面算子） | — | 记录 |
| 12 | PMULL（无进位乘） | `Aes.PolynomialMultiplyWideningLower/Upper` | `Pclmulqdq` 只有 2×64 位 | CLMUL 版 CRC（若 #1 不可用的备选） | 备选 | 记录 |

---

## 3. 分条说明

### 3.1 【P0】硬件 CRC32 —— x86 拿不到的最实在一条

**为什么是 ARM 专属**：PNG 用 CRC-32/ISO-HDLC（IEEE，多项式 `0xEDB88320`）。
x86 的 `SSE4.2 CRC32` 实现的是 **Castagnoli（CRC-32C）**多项式，**算不出 PNG 要的值**；
x86 想加速只能手写 CLMUL + Barrett 归约（代码量大、要对齐 16 字节块）。
ARM 的 `crc32b/h/w/x` 正是 IEEE 多项式，`crc32cb/…` 才是 Castagnoli —— 两档都有。

**落点**：
- `src/Formats/Png/Crc32.cs` — 当前 slice-by-8 表驱动（`Crc32Optimized`）
- 编码：`PngWriter.cs:291-292`，每个 chunk 的 `type + data`
- 解码：`PngDecoder.cs:375 / 420`，IDAT 校验（无参构造路径默认开启校验）

**预估**（按 x86 实测 slice-by-8 ≈ 1.0 GB/s 外推，M4 上待 probe §3 校准）：

| 侧 | 现有耗时 | 换硬件后 | 占比 | 是否过 7% 门槛 |
|---|---|---|---|---|
| PNG 编码 large（246 ms，CRC 输入≈压缩后 7.6 MB） | ~5 ms | <1 ms | ~2% | ✗ 不过 |
| **PNG 解码 large（48.5 ms，同样 7.6 MB）** | ~5 ms | <1 ms | **~10%** | **可能过** |

⇒ **先测解码侧**（`--filter decode/png`），编码侧大概率不够门槛，别浪费时间。

**验收**：probe §2 的检验值 + 123 项产物 hash 逐字节一致。

### 3.2 【P0】LD3 解交织加载 —— 直接砍掉 17 条指令

**现状**（`OctreeQuantizer.cs:614-619`，一次 16 像素 = 48 字节）：

```
3 × LoadBytesPtr
+ 9 × ShuffleBytes（3 个平面 × 3 个源向量，NEON 上是 tbl）
+ 6 × OrBytes（orr）
= 18 条指令，才把 RGB24 拆成 R/G/B 三个 16 字节平面
```

**改法**：`AdvSimd.Arm64.Load3xVector128AndUnzip(byte*)` —— **一条指令**读入 48 字节并直接给出 3 个解交织后的 16 字节平面。
步长正好是 48 字节，与现有循环天然对齐。

**为什么 x86 做不到**：x86 没有解交织加载（`punpck*` 是交织方向），只能用 `pshufb` + `por` 拼。
顺带一提，M4 上 `tbl` 吞吐只有 2/cycle、`orr` 3/cycle，这 18 条指令在 ARM 上比在 x86 上更亏。

**同类落点**：JPEG 编码 `RgbToYCbCr` / `FillMcu420`（RGB24 → Y/Cb/Cr 三个平面）、
`BmpReader.cs:485` 的 BGR→RGB、`SimdHelper` 的通道转换。

**预估**：GIF 量化在 huge 档是 571 ms 的大头（x86 实测量化占 GIF 编码 73%），
解交织段指令数减半以上 ⇒ 整体 GIF 编码有望 +10~30%。

**风险**：尾部（<48 字节）处理要重做；`_mapLut` 查表逻辑不变，产物理论上逐字节一致。

### 3.3 【P0】RDM：SQRDMLAH / SQRDMLSH

**语义**：`acc = saturate(acc + 2·a·b)`，自带 doubling、rounding（round-to-even）、饱和 —— 一条指令。
x86 要用 `pmullw + pmulhw + 移位 + padd + packss` 四到五条拼，**且舍入语义不同**。

**落点**：`FastIDCT.cs`（`Fix_0_541196100` 一类定点常量）、`SimdJpegPipeline.cs` / `SimdJpegEncodePipeline.cs`
的 SIMD IDCT / FDCT、色彩转换的定点矩阵。这些都是纯乘加密集型内核。

**⚠️ 最大的坑**：SQRDMLAH 的 rounding 与饱和语义与现有定点内核**不保证逐位一致**。
先跑 probe §5 对拍；若不一致，**保留原路径**，不要为了用上指令而改变产物
（这与 §3.1 CRC 不同：CRC 只要多项式对就一定逐位一致，RDM 不一定）。

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
| **SVE / SVE2** | .NET 10 已暴露 API，但 M4 大概率不支持。明天 §1 一行代码即可确认；若为 `True` 再议（届时 `OctreeQuantizer.cs:593` 注释里"AVX2 下无解"的 32768 项 LUT gather 就有解了） |
| **SME** | M4 硬件有，.NET 无 API ⇒ 不可用 |
| **UDOT/SDOT 用于 resize** | `docs/VnniResizeFindings.md` 已证明：x86 上 VNNI 量化 resize **比浮点慢 4x**，瓶颈在逐像素 gather 与内存带宽，不在点积。`Dp.DotProduct` 是同一类指令 ⇒ **单独上 UDOT 大概率同样不划算**；只有配合 LD3/UZP 把 gather 批量化后才可能转正（而 §6 的"转置式 gather"正是当年卡住的地方） |
| **PNG 编码主体 / WebP** | 瓶颈是 BCL deflate 与原生 libwebp，不是本库代码 |
| **Adler32** | 已 NEON 化到 7.81 GB/s，无剩余空间 |
| **32768 项 `_mapLut` 的向量 gather** | `OctreeQuantizer.cs:593-594` 的注释已写明 AVX2 无解；NEON 的 `TBL` 最多只有 4 张表 = 64 字节，**同样无解**。除非 SVE 可用 |

补充说明：`AdvSimd.Arm64.VectorTableLookup` 的 2/3/4 表形式（TBL，最多 64 字节表）
确实比 x86 的 `pshufb`（最多 16 字节）强，但**本库目前没有落在 16~64 字节区间内的查表热路径**
（Bayer 抖动用的是 `offTab[y & 3]` 的 4 KB 表，已被 SIMD 加载覆盖），所以这条先记着，不排期。

---

## 5. 落地时的硬性约束（今天 x86 复测得出的）

1. **x86 分支必须保持 1:1**：任何进 `SimdCompat.cs` 的封装，x86 侧必须仍是原来的
   `Sse2./Ssse3./Sse41./Avx2.` 内在函数（今天已逐条核对，当前所有封装都满足）。
2. **7% 准入门槛**、取**跨轮中位数**、同构建 A/B（用 `SimdCompat.ForceScalar` 或静态开关切）。
3. **123 项产物 hash 逐字节一致**（已知豁免只有 5 项 JPEG 解码，见 `ArmPerfBaseline.md §6.1`）。
4. **改完必须重跑 x86 复测**：现成脚本已就绪 ——
   `python .perf/x86-ab.py`（5 轮全量，双口径），参考值：几何平均 **1.008x / 1.006x**，噪声地板 12%。
   只要 x86 几何平均仍在 1.00 附近的噪声带内，就算 ARM 侧改动没有波及 x86。

---

## 6. 明天的建议顺序

1. `dotnet run .perf/arm-api/probe.cs` —— 拿能力清单 + CRC GB/s + LD3 语义（约 1 分钟）
2. 若 CRC 硬件吞吐 ≥ 3× 表驱动：先做 §3.1，`--filter decode/png` 定向 A/B（这是唯一"改起来最省事"的一条）
3. 再做 §3.2 LD3 解交织（GIF 量化 + JPEG 色彩转换），用 `--filter gif` / `--filter encode/jpeg` 定向 A/B
4. §3.3 RDM **最后做**：先对拍语义，不一致就放弃
5. 每做完一条，跑一次全量 5 轮 A/B；定稿前跑一次 x86 复测
