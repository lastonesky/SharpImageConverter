# 验收清单（WebP 第二轮）

## 通用

- [x] 公开 API 未变化；JPEG 与其他格式无改动。
- [x] 每次构建无新增警告（Release 下分析器警告视为错误）。0 警告 / 0 错误。
- [x] `dotnet test --filter "FullyQualifiedName~Webp"` 全绿。300 项通过。
- [x] 全量测试通过（既有 1 项 `IdctGoldenTests.TempProbeFidelityBlocks` 跳过除外）。
      737 项：736 通过、1 跳过、0 失败。
- [x] 未创建 git 提交。

## 任务 2

- [x] 系数 trellis 为真 DP（含 EOB/零游程节点成本），不是逐系数贪心。
      51 个 `(位置, 非零上下文)` 状态，块内回溯取最优。
- [x] 24 组 A/B 数据齐全（体积 + PSNR，libwebp 解码测量）。
- [x] 总体系积 ≥ −8%，平均 PSNR 下降 ≤0.2 dB，单组劣化 ≤ +2%。
      实测 −9.37%、+0.070 dB、最差单组 0.984×（无增长）。
- [x] `rgba_panel` q75/q90 相对 libwebp ≤1.25×，q50 ≤1.35×。
      实测 q50 0.868 / q75 0.890 / q90 1.025 / q95 0.734。
- [x] 4 段量化 + 按段 lf delta 写入合法帧头；libwebp 可解且逐像素 ±1。
- [x] 滤波 level 由实测滤波后 SSE 选择，第一遍不滤波、第二遍一致。

## 任务 3

- [x] Huffman 组划分基于首遍实际码长，而非估计的 `TableBits`。
      组数与 token 集合均由 `PassBits` / `ExactCost` 的真实位数评估。
- [x] 无损产物 libwebp 可解且与源 RGBA bit-exact。maxdiff = 0。
- [ ] 三类平均 ≥ −2%，单图劣化 ≤ +0.5%，数据齐全。
      **未达标**：a_text −0.17%、rgba_softgrad −0.62%，其余 0%；无劣化。
      原因与后续方向见 `docs/PerfReport.md` §6.7。

## 任务 4

- [x] `WebpRowSource` 对 VP8L 不再调用整帧 `DecodeRgba`；VP8 有损分支也已行增量
      （`Vp8RowDecoder` + `AlphRowSource`，见任务 4B）。
- [x] VP8L（含 ALPH）逐行输出与整图解码逐像素一致。
- [x] 短读/截断/并发/动图拒绝行为保持。
- [x] 长图驻留峰值下降 ≥80%，记录实际字节与测量方法。
      4096×4096：67,373,976 B → 4,603,312 B（6.8%，下降 93.2%）。
- [x] 4B（VP8）逐像素一致并达标，无半截实现；实测数据见任务 4B 与 `PerfReport.md` §6.7。

## 任务 4B（VP8 行级流式，已实施）

> 设计：`docs/specs/webp-iteration2/vp8-row-streaming.md`；实现按 tasks.md 阶段 8.1–8.6。
> 全部阈值达标，未回退整帧路径。

- [x] 设计方案完成且决策完整：状态机边界、17/9 行重建窗口与 32/16 行输出窗口的理由、
      滤波延迟模型、行对 RGB 映射、内存公式与数值、接口/异常/并发、阈值与回退。
- [x] 逐像素对拍：全矩阵（1×1、奇数/非 16 倍数宽高、1×N/N×1、1080p、4K、q10/30/50/75/95、
      simple/complex、alpha on/off）与整帧路径 RGBA diff = 0。
      `Vp8RowDecoder_QualityMatrix_*`、`Vp8RowDecoder_FilterAndPartitionMatrix_*`、
      `Vp8RowDecoder_LargeImages_*`、`Vp8RowDecoder_ExtremeThinImages_*`。
- [x] loop filter 专项：mbW=1 / mbH=1、高 filter_level 边缘强梯度、末宏块行裁剪、
      行对边界（`16k-1` 与 `16k` 同批转换）全部 diff = 0。
      `Vp8RowDecoder_StrongEdgeGradient_*`（filter_strength=63、sharpness 0/4/7）。
- [x] 内存：4096² 无 alpha 峰值增量 ≤ 整帧 5%（硬上限 20%）；16383² 平面窗口 ≤2 MB；
      长图相对现状下降 ≥80%；记录测量方法与字节数。
      4096²：1.6%；16383×1 窗口 1,382,653 B；16383×64 下降 83.1%、64×16383 下降 98.6%。
- [x] alpha：P3 行增量后 4K alpha 额外内存 ≤4.5 MB，alpha 各 filter 模式 diff = 0，
      A 通道仅覆盖、不预乘。4K：4,309,168 B。
- [x] 分配/性能：稳态 `ReadRow` 每图分配 ≤32 KB（1080p 实测 0 B）；
      1080p/4K q75 吞吐 ≥现状 0.8×（1.00× / 0.92×）。
- [x] 兼容：公开 API 不变；VP8L 行流式行为不变；动图仍拒绝；
      短读/EOF/截断错误仍为 `InvalidDataException`，已产出行保持有效。
- [x] 任一项不通过按 §9.5 回退，不保留半截实现，并在报告写明：本轮全部达标，无回退。

## 文档/报告

- [x] `docs/WebpEncoder.md`、`docs/PerfReport.md`、`CHANGELOG.md` 与实测一致。
- [x] 最终报告含：成果、量化数据、文件+行范围、验证结果、未达标/风险。
