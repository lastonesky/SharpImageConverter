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

- [x] `WebpRowSource` 对 VP8L 不再调用整帧 `DecodeRgba`（VP8 回退路径保留，
      对应未实施的 4B）。
- [x] VP8L（含 ALPH）逐行输出与整图解码逐像素一致。
- [x] 短读/截断/并发/动图拒绝行为保持。
- [x] 长图驻留峰值下降 ≥80%，记录实际字节与测量方法。
      4096×4096：67,373,976 B → 4,603,312 B（6.8%，下降 93.2%）。
- [x] 4B（VP8）要么逐像素一致并达标，要么完全回退并在报告说明，无半截实现。
      本轮不实施，未改动 VP8 解码路径；取舍与分阶段方案已写入文档与报告。

## 文档/报告

- [x] `docs/WebpEncoder.md`、`docs/PerfReport.md`、`CHANGELOG.md` 与实测一致。
- [x] 最终报告含：成果、量化数据、文件+行范围、验证结果、未达标/风险。
