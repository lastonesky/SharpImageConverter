## 未发布

### 构建
- **`dotnet test` 切换到 .NET 10 SDK 的 MTP 模式**：.NET 10 SDK 起 Microsoft.Testing.Platform
  不再支持挂在 VSTest 目标上，`dotnet test` 全挂（CI 与本机同病）。`global.json` 只声明
  `test.runner = Microsoft.Testing.Platform`（不锁 SDK 版本）；CI 显式传 solution 改用
  `--solution`（新模式不吃位置参数）。**`TempProbeFidelityBlocks` 缺本机
  `%TEMP%\bench\fidelity` 语料时改为 Skip 而非失败**（该探针依赖跑 bench 留下的文件，
  机器间不保证存在，CI 上必挂）。

## 1.3.0（相对 v1.2.0）

### 发布
- **CLI 资产改为依赖框架（framework-dependent）发布**：`tools/build-cli.sh` 由
  `--self-contained true` 改为 `false`，并去掉 `EnableCompressionInSingleFile`
  （NETSDK1176：压缩单文件仅支持自包含）。资产形态不变（win 单文件 `.exe` /
  linux 单 exe tar / osx 目录 tar），原生 libwebp 仍经 `IncludeNativeLibrariesForSelfExtract`
  内嵌、运行时自解压。win-x64 从自包含的 38.1 MB 降到 **1.35 MB**（本机实测，约 1/28）。
  代价是使用者需自行安装 .NET 10 运行时。

### 性能
- **JPEG optimize 大图 28.6 s → 3.45 s（143 MP `examples/progressive.jpg`）**，三个独立原因：
  - **4:2:2 解码补上 SIMD 快速路径**：`TryDecodeInterleavedYCbCrSimd` 此前只认 Y=1×1 / 2×2，
    2×1 直接回退到「三块整幅平面 + 标量双线性交织」，单次重建 **1267 ms**（4:2:0 为 18 ms）。
    新增 2×1 MCU 分支（水平上采样与 4:2:0 共用 `UpsampleAndConvert`），降到 **~30 ms**。
    影响的不只是 optimize：任何 4:2:2 JPEG 的正常解码此前都走慢路径。
  - **Y-SSIM 按块行并行**：原实现是整幅单线程标量扫描（143 MP 242 ms/候选），比并行后的
    PSNR/感知段还贵。分片合并按分片下标，结果确定；候选已并行时（PNG/GIF/WebP 搜索期）
    传 `allowInnerParallel: false` 保持串行，避免超订。
  - **`JpegCompareSubsampling` 默认关闭**：4:2:0 达标后不再强制多跑 4:2:2 + 4:4:4 两轮完整搜索。
    新增 CLI `--opt-jpeg-subsample-compare` 显式开启；开了以后本例产物从 2.36 MB 降到 2.13 MB
    （q42 4:2:2），代价 +4.4 s。

### 移除
- **WebP 五参数搜索（`--opt-webp-params` / `WebpParamSearch`）整体删除**：语料实测只有
  0~1.3% 体积收益，却要多花 2~4 倍搜索时间。连带删除 `WebpEncoderOptions` 的
  `AlphaQuality` / `FilterStrength` / `SnsStrength` / `Segments` / `Preprocessing` /
  `WithTuning`、`WebpParamSearchMaxPixels`、`tools/perf/WebpParamProbe` 与相关文档/测试。
- 1.2.0 条目中该功能的记录保留为历史。

- **JPEG trellis 的 DP 重写（单块 28.9 µs → 3.3 µs，8.5x）**：
  零转移改成一次向量移位（`next[r] = dp[r-1] + coef²`），回溯表从 64×64 int 缩成每位置
  一对 `(prevRun, value)`；码率按 `run = 16q + s` 展开，`min_r` 化为
  `min_s(G[s] + λ·len[s,cat])`，用 `Vector<int>` 一次算 16 个 run 状态；
  候选 category 用 `BitOperations.Log2` 并复用同 category 的码率项。
  端到端：143 MP `progressive.jpg` 44.9 s → 9.7 s、`Amish-Noka-Dresser.png` 730 → 417 ms，
  产物 md5 与旧实现逐字节相同（新增 `VectorizedDp_MatchesLegacyCost` 逐块比对）。
  λ 被 `SIC_JPEG_TRELLIS_LAMBDA` 推到极端时自动回落旧 long 实现。

### 修复
- **4:2:2 编码被静默降级成 4:4:4**：`JpegEncoder.Write(..., JpegEncoderOptions)` 把参数压成
  `options.Subsample420`（bool），`Yuv422` 因此走 4:4:4 分支。optimize 用真正的 4:2:2 系数
  评估画质，却写出与 4:4:4 逐字节相同的字节流（两份产物 md5 相同），CLI `--subsample 422` 同病。
  现改为传 `options.Subsampling`。
- **SSIM 与 PSNR 的并行一致性**：新增 `QualityMetricsParallelTests` 对 SSIM/WorstSsim 的
  串行/并行断言。

### 测试
- 新增 `Jpeg422Tests`：SOF0 采样因子 2×1、4:2:2 产物小于 4:4:4、SIMD 重建与通用（浮点 IDCT）
  路径像素一致（含 97×61 / 33×17 边界 MCU）、系数直通与比特流解码逐字节相同。

### 文档
- **`docs/` 整理：13 份 Markdown 合并为 7 份**，删除已完成使命的过期文档。
  - 新增 `docs/ArmFindings.md`：合并 `ArmPerfBaseline.md` + `ArmExclusiveSimd.md`
    （基线数字、调优后 A/B、NEON 语义等价、LD3/RDM/CRC 三条实测判决）。
  - 新增 `docs/SimdPitfalls.md`：合并两份 `AuditVerification-*.md`
    （审计复核结论 + 已落地修复 + 暂缓原因 + K1–K15 陷阱），去掉逐条任务状态表。
  - 新增 `docs/GifFindings.md`：合并 `GifAlgorithmAnalysis.md` + `GIFQuantizerOptimize.md` 与 `PerfReport.md` 的 GIF 段；
    顺带纠正一处过时说法——默认量化器是 `OctreeBayer`，Wu+Floyd–Steinberg 只是可选项，
    原「量化器分析」把 Wu 当成了唯一/默认路径。
  - `docs/PerfReport.md` 重写为「性能实测记录与已否决方向」：删掉早期手写的
    「性能问题定位 / 优化建议 / 验证方法」清单（其中多条已标注「问题确认不存在」）
    与已作废的手写参考实现数字；AVX-VNNI 从独立文档压缩为一句结论（缩小慢 4.0x、放大慢 1.8x）。
  - 删除 `goal.md`（早期「与成熟库的差距清单」，条目已全部实现或不再适用）、`VnniResizeFindings.md`、
    `PerfCompare-2026-03-08-vs-2026-10-02.md`（由 `tools/perf/run-bench.ps1` 再生成，不入库）。
  - 同步更新 `README.md` / `README.en.md` 的目录树与 `docs/README.md` 索引；
    `src/Core/SimdCompat.cs`、`src/Formats/Gif/OctreeQuantizer.cs`、`tools/perf/SicBench/Bench.cs`
    里的文档引用改为新路径。

## 1.2.0（相对 v1.1.0）

### 修复
- **WebP method 4 高质量悬崖保护**：`WebpEncoderOptions` 在 `quality >= 75` 时把
  `method 4` 强制降为 `method 3`。143 MP 实测 q75：m4 26.3 s / 1,655,814 B，
  m3 7.8 s / 1,597,784 B；q40/q65 不触发悬崖。CLI 显式 `--webp-method 4 --quality 75`
  现在也走 m3。
- **有损格式再压缩的最小收益门槛**：同格式 JPEG/WebP optimize 新增 `MinLossyGainRatio`
  （默认 10%）。收益不足时保留原图，避免为 1%~5% 体积再损失一代画质；跨格式不受影响。
- **WebP optimize 画质口径错误**：搜索期曾把 RGBA 缓冲按 `bytesPerPixel = 3` 比较，
  只读前 3/4 数据且 RGB 权重错位，感知 PSNR 系统性虚高约 8 dB，导致大量图片在 q=40
  被误判达标。现统一为 4 通道；固定质量分支改用产出档 `method 3`；产出后重新解码最终字节
  复评画质（不再用搜索期 m2 的结果冒充最终画质）。语料中 `5_star_base.jpg` 因此从 q52
  （实际感知 41.18 dB，未达标）改为 q81 / 感知 45.84 dB，体积 +69%——这是修正误判的代价。

### 新增
- **JPEG trellis 量化（默认关闭）**：新增 `JpegTrellisQuantizer`（块内 DP，候选
  `{0, 最近值, 最近值±1}`，λ 随量化步长 Q² 缩放）与 `OptimizeOptions.JpegTrellisQuantization` /
  CLI `--opt-jpeg-trellis`。实测体积 −4.1%~−24.2%，代价是 2~7 倍编码时间、最差 1% SSIM
  下降，因此默认不启用（详见 `docs/Optimize.md` §2）。
- **WebP 参数搜索（默认关闭）**：`WebpEncoderOptions` 暴露 `AlphaQuality` / `FilterStrength` /
  `SnsStrength` / `Segments` / `Preprocessing`，`OptimizeOptions.WebpParamSearch` 对 ≤1.5 MP
  小图做单参数消融 + 组合 + 坐标下降；CLI `--opt-webp-params`。语料实测收益 0%~1.3%、
  耗时 2~4 倍，故默认关闭。
- **内存预检**：新增 `MemoryGuard`（文件头宽高探测 + 格式经验峰值估算 + 准入判定）
  与 `OptimizeOptions.MemoryLimitBytes` / `DisableMemoryGuard`、CLI `--memory-limit` /
  `--no-memory-guard`。optimize 预估超预算时拒绝并保留原图（跨格式抛
  `InsufficientMemoryException`）；非 optimize 转换超预算时，BMP/PNG/GIF/WebP → BMP/PNG
  自动切换逐行流式转换（新增 BMP/PNG 行编解码、PNG 逐行 zlib、GIF 首帧/单帧缓冲、
  WebP 增量解码、JPEG 基线 MCU 行熵解码 + SIMD IDCT/行级编码）；目标 WebP 仍需整帧，
  流式后不足则明确报错。143 MP 基线 JPEG→BMP 峰值从 854 MB 降到 65 MB、耗时 +21%；
  progressive 回退整幅系数时从 854 MB 降到 458 MB、耗时 +51%，两者像素与常规路径一致。
  流式 BMP 目标改为 top-down（负高度）顺序写，去掉逐行 seek。
- **跨格式目标格式建议**：`OptimizationResult.Suggestion` 按 alpha / 唯一色数 / 目标格式
  给出建议（BMP→PNG、照片转 PNG/GIF→WebP、带透明转 JPEG→WebP/PNG 等），CLI 打印提示。
- **搜索策略：全候选取最小 + 边界多看 1~2 档**：PNG/GIF 不再只比较二分访问过的候选，
  最高档达标时评估整条固定网格并在全部达标候选中按体积取最小；JPEG/WebP 在最低达标 q
  附近再向下看 1~2 档。`CandidateParallelism` 改为按「当前可用内存」推导，并新增 OOM
  串行回退（PNG/GIF/WebP 均接入）。
- **JPEG 4:2:2 编码**：新增 `JpegChromaSubsampling` 枚举，编码器支持 4:2:2（16×8 MCU、
  2Y+1Cb+1Cr，SIMD + 标量回退）；优化器同时搜索 4:2:0/4:2:2/4:4:4 按体积取小；
  CLI 支持 `--subsample 420/422/444`。`large.jpg` 选择 4:2:2 q46 / 364.7 KB（感知 46.02 dB）。
- **GIF 动画局部调色板 / crop / 帧去重**：新增 `GifEncoder.EncodeIndexedFrames`
  （每帧局部表、帧矩形、无全局表）；差分帧只编码变化区域包围盒，连续相同帧合并时长。
  实测 `large.gif` −11.5%、`final.gif` −24.5%；修复动画 RGBA 透明像素应保留上一帧的合成语义。
- **CLI `--jpeg-progressive`**：普通 JPEG 转换（`--to jpg` / 输出 `.jpg`）可选择 progressive（SOF2），
  默认仍是 baseline。实现方式是先 baseline 编码再无损转写，像素与 baseline 完全一致；
  实测 `car.png` 8,474 → 5,419 B（−36%）。
- **调色板 k-means 精修**：中位切分后增加 1 轮 Lloyd 迭代（`EnableKMeansRefinement` 默认开启），
  同色数下降低量化误差。实测 `Amish.png` −0.06%、`final.gif` −0.72%，画质不降。
- **JPEG 4:2:0 vs 4:4:4 全候选比较**：4:2:0 达标后仍搜索 4:4:4，按体积取小。
  实测 `large.jpg` 463,598 → 373,443 B（−19.5%），感知 45.43 → 46.49 dB。
  新增 `JpegCompareSubsampling`（默认 true）与 CLI `--opt-no-jpeg-subsample-compare`。
- **画质度量外部后端 + 预乘 alpha**：`QualityMetrics.ExternalMetric` 可接入
  SSIMULACRA2 / Butteraugli 等原生实现（内置不捆绑）；RGBA PSNR 改为预乘 alpha，
  透明区不可见 RGB 不再误伤画质。新增 2 个单测。
- **GIF 透明动画支持**：新增 `GifDecoder.DecodeAnimationRgba32` 与 `GifAnimation.RgbaFrames`，
  透明动画按「整帧 + 全局调色板 + 二值透明 + disposal=2」重编码；不透明动画继续帧间差分。
  实测 `large.gif` 2.42 MB → 2.14 MB（−11.5%）。同时修复精确调色板 `Assemble` 的 offset 错位、
  以及画质比较对双方全透明像素 RGB 的误判。
- **画质判据 V2（Y-SSIM + 最差 1% 块）**：`ImageQuality` 新增 `Ssim` / `WorstSsim`，
  块边长按像素数放大（4~16），`OptimizeOptions.MinSsim=0.95` / `MinWorstSsim=0.75` 默认生效；
  CLI 可用 `--opt-min-ssim` / `--opt-min-worst-ssim` 调整。旧 4×4 块平均 PSNR 会被
  「块内结构被抹平」的候选骗过（棋盘格案例 54 dB），SSIM 仅 0.004。
- **GIF 动画帧间差分**：动画候选预留透明索引，首帧完整、后续帧只写变化像素，disposal=1 保留上一帧。
  实测 `final.gif` 23.39 MB → 17.78 MB（−24.0%）；`large.gif` −3.3%。
  同时修复精确调色板对非采样色抛 `KeyNotFoundException` 的问题。
  透明动画（源图带 alpha）仍保留原图，局部调色板/crop 尚未实现。
- **JPEG progressive 编码**：新增 `JpegProgressiveTranscoder`，复用 baseline 系数重写为 SOF2
  （DC 扫描 + 每分量 AC 扫描 + EOBRUN），不重新量化，像素逐字节一致。同 q 实测
  large.jpg −5.4%、medium.jpg −4.7%、Amish q42 −6.9%；`JpegProgressive` 默认开启，
  CLI 可 `--opt-no-jpeg-progressive` 关闭。
- **JPEG 无损 Huffman 表重排**：新增 `JpegHuffmanOptimizer`，对基线单扫描 JPEG 复用解码器系数、
  按实际符号频率重建最优 Huffman 表并重写熵编码段；兼容 restart 标记与元数据剥离。
  `large.jpg` 626,239 → 565,492 B（−9.7%），像素逐字节一致；渐进式自动回退到只去元数据。
- **JPEG 无损重封装（阶段 1）**：新增 `JpegLosslessOptimizer`，删除 APP1（EXIF/XMP）、APP13、COM，
  保留 ICC 与熵编码数据，不碰系数；Orientation≠1 时保留 APP1。同格式 optimize 在
  「原图 / 无损产物 / 有损重编码」间按 10% 收益门槛选择，避免为微小体积再损失一代画质。
  新增 CLI `--opt-lossy-min-gain`。
- **PNG 赢家精修**：调色板图实测 None/Up/Adaptive 三种滤波 + 最小位深 + 按使用频率重排调色板；
  `Amish-Noka-Dresser.png` 111,025 → 85,867 B（−22.6%）。顺带修复灰度候选与 zlib 9 的比较口径。
- **WebP 按尺寸选 method + 无损候选**：≤20 MP 产出档改为 `method 6`（体积更小），
  >20 MP 保持 `method 3`；低色数不透明图额外尝试无损 WebP（走 `WebPEncodeLosslessRGBA` 直通，
  `WebPConfig.lossless` 路径实测并非真无损）。实测 `car.png` −19.0%、`5_star_base` −3.0%、
  `video-001` −3.1%，256² 低色数图无损 814 B/162 B。
- **WebP 大图搜索改为 q40 锚点 + 小网格 + 二分**：143 MP 候选数 19 → 8；默认大图下界保持 40， 
  小图默认 20。显式 `--opt-webp-min 20 --opt-webp-large-min 20` 可在 143 MP 上再省 7.8%（代价是搜索更慢）。
- **PNG 调色板位深 1/2/4 + 灰度颜色类型**：`PngWriter.WritePalette` 支持位深打包，
  optimize 对 ≤16 色的赢家自动降到最小位深；不透明灰度图尝试颜色类型 0。
  实测 16 色 256² 图形 1,214 → 343 B（−71.7%），32 级灰度 1,001 → 583 B（−41.8%）。
- **PNG 抖动/不抖动双候选**：同色数下两个变体都参与画质判定与体积竞争（GIF 早已如此）。
  抖动会打散索引熵、让 deflate 命中率下降，部分图不抖动反而小一个数量级。
  实测 `Amish-Noka-Dresser.png` −19.2%、12.6 MP `large.png` −63.0%，`car.png`（精确取色）不变。
- **PNG 赢家补压 zlib 9**：搜索期仍用 zlib 6 排序，选出赢家后用新增的
  `PngWriter.RecompressIdat` 只重压 IDAT（不重新滤波/量化，像素不变）。默认只对 ≤8 MP 生效
  （`PngMaxDeflatePixelThreshold`，避免大图多花几十秒），CLI 可用 `--opt-no-png-deflate9` 关闭。
  实测 `car.png` −9.9%、`Amish-Noka-Dresser.png` −2.3%。
- **跨格式透明合成**：透明图转 JPEG / BMP 时不再把 alpha 直接丢掉（透明区会变黑），
  而是按新增的 `OptimizeOptions.MatteColor`（默认白）合成到背景，并在 `Method` 里标注背景色。
  目标为 PNG / WebP / 单帧 GIF 时仍保留透明。
- **16-bit / 动画输入保护**：`ImageOptimizer` 在 optimize 前识别 16-bit PNG、APNG 与动画 WebP。
  同格式 optimize 直接保留原图并给出原因（重编码会截断为 8-bit / 只保留首帧）；
  跨格式转换照常执行，但会在方案描述里标注降级。新增 `SourceTraits` 与 3 个单测。
- **`OptimizeOptions.WebpMinQuality` / CLI `--opt-webp-min`**：WebP 搜索下界独立于 JPEG，
  默认仍为 40；显式设 20 可再省约 7.8%（143 MP），但现有搜索会从 1 个候选涨到 19 个
  （11.3 s → 27.0 s），待搜索策略改造后再下调默认值。
- 审计与改进计划文档：[docs/OptimizeAudit.md](docs/OptimizeAudit.md)、[docs/OptimizePlan.md](docs/OptimizePlan.md)。

## 1.1.0（相对 v1.0）
### 规则
- **废止性能准入门槛**。项目此前设有「性能提升不足一定比例即视为无效提升，不保留、不提交」的硬性门槛，
  现整体撤销：该门槛自设立起只服务于**速度**指标，只用于判断「某段代码值不值得动手改」，
  **从不适用于文件大小、画质、噪声或任何速度之外的指标**——那些指标按各自的绝对口径验收
  （体积取实测字节数、画质取 PSNR / 感知 PSNR、产物一致性取 md5）。
  随着性能工程收尾（SIMD 已全链路覆盖、剩余热点经封死性上界判负），项目不再以「提速」立项，
  该门槛失去适用场景。文档与源码注释中原有的比例判定表述已一并删除，
  相关处置统一表述为「性能提升不理想」。
- 与本次改动的关系：`BuildOptimalHuffmanTable` 的修复属于**降体积 + 正确性**修复
  （旧表在长尾分布上比固定 Annex K 表还大 4.06%），本就不适用该门槛，按既定原则保留。

### 新增
- **格式转换与智能压缩打通（CLI `--to <格式> --optimize`）**：转成目标格式时直接产出该格式下体积最小的版本，
  无需「先转换、再单独 optimize 一遍」。库层 `ImageOptimizer.Optimize(input, output)` 现在按输出扩展名路由：
  与源格式相同仍是同格式压缩，不同则解码一次后按目标格式做质量 / 调色板搜索并写出（只经过一代编码，
  画质下限以原始图像为参考，优于对转换产物再压一次的二次有损链）。支持 `.jpg/.jpeg/.png/.webp/.gif/.bmp`
  作为目标（BMP 无压缩，如实标记为无损直写）。`OptimizationResult` 新增 `Converted` 标记；
  跨格式时不走「无收益保留原图」（原格式 ≠ 目标格式），始终写出目标格式文件。
- 压缩器内部重构为「纯计算产物（`OptimizeArtifact`）+ 统一落盘」两段式：
  `Jpeg/Png/GifOptimizer` 拆出 `Produce` 与 `RunFromImage`；WebP 优化独立为 `WebpOptimizer`。
- **WebP 优化改为 RGBA 编解码**：同格式优化此前走 RGB24、透明通道会被丢弃，现在保留 alpha。
- **修复单帧透明 GIF 优化丢透明**：帧映射此前用「RGB24 展开成 RGBA（alpha 恒为 255）」，
  导致预留的 0 号透明槽永远不被命中；现在带透明时用真实 RGBA 参与映射。
- **修复单文件输入时 `--to` 被忽略**：`--to webp photo.jpg` 之前会落到默认的 `.png`；
  现在单文件同样尊重 `--to`。`--to gif` 也不再被归一化成 `.png`（`NormalizeOutputExtension` 补上 `.gif`）。
- 新增单元测试 6 项：PNG→WebP / JPEG→PNG（校验写出的是真 PNG 而非原文件副本）/ PNG→BMP 无损 /
  PNG→GIF / 同格式目标不被误判为转换。
- **ImageFrame 兼容 BGR24 / BGRA32 / RGBA32 中间格式**：`ImagePixelFormat` 新增 `Bgr24`、`Bgra32`、`Rgba32`，
  `ImageFrame` 构造函数支持以这些格式直接装载（按每像素字节数自动校验长度）。
- **SIMD 加速的通道转换原语（`SimdHelper`）**：`SwapRgbBgr24`（RGB24⇄BGR24 就地/异处交换）、
  `SwapRgbaBgra32`（BGRA32⇄RGBA32 就地/异处交换，每批恰好读写 16 字节，就地时无需部分写入保护）、
  `ConvertRgb24ToBgra32`、`ConvertBgr24ToBgra32`、`ConvertBgra32ToRgb24`、`ConvertBgra32ToBgr24`、
  `ConvertRgba32ToBgr24`、`ConvertBgr24ToRgba32`，
  x86 走 SSSE3 `pshufb`、arm64 走 NEON `tbl`，并带标量回退（Rgba32⇄Rgb24 复用已有的 `ExpandRgbToRgba` / `PackRgbaToRgb`）。
- **ImageFrame 转换 API**：`ToRgb24() / ToBgr24() / ToBgra32() / ToRgba32()`（非破坏式，非 RGB24 帧保存时自动经 SIMD 规范化为 RGB24），
  以及零额外内存的就地交换 `SwapRgbBgrInPlace()`（同一位深内交换 R/B：24 位 `Rgb24⇄Bgr24`、32 位 `Bgra32⇄Rgba32`），
  便于与 OpenCV / 相机 / GPU 等 BGR/BGRA 数据源互操作。
- 新增单元测试：BGR24/BGRA32/RGBA32 共 8 个 SIMD 转换原语的「标量参考对比 + 全长度覆盖」校验，
  以及 13 个 ImageFrame BGR/BGRA/RGBA 互操作测试。

### 工具
- 新增 `.github/workflows/release.yml`：推送 `v*` tag 即自动构建 win-x64 / linux-x64 / osx-arm64
  三套 CLI 资产（**每套都做实机冒烟**：`PNG→WebP→PNG` + `PNG→JPEG`，用于证明原生 libwebp 能加载）、
  打包 nupkg/snupkg、生成 `SHA256SUMS.txt` 并创建/更新 GitHub Release。
  手动 `workflow_dispatch` 只接受一个已存在的 tag 且只写**草稿** Release（试跑/补发不会公开发布）。
  runner 选择：`windows-latest` / `ubuntu-latest` / `macos-15`（arm64）——实测 `macos-latest`(=macos-26 arm64)
  会排队超时被取消，`macos-15` 秒级拿到且能实机跑通。
  （`.github/` 在 `.gitignore` 中，新增 workflow 需 `git add -f`。）
- 新增 `tools/build-cli.sh`：构建 CLI 的发布资产（含原生 WebP 库，使用者无需安装 .NET），
  支持 win-x64 / linux-x64 / osx-arm64，产物落在 `.artifacts/release/` 并生成 `SHA256SUMS.txt`。
  脚本放 `tools/` 根目录而非 `tools/release/`：`.gitignore` 的 `[Rr]elease/` 会匹配任意层级的 release 目录。
  已做跨平台适配：GNU tar 才加 `--mode` 强制可执行位（BSD tar/macOS 不需要），校验和优先 `sha256sum`、
  缺省回退 `shasum -a 256`；macOS 分支里的变量紧邻中文一律写成 `${VAR}`（macOS 的 bash 3.2 在非 UTF-8
  locale 下会把中文首字节并入变量名，报 `RID?: unbound variable`）。

### 文档
- 新增 `docs/Release.md`：固化发版口径（版本号 → tag → GitHub Release → 资产清单 → NuGet 自动发布触发），
  含逐步执行清单、回报格式与本机验证手段（win 直接跑、linux 走 WSL、osx-arm64 由 CI 的 macos runner 验证）；
  `docs/README.md` 索引同步更新，`README.md` 的 CLI 章节补充预编译版本的下载入口。

### 改进
- **WebP `--optimize` 在 143 MP 上 39.6 s → 14.2 s（−64%，产物 md5 逐字节不变）**，两处根因都在搜索策略：
  1. `WebpOptimizer` 把候选并行的内存预算写死成 384 MB，而 143 MP 的单候选峰值 ≈ 8 B/px ≈ 1.14 GB，
     `384 / 1140` 被夹成 **1** ⇒ 并行度静默塌成串行，整条二分退化为 7 个候选串行（≈ 28 s）。
     改为跟随 `CandidateParallelism` 的默认预算（可用内存 1/4，夹 384 MB~8 GB）。
     （与之前 PNG optimize 那处是同一类坑。）
  2. 质量二分先探**最高档**再二分；而 WebP 的画质下限普遍宽松，下界 q=40 往往已达标
     （本图 7 次评估全部白做，最终仍停在 q=40）。现在大图（≥8 MP）**先探下界**，达标即收尾；
     不达标再探上界，上界也不达标就直接交出上界；小图不预探，避免多一波（1.2 MP 实测 443 → 530 ms，+18%）。
  - 现状分解（143 MP / q40）：源图解码 0.55 s + 搜索 3.3 s（1 个候选）+ 产出期 m3 编码 8.4 s（占 57%，已是地板）。
  - 新增 `SIC_WEBP_OPT_TIMING=1` 分阶段探针（与 `SIC_JPEG_STAGE_TIMING` 同一约定）。
- **下界命中时不再连带编码最高档**：原先大图「并行探两端」，而 q=95 比 q=40 贵（143 MP / m2：3.62 s vs 2.42 s），
  下界达标时上界的墙钟完全白付。改为串行 `下界 → (不达标才) 上界 → 网格`：
  143 MP 照片搜索期 **4.5 s / 2 个候选 → 3.3 s / 1 个候选（−27%）**，端到端 15.9/16.7 s → 14.7/15.1 s，
  产物 md5 不变。代价只落在「下界与上界都不达标」的图（罕见的画质不达标图，如 64 MP 纯噪声）：
  两个探测串行，搜索期 6.0 → 10.1 s（+4.1 s），产物 md5 仍一致。
- 实测后**否决**的三条（详见 `docs/Optimize.md`）：`thread_level=1`（孤立编码 −7.8%，进管线反而更慢）、
  不透明图改 RGB 导入（编码 −8.6% 但要 +33% 峰值内存换 ~0.6 s）、
  「按产出档直接编下界候选并复用」（达标率约 50%，低于 76% 盈亏平衡点）。
- **WebP 默认档由 `method 4` 改为 `method 3`**（`WebpEncoderOptions` 默认值、`WebpOptimizer.OutputMethod` 同步）。
  libwebp 1.6.0 的 method 4 在 >140 MP 有 4 倍悬崖：143 MP / q75 实测 m4 32.3 s / 1,655,814 B，
  m3 **8.2 s / 1,597,784 B（快 3.8 倍且体积小 3.5%）**；悬崖边界极窄（140.58 MP 7.8 s → 142.99 MP 32.3 s），
  且 m5/m6 在同一张图上均正常（8.5/8.9 s），说明是 m4 特有代码路径而非像素数成本上升。
  在 1~143 MP 的 7 个样本（含两张真实照片）上 m3 体积全部 ≤ m4（差 0.06%~3.5%），速度不劣。
  端到端（JPEG 解码 + RGB 导入 + 落盘）：`progressive.jpg` → webp 默认档 **30,505 ms → 9,483 ms**。
  新增 `--webp-method 0~6` 供显式覆盖（越界值告警并回落默认）。
  optimize 路径「搜索期 m2 排序 + 赢家按产出档补编」的性质不变：补编用原始像素，
  小图实测产物与直接按该质量编码 **md5 完全一致**。
- **GIF `--optimize` 在超大图上把「候选并行」变成了负优化**：143 MP 照片（10650×13426）
  `--to gif --optimize` 端到端 7.3 s，且并行版（16 个候选）反而比串行（5.1 s）更慢 ——
  上一条的候选并行只对「阶梯大部分都会被访问」的场景成立。四处根因与对应修复（`GifOptimizer`）：
  1. **并行评估的候选数远超串行二分真正访问的数量**：二分第一次只探测最高档，若最高档就不达标，
     更低色数只会更差，二分立刻收敛 —— 串行路径全程只评估 **1 档（2 个变体）**，
     而全量并行把整条阶梯 8 档 × 2 变体 = **16 个候选**全算了一遍，8 倍的额外工作无论多少核都补不回来。
     现在**先单独探测最高档**（与 PNG 的同款做法）：不达标就直接收尾，达标才铺开全量并行；
     且该短路只对「搜索期确实降过采样」的大图启用 —— 小图全量并行本来就快，
     拆成两批 `Parallel.For` 会让线程池来不及扩容（实测 5 MP 慢 8%）。
  2. **搜索期的画质评估用整幅图，`MaxSamplePixels` 对单帧完全没生效**：`sampleFrames` 只约束动画帧数，
     单帧时 `sample` 就是全图（143 MP ⇒ 572 MB），`prepare / map / restore / compare` 全按 1.4 亿像素算。
     现在单帧按 `MaxSamplePixels`（8 MP）**均匀抽行**得到搜索素材：调色板与画质判定只用这批行，
     抽样按整行进行（行内像素全取），因此不受像素打包方式影响。
  3. **每个候选都做一次全图 LZW 编码，而体积在多数路径上根本不参与挑选**：
     达标时体积只用于「多个达标候选之间取最小」，未达标时只按感知 PSNR 取最高 ——
     后者连一次编码都不需要。现在产物编码改为**惰性**（`Lazy<byte[]>`）：搜索期只算画质，
     真正需要体积时才付全图「映射 + LZW」。选中候选的全图映射由同样参数重建的量化器完成
     （`Prepare` 确定性 ⇒ 调色板逐项相同）。
  4. **每个候选各做一次全图 RGB→RGBA 展开，而且用的是标量循环**：143 MP 上每个候选多写 572 MB，
     并行时十几份同样的临时缓冲同时存活。现在单帧只展开一次并共享（改用 `SimdHelper.ExpandRgbToRgba`），
     候选的 `frameRgbaBuffer` 只在多帧动画时才分配。
  另外单帧的「常规编码基线」与选中候选的产物编码互相独立，大图上把它放到后台起跑，与搜索重叠。
  实测（143 MP，Release，同机交错 A/B，产物 md5 三次一致）：
  端到端 **7346 → 2369 ms（−68%）**；搜索期 `prepare 2329→106 / map 17136→73 / quality 7689→27 ms`；
  候选 **16 → 2**；画质不降反升（PSNR 41.81→**41.90 dB**、感知 43.51→**46.45 dB**），
  体积 22.11 → 22.13 MB（+0.07%，采样建调色板的正常波动）。
  ≤8 MP 的图不降采样、也不延迟编码，产物 **md5 与改前逐字节相同**（`car.png` / `Amish-Noka-Dresser.jpg` /
  渐变图 / 5 MP 合成图均验证），耗时持平（±2%）；动画 GIF 与带透明 PNG 的产物亦逐字节相同。
- **GIF `--optimize` 候选并行 + 三处零散浪费**（沿用 PNG / JPEG optimize 的同一套优化思路）。
  GIF 的量化 / 映射 / LZW 全是单线程（与 PNG 的 deflate、WebP 的 libwebp 同构），
  搜索期 8~16 个候选此前串行执行，24 核单文件只用到 1 个核。
  1. **候选并行**：并行评估「阶梯 × 抖动」全部候选，再用 `ImageOptimizer.ReplayLadderVisits`
     复刻串行二分**实际会访问**的候选子集，只把这一子集交给挑选逻辑 —— 多算的候选直接丢弃，
     产物与串行路径 **md5 逐字节相同**。并行度走既有的 `CandidateParallelism` 内存预算
     （单候选峰值 ≈ 索引 1 B/px + 还原缓冲 4 B/px），大图自动塌回串行。
     同二进制交错 A/B（`SIC_GIF_NO_PARALLEL=1` 切串行）：1 MP 733 → **320 ms**（−56%，5/5 轮胜）、
     5 MP 1740 → **688 ms**（−61%），产物 md5 全等。
  2. **画质比较的内层并行按核数自适应**：`QualityMetrics.Compare` 新增 `allowInnerParallel` 重载。
     候选数喂不满核数时保留按带并行（填满空闲核），已能占满核数时关掉（16 候选 × 核数 只会互相抢占）。
     5 MP 实测：无脑关掉 768~812 ms，自适应后 645~722 ms。
  3. **`RestoreRgba` 改为 32 位查表**：调色板先展开成「索引 → RGBA32 打包」，逐像素从
     4 次字节查表 + 4 次字节写降到 1 次 32 位查表 + 1 次 32 位写（1 MP 单次 4.6 → 2.3 ms，
     8 候选省 ~18 ms；PNG optimize 走同一函数同样受益）。大端平台保留标量回退。
  4. **单帧免拼索引 + 基线画质延迟评估**：`BuildSampleIndices` 单帧直接复用索引缓冲（省一次全图复制）；
     「常规编码基线」的画质此前每次都跑一遍解码 + 全图比较（1 MP 约 40 ms），
     现在只编码字节，只有真正退回基线这条冷路径才评估画质（1 MP 65 → 25 ms）。
  探针：`SIC_GIF_OPT_TIMING=1` 输出 prepare / map / encode / quality / baseline 分阶段累计。
  1 MP 分阶段（串行，累计 CPU）：prepare 83.1、map 122.7、encode 43.1、
  quality 285.6（restore 36.5 + compare 249.1）、baseline 65.4 ms —— **compare 是单候选最大头（37%）**，
  也是后续唯一还值得继续压的部分（4 通道块累加尚未向量化）。

### 修复
- **GIF `--optimize`（跨格式转换）产物可能比不加 `--optimize` 的普通转换还大 1~4 倍**：
  优化器只搜「调色板颜色数」，抖动固定用 Floyd–Steinberg 全强度（`DitherStrength = 1.0`），
  而 FS 的误差扩散把索引流打成高频噪声、LZW 字典命中率暴跌 —— 同一色数下体积可膨胀 5 倍以上
  （`car.png` 256 色：抖动 92 152 B / 无抖动 17 492 B，600×450），
  且感知 PSNR（4×4 块平均）天然偏袒抖动（误差在块内互相抵消：54.2 dB vs 44.1 dB），
  于是画质下限几乎总是选中抖动候选，再叠加「跨格式路径没有体积兜底」，倒挂就发生了。
  实测（`car.png → gif`，普通转换 60 472 B）：`--optimize` 74 838 B（+24%）。
  三处修复（`GifOptimizer.Produce`）：
  1. **抖动成为独立搜索维度**：每个颜色数同时评估「抖动 / 无抖动」两个变体（同色数共享一次
     `Prepare`，只多一次映射与编码），在达标候选里仍按体积取最小。
  2. **抖动体积预算**：抖动变体必须 ≤ 同色数无抖动变体 × 2.0，否则视为「抖动收益抵不过 LZW 损失」直接弃用
     ——防止感知 PSNR 对抖动的系统性偏袒把 5 倍体积的候选抬进终选。
  3. **单帧常规编码基线兜底**：与不加 `--optimize` 完全同参（八叉树 + Bayer8、256 色）额外编码一次，
     任何候选不比它小就退回基线 —— 保证 `--optimize` 永不劣于普通转换。
  配套修复 `PaletteQuantizer`：新增 `ResetLookupCache()` —— 「颜色桶 → 最近色」记忆化缓存以桶为 key、
  首个落入像素决定值，抖动与无抖动两种映射**不能共享缓存**，否则后评的变体会拿到被污染的次优最近色
  （产物依赖评估顺序，不可复现）；新增 `IsExactPalette` 供精确取色时跳过第二个变体（精确调色板不走抖动，两变体同产物）。
  效果（产物均为合法 GIF、解码尺寸不变）：`car.png → gif --optimize` 74 838 → **17 492 B**（普通转换 60 472 B）；
  全范围渐变合成图 600×450：普通转换 37 246 B → `--optimize` **24 738 B**（−34%，FS 候选全部被预算拒绝）；
  照片类 `Amish-Noka-Dresser.jpg → gif --optimize` 保持 100 074 B（16 色 + 抖动，预算内达标，无回退）。
  新增回归测试 `Optimize_ConvertPngToGif_NotLargerThanPlainConversion`。
- **JPEG `--optimize` 的搜索期把与质量无关的 produce 重复算了 6 遍**：`produce`（RGB→YCbCr + 4:2:0
  下采样 + 电平平移）只读源图和几何，**与质量档无关**，但旧实现每评估一个候选就从头跑一遍；
  143 MP 实测它占单候选 **56%（186/334 ms）**，5~6 个候选白白重复。
  新增 `JpegSampleCache`：把采样后的样本按 MCU 存成 `sbyte`（电平平移后取值域正好 [-128,127]，
  143 MP 的 4:2:0 约 215 MB），整幅采一次（按行并行，13 ms）后各档共用；
  新增 `JpegEncoder.EncodeFromSamples`：按行并行做「量化 + scatter 到组件平面」，
  每 MCU 用 1.5 KB 栈缓冲原地 DCT+量化，不再走 MCU 批的池化租借与队列同步。
  样本逐位相同 ⇒ 量化系数逐位相同 ⇒ 产物 **md5 逐字节不变**（`543a15bb…`）。
  同二进制交错 A/B（143 MP）：端到端 3145/3551/3334 → **2648/2700/2468 ms**；
  搜索期单独计量 1611 → 526 ms（−67%）。
- **搜索期重建的输出缓冲每个候选都重新分配 429 MB**：`ReconstructImage` 里 `new byte[w*h*3]`
  会被运行时清零，实测 **26 ms/次**，而重建会把每个像素都写满，清零纯属浪费。
  `Reconstruct` 现在复用一块 `GC.AllocateUninitializedArray` 缓冲（不清零，0.1 ms 量级）。
  注意返回的数组是「借」的：下一次重建会覆写，调用方必须立刻消费（搜索期正是「重建 → 比较」紧邻）。
- **JPEG `--optimize` 的搜索期不必要地走了一趟熵编码 + 熵解码**：二分判定只看画质分数，
  体积从不参与决策（赢家最终一定会重新完整编码一次），但旧实现每个候选都先编码成字节流、
  再解码回像素来算 PSNR。Huffman 只改变系数的**编码形式**、不改变系数本身（熵编解码严格无损），
  所以这趟往返对结果零影响，纯属浪费：143 MP 实测熵解码 **192 ms/候选**、熵编码 192 ms（并行，墙钟被色转换遮住）。
  现在搜索期改走新的 `JpegEncoder.EncodeCoefficients` + `JpegCoefficientSink`：编码器在熵编码之前
  把每个 MCU 的量化系数直接按「解码器组件平面」布局交出，再经 `JpegFrameState.AdoptCoefficients`
  走与正常解码**完全相同**的 `ReconstructImage`（反量化 + IDCT + 上采样 + 色彩转换）得到像素。
  落盘产物仍走完整编码，**md5 逐字节不变**。
  新增 `JpegCoefficientBypassTests`（4 个质量档 + 4:2:0/4:4:4，断言「系数直通重建」与「字节流解码」
  的 SHA-256 相等），以及 `CoefficientBypass_ReusesBufferAcrossQualities`（跨候选复用同一份缓冲）。
  实测（143 MP / 6 候选，同进程交错两轮）：搜索期 5895/5791 ms → **4809/5063 ms**。
- **`QualityMetrics.Compare` 是 JPEG `--optimize` 的头号开销，却是标量单线程**：143 MP 单次 ≈ 880 ms，
  6 个候选约 5.3 s，占搜索期八成。改成按「带」分片并行（一带 = `blockSize` 行，4×4 块不跨带，
  分片内的累加顺序与串行逐项相同；分片数固定为 `min(核数, 带数)`，合并按分片下标，
  因此同机结果完全确定，不会因线程调度让「刚好压线」的候选随机翻转）。
  **5280 ms → 391 ms（13.5×）**，分数差最大 `|ΔPSNR| = 2.6e-8 dB` / `|Δ感知| = 2.2e-10 dB`。
  并行入口要求输入坐在数组上（Span 不能跨线程捕获），传 Span 的调用仍走串行实现，口径一致。
  新增 `QualityMetricsParallelTests`（含 4 个像素以内不整除的边缘尺寸）。
  合计：143 MP `--optimize` 端到端（同机交错 A/B ×2）7124/7105 ms → **3473/3251 ms（−52%）**，
  产物 md5 `543a15bb…` 四份全等；`--opt-verbose` 的候选日志不再带「N 字节」（搜索期不再产字节流）。
- **普通转换写 PNG 时对不透明图也输出 RGBA（颜色类型 6）**：`PngWriter.WriteRgba` 不看 alpha，
  而 `Configuration.SaveRgba32` 一律走它，于是每像素都白写一个恒为 255 的 alpha 字节。
  10650×13426（143 MP）不透明照片实测 **31,919,483 B → 29,469,583 B（−7.7%）**，
  且 deflate 少处理 25% 原始字节，端到端（同机交错 A/B ×3）3135/3187/3186 ms → **2763/2755/2762 ms（−13%）**。
  现在 `WriteRgba` 先做一次「alpha 是否全 255」判定，命中则打包成 RGB24 按颜色类型 2 写出。
  判定用 `SimdHelper.IsOpaqueRgba`（以 uint 视图按 `Vector<uint>` 比较最高字节，带 alpha 的图在头几个向量就返回，
  143 MP/545 MB 实测 22~29 ms），打包走已有的 `SimdHelper.PackRgbaToRgb`（SSSE3 `pshufb` / NEON `tbl`，45~55 ms），
  合计不到它省下的 deflate 时间。独立解码器（只用 `System.IO.Compression`）校验 RGB+alpha 的 SHA-256 与改前逐像素一致；
  带 alpha 的图仍写颜色类型 6。新增 7 个单元测试（1×1 / 奇数宽 / 向量边界 / 首尾单像素透明）。
  同时把 `PngOptimizer` / `ImageOptimizer` 里各自手写的 `IsOpaque` 循环、以及 `PngOptimizer.PackRgb`
  的标量打包统一到上述两个 SIMD 原语，去掉三份重复实现。
- **PNG `--optimize` 在照片上产出的文件比不加 `--optimize` 还大 19%**（自适应滤波是个错的启发式）：
  `PngAdaptiveFilter` 逐行挑「滤波后字节的有符号绝对值和」最小的滤波器（PNG 经典的 MSAD 代价），
  但它优化的是 L1 范数，deflate 真正吃的是**跨行重复**——整列统一 Up 时相邻行的差分高度相似，
  LZ77 能跨行匹配整行；逐行换滤波器会把这条匹配链打断。
  10650×13426（143 MP）照片实测（同一份像素、level 6、无元数据）：**Up 29,469,583 B，
  逐行自适应 35,142,122 B（+19.2%）**，而该图 2972/3000 行都被选成了 Paeth；
  自适应还要慢 2.4 倍（2040 ms → 4959 ms）。普通转换走的是 Up，于是一路出现
  「转 PNG 3 s / 31.9 MB，而 `--optimize` 8 s / 33.5 MB 反而更大」。
  现在无损真彩色重写改成 **Up 与自适应各完整编码一遍、按实际字节数取小者**（两遍并行，墙钟等于较慢那遍），
  方法名如实标注用了哪种滤波（`PNG 无损重写（Up 滤波）` / `（自适应滤波）`）。
  同一张图：29,469,583 B（-16.1%），独立解码器校验 RGB 的 SHA-256 与普通转换逐像素一致。
  调色板路径保留自适应：1000×1000 16 色产物实测 Up 145,270 B vs 自适应 140,614 B（自适应小 3.2%），
  说明两种滤波各有胜场，所以是「实测取小」而不是「一刀切换成 Up」。
- **PNG `--optimize` 在 143 MP 图上要 41 s**（两处叠加：并行度静默塌成 1 + 白算 7 个候选）。
  ① `CandidateParallelism` 的默认内存预算固定 512 MB，而单候选峰值是「索引 1 B/px + 还原缓冲 4 B/px」
  ≈ 5 B/px（143 MP 约 716 MB）⇒ `512 MB / 716 MB` 被夹成 1，**并行度静默退化为串行**；
  改为按 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4` 推导（夹在 384 MB ~ 8 GB），
  63.5 GB 的机器上 8 档候选全并行，8 GB 机器上自动退到 1~2。
  ② 原实现先把整条 8 档颜色阶梯**全部**编码一遍，再按二分实际访问序列挑结果；
  而二分的第一步永远是最高档，它不达标时其余 7 个候选一个都不会被访问——
  该照片 256 色候选只有 38.22 dB 感知分（门槛 45.3 dB），8 次编码只用了 1 次。
  现在先只算最高档，达标才把其余候选一次波并行铺满；`dop == 1` 时不预评估，按二分访问顺序惰性编码。
  交错 A/B（同一份二进制 ×3，143 MP，`--optimize` 端到端）：**41.4 / 40.6 / 41.5 s → 8.75 / 8.58 / 7.88 s**。
  最高档达标时的产物不受影响：1000×1000 16 色图与改前逐字节一致（md5 `2e1e62a5…`），
  带 alpha 的 PNG 同样逐字节一致（`803b919d…`）。
- **按图优化的 Huffman 表在高细节图上反而比固定 Annex K 表更大**（`JpegEncoder.BuildOptimalHuffmanTable`）：
  建树前把符号频率缩放到总权重 ≤500 来规避 16 位码长上限，长尾稀有符号因此被抹成同权、
  高频符号码长被拉长。1254×1254 高细节图的 AC 亮度表相对最优解偏 +6.80%，
  比固定 Annex K 表还大 4.06%（`--optimize` 产物因此比 `--quality 89` 直出还大 5,608 字节）。
  现在改为**用真实频率建树 + libjpeg（`jchuff.c jpeg_gen_optimal_table`）的限长调整循环**：
  从最长组取两枚码，一枚搬进短一位的组、同时把一个更短的码字前缀一分为二，Kraft 和与符号总数守恒。
  同一张图熵编码 1,903,946 → 1,841,491 bit（-3.3%，比 Annex K 省 13,714 bit，仅比最优解大 0.01%），
  文件 238,493 → 231,370 字节（-3.0%），量化系数流逐符号完全一致（像素不变）。
  限长结果若结构不可用（符号数不守恒 / 出现负数 / 有残留 >16 位组）自动退回旧路径，保证 DHT 永远合法。
  这是**正确性修复而非提速**：旧实现的表在同一分布下最差比最优解差 13.05%，修复后最差 +0.26%。
- 新增单元测试：`BuildOptimalHuffmanTable_SteepTailDistribution_StaysNearOptimal`
  （四种长尾分布下码位代价须在最优解 1% 以内，旧实现偏 +4.9%~+13.1% 会被拦下）；
  `SkewedDistribution` 用例补上「限长后仍是合法前缀码 + Kraft 合法」断言；
  `RandomPhotoStillValid` 断言由 `optimized <= plain × 1.02` 收紧为 `optimized < plain`
  （高频细节图正是旧实现的失分区）。
- **macOS 上 WebP 原生库加载不了（既有缺陷，影响 NuGet 的 macOS 消费者）**：仓库里的 `osx-arm64/*.dylib`
  带着构建机的 `@rpath`（`/Users/lastonesky/Project/libwebp/build`）与版本化依赖名
  （`@rpath/libsharpyuv.0.dylib`、`@rpath/libwebp.7.dylib`），在别的机器上依赖解析失败。
  实测：无论单文件还是目录式打包，在真实 arm64 macOS 上都报「未能加载 WebP 原生库」。
  - 发布产物侧已修：macOS 改为目录式发布，构建后用 `install_name_tool -add_rpath @loader_path`
    给每个 dylib 补 rpath、按依赖名提供版本化副本（`libwebp.7.dylib` 等）、并对所有 dylib 与主程序
    做 ad-hoc 签名（arm64 上未签名的可执行代码会被直接拒绝加载）；CI 在 `macos-15` 上实机冒烟通过。
  - **尚未修**：仓库里这两个平台上随包发布的原生库本身（`src/runtimes/osx-arm64/native/*.dylib`）
    未改动，因此 NuGet 包在 macOS 上仍会命中同一问题；linux 的 `.so` 也带着失效 RUNPATH
    （`/home/ted/src/libwebp-main/build`，目前实测能加载，属侥幸）。
- 原生库选择由「宿主 OS」改为「按 RID」（`src/SharpImageConverter.csproj` 的 `_SicNativeRid`）：
  此前在 Windows 上发布 linux-x64 会把 Windows 的 `libwebp.dll` 拷进 Linux 产物（反向同理）。
  未指定 `RuntimeIdentifier` 时行为不变（仍按宿主 OS），因此本机开发/测试/AOT 无影响。
- `Cli/SharpImageConverter.Cli.csproj` 不再重复拷贝 `../src/runtimes/**`：原先会把其他平台的原生库
  （linux `.so` / osx `.dylib`）一并带进发布目录，并被单文件 exe 的原生库清单引用（产物已实测可跑）。
- **JPEG 测试之间通过全局 SIMD 开关互相污染**：`JpegEncoderSimdTests` 为拿标量基线会把
  `SimdJpegEncodePipeline.ColorSupported` / `FdctSupported` 置 `false` 再在 `finally` 里恢复，
  而 xUnit 默认让不同测试类并行；窗口期内别的 JPEG 测试会跑到标量路径上，一旦「同一输入编两遍」
  的比较两侧分处窗口内外，结果就不再逐位相同。实测 `JpegCoefficientBypassTests` 的
  `CoefficientBypass_MatchesBitstreamDecode(q=60, 4:2:0)` 在首轮全量里偶发失败一次、其后 8 轮全过。
  新增 `JpegSimdStateCollection`（`DisableParallelization = true`），把 6 个跑 JPEG 编解码的测试类
  收进同一 collection 串行执行。生产代码从不写这两个静态属性，因此不是产品缺陷。

## 1.0.0（相对 v0.2.8）
### 规则
- **测量纪律**：耗时判定取重复测量的**中位数**（`--gif-bench N` 的 median），
  不取单次、不取 min/max；上界分析要做，但只用于判断某一段改不改得动。
  （本项目原有的性能准入门槛已废止，见「1.1.0 → 规则」。）
- **LZW 产物验收口径（2026-10-03 修订）**：新 LZW 算法不要求与旧算法产物逐字节一致，
  改为要求**往返一致**——`解压(压缩(x))` 必须等于编码前的数据（LZW 前索引的 md5 相同）。
  `LzwRoundTripTests`（8 例）即该口径的落地，断言为逐字节全等。
  **算法改动的验收指标 = LZW 编码耗时**（非压缩率）；据此 lazy matching 与自适应提前 clear
  经封死性上界判负（多付的查表/清表开销 > 收回的出码收益），只分析未实现。

### 改进
- **arm64 全面补齐 SIMD：新增跨架构原语层 `SimdCompat`，JPEG/PNG/GIF/BMP/缩放全链路提速（JPEG 解码最高 5.9x）。**
  起因：库中所有 SIMD 路径都由 `Sse2/Ssse3/Sse41/Avx2/AvxVnni.IsSupported` 守卫，
  而 arm64 上这几个属性**全部为 false** ⇒ 整条向量路径在 ARM 上静默退化为标量死代码
  （14 个文件、约 490 处调用点）。
  - **做法**：新建 `src/Core/SimdCompat.cs` 作为跨架构原语层，每个方法按 `IsSupported` 分派。
    **x86 分支保持逐字不变**（对 x86 零影响），arm64 分支走 NEON。
    映射要点：`UnpackLow/High`→`ZipLow/ZipHigh`；`PackUnsignedSaturate`→`ExtractNarrowingSaturateUnsigned{Lower,Upper}`；
    `pmaddwd`→`MultiplyWideningLower/Upper`+`AddPairwise`；`psadbw`→`uabd`+3×`AddPairwiseWidening`；
    `phaddd`→`AddPairwise`；`pmovzxbd`→两级 `ZeroExtendWidening`；整寄存器移位→`ExtractVector128`。
  - **纠正两处认知**（均有单测固定）：
    1. `tbl` 与 `pshufb` **不完全等价**——`pshufb` 在掩码 bit7=1 时出 0、否则取 `mask & 0x0F`（16..127 会**折叠**），
       而 `tbl` 索引 ≥16 一律出 0。`ShuffleBytes` 统一按 `mask & 0x8F` 归一化后才等价。
    2. NEON `EXT` 方向：`ExtractVector128(v, zero, n)` ≡ `psrldq(v,n)`、`ExtractVector128(zero, v, 16-n)` ≡ `pslldq(v,n)`；
       且 **n=0 时 16-0=16 越界会抛异常**，而 x86 `pslldq x,0` 合法，已单独短路。
  - **新增 `SimdCompatTests`（24 例）**：每个原语按 Intel SDM / ARM ARM **独立推导**标量参考实现后逐 lane 对拍
    （不引用被测代码）。这套测试立即抓出真实 bug——`ShiftLeftBytes(v, 0)` 在 ARM 上抛
    `ArgumentOutOfRangeException` 而 x86 合法。全量单测 189 → **213 通过**。
  - **实测 A/B**（`tools/perf/run-ab.sh`，两侧各 2 轮、跨轮中位数、产物哈希比对）：
    | 链路 | 加速 |
    |---|---|
    | JPEG 解码 large / medium / 渐进式 huge | **5.91x / 4.68x / 3.73x** |
    | RGB→灰度 | 2.04x ~ 3.03x |
    | WebP 解码 | 1.21x ~ 2.50x |
    | 双线性缩放（200% 与 50%） | 1.75x ~ 2.06x |
    | 双三次缩放 200% | 1.61x ~ 1.81x |
    | BMP 编 / 解码 | 1.38x ~ 2.14x |
    | PNG **编码**（Adler32） | +8.4% ~ +9.6% |
    | PNG **解码** | +2%（**无收益**：走 BCL `ZLibStream`，不经本库 Adler32） |
    | GIF 编解码 | +3.4% ~ +14.2%（标量回退本就是查表实现，天花板低） |
    123 项中 58 项提速明显；118 项产物逐字节一致，
    5 项 JPEG 解码差异为**继承自 x86 的既有差异**（SIMD 用色度复制、标量用双线性上采样）。
    逐项取证与三个存疑项的分析见 `docs/ArmFindings.md` §7。
  - **Adler32 移植**：`s1 += b[i]; s2 += s1;` 的跨迭代依赖链把标量吞吐锁在 1.62 GB/s；
    改用 `psadbw` 求 s1、`pmaddwd` 求加权 s2、`HorizontalSumInt32`（NEON 单条 `addv`）归约后达 **7.81 GB/s（4.8x）**，
    12 组数据（含 5552 边界与 `Update` 串联）逐位一致。
  - **修掉基准自身三个会骗人的缺陷**（否则会把上面这些收益误判成回归）：
    1. 固定热身 40 次不足以跨过 tier-0→tier-1 阈值（实测 **80~150 次**），而向量路径的 tier-0
       代码体量远大于标量路径 ⇒ tiny/small 全项被系统性判慢，测出 `upscale-200%@tiny 0.271x`
       这种"输出 0.9 MB 比输出 5.6 MB 还慢 2.4 倍"的物理不可能数字。
       **改为在 `SicBench.csproj` 关掉分层编译**（`TieredCompilation=false`）：首次 JIT 即完全优化，
       热身固定 8 次即可（实测 0.58/0.77/0.62 ms，与热身 1000 次的 0.55 ms 一致），
       整轮 A/B 从 ~15 min 降到 **3 min 58 s**。
    2. `Measure` 在**每个计时样本前**强制 compacting GC，GC 尾部开销反而落进计时窗口：
       改成只在循环开始前整理一次（同算子三轮 2.028/0.888/0.847 ms 乱跳 → 1.000/1.070/1.104 ms 稳定）。
    3. `best()` 用 `current*` glob 会命中上一轮的 `*-final-*.csv`，把**不同构建**的行混进一张表，
       导致产物哈希 DIFF 被"更快的那一行"掩盖 ⇒ 改为按 stamp 精确匹配 + **跨轮中位数**选择器。
- **JPEG 编码器新增「按图优化的 Huffman 表」（`JpegEncoderOptions.OptimizeHuffman`）。**
  起因：`progressive.jpg`（143MP，约 2/3 是近纯白背景，TinyPNG 能压 28.5%、我们却判「无收益」）。
  取证：同一量化表下（q72 的表 ≈ 原图表，luma 64 值和 2097 vs 2136）我们出 4.38 MB、原图 3.09 MB（+42%）；
  纯色图探针显示标准 Annex K 表的空块开销高达 **5.38 bits/块**（DC cat0 2b + AC EOB 4b），
  平坦内容占比高时比特流几乎全被空块开销吃掉。
  修复：两遍编码——第一遍复用「生产 → DCT 量化」管线只统计 DC/AC 符号分布（不产出字节），
  第二遍用生成的最优表编码。表生成用显式树 + BFS 求码长（先前的链式合并把 codesize[c2] 整个漏掉，
  8 符号只产出 2 个码长；已由单测拦截），并先把总频率缩到 1000 以内（Fib 上界保证码长 ≤15），
  彻底避开「限长调整只改计数不改码长」的坏表陷阱。生成表 Kraft 和恰为 65536（满树）。
  - 实测（143MP progressive.jpg）：q50 4.04 MB → **2.90 MB**；优化器端到端 2.95 MB → **2.60 MB（省 12.0%）**，
    从「无收益」变为有效压缩；普通照片 photo100.jpg 同画质 76.1 KB → **68.3 KB（-10%）**。
    代价是编码耗时约 ×2（143MP：246 → 465 ms/次），默认只在智能压缩里开启，
    `Image.Save` 的 JPEG 默认路径不变。
  - **修复：优化表不得含「全 1 末码」。** 首版生成的满树在最深组恰好贴满时末码必为全 1
    （如等频 3 符号 → bits=[1,2] → 末码 11），违反 T.81 K.2「no code is allowed to be all
    ones」，被 GDI+/libjpeg 系看图软件以 "Bogus Huffman table definition" 拒绝（本例 4 张表
    全部违规；自家解码器与 Chromium 未做该检查所以能解，差点漏过）。修复与 libjpeg 的
    freq[256] 同思路：加一个频率为 1 的虚拟叶子独占最深组的最后一个码位（全 1 位），
    组装时挖掉——确定性构造，无需迭代，代价是 Kraft 和略小于满树（合法，前缀码无歧义）。
    新增 `IsLegalPrefixCode`（与 jdhuff.c Figure C.2 逐组检查一致）并暴露给测试；
    新增等频 3~17 符号、真实 Annex K 表回归测试；GDI+ 实测打开通过。
    教训：**跨解码器验证不能只信自家往返一致**——自家人可能「一致地错」。
  - 新增 `JpegHuffmanOptimizeTests`（12 例）。全量 189 单测通过。
- **智能有损压缩（TinyPNG 式），`SharpImageConverter.Compression`。**
  `ImageOptimizer.Optimize(input, output?, options?)` / CLI `--optimize`：保持原格式把
  JPG/PNG/GIF/WebP/BMP 压到「肉眼几乎无差别」的最小体积，压不动就保留原图。
  - **画质判据**：`QualityMetrics` 同时给出「4×4 块平均后的感知 PSNR」与「逐像素 PSNR」。
    前者抹平 dithering 颗粒（照片 256 色抖动：逐像素 36.6 dB vs 感知 48.5 dB），后者兜住
    「高频细节被抹平 / 抖动过头」这类块平均看不见的损伤。默认下限由 `TargetQuality`
    推导：`MinPerceptualPsnr = 33 + 0.14*TargetQuality`（均衡档 88 → 45.3 dB），
    另加 `MinRawPsnr = 30 dB`。阈值为 900×600 合成图实测标定。
  - **PNG**：≤256 色时精确取色（无损）；否则量化到调色板（颜色类型 3，每像素 1 字节）
    + Floyd–Steinberg 抖动，颜色数按画质下限二分搜索（8 档只需 3~4 次评估）。
    体积不随颜色数严格单调，故在达标候选里按体积取最小。不达标或收益 <10% 时回退
    无损真彩色重写——渐变类图像抖动后索引近乎随机，实测调色板会比真彩色大 19 倍。
  - **JPEG / WebP**：二分搜索「刚好达标」的最低质量；JPEG 默认 4:2:0，色度高频丰富导致
    不达标时自动退回 4:4:4 再搜一轮。WebP 搜索下界提到 65（低质量区间省不了多少却明显变糊）。
  - **GIF**：跨帧共享全局调色板并降色，动画同样支持；新增 `GifEncoder.EncodeIndexed`
    直写「调色板 + 索引」，跳过编码器内部二次量化（否则画质不可控）。
    带透明通道的动画 GIF 解码只得到 RGB24 帧序列，重编码会丢透明度，故保留原图并说明原因。
  - **PaletteQuantizer**：精确取色优先；5bit/通道 + alpha 5bit 直方图（大图抽样、只清零用过的桶）；
    中位切分按「像素数 × 最大通道跨度」选盒、按累计像素中位数切开；
    最近色查找用「5bit RGB 粗查表定种子 + 每色 16 近邻短表精修」+ 20bit 键结果缓存，
    避免逐像素扫 256 色。抖动只扩散 RGB（alpha 抖动在半透明区会产生可见噪点）。
  - **配套**：`PngWriter` 新增调色板写入（PLTE/tRNS）与 `PngFilterMode`（Up/Adaptive/None）
    及 Deflate 级别参数；新增 `PngAdaptiveFilter`（逐行 5 选 1，SIMD）。
  - 实测（900×600 合成图）：照片 PNG 845 KB→451 KB（-46.7%）、q100 照片 JPEG 352 KB→76 KB（-78.4%）、
    照片 GIF 380 KB→300 KB（-21.1%）、全色域渐变 PNG 13.6 KB→8.3 KB（-38.8%，无损）、
    6 色图形 PNG 3.3 KB→1.4 KB（-57.4%，无损）。3.84 MP PNG 全流程 1.1 s（含 4 次候选评估）。
  - 新增 `CompressionTests`（16 例），含自适应滤波/调色板 PNG 往返一致性、量化器预算与透明槽、
    度量一致性、三种格式端到端优化。全量 177 单测通过。
- **GIF LZW 输出端改为 64bit bit pack writer。**
  位先累积进 64 位累加器、超过 52 位按组吐出 6~8 个整字节，子块还剩 ≥8 字节时一条
  无对齐 8 字节 store 落包，替代原「每码字逐字节 shift + 写包 + 块界检查」的固定开销。
  归因对照（探针跳过全部位打包、只留码宽状态机）：位打包+写出占 LZW 阶段约 12.7%
  （143 MP：422 → 368 ms）；宽写口把这段自身提速约 21%。
  `examples/progressive.jpg` `--gif-bench 5`，10 轮交错 × 4 臂池化中位：
  | 指标 | 基线 | 64bit writer | 提升 |
  |---|---|---|---|
  | LZW 阶段 | 422.1 ms | **410.6 ms** | −2.7%（9/10 轮胜） |
  | 端到端 total | 467.8 ms | **454.2 ms** | −2.9% |
  | 位打包+写出（归因） | 53.8 ms | **42.3 ms** | **−21%**（性能提升理想） |
  progressive（dithering 开/关）、5_star_base、car 四组产物 md5 逐字节一致，161 单测通过。
- **实测否决：LZW 输入经 64KB 暂存缓冲分块喂入。**
  上界分析：输入是顺序读、已被硬件预取覆盖，暂存只多付一次全量 memcpy；
  实测 LZW 422 → 440 ms（+4.2%，10/10 轮更慢）。结论已写入 `LzwEncoder` 类注释。
  过程教训：消费位必须右移出累加器（基线 `>>=` 语义），用「保留低 r 位」的掩码会把
  尾巴留在 [n*8, n*8+r) 却留下已消费的低位——产物坏在第一次 spill 的末尾字节，
  md5 对比立即拦截（首差出现在 LZW 数据第 6 字节）。
- **实测否决：64KB 批写缓冲（子块 Write 合批）。**
  归因探针（跳过子块 `stream.Write`，`SIC_GIF_NOPKTWRITE`）：子块写出上界仅
  4.75 ms ≈ LZW 阶段的 1.2%（10 轮交错 × `--gif-bench 5` 配对中位，8/10 轮），
  该上界是「调用开销 + memcpy + 扩容」总和，批写只能回收第一项。
  独立微基准（同 23.4 MB 字节量，91725×256B vs 357×64KB，10 轮交错配对）
  实测可回收 **−0.05 ms（胜负 5/10 = 零）**——.NET 小 Write 本就是 memcpy 进内部缓冲，
  每包调用开销已被摊薄；合批反而多付一次全量拼接 memcpy。未实现即否决，
  探针已移除，结论写入 `LzwEncoder` 类注释。
- **JPEG 解码重建（IDCT + YCbCr→RGB）按 MCU 行并行。**
  `TryDecodeInterleavedYCbCrSimd` 的 `my` 行循环改为 `Parallel.For`：每个 MCU 行只写
  `output` 的 `[my*blockH, (my+1)*blockH)` 行区间、互不相交，系数缓冲在熵解码结束后只读，
  量化表共享只读，SIMD 重建内部全是 `static readonly` 常量 ⇒ 线程安全无需同步。
  门控 `60_000 × ProcessorCount + 300_000` 像素（与量化映射同式），小图保持单线程。
  `examples/progressive.jpg`(143 MP) `--jpeg-bench 7` 中位数：
  | 阶段 | 优化前 | 优化后 | 提升 |
  |---|---|---|---|
  | decode total | 440.5 ms | **223~229 ms** | **−49%** |
  | reconstruct | 255.1 ms | **37.5~45.7 ms** | **−82~85%** |
  | entropy（同进程对照组，未改动） | 176.8 ms | 176.8 ms | 0% |
  8 个基线产物（含 143MP 解码/回编码、CMYK/RGB、灰度、q85+444 参数组）**逐字节一致**，152 单测通过。
- **JPEG 编码流水线批大小 `McuBatchSize` 16 → 64。**
  队列容量按「批」计（`clamp(ProcessorCount,2,16)` 批），批=16 时在途 MCU 仅 256 个，
  下游频繁饿等，35K 次入队的信号量唤醒延迟累计可观；批 ≥64 后流水线加深，
  produce-wait 61.8 → ~10ms、huffman-wait 90 → ~21ms。
  同二进制 `SIC_JPEG_BATCH` env 交错 A/B（3 轮，encode total 中位数）：
  批 16 → 292/283/301 ms，批 64 → 204/207 ms，批 128 → 198/203 ms，批 256 → 199 ms
  ——**64 起进入平台（−30%）**，256 因在途工作集变大回落，64 与 128 无显著差异，
  按在途内存（两队列满载 ≈19MB vs ≈38MB）取 64。批大小不影响产物
  （Huffman 按每 MCU 全局 `Sequence` 排序，与分批无关），8 基线产物逐字节一致、152 单测通过。
  小图不受影响：`5_star_base.jpg` 编码 11.5 → 11.4 ms（噪声内）。
- **实测否决：produce（RGB→YCbCr 取样）按行带多生产者并行。**
  `Parallel.ForEach` 多点直接入队：编码 total 303 → 921 ms（dop=∞）/572 ms（dop=4），
  并行度越高越慢、交叉点在单线程。原因：① 多生产者争抢 16 批容量的 sampleQueue 形成波状停顿；
  ② 乱序入队使 Huffman 从 `== expected` 直写快路径退到 pending ring 冷读
  （huffman-busy 218 → 296/397 ms，143MB 系数冷读）。结论已写入 `ProduceRgbSamples` 上方注释；
  若将来重做，正确方向是「并行填充 + 单点按序派发」。
- **`--jpeg-debug` 诊断输出接通控制台。** `JpegEncoder` 的诊断一直写 `Trace.WriteLine`，
  但 CLI 从未注册监听器，输出全部丢失；现在 `--jpeg-debug` 时注册 `ConsoleTraceListener`，
  可看到 `[jpeg-timing] total=… FillMcu420=…`（色彩转换占比）。新增 `--jpeg-bench N`
  分阶段基准与 `SIC_JPEG_STAGE_TIMING=1` 分阶段探针（produce/dct/huffman 及其 wait 槽位、
  解码 entropy/reconstruct/idct-color），测量方法与 `--gif-bench` 对齐。
- **PNG 解码反滤波重构：按滤波器类型分派专用函数，Paeth 用代数化简，并复用已有 SIMD 色彩转换。**
  旧实现把整行先 `CopyTo` 进目标缓冲、再原地改，且逐字节在循环里做 `switch`——
  真实 PNG（libpng/PIL 自适应滤波）里 Paeth 通常占 90% 以上行，逐字节分支同时破坏分支预测与指令缓存。
  新实现：① 一次分派到 `UnfilterSub/Up/Average/Paeth`，从 `src` 直读、写 `dst`，省掉一遍整行拷贝
  （左邻 a 取自 dst、上邻 b 与左上邻 c 取自 prev，三者都不在 src，故语义安全）；
  ② 首 `bpp` 字节单独处理，热循环内不再有 `i >= bpp` 判断；
  ③ Paeth 用代数化简——展开后 `p-a = b-c`、`p-b = a-c`、`p-c = (b-c)+(a-c)`，`p` 本身无需计算，
  且 `pa`/`pb` 可并行求值，依赖链缩短一层。另注：首 bpp 字节的 a、c 均为 0，
  此时 Paeth **退化为 Up**，直接按 Up 处理。
  ④ `UnfilterRgba8ToRgbDirect` / `UnfilterRgb8ToRgbaDirect` 里手写的逐像素标量搬运
  改为复用 `SimdHelper.PackRgbaToRgb` / `ExpandRgbToRgba`（SSSE3 `pshufb`）。
  同一份二进制内用 `SIC_PNG_LEGACY_UNFILTER=1` env 探针交错 A/B，7 轮取中位数：
  | 输入 | 优化前 | 优化后 | 提升 |
  |---|---|---|---|
  | 4000×3000 RGBA（Paeth 占 97% 行） | 194.65 ms | 161.14 ms | **−17.2%** |
  | 2000×1500 RGB | 70.52 ms | 45.40 ms | **−35.6%** |
  | 640×480 RGB | 9.31 ms | 6.31 ms | **−32.2%** |
  | `Amish-Noka-Dresser.png`（几乎无滤波行） | 3.94 ms | 3.88 ms | −1.5% |
  最后一行是预期内的零收益：该图滤波器几乎全为 None，反滤波本就不做事。
  正确性：19 张语料（含 Adam7 隔行、调色板、灰度+Alpha、16 位灰度、1×1/5×3/37×11 奇宽）
  RGB 与 RGBA 解码产物**逐字节一致**，并与 Pillow 逐一对齐；152 个单元测试通过。
- **LZW 编码器字典查找改为两级（过滤缓存 + 散列表），并把槽位从 64 位压到 32 位。**
  编码循环是「算散列 → 载入槽位 → 比较 → 更新 `ent`」的串行依赖链，每像素一次，既不能向量化
  也不能软件流水；二级表 256 KB 必然落在 L2 上，那次加载的十余周期延迟就是整条链的主体。
  新增的一级缓存只有 128 KB、按下标 `fcode & (CSIZE-1)` 直接映射（低 12 位即 `ent`，
  等价于每个前缀 8 个槽），局部性远好于散列表，实测 **87.5%** 的查找在此命中。
  三个已实测的关键点（详见 `LzwEncoder` 类注释）：① 两级加载必须**并行发射**——
  若写成「先判缓存、未命中再查大表」，未命中要串行付两次加载延迟，实测比不用缓存还慢 4 ms；
  ② 缓存下标**不能依赖散列值**——用主表 `h` 当下标时噪声图命中率 53%→61%，但下标要等 imul
  算完才能发出，progressive.jpg 425 → 686 ms；③ 缓存**必须参与插入**，只在命中时回填会更慢。
  命中判定改为取差值 `d = slot - (fcode << 12)`：命中时 `d` 恰为字典编号，一次减法同时完成
  「比较键」与「取编号」，并用 `d - 1u < 4095u` 把空槽（`d == 0`）与键不同的情形一起排除，
  从而不必额外判断空槽就能区分「空槽」与「键为 0 的合法项」。
  （早期版本直接比较 `(slot >> 12) == fcode`，空槽 0 会被误判成 `(c=0, ent=0)` 的命中，
  少输出一个码字且少插一条——表面快 5%，实际产物 md5 已不一致，属**假阳性收益**。）
  `examples/progressive.jpg`(10650×13426, 143 MP) `--gif-bench 5` 中位数：
  | 阶段 | 优化前 | 32 位槽位 | +一级缓存 |
  |---|---|---|---|
  | LZW | 564 ms | 537 ms（−5%） | **420 ms（−25%）** |
  | 编码 total | 622 ms | — | **483 ms（−22%）** |
  缓存槽位 2^12/2^13/2^14/**2^15**/2^16/2^17 → 544/501/458/**430**/429/443 ms（峰值 128 KB）；
  二级表在有缓存后仍取 2^16（2^14/2^15/2^16 → 436/418/414 ms）。
  5 张样例图 × 多组参数（默认/wu 量化器/关抖动/灰度/缩放/2×2 极小图）产物**逐字节一致**。
  **已知代价**：收益取决于缓存命中率，低于约 60% 的图会略微变慢——
  `examples/5_star_base.jpg`（1.2 MP，噪声多，重置密度是 progressive 的 3.3 倍，命中率仅 53%）
  LZW 6.4 → 7.2 ms（+12%）；同目录 `car.png`（81.6%）为 −18%。消融实验确认与清零缓存无关（占 1.9%）。
- 新增八叉树量化 + Bayer 有序抖动（`OctreeQuantizer`），作为 GIF 编码默认量化器；
  原 Wu 量化 + Floyd–Steinberg 误差扩散（`Quantizer`）保留，可通过 `GifEncoder.QuantizerKind`
  / 适配器 `QuantizerKind` / CLI `--gif-quantizer wu` 切回。新方案用 5-bit 直方图喂入深度 5
  八叉树、归约到 ≤256 叶、映射走 5-bit LUT，抖动改用无依赖链的 Bayer 4×4（替代误差扩散串行链），
  量化阶段显著更快；输出**不**与原实现保持 MD5 一致（设计使然，见项目约定）。`DitherStrength` 可调。
- **修正 Bayer 抖动默认值：48 → 8（一个量化步长）。** 原默认值把抖动幅度设成了量化步长的约 5.6 倍
  （偏移 `[-23, +22]`，而 5-bit LUT 的步长为 8），属严重过抖动。实测（平滑渐变 512×512 +
  `examples/Amish-Noka-Dresser.jpg`）：
  | 指标 | S=0(不抖) | **S=8(新默认)** | S=48(旧) | Wu+FS |
  |---|---|---|---|---|
  | 照片总误差 RMS | 1.30 | **2.03** | 12.85 | 1.96 |
  | 照片颗粒 RMS | 0.91 | **1.73** | 11.56 | 1.79 |
  | 照片 1/4 缩放走样 RMS | 10.99 | **11.31** | 22.99 | 11.01 |
  | 渐变最长同值游程(px) | 64 | **3** | 1 | 15 |
  即 S=48 相对 S=8 **颗粒大 6.7 倍、缩小时最近邻走样大 2 倍，而色带抑制没有任何额外收益**
  （块间跳变同为 4.12、最长同值游程已降到 3）。过强的抖动还把 4×4 网格变成强周期图案，
  在看图软件用最近邻采样缩放时被走样成摩尔纹（Wu+Floyd–Steinberg 无周期结构故不受影响）。
  附带收益：索引更规整，143 MP 图编码 total 中位 746.0 → 684.3 ms（**−8.3%**）。
  新增 CLI `--gif-dither N` 与 `GifEncoder.DitherStrength` / 适配器 `DitherStrength` 可调。
- `OctreeQuantizer` 的 Bayer 映射阶段整数化 + SSSE3 向量化。原实现逐像素做 3 次
  「byte→float 转换 + 浮点加 + `Math.Clamp`(两次比较分支) + float→int 截断」，实测占整个量化 78%。
  改写分两步：① 因 v 为整数时 `floor(v+x) = v + floor(x)`，可把 `t*strength` 精确化为整数偏移
  `floor(strength*(2k-15)/32)`（strength=48 时即 `3k-23`），再把「加偏移 + clamp + >>3」
  预计算成 16 相位 × 256 项的 5-bit 表，逐像素只剩 3 次查表；② 进一步用 SSSE3 一次处理 16 像素：
  `pshufb` 三路反交错出 R/G/B 平面，16 位通道内 `paddw + pminsw/pmaxsw` 饱和 + `psrlw 3`，
  合成 16 个 cube 下标。cube→调色板索引的 32768 字节 gather 无法向量化（AVX2 无按字节、
  15 位下标的 gather，dword gather 反而更慢），故保留标量。
  `examples/progressive.jpg`(10650×13426) `--gif-bench 11` 中位数：quantize 553.1 → 215.0（整数化）
  → 115.9 ms（SIMD），编码 total 1164.1 → 719.1 ms；相对原 Wu 量化器 quantize 快 11.4x。
  5 张样例图产物与优化前**逐字节一致**（md5 相同）。
  注：与 `docs/PerfReport.md` 中「量化不可 SIMD」的结论不同——该结论针对 Floyd–Steinberg 的
  **串行误差扩散链**（依赖链受限）；Bayer 逐像素独立、无跨像素依赖，属吞吐受限，故 SIMD 有效。
- `OctreeQuantizer` 映射阶段（cube → 调色板索引）并行化 + 不抖动路径补 SSSE3。
  映射逐像素独立、无跨像素依赖链，此前却一直是串行单线程：143 MP 图上它独占 quantize 的
  ~85 ms，占编码 total 的近 9%。现按 `60_000 × ProcessorCount + 300_000` 像素门控（与直方图同尺度）
  用 `Parallel.For` 按行切片，小图仍走单线程。同时把原先只有抖动路径才有的 SSSE3 版
  移植给不抖动路径（`MapDirectSimd`，去掉偏移表与 pminsw/pmaxsw 即可），
  修掉了「开抖动反而比不开更快」的怪现象（24 核 145.7 vs 120.5 ms）。
  `examples/progressive.jpg` `--gif-bench 11` 中位数（24 核，同二进制 env A/B）：
  抖动开 quantize 127.0 → **42.7 ms**（−66.4%）、total 997.8 → **903.7 ms**（**−9.4%**）；
  抖动关 quantize 158.2 → **42.5 ms**（−73.1%）、total 930.6 → **822.3 ms**（**−11.6%**）。
  LZW 耗时不变（871.7 → 867.9 ms，噪声内），端到端增益全部来自量化阶段。
  5 张样例图 × 抖动开/关共 10 组产物与改动前**逐字节一致**；2 MP 图并行/串行持平，无退化。
- GIF 量化器直方图按图片尺寸选择单/多线程：原实现无条件 `Parallel.For`，每个 worker 都要从 `ArrayPool`
  租借并清零 5 个 35,937 元直方图（约 1.44 MB/线程），最后在 `lock` 内把全部 35,937 个 bin 串行合并回主直方图。
  这个固定成本与图片大小无关，小图上远大于并行省下的扫描时间。现按
  `60_000 × ProcessorCount + 300_000` 像素为界：低于阈值单线程直接累加进主直方图，省掉租借/清零与合并。
  阈值随核数缩放（开销随线程数增长）：实测交叉点 4 核约 0.5 MP、8 核约 1.05 MP、24 核约 1.6 MP。
  24 核 2400×1800→各尺寸 `--gif-bench` 中位数（抖动开）：512×512 10.010→5.991 ms（**−40.1%**）、
  800×600 12.659→9.274（**−26.7%**）、1024×1024 20.208→18.060（**−10.6%**）、1280×720 −8.9%、
  1440×900 −3.3%，≥1.9 MP 回落到并行路径（−1% 以内，属噪声）。抖动关闭时收益更大（小图 −44%~−50%）。
  8 个尺寸 × 抖动开/关共 16 组产物与优化前**逐字节一致**。
- LZW 编码器字典查找重构：原哈希 `h = (c << 4) ^ ent` 因 `c <= 255`、`ent < 4096` 恒落在 `[0, 4095]`，
  8191 个槽位实际只用一半，装载因子为 1.0，探测链严重退化。改为 64 位乘法散列（Fibonacci hashing）
  配 32768 槽位（装载因子约 0.12）+ 线性探测；槽位打包为 `((fcode + 1) << 12) | code`，
  键与值落在同一条 cache line，一次探测一次加载。槽位 0 表示空，键存 `fcode + 1` 避免
  `(c=0, ent=0)` 与空标记冲突。表经 `ArrayPool` 租用，避免动画逐帧触发大对象 GC。
  2400×1800 实测编码 LZW 28.998ms → 21.245ms（1.37x），输出字节与优化前完全一致。
- ~~LZW 解码器改为直写输出~~ **已回退**：该改动实测解码 LZW 12.875ms → 12.025ms（仅 1.07x），
  性能提升不理想，不保留，解码器已还原至优化前实现。
  实验结论（含同轮实测否决的三个改动：一次补 4 字节的位缓冲 -12%、
  prefix/suffix 打包为单个 int -5%、二级跳转表一次展开两像素 -5%）
  已沉淀至 `docs/PerfReport.md` 的「GIF LZW 优化实验记录」，避免重复尝试。
- 新增 GIF 编解码耗时统计：`GifTiming` 按阶段记录 quantize / prepare / header / lzw / render / background
  与未归入阶段的其余时间，并给出占比与吞吐（Mpx/s）。`GifEncoder` / `GifDecoder` 及其适配器新增
  `EnableDiagnostics`、`DiagnosticsLog` 与 `LastTiming`：默认关闭，关闭时热路径不调用 Stopwatch（零开销），
  开启后报表经 `DiagnosticsLog` 输出，未设置时回落到 `Trace`。
  CLI 新增 `--gif-debug`（单次转换的分阶段耗时）与 `--gif-bench N`（重复 N 次取最小/中位/平均，只测量不产出文件）。
- `SimdHelper.AddBytesInPlace` 改为真正的 SIMD：SSE2/AdvSimd 下用 128 位整字节加法（`paddb`，天然 mod 256 回绕），移除原先 Widen/Narrow 的迂回实现。
- 新增 `SimdHelper.GrayscaleRgb24InPlace`：SSSE3 `pshufb` 三路反交错 + 16 位定点加权，每批 16 像素，`Processing.Grayscale()` 已接入。
- 新增 `SimdHelper.ExpandGrayToRgb` / `PackRgbaToRgb` / `ExpandRgbToRgba`，替换 `Configuration` 中的逐像素格式互转，并用 `GC.AllocateUninitializedArray` 避免多余清零。
- `Processing.ResizeBicubicOptimized` 移除 `Vector<float>` 伪 SIMD 脚手架（声明了向量缓冲但内层全为标量），重写为干净的标量实现，并复用行基址减少重复乘法。
- Resize 系列的目标缓冲区改为 `GC.AllocateUninitializedArray`，与池化的索引/权重数组配合降低分配开销。
- JPEG 量化表与整数 DCT reciprocal 表按 quality 缓存（质量已归一化到 `[1,100]`），避免每次编码重建。
- 清理 `JpegEncoder` 中 `// float FDCT removed` 残留注释，改为说明为何只用整数 FDCT；移除 `SimdHelper` 中无主代码调用的 `GetVectorPaddedByteLength` / `AllocateAligned<T>` 泛型重载。
- `Processing.ResizeBilinear` 新增 SSSE3+SSE4.1 路径：一次 8 字节载入同时取到 x0 与 x1 两个像素，
  `pshufb` 拼出 `[a0,b0,a1,b1,...]` 交错 short，`pmaddwd` 用 q/r 拆分精确还原 11 位权重，
  `pmulld` 做垂直插值，每批 4 个像素。数值与标量路径逐位一致。
- `Processing.ResizeArea` 把只依赖 dx / dy 的区间与重叠权重提到循环外预计算，内层只剩乘加累加。
- `ImageFrame.ApplyExifOrientation` 把方向分支提到双层循环外；case 4 改为整行块拷贝；
  case 5-8（转置类）改为 32×32 分块转置，避免目标端按整行跨度写入导致的缓存抖动。
- `ImageFrame.ApplyExifOrientation` 与 `Processing.Clone` 的目标缓冲改用 `GC.AllocateUninitializedArray`。
- `JpegEncoder` 中 `new Vector<int>(span)` 改为 `Vector.LoadUnsafe`，去掉中间拷贝。
- `PngWriter.ApplyUpFilterSimd` 同样改为 `Vector.LoadUnsafe` / `StoreUnsafe`，去掉每步 `Slice(i)` 的
  重复边界检查。滤波函数本身提速 1.36-1.85x，但对 PNG 编码端到端只有 ~0.1%（滤波仅占编码耗时 0.3%）。
- 修正 `Processing.ResizeBilinear` 的 XML 文档注释：此前它被挤到了 SIMD 掩码字段上，导致 warning CS1572。
- `Processing.ResizeBicubicOptimized` 新增 SSSE3+SSE4.1 路径：把 4 像素×4 抽头的 16 次取样压成一次 4 字节载入
  拿到 R/G/B 三条 lane，水平与垂直各做 4 次 `mulps`+`addps`。结合顺序、
  夹取与取整方式都对齐标量实现，输出逐位一致。`examples/progressive.jpg`（10650×13426）
  上实测 1.5x 放大 176.5ms → 49.0ms（3.6x）、2x 放大 326.7ms → 91.1ms（3.6x）。
  SIMD 覆盖不到两个边界情况，均回退标量：抽头夹到最后一个源像素（4 字节载入越界）、
  每行最后一个输出像素（只能按 3 字节写）。
- `Processing.ResizeBilinear` 的 SIMD 循环从每批 4 像素扩到 8 像素：两批之间没有数据依赖，
  两条依赖链可以并行发射。同样条件下实测 2x 放大 133.6ms → 125.4ms（1.07x）、
  4x 放大 537.5ms → 496.9ms（1.08x）；缩小方向受限于写带宽，基本持平（±2%）。
- 把 `ResizeBilinear` 的定点常数（Shift / Scale / RoundingOffset）提到类级别，
  以便抽出的 `BilinearCore4` 辅助方法复用。

### 工具
- 新增 `tools/gif-compare.sh` 与 `tools/gif-timing-master.patch`：把 GIF 计时代码移植到
  基准分支（默认 master）的临时 git 工作树上，让两个分支跑同一套 `--gif-bench` 口径，
  用于量化优化前后的差异。补丁针对 master @ `0bbf1f9`，若该分支的 `GifDecoder` 再有改动需重新移植。

### 文档
- 工程类文档统一收拢到 `docs/`：`PerfReport.md`、`AuditVerification.md`、`goal.md` 移入，
  JPEG 规范原文移入 `docs/reference/`，并新增 `docs/README.md` 作为索引。
  `README.md` / `README.en.md` / `CHANGELOG.md` / `THIRD-PARTY-NOTICES.md` 按生态约定保留在根目录，
  `.trae/rules/project.md` 因 IDE 按固定路径读取而保持原位。
- `goal.md` 中的引用由失效的绝对路径（`file:///d:/...`，盘符本身已错）改为仓库内相对路径。
- 删除根目录误留的 `gcm-diagnose.log`（Git Credential Manager 诊断输出，未被 git 跟踪，
  内容仅含环境变量名与路径，无凭据值）。

### 测试
- 新增 `SharpImageConverter.Tests/SimdPixelOpsTests.cs`，覆盖新增 SIMD 路径与标量实现的逐字节一致性、0..70 长度边界、mod 256 回绕语义，并显式断言本机 SSSE3 可用以免 SIMD 分支漏测。
- 新增 `SharpImageConverter.Tests/ResizeConsistencyTests.cs`，用改动前的原样算法做参考实现，逐位校验
  ResizeBilinear（13 组尺寸 + 4900 组小尺寸穷举）与 ResizeArea。
- 新增 `SharpImageConverter.Tests/ExifOrientationTests.cs`，对 8 个方向在多种尺寸（含跨分块边界）下逐像素校验。
- `ResizeConsistencyTests` 补上此前缺失的双三次覆盖：新增 `RefBicubic` 参考实现（改动前的原样标量算法）、
  13 组尺寸的 `[Theory]` 用例，以及 8×7×8×7 的小尺寸穷举扫描——用于逼出 SIMD 的两个回退边界。
  把 `ResizeBicubicOptimized` 的截断转换改回最近取整即可看到 11 个用例失败，说明确实覆盖了 SIMD 分支。

## 0.2.2（相对 v0.2.1）
### 改进
- JPEG 解码流程重构，统一使用 ImageFrame.LoadJpeg，减少分叉路径与维护成本。
- JPEG IDCT 内核拆分为两阶段，降低运行时计算开销。
- PNG 解码的像素转换使用不安全指针路径优化性能。
- PNG 写入的 Up 过滤器 SIMD 优化，减少内存分配。
- 图像解码与处理整体路径减少内存分配与拷贝。
- JpegImage 移除未使用的缓存字段，清理冗余状态。

### 修复
- JPEG 图像边界处理修复，避免潜在越界访问。
- CLI JPEG 解码修复元数据丢失与方向处理问题。
- BMP 格式检测与读写流程修复并增强健壮性。

## 0.2.1（相对 v0.2.0）
- 版本号：`src/SharpImageConverter.csproj` 从 `0.2.0` 升级为 `0.2.1`。
- 变更范围：共 6 个文件，约 `+397 / -334`（`git diff v0.2.0..HEAD --stat`）。
- 主要方向：JPEG 编解码路径继续收紧内存分配策略（ArrayPool/MemoryPool），并修复测试侧对静态解码 API 的调用方式。

### 代码改动摘要
- `src/Formats/Jpeg/JpegEncoder.cs`
  - 位流写缓冲改为池化租借/归还，减少每次编码时的临时分配。
  - 有序 Huffman 阶段的 pending 容器改为池化扩容与回收，降低 GC 压力。
  - ICC APP2 分片写入改为基于 `MemoryPool<byte>` 的复用缓冲写出。
  - Huffman 表改为静态复用，并修正静态初始化顺序问题。
- `src/Formats/Jpeg/JpegDecoder.cs`
  - `Decode(Stream)` 路径改为池化读取，去掉 `MemoryStream + ToArray` 方式。
  - ICC 收集器改为池化 chunk 管理，并在同步/异步解析完成后统一释放。
- `SharpImageConverter.Tests/*.cs`
  - 测试代码切换到静态 `JpegDecoder` API（`Decode` / `DecodeFromStreamAsync`）。
  - 兼容 `JpegImage` 当前接口，移除不可用成员调用。

## 0.2.0
- 相比 0.1.6，本版本对 JPEG/PNG/GIF/BMP/WebP 全链路进行了较大规模重构与优化，总体聚焦性能、稳定性与流式处理能力。
- JPEG：引入 SIMD 优化的 IDCT 与颜色转换、MCU 批处理与流水线并行编码；新增 APP2 ICC 配置文件支持与可选 CMYK 转换模式；修复多个解码验证问题与资源泄漏问题。
- PNG：解码器采用数组池、定点计算与对齐内存缓冲优化内存与性能，减少不必要颜色转换，并提升网络流读取健壮性。
- GIF：量化器升级为基于直方图的 K-means 实现并改进 LZW 编码，支持抖动开关；补充动画编码能力、并发控制与元数据支持。
- WebP：修复非可查找流（non-seekable stream）解码问题，提升在网络流场景下的兼容性。
- Core/BMP/Processing：新增 SIMD 通用辅助能力，优化 BMP 读写与双线性缩放性能，补充尺寸检查并降低部分路径的内存分配。
- 测试与示例：新增格式检测、元数据、并行处理等测试覆盖，并更新示例脚本与发布流程相关配置。

## 0.1.7
- 修复webp解码错误
## 0.1.6.2-preview
- 优化GIF/PNG的网络流处理能力和图片缩放方法中的内存分配方式
## 0.1.6.1-preview
- 优化缩放处理方法
## 0.1.6
- 重点优化了GIF编码的性能和JPEG编码的性能
## 0.1.5
- JPEG解码添加了CMYK和YCCK的颜色支持 (Added CMYK and YCCK color support in JPEG decoder)
## 0.1.5-preview
- 将各格式的实现，放入对应的子命名空间中 (Moved each format implementation into its own sub-namespace)
- Jpeg解码添加了Stream支持 (Added stream support to JPEG decoder)
## 0.1.4
- 恢复JpegEncoder到重构之前的版本 (Reverted JpegEncoder to the pre-refactor implementation)
- 完善灰度格式之间转换的逻辑 (Improved conversion logic between grayscale formats)

## 0.1.4.1-preview
- 添加对灰度 PNG 和灰度 BMP 格式的支持 (Added support for grayscale PNG and grayscale BMP)

## 0.1.3
- 修复JpegDecoder 解码 /examples/Amish-Noka-Dresser.jpg 错误的问题 (Fixed JpegDecoder decoding error for /examples/Amish-Noka-Dresser.jpg)
- PNG：在多数图片上显著降低压缩后体积（例如原先约 110 MB 的 PNG，现在可缩小到约 30 MB，具体效果取决于图像内容），同时略微提升压缩速度。 (PNG: Significantly reduced output size for many images (e.g., ~110 MB down to ~30 MB, depending on content) with slightly faster compression)
- JPEG：大幅提升解码速度，并在编码路径上带来小幅性能提升。 (JPEG: Greatly improved decode performance and modestly improved encode performance)
- BMP：写出路径经过优化，在同一环境下写出约 400 MB 的 BMP 文件，耗时从约 400 ms 降低到约 270 ms。 (BMP: Optimized write path; writing a ~400 MB BMP now takes ~270 ms instead of ~400 ms on the same machine)
