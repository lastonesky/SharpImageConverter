# 验收清单（WebP 第二轮）

## 通用

- [ ] 公开 API 未变化；JPEG 与其他格式无改动。
- [ ] 每次构建无新增警告（Release 下分析器警告视为错误）。
- [ ] `dotnet test --filter "FullyQualifiedName~Webp"` 全绿。
- [ ] 全量测试通过（既有 1 项 `IdctGoldenTests.TempProbeFidelityBlocks` 跳过除外）。
- [ ] 未创建 git 提交。

## 任务 2

- [ ] 系数 trellis 为真 DP（含 EOB/零游程节点成本），不是逐系数贪心。
- [ ] 24 组 A/B 数据齐全（体积 + PSNR，libwebp 解码测量）。
- [ ] 总体系积 ≥ −8%，平均 PSNR 下降 ≤0.2 dB，单组劣化 ≤ +2%。
- [ ] `rgba_panel` q75/q90 相对 libwebp ≤1.25×，q50 ≤1.35×。
- [ ] 4 段量化 + 按段 lf delta 写入合法帧头；libwebp 可解且逐像素 ±1。
- [ ] 滤波 level 由实测滤波后 SSE 选择，第一遍不滤波、第二遍一致。

## 任务 3

- [ ] Huffman 组划分基于首遍实际码长，而非估计的 `TableBits`。
- [ ] 无损产物 libwebp 可解且与源 RGBA bit-exact。
- [ ] 三类平均 ≥ −2%，单图劣化 ≤ +0.5%，数据齐全。

## 任务 4

- [ ] `WebpRowSource` 不再调用整帧 `DecodeRgba`。
- [ ] VP8L（含 ALPH）逐行输出与整图解码逐像素一致。
- [ ] 短读/截断/并发/动图拒绝行为保持。
- [ ] 长图驻留峰值下降 ≥80%，记录实际字节与测量方法。
- [ ] 4B（VP8）要么逐像素一致并达标，要么完全回退并在报告说明，无半截实现。

## 文档/报告

- [ ] `docs/WebpEncoder.md`、`docs/PerfReport.md`、`CHANGELOG.md` 与实测一致。
- [ ] 最终报告含：成果、量化数据、文件+行范围、验证结果、未达标/风险。
