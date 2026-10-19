# 智能有损压缩（`SharpImageConverter.Compression`）

TinyPNG 式的「丢进去就能小很多、肉眼几乎看不出差别」能力。
默认输入 JPG / PNG / GIF / WebP / BMP，输出**同格式**（BMP 例外，转 PNG）的最小版本；
输出扩展名与源格式不同时，自动走「转换即最优」——一步产出目标格式的最小体积（见 §5）。

- 库入口：`ImageOptimizer.Optimize(input, output?, options?)`
- CLI：`--optimize`；与 `--to <格式>` 组合即为「转换 + 智能压缩」
- 源码：`src/Compression/`

---

## 1. 设计要点

### 1.1 为什么需要「感知」画质度量

调色板量化 + 抖动会把误差打散成高频颗粒：逐像素 PSNR 会掉到 30~36 dB，
但人眼看到的（低频、面积平均后的）画面几乎不变。反过来，
单纯看逐像素 PSNR 又会不分青红皂白地把「可接受的有损」也否掉。

因此 `QualityMetrics.Compare` 同时给出两个值：

| 指标 | 含义 | 用途 |
|---|---|---|
| `PerceptualPsnr` | 先做 4×4 块平均再算 PSNR | **快速预筛**：抹平抖动颗粒 |
| `Psnr` | 逐像素 PSNR | **副判据**：拦住「高频细节被抹掉 / 抖动过头」，下限 `MinRawPsnr`（默认 30 dB） |
| `Ssim` | Y 通道 SSIM 均值（块边长随像素数放大） | **V2 主判据**：结构相似度，默认下限 0.95 |
| `WorstSsim` | 最差 1% 块的 SSIM | **V2 副判据**：抓局部糊掉/块效应，默认下限 0.75 |

实测（900×600 合成图）：

| 场景 | 逐像素 PSNR | 感知 PSNR |
|---|---|---|
| PNG 256 色 + 抖动（照片） | 36.6 dB | 48.5 dB |
| PNG 128 色 + 抖动（照片） | 34.4 dB | 43.6 dB |
| PNG 256 色**不**抖动（照片） | 22.1 dB | 29.4 dB |
| JPEG q80（照片） | — | 45.3 dB |

可以看到：抖动让感知分提升近 20 dB（这正是我们要的效果），
而「块平均」对**纯噪点被抹平**这类损伤不敏感，所以再叠一条逐像素下限兜底。

V2 的块边长按 `clamp(round(sqrt(w*h)/500), 4, 16)` 取：1 MP 用 4、12 MP 约 6、143 MP 用 16，
避免大图仍按 4 px 尺度评估。人工 4×4 棋盘格在旧指标下感知 PSNR 仍有 54 dB（块平均恰好抹平），
但 Y-SSIM 只有 0.004，会被 `MinSsim` 拦下。

RGBA 比较使用**预乘 alpha**：`RGB * A / 255`，透明区的不可见 RGB 不参与计分。
带 alpha 的图暂不跑 Y-SSIM（仍用预乘后的 PSNR）。

需要 SSIMULACRA2 / Butteraugli 时，可设置 `QualityMetrics.ExternalMetric` 委托接入原生实现；
内置不捆绑这些算法。

### 1.2 画质下限怎么定

`OptimizeOptions.TargetQuality`（0-100，默认 88）映射为：

```
MinPerceptualPsnr = 33 + 0.14 * TargetQuality
```

即 88 → 45.3 dB，95 → 46.3 dB，75 → 43.5 dB。
标定依据是上面那组实测：均衡档下照片会停在 **256 色 / q80 附近**，
与「放大看才发现有色阶、正常看没区别」的主观感受一致。

想自己调就直接赋值 `MinPerceptualPsnr` / `MinRawPsnr`。

### 1.3 候选搜索

颜色数、JPEG 质量都满足「越激进 → 体积越小、画质越差」的单调关系，因此用二分搜索：
8 档颜色阶梯只需 3~4 次评估，质量区间 40~95 只需 6~7 次编码 + 解码。

**搜索期用低一级的 deflate，产出期再给赢家补最高档**：二分的判定完全来自画质（纯像素比较），
体积只用于「在达标候选里挑一个最小的」；而候选之间的体积差距有 10%~40%，远大于压低 deflate
级别带来的 2.6%~6.6% 偏差，因此 PNG 搜索期统一用 `CompressionLevel.Optimal`（zlib 6）。
4.19 MP 实测：调色板单候选编码 682.0 → 91.7 ms（7.4 倍），PNG `--optimize` 全流程
4161 → ~1210 ms。

赢家确定后再用 `PngWriter.RecompressIdat` 按 `SmallestSize`（zlib 9）重压一次 ——
只解压 IDAT 后原样重压，不重新滤波/量化，像素逐字节不变。成本是 7~10 倍 deflate，
故默认只对 ≤8 MP 生效（`OptimizeOptions.PngMaxDeflatePixelThreshold`）；
需要极限压缩可调大阈值或直接设 `PngMaxDeflateOnWinner`。同二进制 A/B：
`car.png` 60,521 → 54,549 B（−9.9%）、`Amish-Noka-Dresser.png` 140,614 → 137,402 B（−2.3%）。
该级别**只影响 `--optimize`**，普通 `--to png` 走 `PngWriter` 自己的默认值，不受影响。

#### 1.3.1 候选评估并行化（PNG / WebP）

PNG 的 `deflate` 是**单线程流式**实现，libwebp 的编码也是单线程（`thread_level=0`），
两者在 12 核上只用 1 个核 ⇒ **候选并行是这两条路径唯一能吃到多核的杠杆**。
（JPEG 相反：它的编码器内部已是 produce/dct/huffman 三段流水线，外层再并行只会超订。）

二分天生串行（下一个评估哪个取决于上一个是否达标），故改成：

```
阶段一：粗网格（两端必含）并行扫一遍 → 找第一个达标的网格点
阶段二：在 (前一格, 命中格) 的开区间里全枚举并行扫一遍
```

因为画质随质量单调，两阶段找到的仍是**最低达标质量**，与二分同解。
PNG 的阶梯只有 8 档，按 `ImageOptimizer.ReplayLadderVisits` **复刻二分实际会访问的候选序列**来挑结果 ——
这样「参与体积比较的候选集合」与串行二分逐个相同，产物逐字节不变。

候选**不预评估整条阶梯**：二分的第一步永远是最高档，先只算它；它不达标时（照片在 45 dB 感知门槛下
几乎必然如此）其余候选一个都不会被访问，也就不该被编码。达标时才把剩余候选一次波并行铺满
（墙钟 ≈ 最慢一个候选）；`dop == 1` 时干脆不预评估，按访问顺序惰性编码。
143 MP 照片上这条「先算最高档」把 8 次编码减到 1 次（该图 256 色只有 38.22 dB 感知分）。

实测（2048²/4.19 MP，同一份二进制）：

| 项 | 改前 | 改后 |
|---|---|---|
| PNG `--optimize` 单文件（冷进程） | 1418 ms（中位） | **744 ms**（中位），产物 md5 逐字节一致 |
| 8 张 PNG 批量 `--optimize` | 4920 ms | **3352 ms**（1.47x），产物逐字节一致 |
| 8 张 WebP 批量 `--optimize` | 20055 ms | **7746 ms**（2.59x） |
| 143 MP 照片 `--optimize`（交错 A/B ×3） | 41.4 / 40.6 / 41.5 s | **8.75 / 8.58 / 7.88 s** |

`ImageOptimizer.CandidateParallelism(候选数, 单候选峰值字节, 内存预算)` 同时按
CPU 核数和内存预算夹上限，避免目录批量时与外层并行度叠加爆内存。
内存预算缺省按「当前可用内存」推导：`(TotalAvailableMemoryBytes − GC.GetTotalMemory()) / 4`，
夹在 64 MB ~ 8 GB。单候选峰值是「索引 1 B/px + 还原缓冲 4 B/px」≈ 5 B/px；
原先固定 512 MB 在 143 MP 上会被夹成 `dop = 1` 并**静默退化为串行**。
单纯用 `TotalAvailableMemoryBytes / 4`（GC 可用的**总量**）仍会在进程已占用一两 GB 或
小容器里高估空闲内存，因此改为扣掉当前托管堆占用。
候选并行统一走 `ImageOptimizer.ForEachCandidate`：真遇到 `OutOfMemoryException` 时
GC 一次后以并行度 1 重跑同一批候选，避免整个 optimize 因内存峰值直接失败。

#### 1.3.2 搜索期不经过比特流（JPEG）

JPEG 的二分判定只看画质分数，体积从不参与决策（赢家最终一定会重新完整编码一次）。
而 Huffman 只改变系数的**编码形式**、不改变系数本身（熵编解码严格无损），
所以「编成字节流、再解码回来算 PSNR」这一趟对结果零影响，纯属浪费：
143 MP 上熵解码 **192 ms/候选**，编码侧熵编码另有 192 ms（并行，墙钟被色转换遮住）。

搜索期因此改走系数直通：

```
RGB24 ─produce(色彩转换)→ YCbCr 采样 ─FDCT+量化→ 量化系数
         │(只跟源图有关，采一次缓存)                │
         ▼                                          │
   JpegSampleCache (sbyte/MCU)                      │
         │各质量档复用                               │
         ▼                                          ▼
  EncodeFromSamples ─FDCT+量化→ 系数 ── IMcuCoefficientSink 按「解码器组件平面」布局收下
                                                    ▼
                        ReconstructImage（反量化 + IDCT + 上采样 + YCbCr→RGB）
                                                    ▼
                            QualityMetrics.Compare（按带分片并行）
```

- `JpegEncoder.EncodeCoefficients` 把每个 MCU 的量化系数直接交给 `JpegCoefficientSink`，
  不产生任何字节流（`ProcessHuffmanOrdered` 在 `coefficientSink != null` 时整段跳过 `WriteMcuHuffman`）。
- `JpegFrameState.AdoptCoefficients` 让这批系数走**与正常解码完全相同**的 `ReconstructImage`，
  因此像素逐字节一致：q = 30 / 46 / 53 / 90 × 4:2:0 / 4:4:4，重建结果的 SHA-256 与
  「编码成字节流再解码」全等（`JpegCoefficientBypassTests`）。
  注意这条断言是必要的 —— 只看最终产物 md5 是不够的，选中的质量有可能碰巧没变。
- 落盘产物仍走完整编码，最终文件 md5 逐字节不变。
- `--opt-verbose` 的候选日志不再带「N 字节」：搜索期没有字节流可报。

**produce 也不是每档都要重算的。** 它只读源图和几何（RGB→YCbCr、4:2:0 下采样、电平平移），
与质量档无关，却占单候选 56%（143 MP：186 / 334 ms）。`JpegSampleCache` 把它按 MCU 存成
`sbyte`（电平平移后取值域恰好 [-128, 127]，4:2:0 约 215 MB），整幅采一次（按行并行，**13 ms**），
之后每档只做量化：`JpegEncoder.EncodeFromSamples` 按行并行、每 MCU 用 1.5 KB 栈缓冲原地
DCT+量化，省掉 MCU 批的池化租借、队列同步与 `LastNzPacked`（只有熵编码用）。
搜索期因此也从「流式管线」变成「批量并行」，`--opt-verbose` 的候选顺序仍按二分访问序。

**重建输出缓冲跨候选复用。** `ReconstructImage` 的 `new byte[w*h*3]`（143 MP = 429 MB）会被
运行时清零，实测 **26 ms/次**，而重建会把每个像素写满。`JpegCoefficientSink.Reconstruct`
改用一块 `GC.AllocateUninitializedArray` 缓冲（0.1 ms 量级）。这块缓冲是「借」的：
下一次重建会覆写，`JpegImage` 也直接引用它，所以调用方必须立刻消费（搜索期正是「重建 → 比较」紧邻）。

`QualityMetrics.Compare` 同时改成按带分片并行（一带 = `blockSize` 行，4×4 块不跨带，
分片内的累加顺序与串行逐项相同；分片数固定为 `min(核数, 带数)`、合并按分片下标，
因此同一台机器上结果完全确定，不会因线程调度让「刚好压线」的候选随机翻转）。

实测（143 MP / 6 候选，同机交错 A/B）：

| 项 | 改前 | 改后 |
|---|---|---|
| 搜索期合计 | 5895 / 5791 ms | **526 ms + 采集 13 ms** |
| 其中单候选 | 334 ms | **85 ms**（量化+重建+评估） |
| 单次 `Compare` | 880 ms | **65 ms**（13.5×，`|ΔPSNR| ≤ 2.6e-8 dB`） |
| 端到端 `--optimize`（同二进制 `SIC_JPEG_NOCACHE` 对照） | 3145 / 3551 / 3334 ms | **2648 / 2700 / 2468 ms** |

`SIC_JPEG_NOCACHE=1` 可让搜索期退回「每候选重跑 produce」，用于同二进制内对照 —— 跨二进制
比较有 ±2% 系统偏差，不足以判断这个量级的收益。
产物 md5 `543a15bb…` 四份全等；PNG（`2e1e62a5…`）与 WebP（`6341e94c…`）路径回归一致。

4.19 MP 的 PNG `--optimize` 全流程约 0.75~1.1 s（最高档候选 + 达标后的并行阶梯 + 必要时无损重写）。

#### 1.3.3 全候选取最小 + 边界多看 1~2 档

搜索的**定位**与**挑选**分开：

- 定位仍可用二分/锚点，但挑选阶段不再复刻二分访问序列。PNG / GIF 在最高档达标时
  把整条固定网格（颜色数阶梯 × 抖动变体）评估完，在**所有达标候选**里按体积取最小；
  最高档不达标时仍只算最高档（更低色数画质只会更差）。固定网格保证结果与核数/内存无关。
- JPEG / WebP 在收敛到最低达标 q 后，再向下看 1~2 档：质量-体积不严格单调
  （量化表整数取整、Huffman 表切换会造成局部反转），低一档偶尔仍达标且更小。
  WebP 还会在**所有已评估**候选（锚点/网格/二分/边界）里按搜索档 m2 体积取最小。
- JPEG 的三种色度采样（4:2:0 / 4:2:2 / 4:4:4）是**独立的整轮搜索**：默认只在更激进的
  布局不达标时才回退（`JpegCompareSubsampling = false`）。开启比较后每个布局都跑一轮，
  再在所有达标候选里按体积取最小；它与「边界多看 1~2 档」不同，代价是整轮而非一两档。
- 这些候选都已在搜索期付过编码成本，多出来的达标候选没有理由丢弃；产物仍经过
  最终产出档重编与复评，`Quality` / `MeetsBar` 以最终字节为准。

#### 1.3.4 内存预检（optimize 拒绝 / 转换流式降级）

解码前只读文件头拿宽高（JPEG SOF、PNG IHDR、GIF LSD、WebP VP8X/VP8/VP8L、BMP DIB），
按格式经验系数估算峰值，与预算比较：

```
预算 = 显式上限（--memory-limit / SIC_MEMORY_LIMIT_MB / OptimizeOptions.MemoryLimitBytes）
      或 (GC 可用总量 − 进程工作集) × 0.75，下限 64 MB
```

- **optimize**：估算超过预算时直接拒绝，同格式保留原图并写入 `Reason`；
  跨格式 optimize 没有“保留原格式”的退路，抛 `InsufficientMemoryException`，CLI 按文件捕获。
- **非 optimize 转换**：超预算时按「源格式 × 目标格式」决定：
  - 源 BMP/PNG/GIF/WebP/JPEG → 目标 BMP/PNG/GIF/JPEG：走 `StreamingConversion` 管线
    （BMP/PNG 两端逐行；GIF 首帧解码后逐行；WebP 用 libwebp 增量解码逐行；
    JPEG 基线单扫描按 MCU 行熵解码 + SIMD IDCT、progressive 回退到整幅系数 + SIMD 行带重建；
    目标 JPEG 按 MCU 行读入并立即熵编码；目标 GIF 做单帧缓冲后编码）。
    流式 BMP 目标写 **top-down（负高度）**，按行顺序写，不再逐行 seek。
  - 目标 WebP：libwebp 编码器只接受整幅输入，仍只能拒绝或走常规路径。
- **已知限制**：GIF 编码必须对整帧建调色板，因此只是“单帧缓冲”而不是逐行；
  WebP 编码只接受整幅输入。JPEG **基线单扫描** 已做到逐 MCU 行熵解码 + SIMD IDCT，
  不再保留整幅量化系数；**progressive/非交错/4:2:2** 回退到整幅系数 + SIMD MCU 行带重建
  （仍约 3 B/px + 压缩文件），但不再分配整幅 RGB。JPEG 行级编码只使用标准 Huffman 表
  （不做两遍优化表，`--jpeg-progressive` 会在写完后用现有转码器再转一次），
  且暂不支持 EXIF Orientation≠1（会退回常规解码/拒绝）。

  **143 MP 实测（JPEG → BMP，同机）**：

  | 输入 | 路径 | 峰值工作集 | 耗时 | 像素 |
  |---|---|---:|---:|---|
  | 基线 4:2:0 | 常规 | 854 MB | 641 ms | 基准 |
  | 基线 4:2:0 | 流式（MCU 行熵解码 + SIMD） | **65 MB** | 775 ms | 与常规逐像素一致 |
  | progressive | 常规 | 854 MB | 690 ms | 基准 |
  | progressive | 流式（整幅系数 + SIMD 行带） | **458 MB** | 1039 ms | 与常规逐像素一致 |

  ⇒ 基线流式内存约为常规的 **7.6%**，耗时仅 +21%；progressive 内存约减半，耗时 +51%。
- 估算使用保守系数（见 `src/Compression/MemoryGuard.cs`），不是精确模型；
  读不到宽高时放行，避免探测失败误伤正常文件。
- `--no-memory-guard` / `OptimizeOptions.DisableMemoryGuard` 可关闭预检（回到 OOM 回退行为）。

搜索是这条链路的主要开销：每次候选 = 一次量化与重建（开启 `OptimizeHuffman` 时赢家再走两遍
完整编码：先统计符号分布再编码）+ 一次画质评估。1.57 MP 的 PNG→JPEG 实测
（1254×1254，Release）：单次普通编码 0.17 s、单次带优化 Huffman 的候选 0.32 s、
7 次候选的完整搜索 1.10 s —— 即**搜索约占 70%**（二分 [40,95] 恰好 7 次评估，
`--opt-verbose` 会把每个候选的画质打出来）。

体积并不严格随颜色数单调（中位切分在不同预算下切法不同，会有几个百分点的抖动），
所以最后一步是在**所有达标候选中按体积取最小**，而不是直接取最激进的那个。

#### 1.3.5 大图回归修复：4:2:2 SIMD 重建、并行 SSIM、采样比较默认关闭

1.2.0 新增的 4:2:2 只补了编码器，解码/重建侧一直落在通用路径上：
`TryDecodeInterleavedYCbCrSimd` 只认 Y=1×1 与 Y=2×2，2×1 直接 `return false`，
退回「三块整幅平面 `DecodeSpatial` + 标量双线性交织」。optimize 每评估一档质量都要重建一次，
143 MP 单次 **1267 ms**（对比 4:2:0 的 18 ms / 4:4:4 的 25 ms）。现补上 2×1 MCU 分支
（`SimdJpegPipeline.TransformAndConvertYCbCr422`，水平 2 倍上采样与 4:2:0 共用同一套
`UpsampleAndConvert`），单次重建降到**~30 ms**。

同一提交引入的 Y-SSIM 是**整幅单线程标量扫描**（143 MP ~242 ms/候选），比并行化后的
PSNR/感知段（~70 ms）还贵。现按「块行」分片并行（SSIM 块互不重叠，合并按分片下标，
结果确定；候选已经并行时用 `allowInnerParallel: false` 保持串行）。

`JpegCompareSubsampling` 的默认值也从 `true` 改为 `false`：4:2:0 达标时不再强制多跑
4:2:2 + 4:4:4 两轮完整搜索。

三项叠加后 `examples/progressive.jpg`（143 MP）`--optimize` 端到端：

| 项 | 改前 | 改后 |
|---|---:|---:|
| 端到端（默认，只搜 4:2:0） | 28.6 s | **3.45 s** |
| 端到端（`--opt-jpeg-subsample-compare`，搜三种采样） | 28.6 s | **7.84 s** |
| 产物（默认） | 2.36 MB / q46 4:2:0 | 不变 |
| 产物（开启采样比较） | 2.36 MB | **2.13 MB / q42 4:2:2** |

同时修掉一个让 4:2:2 形同虚设的 bug：`JpegEncoder.Write(..., JpegEncoderOptions)` 把参数
降级成 `options.Subsample420`（bool），于是 `Yuv422` 被静默写成 4:4:4 —— optimize 用真正的
4:2:2 系数算画质、却吐出与 4:4:4 逐字节相同的产物。现已改为传 `options.Subsampling`。

---

## 2. 各格式策略

### PNG（收益最大）

0. **16-bit PNG / APNG 同格式 optimize 直接保留原图**：解码器只保留高字节 / 首帧，
   重编码会造成静默降级；跨格式转换会照常执行，并在方案描述里标注“源为 16-bit/动画”。
1. 颜色数 ≤ 上限时直接**精确取色**（无损），不再做有损尝试。
2. 否则量化到调色板（颜色类型 3）。索引按实际颜色数使用 1/2/4/8 位：
   ≤2→1、≤4→2、≤16→4，其余 8 位；16 色图形的原始 IDAT 因此只有 8 位索引的 1/4。
   **抖动是搜索维度**：
   同色数下「抖动 / 不抖动」两个变体都参与画质判定与体积竞争；抖动会打散索引熵、显著增大 deflate
   体积（GIF 实测同图 92 KB vs 17 KB），因此不能预设「抖动画质更好就该选它」。
3. 赢家精修：实测 `None` / `Up` / `Adaptive` 三种行滤波、最小位深与 8-bit，
   再按索引使用频率重排调色板；只对 ≤8 MP 或 ≤16 色的图做。
   实测 `Amish-Noka-Dresser.png` 从 111,025 → 85,867 B（−22.6%）。
4. 不透明且 R=G=B 的灰度图额外尝试**颜色类型 0**（8-bit 灰度），
   比调色板少一个 PLTE，也不必经过索引映射。
5. 量化不达标时回退**无损真彩色重写**：真彩色（非调色板）编码，对渐变类图像尤其有效
   （实测抖动后索引近乎随机，调色板反而会比真彩色大 19 倍）。
   行滤波在 **Up 与逐行自适应之间实测取小**——自适应是 MSAD 启发式（最小化滤波后字节的 L1 范数），
   而 deflate 吃的是跨行重复：整列统一 Up 时相邻行差分高度相似，LZ77 能跨行匹配整行，
   逐行换滤波器会打断这条链。143 MP 照片实测 Up 29,469,583 B vs 自适应 35,142,122 B（**自适应大 19.2%**，
   且慢 2.4 倍），而 1000×1000 16 色调色板产物则是自适应小 3.2% ⇒ 不能按启发式定，只能实测。
6. 量化收益不足 10% 时也会额外算一次无损重写做对比。

> 实测：照片类 845 KB → 451 KB（-46.7%）；全色域渐变 13.6 KB → 8.3 KB（-38.8%，无损）；
> 抖动维度加入后：`Amish-Noka-Dresser.png` 137,402 → 111,025 B（−19.2%），
> 12.6 MP 的 `large.png` 7,334,753 → 2,716,630 B（−63.0%）；
> 6 色图形 3.3 KB → 1.4 KB（-57.4%，无损）。

### JPEG

1. **先做无损重封装**：`JpegLosslessOptimizer` 删除 APP1（EXIF/XMP）、APP13（IPTC）、COM，
   保留 ICC；基线单扫描图再用 `JpegHuffmanOptimizer` 统计实际系数频率、重写最优 Huffman 表
   与熵编码段（jpegtran -optimize 等价）。两条路径都不碰量化系数，像素逐字节不变；
   EXIF Orientation ≠ 1 时保留 APP1。无损产物比原图小且无需再压时直接采用（零代际损失）。
2. 有损路径：解码 → 以 4:2:0 重编码，二分搜索「刚好达标」的最低质量。
3. **progressive 产出**：baseline 编好后，`JpegProgressiveTranscoder` 复用同一批系数重写成 SOF2
   （DC 扫描 + 每分量 AC 扫描 + EOBRUN），与 baseline 按体积取小；不做 successive approximation。
   实测同 q 下 −4.7%~−6.9%，像素逐字节一致；`JpegProgressive` 默认开启，
   CLI 可用 `--opt-no-jpeg-progressive` 关闭。
   有损结果必须比 `min(原图, 无损产物)` 再小至少 `MinLossyGainRatio`（默认 10%）且达标，
   才值得再损失一代画质；否则采用无损产物或保留原图。
   搜索期不经过比特流（系数直通 + 并行画质评估），见 [1.3.2](#132-搜索期不经过比特流jpeg)。
2. 若 4:2:0 始终不达标（彩色文字、色度高频丰富的图），自动退回 4:2:2、4:4:4 再各搜一轮，
   在**达标的候选**里按体积取最小。4:2:0 已经达标时默认不再比较色度采样方式
   （`JpegCompareSubsampling = false`）——绝大多数图仍是 4:2:0 最小，多搜两轮纯属浪费；
   想要「宁可多花时间也取最小」可开 `--opt-jpeg-subsample-compare`（143 MP 实测 +4.4 s，
   本例把产物从 2.36 MB 再压到 2.13 MB）。
3. **按图优化的 Huffman 表**（`JpegEncoderOptions.OptimizeHuffman`，默认开启）：
   编码器的标准 Annex K 表是固定的，与实际符号分布不匹配，空块开销高达 5.38 bits/块
   （DC 2b + EOB 4b）。对大片平坦区域的图（扫描件、截图、大留白照片）这部分开销占比极高。
   两遍编码：先跑一遍「生产 → DCT 量化」只统计符号分布，再用**真实频率**建最优表重新编码，
   码长超过 16 位时按 libjpeg（`jchuff.c jpeg_gen_optimal_table`）的调整循环压回，
   可再省 20%~40%（143MP 大留白扫描图：2.95 MB → 2.60 MB）。代价是编码耗时约 ×2。

   > **不要在建树前缩放频率**。曾用「把总权重缩到 ≤500」来规避 16 位限长，代价是长尾稀有符号
   > 被抹成同权、高频符号码长被拉长：1254×1254 高细节图的 AC 亮度表相对最优解偏 **+6.80%**，
   > 比固定 Annex K 表还大 **4.06%**（这是「加 `--optimize` 体积反涨」的次因）。
   > 改成精确频率 + libjpeg 限长后，同一张图只偏 **+0.01%**，比 Annex K 表省 13,714 bit。
   > 码长分配沿用 libjpeg 的「符号表按（原码长，符号值）升序 + 调整后的计数」，两者必须配套。
4. 默认丢弃 EXIF（保留 ICC，避免色彩呈现变化）；已解码时方向已生效，不会转错。

> 实测：q100 的 352 KB 照片 → 68 KB（-80.6%）；大留白扫描图 2.95 MB → 2.60 MB（-12.0%）；
> 已经是高质量编码的普通照片可能仍判定「无收益」并保留原图。

**progressive**：已实现 spectral selection 版 progressive（DC + 每分量 AC + EOBRUN），
同 q 下比 baseline 小 4.7%~6.9%。尚未做 successive approximation（Ah/Al）与更细的频谱分段，
那部分还能再追 2%~5%。

### JPEG trellis（opt-in，默认关闭，P3-1c）

`JpegTrellisQuantizer` 把每个 AC 系数的量化从「唯一最近值」改成块内动态规划：
候选 `{0, 最近值, 最近值±1}`，目标函数 `Σ误差² + 64λ×码率`（系数域误差，Parseval ⇒ 像素 SSE），
码率用标准 Huffman 表的 (run,size) 码长 + ZRL/EOB 估计；λ 随量化步长 `Q²` 缩放
（`λ ≈ 60·(scale/100)²`，`scale` 与 `BuildQuantTable` 同式，可用 `SIC_JPEG_TRELLIS_LAMBDA` 覆盖）。
因为要看到量化前的 DCT 系数，trellis 路径使用标量 FDCT，不走 SIMD 融合量化。

开关：`OptimizeOptions.JpegTrellisQuantization` / CLI `--opt-jpeg-trellis`（默认 false）。
同机实测（PNG→JPEG，`--optimize`）：

| 样本 | 基线 | trellis | 体积 | 感知 PSNR | 最差 1% SSIM | 耗时 |
|---|---:|---:|---:|---|---|---:|
| `small.png` 0.48 MP | 29,111 B | 27,920 B | −4.1% | 45.47 → 45.71 | 0.753 → 0.757 | 0.6 → 1.3 s |
| `medium.png` 1.57 MP | 82,724 B | 68,755 B | −16.9% | 45.39 → 45.40 | 0.812 → 0.761 | 1.4 → 4.2 s |
| `Amish-Noka-Dresser.png` 1.0 MP | 27,399 B | 21,382 B | −22.0% | 45.37 → 45.43 | 0.835 → 0.800 | 0.9 → 2.5 s |
| `car.png` 0.27 MP | 3,901 B | 3,005 B | −23.0% | 46.25 → 46.10 | 0.871 → 0.780 | 0.4 → 1.2 s |
| `large.png` 12.6 MP | 377,105 B | 285,690 B | −24.2% | 45.45 → 45.35 | 0.937 → 0.890 | 3.2 → 23.0 s |

结论：收益显著（4%~24%），但代价是 2~7 倍编码时间，并且最差 1% 块 SSIM 明显下降
（逼近 0.75 门槛）——它是在画质门槛内**重新分配误差**，不是无代价的同质量压缩。
因此默认不启用；需要极限体积且能接受该权衡时再显式打开。

> **DP 实现（2026-10 重写）**：上表的耗时是旧实现。旧 DP 每块 63×64 个状态、每个状态现算
> `(run,size)` 码长并写 64×64 的回溯表，实测 **28.9 µs/块**（占 trellis 量化耗时的 99%，
> 标量 FDCT 只占 1%）。现在：
>
> - 零转移是确定的（`next[r] = dp[r-1] + coef²`），用一次向量移位完成；
>   只有 `next[0]` 需要取 min，回溯表从 64×64 int 缩到每位置一对 `(prevRun, value)`。
> - 码率按 `run = 16q + s` 展开：`bits = q×ZRL + len[s,cat]`，于是
>   `min_r(dp[r] + λ×bits) = min_s(G[s] + λ×len[s,cat])`，而 `G[s] = min_q(dp[16q+s] + q×λZRL)`
>   与候选无关、每位置只算一次；全部用 `Vector<int>`（SSE2/AVX2/NEON 由 JIT 选择）。
> - int32 定点（最大代价 ≈ 6×10⁸ < 2³⁰），加饱和；λ 被环境变量推到极端时回落旧 long 实现。
> - 候选的 `category` 用 `BitOperations.Log2`，同 `category` 的码率项在同位置复用。
>
> **实测**：单块 DP 28.9 µs → **3.3 µs（8.5x）**；`Amish-Noka-Dresser.png` 端到端
> 730 → 417 ms，143 MP `progressive.jpg` 44.9 s → **9.7 s（4.6x）**；三份产物 md5
> 与旧实现逐字节相同（新增 `VectorizedDp_MatchesLegacyCost` 以 5 个 λ × 40 个随机块
> 断言量化系数与 legacy 完全一致）。VNNI 在这里不适用（DP 是 min-plus，不是 int8 点积）。

### GIF

1. 解码全部帧，**跨帧共享一张全局调色板**（比每帧局部调色板更省）。
2. 二分搜索最少颜色数：颜色越少，LZW 字典命中率越高。
3. 用新增的 `GifEncoder.EncodeIndexed` 直接写「调色板 + 索引」，跳过编码器内部的二次量化，
   否则量化两次会导致画质不可控。
4. **帧间差分（不透明动画）**：动画候选预留 1 个透明索引；首帧完整，后续帧与上一帧索引相同的
   像素写透明索引，disposal=1 保留上一帧。差分帧只编码**变化区域包围盒**（crop），
   无变化帧写成 1×1 透明像素。实测 `final.gif` 23.39 MB → 17.65 MB（−24.5%）。
5. **局部调色板**：在全局表候选之外，对赢家再做一次「每帧独立量化 + 局部表 + 整帧」候选，
   按体积取小；`GifLocalPalettes` 默认开启。局部表与帧间差分互斥，因此是独立候选。
6. **帧去重**：连续完全相同的帧合并为一条，时长相加（无损）。
7. 透明动画走整帧 + 二值透明 + disposal=2，`large.gif` 2.42 MB → 2.14 MB（−11.5%）。
5. 单帧 GIF 保留透明通道（索引 0 预留）；透明动画现在会解码为 RGBA 帧序列，
   走「整帧 + 全局调色板 + 二值透明 + disposal=2」重编码，保留透明通道。
   透明动画暂不做帧间差分（单个透明索引无法同时表达“与上一帧相同”和“透明”）。

> 实测：照片类 GIF 380 KB → 300 KB（-21.1%，64 色 + 抖动）。

### WebP / BMP

- **动画 WebP 同格式 optimize 直接保留原图**（解码只取首帧），跨格式转换会在方案里标注。
- WebP：同样做质量搜索。当前策略（2026-10-17）：
  - **产出档按尺寸**：≤20 MP 用 `method 6`（实测体积小于 m3，耗时 +5%~14%），
    >20 MP 用 `method 3`（143 MP 上 m6 反而更大）。
  - **搜索用 q40 锚点 + 4 点小网格 + 二分**（`SearchQualityAnchored`）：锚点达标只在 (min,40] 找，
    不达标才进 (40,95]。143 MP 的候选数从 19 压到 8。
  - **下界**：小图 `WebpMinQuality=20`；>20 MP 默认 `WebpLargeImageMinQuality=40`
    （143 MP 从 q40 降到 q34 只省 7.8% 却多花 ~15 s）。需要极限体积时显式
    `--opt-webp-min 20 --opt-webp-large-min 20`。
  - **无损候选**：不透明、≤20 MP、唯一色数 ≤4096 时编码一次无损 WebP 与有损结果比体积。
    无损必须走 `WebPEncodeLosslessRGBA` 直通；`WebPConfig.lossless=1` 的配置路径实测仍有
    1~5 的像素误差（ffmpeg 解码同样如此），不是真无损。
  - m6 与 m2/m3 画质不完全一致：边界上 m6 不达标时先同 q 退回 m3，再升 q。
  - `WebpMinQuality`（默认 20，可用 `--opt-webp-min` 调整）。
  搜索期用 libwebp 的便宜档 `method 2`（比 `method 3` 便宜约一个数量级），赢家再按 `method 3` 补编一次 ——
  2048² 实测 1769 → 1191 ms / 1125 → 1076 ms / 1407 → 1208 ms，三张图产物与全程 `method 3` **逐字节一致**
  （补编用的是原始像素，与搜索期编码无关）。
  产出档从 `method 4` 改成 `method 3` 的原因见下一节：libwebp 1.6.0 的 m4 在 >140 MP
  且质量较高时有 4 倍悬崖，且 7 个样本（1~143 MP）上 m3 体积全部 ≤ m4。
  （2026-10-17 复现：143 MP 图上 m4 在 q40/q65 都是 ~7.8 s、无悬崖；**q75 时 7.8 s → 26.3 s**，
  体积 1,597,784 → 1,655,814 B（更大）。m5/m6 在 q75 无悬崖（8.4/8.7 s），但体积也都不比 m3 小。）
  ⇒ `WebpEncoderOptions` 构造时强制：**quality ≥ 75 且 method == 4 → 降为 3**。
  调用方显式传 `--webp-method 4 --quality 75` 也只会走 m3（实测 7.8 s / 1,597,784 B）。
  复现/诊断可设 `SIC_WEBP_ALLOW_M4_HIGHQ=1` 绕过保护。
  追加实验：阈值是 q≥75（q74.9 无悬崖，q75/76/80 均 26~35 s）；同样 143 MP 尺寸下，
  把原图重编码或模糊后 q75 m4 只有 ~9.7 s，141 MP 裁剪图 ~7.6 s ⇒ **内容 + 质量**共同决定，
  不是单纯像素数。源码上 m4 是 `RD_OPT_BASIC + full-SNS`、没有 m3 的 fast-probe；
  `frame_enc.c` 在 partition0 超限时会减半 header 预算并**重跑整幅分析 pass**，
  与 3 倍耗时吻合。m3 无 SNS、m5/m6 走 trellis，均不触发。
  历史上这里把下界硬抬到 65，理由是「低质量区间省不了多少体积却明显变糊」；
  实测 q=65 时画质已达标并被选中，说明 40~64 从未被试过。放开后 4.19 MP 照片类图
  44,554 → 30,234 B（−32%），PSNR 46.22 → 43.68 dB、感知 50.05 → 47.52 dB（正确 4 通道口径）。
- **143 MP 的 optimize 从 39.6 s 降到 14.2 s（−64%，产物 md5 不变）**，两处根因：
  1. **并行度被内存预算静默夹成 1**：`WebpOptimizer` 曾写死 `memoryBudget: 384 MB`，而单候选的
     峰值 ≈ 8 B/px（143 MP ≈ 1.14 GB）⇒ `384 MB / 1.14 GB = 0` ⇒ `degree = 1`，整条搜索退化成串行二分
     （7 个候选 × (m2 编码 2.8 s + 解码 0.9 s + 比较 0.9 s) ≈ 28 s）。改成跟随
     `CandidateParallelism` 的默认预算（可用内存 1/4，夹 384 MB~8 GB）后恢复真并行。
     —— 与 PNG 那处是同一类坑，**凡传死预算都要先算一遍「预算 / 单候选字节数」会不会夹成 1**。
  2. **二分的第一个探针选错了端点**：画质下限普遍宽松（感知 33 + 0.14×TargetQuality），
     WebP 在**下界 q=40 往往就已达标**，而旧轨迹先探最高档 95 再二分，7 次评估全部白做。
     现在**大图（≥8 MP）按「下界 → 上界 → 网格」顺序探**：下界达标即收尾（它已是允许的最小质量，
     任何轨迹都会收敛到它）；下界不达标才探上界，上界也不达标就直接交出上界（此时铺网格纯属白算）；
     只有下界不达标而上界达标才铺网格 + 窗口。小图（<8 MP）不预探——那里「网格含两端、命中 lo 即返回」
     本来就只要一波，预探反而多一波（1.2 MP 实测 443 → 530 ms，+18%）。
     **不并行探两端**：上界比下界贵（143 MP / m2：q95 3.62 s vs q40 2.42 s），下界达标时上界的墙钟全白付。
     143 MP 照片搜索期 4.5 s / 2 候选 → **3.3 s / 1 候选**（−27%），端到端 15.9/16.7 s → 14.7/15.1 s，md5 不变；
     代价只落在「两端都不达标」的罕见图（64 MP 纯噪声：搜索期 6.0 → 10.1 s，产物仍一致）。
  - 分阶段（143 MP / q40 / degree=7）：解码源图 0.55 s + 搜索 3.3 s（1 个候选，编码 2.5 + 解码 0.7）
    + 产出期 m3 编码 8.4 s。**产出期编码已占总时长 57%**，是当前地板。
  - 探针：`SIC_WEBP_OPT_TIMING=1`（输出搜索墙钟、候选数、每候选 CPU、产出编码耗时）。
- **已实测但否决的三条**（别重复尝试）：
  - `config.thread_level=1`：孤立编码 −7.8%（143 MP q40 m3 8.82 → 8.14 s，产物字节不变），
    但放进真实管线**反而更慢**（产出编码 8.8~9.2 s vs 8.4 s；搜索期与候选并行抢核），故保持 0；
  - 不透明图改走 RGB 导入：libwebp 的 RGBA→ARGB→YUV 确实比 RGB→YUV 慢 8.6%
    （同进程 143 MP：m3 7593 vs 8247 ms、m2 2242 vs 2435 ms，md5 相同），但要多持一份 packed RGB
    （143 MP +429 MB ≈ +33% 峰值内存）换 ~0.6 s，性价比不划算；
  - 直接按产出档编下界候选、达标就复用（省掉搜索的那次 m2 编码）：只在「下界一定达标」时成立，
    下界不达标的图要多付一次 m3 编码（143 MP +8.4 s）。4 通道口径下 q=40 实测达标率 3/5，
    低于 76% 的盈亏平衡点，不做。
- **画质口径已修正（2026-10-17，会改变选中的 q）**：搜索期的画质比较曾对 **RGBA 缓冲按
  `bytesPerPixel = 3`** 调用（`QualityMetrics.Compare(reference, decoded, w, h, 3)`），即把 RGBA
  字节流当 RGB 三元组读：只覆盖前 3/4 的数据、RGB 权重落在 RGBA 的交错字节上、4×4 块几何也错位。
  感知 PSNR 因此系统性虚高 —— 同一张 143 MP 图 q=40：3 通道口径 53.71 dB，4 通道口径 45.74 dB
  （差 8 dB，而达标线是 45.3 dB）。固定质量分支（`o.JpegQuality`）用的又是 4 通道，两条路径口径不一致。
  现已统一为 4 通道，并让固定质量分支走产出档 `method 3`；产出后会对最终字节重新解码、复评画质，
  `Quality` / `MeetsBar` 不再来自搜索期的 m2 编码。**代价是选档变化，属产品行为变更。**
  **实测（5 个样本，达标线 感知 45.32 dB / 像素 30 dB，下界 40）**：

  | 样本 | MP | 旧口径 q / 体积 | 新口径 q / 体积 | 4 通道实测画质 |
  |---|---|---|---|---|
  | `progressive.jpg` | 143 | 40 / 1,080,478 B | 40 / 1,080,478 B | 感知 46.60（+1.28） |
  | `Amish-Noka-Dresser.png` | 1.0 | 40 / 9,864 B | 40 / 9,864 B | 感知 45.31（+0.00） |
  | `car.png` | 0.3 | 40 / 1,720 B | 40 / 1,720 B | 感知 47.18（+1.86） |
  | `5_star_base.jpg` | 1.2 | 52 / 121,164 B | **81 / 205,192 B** | 感知 45.84（旧产物实际只有 41.18） |
  | `video-001.rgb.jpg` | 0.02 | 95 / 5,934 B | **91 / 4,718 B** | 感知 45.37（−20.5% 体积） |

  ⇒ 旧口径把「大图照片」的余量抬到 7~9 dB，等于**几乎每张大图都在下界 q=40 收工**（搜索只值 1 个候选，
  这是上一条优化的前提）。换成正确口径后，q=40 仍有 3/5 样本达标（余量 0.00~1.86 dB），
  其余样本必须向上搜索；`5_star_base` 的产物因此增大 69%，这是「以前其实没达标」的如实代价。
  把下界降到 20 还能再省（143 MP：q34 / 996,350 B，−7.8%），但现有网格+窗口搜索会从 1 个候选
  涨到 19 个、端到端 11.3 s → 27.0 s，故默认仍保持 40，等 WebP 搜索改成小扇出策略后再下调
  （见 `OptimizePlan.md` P1-6）。
- BMP：无法压缩，统一输出 PNG。

---

## 3. 实现清单

| 文件 | 作用 |
|---|---|
| `src/Compression/ImageOptimizer.cs` | 入口、格式嗅探、同格式/跨格式路由、二分搜索、收尾写盘 |
| `src/Compression/PngOptimizer.cs` | 调色板量化 + 无损重写回退 |
| `src/Compression/JpegOptimizer.cs` | 质量搜索 + 4:2:0 → 4:2:2/4:4:4 回退（采样比较为 opt-in） |
| `src/Compression/GifOptimizer.cs` | 跨帧共享调色板 + 降色 |
| `src/Compression/WebpOptimizer.cs` | WebP 质量搜索（RGBA，保留 alpha） |
| `src/Formats/Jpeg/JpegTrellisQuantizer.cs` | 块内 DP 的 trellis 量化（opt-in） |
| `src/Compression/OptimizeArtifact.cs` | 「纯计算产物」中间结构，供同格式/跨格式两条路径复用 |
| `src/Compression/PaletteQuantizer.cs` | RGBA 中位切分量化器 |
| `src/Compression/ImageQuality.cs` | 感知 / 逐像素画质度量 |
| `src/Compression/OptimizeOptions.cs` | 选项与画质档位 |
| `src/Compression/OptimizationResult.cs` | 结果（体积、方案、画质、是否跨格式转换、目标格式建议） |
| `src/Compression/MemoryGuard.cs` | 文件头宽高探测 + 峰值内存估算 + 准入判定（optimize/转换） |
| `src/Processing/StreamingConversion.cs` | 低内存逐行转换调度（BMP/PNG/GIF/WebP → BMP/PNG/GIF） |
| `src/Formats/*/*Row*.cs` | BMP/PNG 行编解码、GIF 首帧/单帧缓冲、WebP 增量解码、JPEG MCU 行 IDCT 行源 |
| `src/Formats/Jpeg/JpegFrameState.RowSource.cs` | JPEG 按 MCU 行反量化/IDCT/上采样/色彩转换 |

配套改动：

- `PngWriter` 新增调色板写入（PLTE/tRNS，颜色类型 3）、`PngFilterMode`（Up/Adaptive/None）、Deflate 级别参数。
- `PngAdaptiveFilter`：逐行在 None/Sub/Up/Average/Paeth 中选代价最小者，SIMD 实现。
- `GifEncoder.EncodeIndexed`：调色板 + 索引直写，支持单帧与动画、全局调色板、透明索引。

### PaletteQuantizer 的几个关键取舍

- **精确取色优先**：颜色数不超预算时建无损调色板，走 `Dictionary<uint,int>` 并在超预算时提前退出。
- **直方图**：5 bit/通道 + alpha 5 bit（不透明图只需 32 K 桶），大图抽样，桶只清零用过的部分。
- **中位切分**：按「像素数 × 最大通道跨度」选盒、按累计像素中位数切开；盒的调色板色取加权均值。
- **Lloyd 精修**：中位切分后按 5-bit 直方图桶权重做 1 轮 k-means（归桶 → 加权均值更新），
  同色数下降低量化误差；`QuantizeOptions.EnableKMeansRefinement` 默认开启。
- **最近色查找**：逐像素扫 256 色太慢，用「5 bit RGB 粗查表定种子 + 每色 16 近邻短表精修」，
  再叠一层 20 bit 键的结果缓存，实际降到常数级。
- **抖动**：Floyd–Steinberg 只扩散 RGB。alpha 抖动会在半透明区域产生可见噪点，故 alpha 取最近值。

---

## 4. 用法

### API

```csharp
using SharpImageConverter.Compression;

var result = ImageOptimizer.Optimize("photo.png");             // → photo.min.png
Console.WriteLine(result.Method);        // PNG 调色板 256 色 + 抖动
Console.WriteLine(result.SavedRatio);    // 0.467
Console.WriteLine(result.Quality);       // PSNR 36.57dB / 感知 48.51dB / 平均误差 2.54

if (result.KeptOriginal) { /* 无收益，输出是原图副本 */ }
```

| `OptimizeOptions` | 默认 | 说明 |
|---|---|---|
| `TargetQuality` | 88 | 目标画质 0-100，映射到感知 PSNR 下限 |
| `MinPerceptualPsnr` | 由 `TargetQuality` 推导 | 感知 PSNR 下限（dB） |
| `MinRawPsnr` | 30 | 逐像素 PSNR 下限（dB） |
| `MinSsim` | 0.95 | Y-SSIM 均值下限（0-1，0 表示不限制） |
| `MinWorstSsim` | 0.75 | 最差 1% 块 SSIM 下限（0-1，0 表示不限制） |
| `MaxColors` | 256 | PNG/GIF 调色板上限 |
| `EnableDithering` | true | Floyd–Steinberg 抖动 |
| `StripMetadata` | true | 丢 EXIF，保留 ICC |
| `JpegQuality` | null | 指定后不做搜索 |
| `JpegMinQuality` | 40 | JPEG 搜索下界 |
| `WebpMinQuality` | 20 | WebP 小图搜索下界（独立于 JPEG） |
| `WebpLargeImageMinQuality` | 40 | >20 MP 的 WebP 搜索下界（大图默认保速度） |
| `WebpTryLossless` | true | 低色数不透明图是否尝试无损 WebP |
| `WebpLosslessMaxUniqueColors` | 4096 | 无损候选的唯一色数上限 |
| `WebpLosslessMaxPixels` | 20 MP | 无损候选的像素上限 |
| `PngMaxDeflateOnWinner` | true | PNG 赢家是否再用 zlib 9 重压一次 |
| `PngMaxDeflatePixelThreshold` | 8 MP | 上述重压的像素上限（大图跳过，避免十几秒开销） |
| `AdaptiveFiltering` | true | PNG 无损重写在 Up / 逐行自适应之间实测取小（关闭则只试 Up） |
| `KeepOriginalWhenNoGain` | true | 变大就保留原图 |
| `MinSavingRatio` | 0 | 至少省该比例才采用 |
| `MinLossyGainRatio` | 0.10 | 同格式 JPEG/WebP 再压缩的最低收益，低于此值保留原图（避免为 1%~5% 再损失一代） |
| `JpegTrellisQuantization` | false | JPEG trellis 量化（4%~24% 体积 / 2~7 倍耗时，见 §2） |
| `MemoryLimitBytes` | 0（自动） | 内存预检预算（字节）；超过时 optimize 拒绝、转换尝试流式/拒绝 |
| `DisableMemoryGuard` | false | 关闭内存预检 |
| `Log` | null | 打印每个候选的体积与画质 |

预设：`OptimizeOptions.Conservative` / `Balanced` / `Aggressive`。

### CLI

```bash
dotnet run -- photo.png --optimize
dotnet run -- photo.png --optimize --memory-limit 4096         # 4 GB 内存预算（MB）
dotnet run -- photo.png --optimize --no-memory-guard           # 关闭内存预检
dotnet run -- photo.png --optimize --opt-jpeg-trellis          # trellis 量化（默认关闭）
dotnet run -- photo.png photo.progressive.jpg --to jpg --jpeg-progressive
dotnet run -- photo.jpg photo.small.jpg --optimize --opt-quality 75 --opt-verbose
dotnet run -- d:\site d:\site-min --optimize --recursive --parallel 8
```

---

## 5. 转换 + 智能压缩（一步到位）

### 5.1 动机

优化与格式转换原本是两条互斥路径：`--optimize` 保持原格式，`--to` 只转换。
想把 JPG 转成最小体积的 WebP，得先 `--to webp` 再对产物 `--optimize`。
后者的画质下限是拿**已经降质过的中间产物**当参考，属于二次有损，质量白丢一代。

现在 `--optimize` 与 `--to`（或带不同扩展名的显式输出路径）组合时，
会**解码一次源图，直接按目标格式做质量 / 调色板搜索并写出**，只经过一代编码。

### 5.2 库层语义

`ImageOptimizer.Optimize(input, output, options)` 的路由以**输出扩展名**为准：

| 输出扩展名 | 行为 |
|---|---|
| 与源格式相同 | 同格式压缩（原有行为）：无收益可复制原图，默认输出 `<原名>.min.<原扩展名>` |
| 与源格式不同 | **跨格式转换**：按目标格式搜索最小体积并写出；无论是否比源文件小都写出目标格式 |

跨格式时 `OptimizationResult.Converted == true`。此时 `KeptOriginal` 恒为 false
（「无收益保留原图」在跨格式场景没有意义——原格式 ≠ 目标格式），
`SavedRatio` 是两种格式的体积对比，仅供参考。

跨格式结果还可带 `Suggestion`（更优目标格式建议，启发式、不额外编码）：
目标不支持 alpha 却带透明 → 建议 WebP/PNG；照片类内容转 PNG/GIF → 建议 WebP；
颜色很少却转有损 JPEG → 建议 PNG/WebP；目标 BMP → 建议 PNG。CLI 会打印
`💡 目标格式建议: ...`。同格式 optimize 不产生建议。

CLI 路由条件：显式给了 `--to`，或输出路径带一个与源格式不同的可识别扩展名（`.jpg/.jpeg/.png/.bmp/.webp/.gif`）。
目标扩展名与源一致时仍走同格式压缩，避免误覆盖源文件。

### 5.3 各目标格式

| 目标 | 做法 | 备注 |
|---|---|---|
| `.jpg` / `.jpeg` | 质量二分搜索（4:2:0 → 4:2:2/4:4:4 回退）+ 按图优化 Huffman 表 | alpha 按 `OptimizeOptions.MatteColor`（默认白）合成到背景 |
| `.png` | 调色板量化二分搜索，必要时回退无损真彩色重写 | 保留 alpha |
| `.webp` | 质量搜索，下界 40；搜索期 `method 2`、赢家补 `method 3` | 保留 alpha（此前同格式优化只走 RGB） |
| `.gif` | 单帧调色板降色搜索 | 静态图；透明像素保留（索引 0 预留） |
| `.bmp` | 无损真彩色直写 | BMP 无压缩，写明「该格式无压缩」 |

> 从 `.png` 转 `.gif` / `.bmp` 等场景下，产物可能比源文件大（容器特性所致），
> 但因为用户显式要求转换，仍会写出目标格式，不会退化成复制原文件。

### 5.4 用法

```bash
# 单文件：JPG 转最小体积 WebP
dotnet run -- photo.jpg --to webp --optimize

# 显式输出路径：扩展名与源不同即自动走转换+压缩
dotnet run -- photo.jpg out.webp --optimize

# 目录批量：整目录转 WebP 并压到最小
dotnet run -- d:\images d:\out --to webp --optimize --recursive --parallel 8

# 调参同样生效
dotnet run -- photo.png --to jpg --optimize --opt-quality 75 --opt-verbose
```

输出示例（实测 240×160 合成照片）：

```text
✅ photo.jpg → photo.webp: 54.8 KB → 12.8 KB | WebP 质量 65 | PSNR 43.12dB / 感知 53.78dB / 平均误差 0.76
✅ photo.png → photo.jpg: 77.2 KB → 4.9 KB | JPEG 质量 68（4:2:0） | PSNR 43.61dB / 感知 45.44dB / 平均误差 1.18
✅ photo.png → photo.gif: 77.2 KB → 73.1 KB | GIF 调色板 32 色 + 抖动 | PSNR 36.78dB / 感知 48.33dB / 平均误差 2.38
✅ photo.png → photo.bmp: 77.2 KB → 791.1 KB | BMP 无损（该格式无压缩） | PSNR 99.00dB / 感知 99.00dB
```

---

## 6. 已知边界

- **16-bit PNG / APNG / 动画 WebP** 的同格式 optimize 会直接保留原图（重编码会截断位深或只留首帧）；
  跨格式转换会在方案描述里标注降级。详见 §2。
- **带透明通道的动画 GIF** 不做有损优化（解码器只暴露 RGB24 帧序列），会保留原图并说明原因。
- 半透明 PNG 的 alpha 不参与抖动，极平滑的 alpha 渐变可能出现轻微色带（5 bit 桶精度上限）。
- 感知度量基于 4×4 块平均，对「纯噪点纹理被抹平」不敏感，靠 `MinRawPsnr` 兜底；
  若你的素材以高频纹理为主，建议把 `MinRawPsnr` 提到 33~35。
- 已经是高质量编码的产物（如 q92 的 JPEG）通常判为「无收益」，这是预期行为而非失败。
