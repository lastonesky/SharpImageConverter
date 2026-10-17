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
| `PerceptualPsnr` | 先做 4×4 块平均再算 PSNR | **主判据**：抹平抖动颗粒，代表「看不看得出来」 |
| `Psnr` | 逐像素 PSNR | **副判据**：拦住「高频细节被抹掉 / 抖动过头」，下限 `MinRawPsnr`（默认 30 dB） |

实测（900×600 合成图）：

| 场景 | 逐像素 PSNR | 感知 PSNR |
|---|---|---|
| PNG 256 色 + 抖动（照片） | 36.6 dB | 48.5 dB |
| PNG 128 色 + 抖动（照片） | 34.4 dB | 43.6 dB |
| PNG 256 色**不**抖动（照片） | 22.1 dB | 29.4 dB |
| JPEG q80（照片） | — | 45.3 dB |

可以看到：抖动让感知分提升近 20 dB（这正是我们要的效果），
而「块平均」对**纯噪点被抹平**这类损伤不敏感，所以再叠一条逐像素下限兜底。

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

**搜索期的编码用低一级的 deflate**：二分的判定完全来自画质（纯像素比较），体积只用于
「在达标候选里挑一个最小的」；而候选之间的体积差距有 10%~40%，远大于压低 deflate 级别
带来的 2.6%~6.6% 偏差，因此 PNG 搜索期统一用 `CompressionLevel.Optimal`（zlib 6）
而不是 `SmallestSize`（zlib 9，见 `ImageOptimizer.OptimizePngDeflateLevel`）。
4.19 MP 实测：调色板单候选编码 682.0 → 91.7 ms（7.4 倍），PNG `--optimize` 全流程
4161 → ~1210 ms，代价是产物 +3.90%（922,581 → 958,558 B）。
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
内存预算缺省按 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4` 推导（夹在 384 MB ~ 8 GB）：
单候选峰值是「索引 1 B/px + 还原缓冲 4 B/px」≈ 5 B/px，原先固定 512 MB 在 143 MP 上会被夹成
`dop = 1` 并**静默退化为串行**。

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

搜索是这条链路的主要开销：每次候选 = 一次量化与重建（开启 `OptimizeHuffman` 时赢家再走两遍
完整编码：先统计符号分布再编码）+ 一次画质评估。1.57 MP 的 PNG→JPEG 实测
（1254×1254，Release）：单次普通编码 0.17 s、单次带优化 Huffman 的候选 0.32 s、
7 次候选的完整搜索 1.10 s —— 即**搜索约占 70%**（二分 [40,95] 恰好 7 次评估，
`--opt-verbose` 会把每个候选的画质打出来）。

体积并不严格随颜色数单调（中位切分在不同预算下切法不同，会有几个百分点的抖动），
所以最后一步是在**所有达标候选中按体积取最小**，而不是直接取最激进的那个。

---

## 2. 各格式策略

### PNG（收益最大）

1. 颜色数 ≤ 上限时直接**精确取色**（无损），不再做有损尝试。
2. 否则量化到调色板（颜色类型 3，每像素 1 字节而非 3~4 字节）+ Floyd–Steinberg 抖动。
3. 量化不达标时回退**无损真彩色重写**：真彩色（非调色板）编码，对渐变类图像尤其有效
   （实测抖动后索引近乎随机，调色板反而会比真彩色大 19 倍）。
   行滤波在 **Up 与逐行自适应之间实测取小**——自适应是 MSAD 启发式（最小化滤波后字节的 L1 范数），
   而 deflate 吃的是跨行重复：整列统一 Up 时相邻行差分高度相似，LZ77 能跨行匹配整行，
   逐行换滤波器会打断这条链。143 MP 照片实测 Up 29,469,583 B vs 自适应 35,142,122 B（**自适应大 19.2%**，
   且慢 2.4 倍），而 1000×1000 16 色调色板产物则是自适应小 3.2% ⇒ 不能按启发式定，只能实测。
4. 量化收益不足 10% 时也会额外算一次无损重写做对比。

> 实测：照片类 845 KB → 451 KB（-46.7%）；全色域渐变 13.6 KB → 8.3 KB（-38.8%，无损）；
> 6 色图形 3.3 KB → 1.4 KB（-57.4%，无损）。

### JPEG

1. 解码 → 以 4:2:0 重编码，二分搜索「刚好达标」的最低质量。
   搜索期不经过比特流（系数直通 + 并行画质评估），见 [1.3.2](#132-搜索期不经过比特流jpeg)。
2. 若 4:2:0 始终不达标（彩色文字、色度高频丰富的图），自动退回 4:4:4 再搜一轮。
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

**与 TinyPNG 的剩余差距**：TinyPNG 产物是 progressive（SOF2）编码，同画质下比 baseline 再小
5%~10%；我们的编码器目前只支持 baseline（`WriteSof0`）。要进一步追平需要实现 progressive 编码
（多扫描、频谱选择、逐次逼近、EOB 游程），是一项独立的大改动。

### GIF

1. 解码全部帧，**跨帧共享一张全局调色板**（比每帧局部调色板更省）。
2. 二分搜索最少颜色数：颜色越少，LZW 字典命中率越高。
3. 用新增的 `GifEncoder.EncodeIndexed` 直接写「调色板 + 索引」，跳过编码器内部的二次量化，
   否则量化两次会导致画质不可控。
4. 单帧 GIF 保留透明通道（索引 0 预留）；**带透明通道的动画**目前只解码得到 RGB24 帧序列，
   重编码会丢失透明度，因此直接保留原图并给出原因。

> 实测：照片类 GIF 380 KB → 300 KB（-21.1%，64 色 + 抖动）。

### WebP / BMP

- WebP：同样做质量搜索，下界取 `JpegMinQuality`（默认 40）。
  搜索期用 libwebp 的便宜档 `method 2`（比 `method 3` 便宜约一个数量级），赢家再按 `method 3` 补编一次 ——
  2048² 实测 1769 → 1191 ms / 1125 → 1076 ms / 1407 → 1208 ms，三张图产物与全程 `method 3` **逐字节一致**
  （补编用的是原始像素，与搜索期编码无关）。
  产出档从 `method 4` 改成 `method 3` 的原因见下一节：libwebp 1.6.0 的 m4 在 >140 MP 有 4 倍悬崖，
  且 7 个样本（1~143 MP）上 m3 体积全部 ≤ m4。
  历史上这里把下界硬抬到 65，理由是「低质量区间省不了多少体积却明显变糊」；
  实测 q=65 时画质已达标并被选中，说明 40~64 从未被试过。放开后 4.19 MP 照片类图
  44,554 → 30,234 B（−32%），PSNR 46.22 → 43.68 dB、感知 50.05 → 47.52 dB（正确 4 通道口径）。
- BMP：无法压缩，统一输出 PNG。

---

## 3. 实现清单

| 文件 | 作用 |
|---|---|
| `src/Compression/ImageOptimizer.cs` | 入口、格式嗅探、同格式/跨格式路由、二分搜索、收尾写盘 |
| `src/Compression/PngOptimizer.cs` | 调色板量化 + 无损重写回退 |
| `src/Compression/JpegOptimizer.cs` | 质量搜索 + 4:2:0/4:4:4 回退 |
| `src/Compression/GifOptimizer.cs` | 跨帧共享调色板 + 降色 |
| `src/Compression/WebpOptimizer.cs` | WebP 质量搜索（RGBA，保留 alpha） |
| `src/Compression/OptimizeArtifact.cs` | 「纯计算产物」中间结构，供同格式/跨格式两条路径复用 |
| `src/Compression/PaletteQuantizer.cs` | RGBA 中位切分量化器 |
| `src/Compression/ImageQuality.cs` | 感知 / 逐像素画质度量 |
| `src/Compression/OptimizeOptions.cs` | 选项与画质档位 |
| `src/Compression/OptimizationResult.cs` | 结果（体积、方案、画质、是否跨格式转换） |

配套改动：

- `PngWriter` 新增调色板写入（PLTE/tRNS，颜色类型 3）、`PngFilterMode`（Up/Adaptive/None）、Deflate 级别参数。
- `PngAdaptiveFilter`：逐行在 None/Sub/Up/Average/Paeth 中选代价最小者，SIMD 实现。
- `GifEncoder.EncodeIndexed`：调色板 + 索引直写，支持单帧与动画、全局调色板、透明索引。

### PaletteQuantizer 的几个关键取舍

- **精确取色优先**：颜色数不超预算时建无损调色板，走 `Dictionary<uint,int>` 并在超预算时提前退出。
- **直方图**：5 bit/通道 + alpha 5 bit（不透明图只需 32 K 桶），大图抽样，桶只清零用过的部分。
- **中位切分**：按「像素数 × 最大通道跨度」选盒、按累计像素中位数切开；盒的调色板色取加权均值。
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
| `MaxColors` | 256 | PNG/GIF 调色板上限 |
| `EnableDithering` | true | Floyd–Steinberg 抖动 |
| `StripMetadata` | true | 丢 EXIF，保留 ICC |
| `JpegQuality` | null | 指定后不做搜索 |
| `JpegMinQuality` | 40 | 搜索下界 |
| `AdaptiveFiltering` | true | PNG 无损重写在 Up / 逐行自适应之间实测取小（关闭则只试 Up） |
| `KeepOriginalWhenNoGain` | true | 变大就保留原图 |
| `MinSavingRatio` | 0 | 至少省该比例才采用 |
| `Log` | null | 打印每个候选的体积与画质 |

预设：`OptimizeOptions.Conservative` / `Balanced` / `Aggressive`。

### CLI

```bash
dotnet run -- photo.png --optimize
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

CLI 路由条件：显式给了 `--to`，或输出路径带一个与源格式不同的可识别扩展名（`.jpg/.jpeg/.png/.bmp/.webp/.gif`）。
目标扩展名与源一致时仍走同格式压缩，避免误覆盖源文件。

### 5.3 各目标格式

| 目标 | 做法 | 备注 |
|---|---|---|
| `.jpg` / `.jpeg` | 质量二分搜索（4:2:0 → 4:4:4 回退）+ 按图优化 Huffman 表 | alpha 被丢弃（JPEG 不支持） |
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

- **带透明通道的动画 GIF** 不做有损优化（解码器只暴露 RGB24 帧序列），会保留原图并说明原因。
- 半透明 PNG 的 alpha 不参与抖动，极平滑的 alpha 渐变可能出现轻微色带（5 bit 桶精度上限）。
- 感知度量基于 4×4 块平均，对「纯噪点纹理被抹平」不敏感，靠 `MinRawPsnr` 兜底；
  若你的素材以高频纹理为主，建议把 `MinRawPsnr` 提到 33~35。
- 已经是高质量编码的产物（如 q92 的 JPEG）通常判为「无收益」，这是预期行为而非失败。
