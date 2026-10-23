# JPEG 代码审计：可清理项与剩余性能空间

> 审计范围：`src/Formats/Jpeg/**`、`src/Compression/Jpeg*.cs`、`Cli/Program.cs` 中与 JPEG 相关的分支。
> 基线：`master` @ `d5f8df6`（2026-10-23）。
> 方法：Roslyn 全量分析器（`AnalysisMode=All`，取 CA1811/CA1822/CA1823）+ 标识符引用扫描 + 逐条人工 grep 确认；
> 性能数字全部为本机实测（`i7-10750H`，12 逻辑核，平衡电源），并注明是「实测」还是「上界推算」。

---

## 0. 结论先行

**能清掉的死代码约 260 行，另有三块约 1200 行的「半成品 / 重复实现」需要做决策而不是直接删。**

**性能上还剩两个大于 25% 的口子，都不在编解码算子里，而在两处外围：**

1. `QualityMetrics.Compare` 自身（`--optimize` 每条路径的画质评估）——**已实测：只改一个常量就能拿到 16%**，再叠加 SIMD 有望到 40%+。
2. 编码的 `produce` 阶段（RGB→YCbCr）是唯一决定 `total` 的串行阶段（占空比 98%），并行化上界 −25~−45%。

解码侧没有空间：`entropy` 占 62%，单 scan 内不可并行（与 `docs/PerfReport.md` §5.3 结论一致）。

---

## 0.1 执行记录（2026-10-23）

已落地的部分（trellis 暂缓，见 §2.1 的修正）：

| 项 | 结果 |
|---|---|
| §1 全部死代码（除第 5 条） | 已删，净减 **约 220 行**；`dotnet build` 零警告，372 个非 Slow 测试全绿 |
| §1 第 5 条 | **撤销**：`IAsyncDisposable` 确实被 `JpegDecoder.cs:61` 的 `await using` 使用，删了会泄漏池化缓冲 |
| §2.4 `JpegRowSource` 吞掉 `useFloatingPointIdct` | 已接线（解析入口 → 行源），并**明确拒绝**会降质的组合，见 §2.4 |
| §3.1 并行阈值 4_000_000 → 500_000 | 已落地，1.2 MP 图 `--optimize` 墙钟 **−10.5%**（交错 8 对取中位） |
| §3.1 RGB 三通道专用循环 | 已落地，串行 `Compare` **34 → 20 ns/px（1.70x）**；产物 md5 全语料不变 |

验收口径：语料 8 个文件（JPEG/PNG/GIF/WebP，0.02~4.19 MP）逐个跑 `--optimize`，
改动前后产物 **md5 全部一致**——即两个改动都没有翻转任何「刚好压线」的候选。

| 文件 | 尺寸 | 改动前 md5 | 改动后 |
|---|---|---|---|
| 5_star_base.jpg | 1200×1000 | 1AA419A6… | 一致 |
| Amish-Noka-Dresser.jpg | 1000×1000 | 53299EBE… | 一致 |
| src.jpg / src.gif / src.png / src.webp | 2048×2048 | — | 一致 |
| video-001.rgb.jpeg / video-001.cmyk.jpeg | 150×103 | — | 一致 |

---

## 1. 可直接删除（零风险）

| # | 位置 | 内容 | 依据 |
|---|---|---|---|
| 1 | `src/Formats/Jpeg/JpegImage.cs:422-620` | `ConvertToRgba32()`，**199 行**（占该文件 26%），支持 Gray8/Rgb24/YCbCr24/CMYK32 四种输入 | 全仓 grep 只有声明处一处；`JpegImage` 对外只暴露 `ToRgb24()` |
| 2 | `JpegImage.cs:87` | `internal byte[] PixelDataArray => pixelData;` | 零引用 |
| 3 | `SimdJpegEncodePipeline.cs:38,41,44,45,47` | `VFix_0_390180644` 等 5 个 `Vector128<int>` 常量 | CA1823 确认；同一文件里 FDCT 已改用别的常量集 |
| 4 | `SimdJpegPipeline.cs:448-449` | `FancyH1x2` 里的 `evenH` / `oddH`（连同只喂给它们的 `sch` / `sch3` / `sph` / `snh`） | 算完不写。见 §3 第 3 条：这不只是死代码，是热点里的浪费 |
| 5 | ~~`JpegBitReader.cs:382`~~ | ~~`DisposeAsync()` + `JpegStreamInput` 的 `IAsyncDisposable`~~ | **误判，已撤销**：`DisposeAsync` 就在 `JpegStreamInput` 上，`JpegDecoder.cs:61` 有 `await using var input = new JpegStreamInput(stream)`，删掉会漏掉池化缓冲的归还 |
| 6 | `src/Compression/JpegOptimizer.cs:21-22,106-110` | `SIC_JPEG_NOCACHE` 环境变量 A/B 分支（`s_disableSampleCache`） | 一次性对照脚手架，样本缓存已定论落地 |
| 7 | `src/Compression/ImageOptimizer.cs:446-470` | `ReplayLadderVisits`，约 30 行 | PNG optimize 已不再走 `SearchLadder`（现只用 `BuildColorLadder`），GIF 只用 `SearchLadder`；复刻访问序列的机制整体失效。（顺带说明：项目记忆里「PNG 8 档阶梯全枚举并行 + ReplayLadderVisits」已与代码不符） |
| 8 | `src/Core/SimdCompat.cs:434,438,450`<br>`src/Core/SimdHelper.cs:793` | `SubtractInt16` / `AddUInt16` / `SubtractInt32` / `RoundDownToMultiple` | 零引用（`SimdCompat` 里其余包装都在用，这 3 个是遗留） |

另有 5 处**非 JPEG** 的同性质遗留，一并列出：`GifEncoder.EncodeAnimationRgba`（301）、`WebpCodec.CreateIncrementalDecoder`（96）、`Configuration.RegisterFormat`（89）、`Interfaces.IImageInfo`（110）、`Quantizer.BITS`（14）。

## 2. 需要决策的「半成品 / 重复实现」

### 2.1 Trellis 量化：已接入两条路径，基准补齐（2026-10-23）

> **修正（2026-10-23）**：本节初稿说「CLI 没有任何开关」是错的。`Cli/Program.cs` 解析
> `--opt-jpeg-trellis` 并透传为 `OptimizeOptions.JpegTrellisQuantization`。
> 也就是说这条分支**可以用 CLI 跑**，缺的是基准与回归，不是入口。

`src/Formats/Jpeg/JpegTrellisQuantizer.cs`（546 行）+ `JpegEncoder.TrellisContext`（2736-2790）+ `DctQuantizeInPlace` 的 trellis 分支（2321-2330）+ `OptimizeOptions.JpegTrellisQuantization` + `JpegTrellisQuantizerTests`。

**已补齐的入口与基准：**

- `--optimize --opt-jpeg-trellis`（原有）：搜索期评分与最终编码都走 trellis。
- `--to jpg --jpeg-trellis`（本次新增）：普通转换路径的三个 `JpegEncoderOptions` 构造点
  （`SaveJpegFromRgb` / `SaveJpegFromRgba` / `SaveGifFrame`）都接上了开关，默认关闭。
- 实现机制：`DctQuantizeInPlace` 遇 trellis 时**退回标量 FDCT**（SIMD 融合路径会原地销毁量化前系数），
  然后 per-block DP：候选 `{0, nearest, nearest±1}`、码率按 (run,size) 查标准 AC Huffman 码长、
  失真取系数域平方误差，λ 随质量缩放（`LambdaForQuality`）。快路径是 run 状态展开 + `Vector<int>` min/add。

**实测 RD（探针 `E:\tmp\trellisprobe`，4:4:4，同 q 对比最近值量化）：**

| 素材 | 尺寸 | 体积变化 | PSNR 变化 | 编码耗时 |
|---|---|---|---|---|
| `src.png`（单代源） | 4.19 MP | **−12% ~ −25%** | −0.5 ~ +0.4 dB | 3~10×（25→240 ms） |
| `Amish-Noka-Dresser.jpg` | 1.0 MP | −9% ~ −26% | −1.6 ~ −0.04 dB | 3~10×（5→50 ms） |
| 叠加 optimizeHuffman（`--optimize` 实际配置） | 4.19 MP | −19% ~ −22% | −0.5 ~ −0.2 dB | 2.5~10× |

同尺寸插值（src.png）：trellis 少 8~15% 体积时 PSNR 持平或略高；RD 前沿整体优于最近值量化。

**两个必须知道的口径：**

1. **在 `--optimize` 的验收口径下 trellis 会让文件更大**：optimize 的门槛是感知 PSNR 达标后体积最小，
   而 trellis 用体积换的是「原始 PSNR/SSIM/平均误差」——同感知分下它把体积花在了指标看不到的地方。
   实测 `5_star_base.jpg`（感知门槛 44.2）：基线 q71 → 207.9 KB（PSNR 37.31 / SSIM 0.982），
   trellis q77 → 215.2 KB（PSNR 39.97 / SSIM 0.992）。**+3.5% 体积换 +2.7 dB 原始 PSNR / +0.010 SSIM**。
   所以 trellis 的定位是「固定 q/固定体积下画质更好」，不是「optimize 再省一截」。
2. **DP 的码率模型用标准 Huffman 表**；当 `optimizeHuffman=true`（optimize 默认开）时第二遍用按图优化的表，
   码率估计与实际有偏差。实测该组合仍有 −20% 体积，偏差可接受；要更准需把第一遍统计的真实码长回灌 DP，代价是再一轮迭代。

**SIMD 覆盖与剩余空间（2026-10-23 实测，探针 `E:\tmp\tcqprobe`）**

先说结论：**trellis 并不是「不能用 SIMD FDCT」**，而是没必要——`SimdJpegEncodePipeline.ForwardDct8x8`
（非融合版，正是为「不带量化」保留的）与标量 FDCT 逐位一致（4.19 MP 全量 4,194,304 个系数零差异），
接上后端到端 **548 vs 549 ms**（10 对交错、中位差 0.11%，产物 md5 相同），因为 FDCT 只占 trellis 单块的 2%。
所以 `DctQuantizeInPlace` 保留标量 FDCT 是为了少一条运行时分派，不是能力限制。

单块成本分解（2048×2048 灰度块、q80、单线程；DP 用逐行复刻版测量，三段只差被删掉的那段）：

| DP 阶段 | ns/块 | 占 DP | 现状 |
|---|---|---|---|
| 骨架：`G[s] = min_q(dp[16q+s] + q·λZRL)` + `next[]` 移位 | 1377 | 37% | 已是 16 lane 向量 min/add |
| 候选枚举 / 分类（含 `MinRate`） | 1363 | 38% | 混合：`MinRate` 向量化但归约差 |
| 回溯定位 `bestRun` | 889 | 24% | 已是向量比较 + lane 扫描 |

- **`MinRate` 是最大单项**：每块约 189 次调用、合计 ≈ 1500 ns（单次 7.97 ns）。标量对照 11.5 ns，
  向量版只快 1.45×——16 个 lane 只用上 1.45×，说明瓶颈不在算，而在 `acc[lane]` 的 8 次逐 lane 提取
  （每次都是一条 shuffle/extract）。改成 shuffle 折叠或「存回栈 + 一次扫描」应能到 2~3 ns/次。
- **码率表每块重算 160 项**：169~202 ns（占 DP 4~5%），只依赖 (λ, 标准 AC 码长表)，可提到 `TrellisContext`。
- **回溯定位**扫 64 个状态找最小代价（889 ns，24%）；可在 `G` 计算时顺带记 argmin_q、在 `MinRate` 里记 argmin_s
  直接得 `bestRun`，但要保住现有「r 升序第一个」的平局规则，且必须逐字节验证产物。

不能靠「加宽向量」解决的部分：DP 是 min-plus 递推，63 个位置**严格串行**（位置 p 的 dp 依赖 p-1），
唯一可并行维度是 16 个 `run mod 16` 状态；单块内的候选枚举（≤3 个）与 (run,size) 码表查找是 gather + 分支，
SIMD 收益有限（VNNI 也帮不上，见 `JpegTrellisQuantizer` 类注释）。块间并行已由 `ProcessDct` 的 `dctWorkers`（≤8）承担。

粗算：上面三处都不加宽向量、只改实现质量，DP 可降 ~30%（4.4 → ~3.1 µs/块），端到端 trellis 编码约 −25%。

**三处优化的落地结果（2026-10-23，探针 `E:\tmp\tcqab`，与 `E:\tmp\sic-baseline` worktree 交错 A/B）**

已全部落地（`JpegTrellisQuantizer` + `JpegEncoder.TrellisContext`）：

1. **码率表上提**：新增 `JpegTrellisQuantizer.Context`（rate 表 + λ64/ZRL/EOB 缩放值），
   由 `JpegEncoder.TrellisContext` 构造时每表建一次（luma/chroma 各一），`QuantizeInPlace` 新增 Context 重载；
   旧签名保留并内部走 Context（测试继续用它）。极端 λ（env 覆盖超 int32 快路径范围）仍回落 legacy long DP。
2. **`MinRate` 水平归约**：256 位向量先 `vextracti128` 折半，128 位内 2 次 shuffle-min，不再逐 lane 提取。
   同进程环境变量 A/B 过 shuffle 折叠 vs 标量 4-lane 提取：**无显著差异**（min 3548 vs 3549 ns/块），
   保留 shuffle 版。
3. **回溯定位改 argmin 分解**：不再整体重扫 64 个状态。`bestRateMin = min_s(g[s] + rate[cat,s])` 且
   回溯只在 `bestCost < Inf32` 时发生 ⇒ 饱和哨兵不参与等式，故「第一个达标的 r」可分解为
   「并列的 s（向量等值比较）× 该列最小 q（≤4 次标量比较）」，取并列 s 中最小的 r——
   与旧 64 状态升序扫描逐位等价（含平局规则）。

实测（4.19 MP / 65536 块、q80、λ=9.6、12 轮轮转取 min，跨运行多次重复）：

| 路径 | ns/块 |
|---|---|
| baseline 旧 DP（含每块建表） | 4205~4323 |
| new 旧签名入口（每块一次 Context 分配，非生产路径） | 3540~3590 |
| **new ctx 直调（= 新生产路径）** | **3252~3371** |

**DP 4205 → 3252 ns/块 ≈ −23%**（未到粗算的 −30%：shuffle 折叠实测没拿到预期的额外收益，
主要进账来自回溯定位的消除）。CreateContext 每图一次，摊销后 <5 ns/块。

逐字节验证：**9 图 × q60/80/95 的 `--to jpg --jpeg-trellis`（27 个产物）与
3 个 `--optimize --opt-jpeg-trellis` 产物，baseline 与新版 md5 全部一致**；
进程内真实图旧签名 vs Context API 差异 0/4,194,304；`VectorizedDp_MatchesLegacyCost`
（5 档 λ × 40 组随机块）继续通过。372 个非 Slow 测试全绿。

**测试素材陷阱（复测时别踩）**：`5_star_base.jpg` 本身是 q80 档量化表编码的渐进式 4:2:2 JPEG；
用「解码→重编码」做 RD 曲线时，在与原图相同量化格点（q80）会出现 PSNR 尖峰，中间档位被二次压缩拉低，
曲线非单调是素材现象，不是编码器 bug（已用 WIC 独立解码交叉验证）。RD 评估要用单代源（PNG 或无损）。

**结论：保留**。默认关闭、显式 opt-in，RD 收益真实（同画质省 8~15% 体积），代价是 3~10× 编码耗时；
与「死代码」不同，这是一条有明确使用场景的可选路径。

### 2.2 三段「条带引擎」重复

fancy 上采样的**算子**已经收敛在 `SimdJpegPipeline.FancyVh2x2 / FancyH1x2`，但**调度壳**有三份独立实现：

| 副本 | 位置 | 取数方式 |
|---|---|---|
| 全量重建 | `JpegReconstruct.cs:285-560`（`Decode420FancyAll/Strip`、`Decode422FancyAll/Strip`） | 整幅系数已在内存 |
| 通用行源 | `JpegFrameState.RowSource.cs:423-500`（`DecodeFusedBand420Fancy`）、`:290-330`（`ComputeFancy422Rows`） | 按 MCU 行带 |
| 基线融合行源 | `JpegBaselineFusedRowSource.cs:203-280`（`DecodeBand420Fancy`） | 边熵解码边重建成 MCU 行 |

证据：`ec26a64`（fancy 上采样）必须同时改这三处 + SIMD 算子，一处漏改就是像素级不一致；`d5f8df6` 又在 `SimdJpegPipeline` 与 `FastIDCT` 之间补鉴位。三份代码的差异其实只有「系数从哪来」，可以抽成 `IBlockBandSource` + 一个条带渲染器；风险中等（解码是最敏感路径），收益是可维护性而非性能。

### 2.3 第三条编码实现：`EncodeStreaming`

`JpegEncoder.cs:598-715` 手写了「逐行读源 → RGB→YCbCr → DCT/量化 → Huffman」全流程（只支持标准 Annex K 表），与主流水线（`EncodeRgbPipeline`）平行维护。串行、无并行、无优化表——功能上确实是不同取舍，但采样/量化/Huffman 的细节必须与主路径保持一致，属于「两份实现必须同步演化」的负债。可考虑改成「单线程 produce 回调 + 复用 `ProcessDct`/`WriteMcuHuffman`」。

### 2.4 其它

- **`JpegRowSource` 吞掉了 `useFloatingPointIdct`**（`JpegRowSource.cs:20,83`）：构造参数收了但从不使用 ⇒ `--stream --idct float` 静默无效。
  **已处理（2026-10-23）**：标志已接线（`ParseHeadersForRows` / `ParseForTranscode` → `JpegFrameState` → `RowSource`），
  但 4:2:0/4:2:2 会被明确拒绝：融合条带是唯一带 fancy 上采样的路径，它只有 SIMD 定点 IDCT；
  一旦要求浮点就只能退回通用行源，而通用行源是双线性上采样。实测合成图（320×240 / 4:2:0 / q90）：
  `浮点行源 vs 浮点整幅` meanErr **1.34**，而 `浮点整幅 vs 定点整幅` 只有 **0.33** ⇒ 逐字节降质多于精度收益。
  4:4:4 无上采样，浮点行源与浮点整幅**逐字节一致**（回归测试 `JpegRowSource_HonorsFloatingPointIdct` 断言了这一点）。
  内存守卫自动触发的流式路径遇到该拒绝时会打印原因并回退整数 IDCT，不至于让转换直接失败。
- `JpegEncoder.Write` 有 6 个重载 + `WriteInternal` 两套（`bool subsample420` 与 `JpegChromaSubsampling`），最内层 `WriteInternal` 12 个形参一路透传；`EncodeCoefficients` / `EncodeFromSamples` 又各两套。可用一个 `EncodeRequest` 结构收敛掉一半重载。
- 每 MCU 一次 `ArrayPool<int>.Shared.Rent(6*64)` + 归还（`ProduceRgbSamples`）：143MP 约 56 万次租借。改成每个 `McuBatch` 租一整块即可，**未实测**，预估 1~3%。

---

## 3. 剩余性能空间（按收益排序）

### 3.1 `--optimize`：画质评估是最大项，且并行阈值定错了一个数量级 ★★★

**实测（本机，`QualityMetrics.Compare`，RGB，1 次调用取中位）：**

| 尺寸 | MP | 串行 | 并行（分片） | 加速 | 串行 ns/px |
|---|---|---|---|---|---|
| 1000² | 1.00 | 24.2 ms | 7.5 ms¹ | 3.22x | 24.3 |
| 1200² | 1.44 | 36.0 ms | 8.8 ms¹ | 4.09x | 28.1 |
| 1448² | 2.10 | 58.1 ms | 12.3 ms¹ | 4.74x | 22.8 |
| 1700² | 2.89 | 69.7 ms | 16.6 ms¹ | 4.21x | 23.1 |
| 2048² | 4.19 | 93.9 ms | 18.8 ms | 5.01x | 22.4 |

¹ 该列是把 `ParallelPixelThreshold` 临时改成 `500_000` 后实测的；默认值下 4 MP 以下根本不进并行分支，实测就是串行那一列。

**问题**：`ImageQuality.cs:123` 的 `ParallelPixelThreshold = 4_000_000` 是按「~1.9 ns/px」推算的（注释原文），
而实测串行是 **22~28 ns/px**，差了 12 倍以上。于是 1~4 MP 的图（绝大多数照片）在 `--optimize` 里全程串行评估，
而 4.19 MP 的图反而只要 18.8 ms——**像素数只有一半的 2.1 MP 图比 4.19 MP 图慢 3 倍**。

**实测端到端收益**（`examples/5_star_base.jpg`，`--optimize`，5 次取中位，含进程启动 ~150 ms）：

| 阈值 | 墙钟中位 | 进程内「总耗时」 |
|---|---|---|
| 4_000_000（现状） | 1069 ms | 837~935 ms |
| 500_000 | 896 ms | 585 ms |

⇒ **一行常量改动的实测收益 −16%（墙钟）/ −19%（进程内）**，且 4 MP 以上完全不受影响。

**口径代价（必须先说清）**：并行路径按「带」分片、按分片下标合并，浮点累加顺序与串行不同（量级 1e-9，见
`CompareSsimCore` 注释），因此同一张图在阈值两侧的 PSNR 末位会不同，理论上可能翻转「刚好压线」的候选。
4 MP 以上早已是这个口径；改阈值等于把 <4 MP 也统一到并行口径。**验收方式**：语料全量跑一遍，比对「选中质量 / 产物 md5」是否变化，
变化项单独判定，而不是靠推断。

**落地结果（2026-10-23，`ParallelPixelThreshold = 500_000`）**

- 串行/并行拐点（同一份二进制、反射调内部并行重载、7 次中位）：
  0.010 MP **0.63x**、0.022 MP 1.28x、0.040 MP 1.75x、0.102 MP 3.78x、0.250 MP 3.79x、4.194 MP 4.98x。
  即 0.02 MP 起并行就划算，500k 是保守取值——目录批量时外层已按文件并行，内层再开满线程只会超订。
- 端到端（`5_star_base.jpg` 1.20 MP，`--optimize`，阈值 A/B 交错 8 对取中位）：**1056 → 945 ms，−10.5%**。
- 批量目录（8 个文件混跑，7 对交错）：7673 vs 7707 ms，**差异 <0.5%**，没有超订退化。
- 语料 8 个文件产物 md5 **全部不变**——浮点累加顺序变化没有翻转任何候选。

**下一步（更大的那块）**：`CompareCore.Accumulate`（`ImageQuality.cs:419-482`）是完全标量的双重循环，两个可直接下手的地方：

1. ~~**`channels == 3` 时 `(v * 255 + 127) / 255` 恒等于 `v`**（`:445-456`）。~~ **已落地**：拆出 `AccumulateRgb`，
   按通道展开、权重提到循环外，累加顺序与通用循环逐项相同。
   实测（串行 `Compare`，1.00/2.10/4.19 MP）：**34.0 → 20.0 ns/px，1.70x**。
   端到端：PNG optimize（搜索期内层串行）**−7.3%**、WebP **−3.9%**；
   1.2 MP JPEG optimize **−0.3%**——因为它已经走按带并行，多出的算力被内存带宽吃掉了。
2. **PSNR/SSIM 内层向量化**：`sum(d²)`、`sum|d|`、块和都是整数可表达（现用 `double` 累加），AVX2 下用
   `vpmaddwd` 直接给出「两两乘积之和」，吞吐可到 2~4 周期/像素；SSIM 的 `yr/yt` 与五个矩（`sr,st,srr,stt,srt`）同样可向量化。
   这一项会改变浮点累加顺序（与 SSIM 分片同性质），不保证 1e-16 级一致。**未做**——上一条已经把标量空转摘掉，
   再往下走必须接受「PSNR 末位不可复现」，需要单独决策。

上界：Compare 若 3x，2048² 单候选从 51 ms 降到 ~34 ms（评估占 38~57%），`--optimize` 约 −30%；
且 PNG / GIF / WebP 的 optimize 同时受益（4.19 MP 单候选：PNG 评估 128.8 ms / 947 ms、WebP 45.3 ms / 477.6 ms）。

### 3.2 编码：`produce` 是唯一的关键路径 ★★★

**实测（`--jpeg-bench`，143 MP `examples/progressive.jpg`，`SIC_JPEG_STAGE_TIMING=1`，3 次内部重复取中位）：**

| 轮次 | total | produce-busy | huffman-busy | produce 占空比 |
|---|---|---|---|---|
| 1 | 740.5 ms | 729.5 ms | 365.3 ms | 98.5% |
| 2 | 863.4 ms | 736.0 ms | 545.1 ms | 85.2% |
| 3 | 670.8 ms | 661.1 ms | 332.3 ms | 98.6% |

- `total ≈ produce-busy`：**produce 阶段（以色彩转换为主体，另含 MCU 池化租借与入队）串行决定了端到端时间**。
  `produce-busy` = `produce` 墙上时间 − `produce-wait`，而 `produce-wait` 实测只有 1.5~2.5 ms，说明生产者几乎不背压、是纯粹的自缚。
- `huffman-busy` 只有 produce 的 50~74% ⇒ Huffman 有 26~50% 的空转余量。
- 结论与 `docs/PerfReport.md` §5.1 一致（produce 302.7 / huffman 330.7 / total 331），但那台机器上两者接近，
  本机实测则明确是 produce 单边卡住。

**不要重做的方向**：把 produce 拆成多线程直接入队已在 2026-09-26 实测否决（303 → 572 ms @dop=4、921 ms @dop=∞，
原因见 `JpegEncoder.cs:1091-1099` 的注释：信号量争抢 + 乱序入队把 Huffman 逼进 pending ring 冷读）。

**建议的做法（他们注释里也指向这个方向）**：新增一级「并行填条带」，不动现有队列语义：

1. `N` 个条带缓冲（每条约 16 个 MCU 行，143 MP 约 5 MB/条，N=8 ⇒ 40 MB 环形），由 `N` 个线程并行把 RGB 转成 YCbCr 写进去（**无队列、无锁**）；
2. 现有单线程 `ProduceRgbSamples` 退化成「从条带缓冲按序取 6 个块 + `Rent`」，不再做色彩转换；
3. 后续 DCT / Huffman 阶段完全不动 ⇒ MCU 序号仍是全局光栅序，**产物逐字节不变**。

上界：把 produce-busy 从关键路径摘掉后落在 `huffman-busy`，即 143 MP 编码 −25~−45%（按上表 365~545 ms）。

### 3.3 Huffman 并行（按位拼接）★★☆

`huffman-busy` 332~545 ms 是 3.2 之后的新地板。可行设计（产物仍可逐字节一致）：

- 按 MCU 行切 `R` 段，每段一个独立 `JpegBitWriter`（各自从 bit 0 开始）；
- DC 预测的跨段依赖用「先扫一遍 DC 值」解决（每块 1 个 int，顺带就是现在 `LastNzPacked` 那一层的副产品），段首预测子立即可知；
- 段间是**纯位拼接**：`bitOffset` 取模 8 后用 64 位漏斗移位即可，不允许中途字节对齐（中途插填充位会解码出多余符号）；
- 仍然只写**一个 SOS**（baseline 顺序模式下同一分量只能出现在一个 scan 里，拆成多个 SOS 是不合规的——这一点要纠正旧笔记里「拼多个 SOS」的说法）。

上界：`huffman-busy` 本身 ÷ R（R≈核数/2），与 3.2 叠加后 143 MP 编码有望到 ~200 ms（−50%+）。工作量最大，建议放在 3.2 之后。

### 3.4 解码：没有大空间 ★☆☆

**实测（同上，143 MP）：** decode total 同进程 3 次中位 557 ms（单次 504~1121 ms 漂移，冷启动那一发最慢），
其中 `entropy` 317~485 ms、`reconstruct` 156~620 ms（并行，波动大）。

entropy 占比 60%+，逐位变长解码 + `code = prefix[code]` 指针追逐，指令级不可向量化（`docs/PerfReport.md` §4 / GifFindings 同结论）；
progressive 必须整扫描完成才能重建。唯一理论出口是 RST 分段并行，现语料无 RST 图 ⇒ 不立项。

顺带：`ec26a64` 那次 fancy 上采样换来的正确性是**拿 +12~19% 解码时间**换的（提交说明自己写明了），
这是既定取舍，不建议为了速度回退。

### 3.5 `--optimize` 的其它小项（合计 5~10%）

- **赢家复用系数**：`JpegOptimizer.Produce` 的 `Score(q)` 已经把赢家质量的量化系数算好并落在 `JpegCoefficientSink` 里，
  最后 `Encode(quality, …)` 又从 RGB 重跑一遍 produce + DCT + 量化（`JpegOptimizer.cs:88-96,134`）。直接从系数写位流可省一次全量编码。
- **420 失败后连跑 422 + 444 两整轮**（`JpegOptimizer.cs:142-146`）：彩色文字类图会付 3 倍搜索代价。422 与 444 的选中质量通常只差一两档，
  可先只跑 444，再决定是否需要 422 兜底（属于行为变更，需要语料验证）。

---

## 4. 附：本次审计的数字出处

| 数字 | 采集方式 |
|---|---|
| Compare 串行/并行、各尺寸 | `E:\tmp\jpegprobe`（反射调 `QualityMetrics.Compare` 内部并行重载；阈值 A/B 用 `sed` 改常量后重建，测完已还原） |
| `--optimize` 端到端 1069 / 896 ms | `Cli` Release 构建，5 次墙钟取中位，同一份二进制内 A/B |
| 4.19 MP 单候选分解（编码 12.3 / 26.2 ms、解码 15.5 ms、Compare 94.0 ms） | `E:\tmp\allfmt`（`Part2_SingleCandidate`） |
| 143 MP 编码/解码分阶段 | `dotnet Cli.dll examples\progressive.jpg --jpeg-bench N` + `SIC_JPEG_STAGE_TIMING=1` |
| 死代码判定 | `AnalysisMode=All` 构建的 CA1823/CA1811 输出 + 全仓标识符引用扫描 + 逐条 grep |

> 本机噪声极大：同一份二进制的单遍编码在三次独立运行里测到 12.3 / 50.1 / 94.4 ms。
> 表里的相对结论都取自**同一次进程内**的对照，跨表比较只用于数量级判断。
