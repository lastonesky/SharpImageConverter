# WebP 第二轮质量/流式迭代规格（任务 2 / 3 / 4）

## 0. 状态与审批

- 本文档为**待审批规格**；审批通过前不修改生产代码。
- 审批后按 `tasks.md` 的顺序**串行**执行（同一工作区不做并发构建）。
- 不创建 git 提交，保留工作区改动。
- 公开 API、JPEG 与其他格式、既有 WebP 行为均保持兼容。

## 1. 目标

1. **任务 2（有损 VP8 压缩率）**：缩小与 libwebp 的同 q 体积差距，重点改善纯色/UI
   （`rgba_panel` 当前 1.5–2.7×）。落地三项：系数 trellis 量化（真实 RD 代价）、
   分段量化重做（按内容自适应、4 段、按段滤波 delta）、滤波强度参与 RD 决策。
2. **任务 3（无损 VP8L 多 pass）**：首遍按实际码长评估，二遍重算 Huffman / 组划分，
   以实际总字节数为准选择，给出压缩率对比。
3. **任务 4（真行级流式解码）**：`WebpRowSource` 不再"先解整帧再逐行"。
   - 4A：VP8L（无损静态图、ALPH alpha 平面）真行增量解码，可直接落地。
   - 4B：VP8（有损）真行增量解码，采用窗口化重建/输出缓冲的分阶段改造。
   - 若 4B 因架构风险未完成，只保留 4A 并在报告中明确说明，不留半截代码。

## 2. 基线测量与验收方法（三项共用）

### 2.1 语料（1024×1024，确定性生成）

| 名称 | 内容 |
|---|---|
| `photo` | `examples/` 内真实照片缩放/裁切到 1024² |
| `ui` | 块状 UI 截图（4 色块、边框、文字条） |
| `grad` | 平滑 RGB 渐变 |
| `a_text` | 白底黑字文字截图（多字号横竖笔画） |
| `rgba_panel` | 大面积纯色面板 + 少量 1px 边框/图标 |
| `rgba_softgrad` | 低频柔渐变（含 alpha 渐变） |

### 2.2 指标

- **体积**：最终 RIFF 字节数。
- **PSNR**：RGB 三通道 MSE；一律用 **libwebp 解码**产物测量，避免自家解码器掩盖编码问题；
  同一码流再用托管解码器解一次，要求 RGB 与 libwebp 逐通道差 ≤1。
- **对照**：libwebp `WebPEncodeRGBA(quality=q)` 同 q；q ∈ {50, 75, 90, 95}。
- **A/B 方式**：改动前先把当前 `SharpImageConverter.dll` 快照到
  `.perf/baseline/`，测量工具用 `AssemblyLoadContext` 在同进程内交替调用
  基线/候选，抵消 CPU 频率与负载漂移（复刻 `tools/perf/WebpProbe/Comparison.cs`）。
- **测量载体**：`.perf/QualityProbe`（临时工程，不进入交付目录）；输出 CSV：
  `case,q,ours_bytes,baseline_bytes,libwebp_bytes,ours_psnr,baseline_psnr,libwebp_psnr,sha256`。

### 2.3 验收阈值

- 任务 2：24 组（6 图 × 4 q）总体积相对基线 **≥ −8%**；平均 PSNR 下降 ≤0.2 dB；
  单组体积劣化 ≤ +2%。`rgba_panel` 的"我方/libwebp"在 q75/q90 **≤ 1.25×**，
  q50 **≤ 1.35×**（当前 1.90/2.00/1.50）。未达标必须给出实测数据与根因，不得隐藏。
- 任务 3：无损体积相对基线，photo/ui/text 三类平均 **≥ −2%**，单图不得劣化 > 0.5%；
  所有产物与源 RGBA **bit-exact**，且 libwebp 可解。
- 任务 4：流式逐行输出与整图解码**逐像素一致**（含奇数宽高、alpha、q30/75/95）；
  `20000×64` 与 `64×20000` 长图的托管驻留峰值相对现状下降 **≥ 80%**
  （用 `GC.GetTotalMemory(false)` 采样 + 进程私有字节对比，记录数值）。

## 3. 任务 2：有损 VP8

### 3.1 系数 trellis（新文件 `src/Formats/Webp/Vp8Trellis.cs`）

**目标**：替换当前"标量四舍五入"的 `Quantize(int coefficient, int dq)`，在量化时按
`D_coef + λ·R` 选择系数电平。

- 输入：一个 4×4 块的 16 个变换域系数（光栅序）、类型（0=Y1 AC / 1=Y2 / 2=UV / 3=i4x4）、
  初始 ctx（邻居非零上下文 0..2）、DC/AC 量化步长、当前概率表、λ。
- 代价模型：从 `_probs` 生成只读成本表 `cost(type, band, ctx, node, bit)`，复用
  `Vp8CostTable`（与真实写位同一张表，单位 1/256 bit）。
- **精确 DP**（不是逐系数贪心）：
  - 状态 `(m, ctxAfter)`：`m` = 已确定的最后一个非零系数扫描位置（−1 表示尚无），
    `ctxAfter` = 该非零之后的节点上下文（1/2；无则初始 ctx）。
  - 位置按扫描序 `n=first..15` 推进，候选电平
    `{0, nearest, nearest±1, nearest±2}`（夹到 ±`MaxCoefficient`）；
  - 空档代价：`m` 与下一个非零 `m'` 之间每个零系数支付
    `node0(1)+node1(0)`（首个零用 `ctxAfter`，其余用 ctx=0）；
  - 终止代价：在任意 `q ∈ (m,15]` 结束支付 `node0(0)`（`q=m+1` 用 `ctxAfter`，否则 ctx=0），
    覆盖 `q=16` 的"无终止位"；
  - 失真：`Σ (c_i − level_i·dq_i)²`；Y2 系数权重 4（WHT/IDCT 增益折算），其余权重 1。
- 输出：扫描序量化电平、反量化光栅序 `short` 槽位、每块 last 索引（供 nz 码）。
- λ：整数 Q8，`λQ8 = TrellisLambda × dq`（dq 取该类型 AC/DC 步长）。`TrellisLambda`
  初值 24，在测量阶段对 {8,16,24,32,48} 做一次扫描，按 2.3 的总体积/PSNR 选值并记录。
- 单块 DP 复杂度上限：16 位置 × 3 ctx × 17 m × 5 候选 ≈ 4k 状态，可接受；不分配堆对象。

**集成点**：

- `Vp8Encoder.PrepareLumaResidual`（L1083）：16 个 Y1 AC 块与 Y2 块。
- `Vp8Encoder.PrepareChromaResidual`（L1159）：U/V 各 4 块。
- `Vp8Encoder.PrepareI4Block`（L889）：i4x4 每块（调用前已有 `context`，需作为参数传入）。
- 量化结果必须同时写入 `_lumaQuant/_uvQuant/_y2Quant`（扫描序）与反量化槽位，
  保证 `WriteResidualTokens` 与重建使用同一电平。
- 两遍编码：两遍都执行 trellis；第一遍用默认概率、第二遍用更新后的概率（现有架构），
  两遍重建仍由同一确定性函数产出。`CostI4Block` 的成本试探保持现状，不重复 trellis。

### 3.2 分段量化重做

替换 `IsFlatMacroblock` / `FlatSegmentThreshold` / `FlatSegmentQDelta`：

- **4 段**，判定只依赖源亮度（两遍一致）：
  - 计算宏块 16×16 的平均绝对水平/垂直梯度 `activity`，以及去均值方差 `variance`；
  - 按整帧抽样（每 4 个宏块取 1）得到 `activity` 的 25/60/85 分位阈值；
  - 分段：0 = 高活动/纹理（默认 q），1 = 中低细节（q+8），2 = 平滑（q+14），
    3 = 近纯色（q+20）；delta 夹到 [0, 127−baseQ]。
- 构造每段 `_y1Dc/_y1Ac/_y2*/_uv*`（现有 `for segment` 循环改为按上表 delta）；
  `_currentSegment` 由 `ClassifyMacroblock` 决定。
- 位流：`WriteGlobalHeader` 打开 segmentation，写 4 段 quantizer delta、4 段
  `filter_strength` delta（见 3.3）、`update_map=1`，并按整帧分段计数写入
  4 叉 segment map 树概率（避免固定 255 的浪费）；`WriteMacroblockModes`
  写实际 segment。
- 若某段在整帧中宏块数为 0，仍写合法 delta，不改变分支结构。
- λ 继续按所在段 `y1Ac` 缩放（`Score` 已如此）。

### 3.3 滤波强度参与 RD

- `_filterLevel` 由 `readonly` 改为可变；新增 `AutoSelectFilterLevel()`，在**第一遍结束后**
  （`_reconY/U/V` 已是整帧未滤波重建）执行：
  - 候选：`{0, base/2, base, min(63, base*2)}`，`base=EstimateFilterLevel(q)`；
  - 每个候选：把重建平面复制到 `_outY/U/V`，按解码端顺序跑完整 `FilterRow`，
    计算滤波后相对源的加权 SSE：`D = 4·SSE_Y + SSE_U + SSE_V`；
  - 因滤波不改变码率，R 为常量，直接取 `D` 最小者（等价 RD）；并列时取较小 level
    以避免无收益的模糊。记录选中 level 到诊断输出。
- 按段滤波 delta：段 0/1 用 `+1/−1`，段 2/3 用 `−2/−4`（夹到 6 位有符号），写入帧头；
  解码端已支持。
- 第二遍使用选定 level 与实际段 delta 写帧头并滤波；第一遍不滤波（保持 `_statsOnly` 语义）。

### 3.4 两遍一致性与降级

- 分段判定与滤波候选只依赖源/重建，不依赖概率表；
- 大图降级路径（关闭分段、强制 16×16）保持：trellis 仍在降级配置下执行；
  `Partition0BudgetBytes` 超限重跑逻辑不变。
- 位流合法性由现有 libwebp 交叉解码测试覆盖。

## 4. 任务 3：无损 VP8L 多 pass

文件 `src/Formats/Webp/Vp8LEncoder.cs`。

### 4.1 现状

- `CollectStatistics`（L841）一遍统计直方图；
- `EstimateCost`（L1178）/`TableBits`（L1229）/`MetaImageBits`（L1240）用估计值做组划分；
- `BuildCodes`（L1246）按直方图建一次 Huffman，`EncodeTokenStream`（L579）直接写出。

### 4.2 改造

1. **首遍实际码长**：`CollectStatistics` 后先 `BuildCodes`，再用只计成本的
   `LsbCostWriter` 遍历同一 token 序列，得到每通道实际码长位数与每组表大小；
   该结果替换 `EstimateCost` 中的 `TableBits` 估计（修正文档所述约 4× 低估）。
2. **组数/组划分按实际代价选择**：对 `numGroups ∈ {1,2,4,8,16}`（受 `MaxHuffmanGroups`
   与 tile 数限制）逐一：
   - 用实际 token 序列重新聚类/合并 tile 直方图，`BuildCodes` 后计算实际
     表位数 + token 位数 + entropy image 位数；
   - 选总位数最小者；组数为 1 时不写 meta-Huffman。
3. **二遍重算**：按选定组划分重新收集该组实际 token 直方图并 `BuildCodes`，
   再写出最终位流；保留已有 `TokenWalker`/token 缓存（≤`MaxCachedTokenPixels`）；
   超出缓存上限的走重遍历路径，语义不变。
4. 保留 `LiteralOnly/Full/Auto` 选项语义；`Auto` 的候选选择仍按最终字节数最小。

### 4.3 兼容与验收

- 产物必须 libwebp 可解且与源 RGBA bit-exact（现有测试矩阵）；
- 体积按 2.3 验收；`Vp8LEncodeDiagnostics.TableBits/TokenBits` 改为实际值。

## 5. 任务 4：真行级流式解码

### 5.1 容器与入口（`WebpRowSource.cs`）

- 流式解析 RIFF：顺序读 chunk，只缓存**当前图像 chunk 的压缩载荷**（VP8/VP8L、ALPH），
  不再 `ReadAll` 后整帧解码。非可定位短读流同样适用。
- 保留现有 API：构造函数、`Width/Height/Channels`、`ReadRow(Span<byte>)`、
  `IsSupported(path)`、`Dispose`、动图显式 `WebpUnsupportedException`。
- 每行输出仍为 RGBA（无损/有 alpha）或 RGB（无 alpha）。

### 5.2 4A：VP8L 行增量（新文件 `src/Formats/Webp/Vp8LRowDecoder.cs`）

- 复用 `Vp8LDecoder` 的头解析/变换解析/Huffman 表构建（把 `DecodeImageStreamCore`
  L144 拆成可复用的"头 + 表"初始化与逐像素推进两段）。
- 状态机：`Init(payload, width, height)` → `DecodeNextRow(Span<uint>)`。
- 需要环回历史：copy 距离最大 1,048,456 像素，维护 `uint[1<<20]` 环形窗口（4 MB）
  + 当前行 + 上一行（predictor 用）；峰值 O(W + 4 MB)，与图像高度无关。
- 变换逐像素应用（subtract-green / cross-color / predictor / color indexing），
  与整帧解码的逆变换顺序一致；`entropy image` 只解一次（≤`MaxHuffmanTiles`）。
- ALPH：同一位流解码器输出 1 字节/像素的行；`WebpRowSource` 与有损 RGB 行按行合并。
- 动图仍拒绝（保持现状）。

### 5.3 4B：VP8 行增量（`Vp8Decoder.cs` 窗口化改造）

- 保留 `Decode()` 全体 API；新增内部流式状态机：
  `Init(payload)` → `DecodeNextMacroblockRow()` → `EmitReadyRows(...)`。
- 平面缓冲：
  - 重建平面改为 **2 个宏块行的滑动窗口**（Y 32 行、UV 各 16 行 + padding），
    每行推进时上一行下移复用；预测/右上样本只依赖上一行与当前行，语义不变；
  - 输出平面同样 2 行窗口；`FilterRow` 产生的上一行在下一宏块行滤波后即可交出；
  - 末行单独 flush。
- 分区：partition0 与 token 分区均按宏块行顺序消费；token 分区沿用 `mb_y & mask`。
- 行输出：把 `CopyRowToOutput` + `CropOutput` 拆为逐行 YUV→RGB（新增
  `Vp8YuvToRgb.ConvertRow`），`WebpRowSource` 直接写目标行。
- 验收：4B 的输出必须与 `Vp8Decoder.Decode` + `DecodeRgba` 逐像素一致。
- **风险控制**：4B 是架构级改造；若在实现中发现无法在本次会话内达到逐像素一致，
  则回退 4B 的所有改动，只保留 4A，并在报告"未达标/风险"中明确写出
  设计取舍、已完成阶段与后续分阶段计划（不得保留半截实现）。

### 5.4 内存验证

- 新增测试 `Streaming/WebpRowMemoryTests.cs`：
  - 构造 `20000×64`、`64×20000`（无损与有损各一份）；
  - 读取全部行期间每 8 行采样 `GC.GetTotalMemory(false)`，记录峰值增量；
  - 断言峰值增量 ≤ 现状测得值的 20%（现状先在同一测试中通过整帧路径测得，
    比较采用同进程、同顺序）；
  - 同时断言内容正确。
- 报告中给出实际数值（字节）与测量方法。

## 6. 文件改动清单（预计）

新增：
- `src/Formats/Webp/Vp8Trellis.cs`
- `src/Formats/Webp/Vp8LRowDecoder.cs`
- `SharpImageConverter.Tests/Streaming/WebpRowMemoryTests.cs`
- `.perf/QualityProbe/`（临时测量工程，不进入交付目录）

修改：
- `src/Formats/Webp/Vp8Encoder.cs`：trellis 集成、分段重做、滤波 RD、帧头写出。
- `src/Formats/Webp/Vp8LEncoder.cs`：实际码长评估、组划分、二遍重算。
- `src/Formats/Webp/Vp8LDecoder.cs`：拆出可复用头/表初始化与逐行推进。
- `src/Formats/Webp/Vp8Decoder.cs`：窗口化行增量（4B）。
- `src/Formats/Webp/WebpRowSource.cs`：流式容器解析、行源分派、ALPH 合并。
- `src/Formats/Webp/Vp8YuvToRgb.cs`：逐行转换入口（若不存在则新建）。
- 测试：`WebpVp8EncoderTests.cs`、`WebpLEncoderTests.cs`、`WebpStreamingDecodeTests.cs`。
- 文档：`docs/WebpEncoder.md` §2/§3/§4、`docs/PerfReport.md` §6.5、`CHANGELOG.md`。

## 7. 验证命令（每阶段独立执行）

```
dotnet build src/SharpImageConverter.csproj -c Release
dotnet test SharpImageConverter.Tests/SharpImageConverter.Tests.csproj --filter "FullyQualifiedName~Webp"
dotnet test SharpImageConverter.Tests/SharpImageConverter.Tests.csproj
dotnet run --project .perf/QualityProbe -c Release -- <baseline-dll> <csv>
```

- 任务 2 每个子步后跑 WebP 过滤测试；任务 2 完成跑全量。
- 任务 3 同上，并额外跑无损 bit-exact / libwebp 交叉解码。
- 任务 4 跑流式测试 + 全量；4B 完成后额外跑全部 VP8 解码对拍。

## 8. 风险与回退

| 风险 | 应对 |
|---|---|
| trellis 与两遍编码产生符号漂移 | 两遍都确定性重算；测量实际码率与 PSNR；若劣化则单独回退 trellis |
| 分段/滤波参数反而增大体积 | 全部改动以 24 组对照数据为准，不达标即回退该子项 |
| VP8L 多 pass 耗时上升 | 仅对 token 缓存内图像做二遍；大图保留单遍并记录 |
| VP8 窗口化引入像素差 | 逐像素对拍全矩阵；不一致则整体回退 4B，只交付 4A |
| 流式输出与整帧 API 行为分叉 | 两者共用同一解码状态机/变换函数；测试固定等价性 |
| 内存测量噪声 | 同进程、同顺序、多次取中位；同时报告 `GC.GetTotalMemory` 与私有字节 |

## 9. 交付报告格式

1. 三项实际成果 + 量化数据（体积/PSNR/内存）；
2. 关键改动文件（路径 + 行范围）；
3. 验证方式与实际结果；
4. 未达标项、风险、4B 是否完成及后续计划。
