# `--optimize` 现状审计：方向性问题与改进空间

> 审计对象：`src/Compression/`（`ImageOptimizer` / `PngOptimizer` / `JpegOptimizer` /
> `GifOptimizer` / `WebpOptimizer` / `PaletteQuantizer` / `ImageQuality`）与 `docs/Optimize.md`。
> 审计目的：在近期「按格式优化 optimize 耗时」之后，回答两个问题：
> **有没有根本方向就错了的地方？有没有明显做得不够好的地方？**
> 对应执行计划见 [OptimizePlan.md](OptimizePlan.md)。
> 修复进展（2026-10-17）：P0-1/2/3、P1-1/2/3、P1-4b 已完成；
> 本文保留的是审计时的原始结论，具体改动与实测数据以执行计划为准。

---

## 0. 结论速览

搜索期工程（JPEG 系数直通、样本缓存、候选并行、按内存夹并行度）本身是扎实的，
不是白做。但**继续在“搜索”上抠时间已接近收益上限，剩下的大头在“判据、候选集合、产出阶段”**，
其中数处属于方向性问题。

| 编号 | 问题 | 影响 | 性质 |
|---|---|---|---|
| A1 | 唯一画质判据是 4×4 块平均 PSNR | 全局选档可能偏离人眼观感，且系统性奖励抖动 | **方向错误** |
| A2 | WebP 用 `bytesPerPixel=3` 读 RGBA | 感知 PSNR 虚高约 8 dB，近期 WebP 调优建立在错判据上 | **Bug / 方向错误** |
| A3 | 已压缩格式一律重编码，无「无损重封装 + 最小收益门槛」 | 用一代画质损失换微小体积 | **方向错误** |
| A4 | GIF 动画只做全局调色板 + 降色 | 漏掉帧间差分/局部调色板等真正大头；透明动画直接不优化 | **方向错误** |
| A5 | PNG 搜索期与产出期不分档，产物永远 zlib 6 | 白丢 3%~7%（zlib 9）到更多（zopfli） | 明显欠缺 |

各格式的具体缺口见 §2，优先级与执行顺序见 [OptimizePlan.md](OptimizePlan.md)。

---

## 1. 全格式共用的地基问题

### 1.1 画质判据：4×4 块平均 PSNR 是错误的目标函数

`src/Compression/ImageQuality.cs` 的 `PerceptualPsnr` = 先做 4×4 盒式平均，再算加权 PSNR。
它有三个硬伤：

- **可被抖动直接“刷分”**。`docs/Optimize.md` §1.1 自己写了“抖动让感知分提升近 20 dB”。
  一个指标如果把误差打散就能涨 20 dB，那它衡量的是“误差是否低频化”，不是“人眼是否看得出来”。
  而整个搜索又是在最大化这个指标——等于**用自己定义的尺子证明自己抖动得对**。
- **对块周期结构近乎全盲**。4 像素周期的条纹/棋盘/振铃在 4×4 平均后会相互抵消，
  PSNR 可以到 99，但人眼很明显。JPEG 的 8×8 块效应、半色调图案都属于这类。
- **与观看尺寸无关**。143 MP 图和 300×200 缩略图都用 4 px 块判定：
  前者在 fit-to-screen 下 8 dB 的误差可能完全看不见，后者 2 dB 就很明显。

后果是具体的：`MinRawPsnr=30` 又把“不抖动”的干净量化拦掉
（文档里 256 色不抖动照片 22.1 dB），于是照片类几乎**只能用抖动**——
而抖动正是 PNG/GIF 体积膨胀的主因（GIF 实测同图 92 KB vs 17 KB）。
这不是调参问题，是判据在把管线往“画质分数好看、文件更大”的方向推。

**建议方向**：换成 SSIMULACRA2 / Butteraugli（离线/可选），
或退一步用 **Y 通道 SSIM + 最差窗口/分位数池化 + 按图像尺寸缩放块大小**，
并单独加一条“色带/噪点”检测。短期至少把 `MinRawPsnr` 和抖动解耦，
允许“不抖动 + 高色数”作为合法候选。

### 1.2 WebP 用 3 通道读 RGBA（已知但未修）

`ImageOptimizer.SearchQualityParallel` / `SearchQuality` 里对 WebP 的 RGBA 缓冲调用
`QualityMetrics.Compare(..., bytesPerPixel: 3)`：只读前 3/4 数据、RGB 权重落在错位的字节上、
4×4 块几何也错。文档自己算了：同一张 143 MP 图 q=40，
3 通道口径 53.71 dB，正确 4 通道 45.74 dB——**虚高 8 dB，而达标线就是 45.3 dB**。

这意味着最近那条“搜索下界优先”的性能优化（以及“大图几乎都在 q=40 收工”的前提）
是**在一个假达标线上成立的**。修正口径后 q=40 会普遍不达标，
搜索策略、并行度、候选预算都要重新评估。

另有两处一致性 bug：

- `JpegQuality` 固定档走 4 通道、搜索走 3 通道，同一功能两套口径；
- `WebpOptimizer` 固定质量分支用 `EncodeAt(q, SearchMethod, isFinal: true)`，
  **最终产物用的是搜索档 method 2，不是 `OutputMethod=3`**。

### 1.3 对已压缩格式“一律重编码”，缺少无损重封装与收益门槛

`JpegOptimizer` 对任何 JPEG 都是解码→重编码，`MinSavingRatio` 默认 0。

- **JPEG 缺少 jpegtran 式的无损优化**：剥离 EXIF、`optimize_coding` 重排 Huffman、
  lossless 转 progressive，这些是**零代际损失**的，几毫秒到几十毫秒，
  常见能省 5%~20%（元数据多的文件更多）。现在的做法是“只要能小 1% 就再压一代”，
  用户在无损场景下白白多丢一代画质。
- 正确顺序应是：原图 → 无损重封装 → 有损重编码，
  **只有有损结果比无损结果再小一个显著比例（如 10%）才采用**。
- WebP / GIF 同理：WebP 可先做 chunk 级重封装/去元数据；
  GIF 可先做无损帧优化（见 §2.4）。

### 1.4 16-bit / 动画输入会被静默降级

`PngDecoder` 对 16-bit 只取高字节，`PngOptimizer` 再标记成“无损”：
8-bit 输出相对“截断后的像素”确实无损，但相对源文件是有损的。
同理，APNG / 动画 WebP 的输入很可能只解第一帧，
optimize 会把动画变成静帧。这类情况至少要检测并拒绝优化（保留原图），而不是静默改写。

### 1.5 搜索策略的自施加约束

- **并行搜索把已经算出来的候选丢掉了**。PNG/GIF 为了“与串行二分逐字节一致”，
  只用 `ReplayLadderVisits` 访问过的候选参与体积比较，而并行阶段多算的达标候选直接丢弃。
  既然已经付了编码成本，**在全部已评估的达标候选里取体积最小**是免费且严格更优的。
  要确定性，可以改成“固定网格全扫 + 全候选比较”，而不是执着于复刻二分轨迹。
- **JPEG/WebP 只取“最低达标 q”，不比较体积**。质量-体积不是严格单调
  （Huffman 表、色度表切换会造成局部反转），低 1 档不一定更小。
  在阈值附近多看 1~2 档的成本很低。
- `SearchQualityParallel` 的网格点数 = `degree`，不同核数的机器看到的候选集合不同。
  现在靠第二阶段全枚举窗口保证同解；一旦改成“全候选取最小”，需要显式的确定性策略。

### 1.6 其他全局项

- `CandidateParallelism` 用 `TotalAvailableMemoryBytes / 4`（总可用，不是当前空闲），
  内存紧张时仍可能 OOM。工程风险，不是方向问题。
- 画质比较对 RGBA 的 alpha 权重 0.2，但 RGB 未预乘：
  完全透明像素的 RGB 噪声仍会计分，而透明区的 RGB 在显示上没有意义。

---

## 2. 各格式的具体缺口

### 2.1 PNG（收益最大，也漏得最多）

1. **最终产物永远 zlib 6，从不给赢家补一遍 9/zopfli。**
   `PngOptimizer.Encode` 自始至终使用 `ImageOptimizer.OptimizePngDeflateLevel`（Optimal）。
   搜索期用 6 是对的，但产出期应像 WebP 那样“搜索用便宜档、赢家再按正式档补编一次”。
   一次额外编码换 3%~7%（zlib 9），zopfli 还能再多一截。
   **这是当前最容易拿的收益，代码改动最小。**
2. **调色板永远是 8-bit。** `PngWriter.CreateIHDRPalette` 写死 `data[8]=8`。
   16 色图应 bit depth 4、4 色用 2、2 色用 1——对 UI/图标/截图，IDAT 直接小 2~8 倍
   （deflate 能补回一部分，但仍显著）。同理没有灰度颜色类型
   （`PngWriter.WriteGray` 已存在，但 Compression 从没用过）。
3. **调色板图像的滤波固定用 Adaptive，从没实测过 None/Up。**
   无损真彩色路径已用 143 MP 实测证明“Up 比 Adaptive 小 19.2%”，
   却对索引图没有做同样的实验。PNG 规范本身也不推荐对调色板图做滤波；
   这里很可能在持续付体积税。
4. **抖动不是搜索维度。** GIF 已发现“抖动 92 KB vs 不抖 17 KB”，
   PNG 却把 `EnableDithering` 当固定开关，从不比较同色数下抖动/不抖动的产物。
   这是 PNG 当前最大的候选集合缺失。
5. **`needLossless` 的 10% 启发式会漏掉更小的无损产物。**
   只要量化省了 11%，就再也不试无损真彩色；完全可能存在“无损比有损候选更小”的图
   （图形、渐变、文字截图）。无损真彩色只多一次编码，应始终参与体积竞争。
6. 量化器只有中位切分，没有 k-means/Lloyd 精修；直方图 5 bit/通道，
   128~256 色档的调色板质量有提升空间（同色数下画质更好，或同画质下更少颜色）。
7. 调色板顺序未针对 deflate 优化（PLTE 重排 / 索引重排）。

### 2.2 JPEG

1. **没有 progressive**（文档已承认），同画质下 5%~10%，是当前最大的单项体积缺口。
2. **4:2:0 一旦达标就永远不试 4:4:4**。色度丰富但 4:2:0 刚好达标的图，
   很可能 4:4:4 在更低 q 下体积更小、观感更好。应两个都搜、都达标时按体积取小；
   4:2:2 也可作为中间候选。
3. **没有 trellis 量化**（mozjpeg/jpegli 的 5%~10%），也没有自定义量化表。
4. 赢家最终要重跑两遍完整编码（统计 Huffman + 编码），
   而搜索期其实已经算过赢家的量化系数，可以直接拿来统计符号分布，省掉一遍 produce+DCT。
5. 无损重封装缺失（见 §1.3）。

### 2.3 WebP

1. 判据 bug（见 §1.2）。
2. **产出 method 全局固定 3**，因为 m4 在 >140 MP 有 4 倍悬崖。
   正确做法是**按像素数选 method**：小图（如 ≤20 MP）用 m4/m5/m6，大图降档。
   现在为了超大图，把所有小图也锁在 m3。
3. **没有 lossless / near-lossless 候选**。截图、图形、UI 素材用有损 WebP q40
   经常比 lossless WebP 还大；cwebp 的 `-lossless -q` / `-near_lossless` 是这类图的正确方向。
4. 没有调 `alpha_quality`、`filter_strength`、`sns_strength`、`segments`、`preprocessing`；
   libwebp 自带搜索（`cwebp -q` 的多轮）就是靠这些。
5. 搜索期用 m2 编码后的解码结果判定达标，产出期换 m3——
   **最终文件的真实画质从未被重新评估**，`Quality` 字段报的是 m2 的数。
   产出后补一次解码+比较（只一次）更诚实。
6. 动画 WebP 输入会被静默处理成单帧（需加保护）。

### 2.4 GIF

1. 动画缺帧间优化：逐帧局部调色板、透明差分、disposal/crop/帧去重、lossy LZW
   （gifsicle `-O3 --lossy` 的核心）。全局调色板只在 PLTE 上省几百字节，
   代价是所有帧共享更差的颜色。
2. 透明动画直接不支持；现实里的 GIF 大多数正是带透明的动画。
3. **画质判定基于抽帧/抽行**：`DownsampleRows` + 采样帧拼接后一次 `Compare`，
   选出的候选可能在全图或未采样帧上不达标。`QualityMetrics.CompareWorst` 存在但没被使用。
4. `MaxDitherCostRatio = 2.0` 是拍的，可让抖动候选直接参与全局最小体积竞争。
5. 单帧基线兜底（Octree+Bayer8 256 色）思路很好，保持不变。

### 2.5 BMP

只能转 PNG，没问题。但 32-bit BMP 的 alpha、以及输出扩展名被改成 `.png` 时的
`OptimizedSize` 口径需要确认。

### 2.6 跨格式

- **透明图转 JPEG/GIF 时没有合成背景**（已修复 JPEG/BMP；GIF 保留二值透明）：
  `JpegOptimizer.RunFromImage` 曾直接 `LoadRgb24`，透明像素的 RGB 被原样带过去（通常是黑），会出黑边。
- 目标格式完全由扩展名决定，没有“同画质下哪种格式更小”的建议能力
  （PNG 照片 → WebP 往往小 30%+）。不算 bug，但与“智能压缩”的定位有落差。

---

## 3. 哪些做法是对的（不要动）

为避免“审计只列问题”，这些经过实测的设计应保留：

- JPEG 搜索期系数直通 + `ReplayLadderVisits` 的像素级等价性（`JpegCoefficientBypassTests`）；
- `JpegSampleCache` 把 produce 与质量档解耦（13 ms / 143 MP）；
- 重建缓冲用 `GC.AllocateUninitializedArray` 避免 429 MB 清零；
- 候选并行 + 按内存预算夹并行度（含 WebP 384 MB 夹成 1 的修复）；
- PNG 无损真彩色「Up vs 自适应实测取小」；
- GIF 的抖动/不抖动双候选、`EncodeIndexed` 直写、常规编码基线兜底；
- WebP 搜索 m2 / 产出 m3 的分档思路（PNG 应照抄）；
- 各格式的 `SIC_*_TIMING` 分阶段探针与同二进制 A/B 方法论。

---

## 4. 总评

搜索期的性能优化已经做得很细，继续在二分/并行上抠时间收益递减。
真正该动的是：**判据是否可信、候选集合是否完整、产出阶段是否尽了最大努力。**
其中 WebP 的 3 通道 bug 和 PNG 的“产出永远 zlib 6 / 永远 8-bit 调色板”
是最像“方向错了”的三处；GIF 动画与 JPEG 无损重封装则是整块能力缺失。
