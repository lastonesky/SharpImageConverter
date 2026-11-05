# 任务拆分与执行顺序（WebP 第二轮）

> 严格串行：每个阶段结束先构建 + 相关测试，通过后才进入下一阶段。
> 不并发执行 `dotnet` 构建；不做 git 提交。

## 阶段 0：基线快照与测量台

- [x] 0.1 构建当前 `src/SharpImageConverter.csproj`（Release），把 DLL 快照到
      `.perf/baseline/SharpImageConverter.dll`。
- [x] 0.2 新建 `.perf/QualityProbe`（net10 console，引用 `src` 工程，复制
      `src/runtimes/win-x64/*.dll`），实现 2.1 六类语料生成、libwebp 同 q 编码、
      PSNR（libwebp 解码）、A/B（`AssemblyLoadContext` 基线 + 候选）、CSV 输出。
- [x] 0.3 跑基线，记录 24 组 `baseline_bytes / libwebp_bytes / PSNR` 与
      `20000×64`、`64×20000` 的流式驻留峰值（现状）。基线 24 组合计
      1,176,518 B（`.perf/quality.csv`）；流式驻留用 4096×4096 对照整帧
      67,373,976 B。
- 通过条件：CSV 完整、SHA 固定、基线 PSNR 与文档 §2 同量级。

## 阶段 1：任务 2A —— 系数 trellis

- [x] 1.1 新建 `Vp8Trellis.cs`：成本表、精确 DP、候选电平、失真权重、λ 参数。
- [x] 1.2 接入 `PrepareLumaResidual` / `PrepareChromaResidual` / `PrepareI4Block`；
      输出同时写量化电平与反量化槽位。
- [x] 1.3 构建 + WebP 过滤测试（编码→libwebp/托管双解码逐像素 ±1）。
- [x] 1.4 `QualityProbe` 扫 `TrellisLambda ∈ {8,16,24,32,48}`，选定值并记录数据。
- 通过条件：位流合法、双解码一致；24 组总体积不劣于基线。
- 若劣化：回退 1.2 的接入，保留 1.1 与测量数据，报告中说明。

## 阶段 2：任务 2B —— 分段量化重做

- [x] 2.1 实现 `ClassifyMacroblock`（activity/variance + 分位阈值），4 段 quantizer。
- [x] 2.2 帧头写 4 段 delta、segment map 概率；`WriteMacroblockModes` 写实际段。
- [x] 2.3 构建 + WebP 测试；`QualityProbe` 对比 `rgba_panel` 等 24 组。
- 通过条件：`rgba_panel` 相对基线体积下降；整体不劣化 > 1%。
- 若无效：保留"关闭分段"的默认配置，回退该子项。

## 阶段 3：任务 2C —— 滤波强度 RD + 按段 delta

- [x] 3.1 `AutoSelectFilterLevel`（候选 level 实测滤波后加权 SSE）。
- [x] 3.2 按段 lf delta 写入帧头；第二遍使用选定 level。
- [x] 3.3 构建 + WebP 测试；24 组数据（体积/PSNR）。
- 通过条件：平均 PSNR 提升或持平，体积不劣化；帧头可被 libwebp 解。
- [x] 3.4 更新 `docs/WebpEncoder.md` §2/§3/§4 与 CHANGELOG；跑全量测试。

## 阶段 4：任务 3 —— VP8L 多 pass

- [x] 4.1 增加实际码长成本遍历（组表 + token），替换 `TableBits` 估计。
- [x] 4.2 组数 {1,2,4,8,16} 按实际总位数选择；选定后二遍重建 Huffman。
- [x] 4.3 构建 + 无损 bit-exact / libwebp 交叉测试；`QualityProbe` 记录无损体积。
- 通过条件：三类平均 ≥ −2%，单图劣化 ≤ +0.5%，bit-exact。
      **实测未达**：a_text −0.17%、rgba_softgrad −0.62%，其余不变；bit-exact 通过。
- [x] 4.4 更新文档；跑全量测试。

## 阶段 5：任务 4A —— VP8L/ALPH 行增量

- [x] 5.1 拆分 `Vp8LDecoder`：头/表初始化 + `DecodeNextRow` 状态机。
- [x] 5.2 新建 `Vp8LRowDecoder`（4 MB 环形历史 + 上一行 predictor + 逐像素逆变换）。
- [x] 5.3 改 `WebpRowSource`：流式 RIFF、VP8L/ALPH 行源、逐行合并输出。
- [x] 5.4 流式测试：无损/含 alpha/短读/截断/并发；新增长图内存测试。
- 通过条件：逐像素一致；长图峰值驻留 ≤ 现状 20%。
- 若 5.1–5.3 未达一致：回退到原整帧路径，报告说明。

## 阶段 6：任务 4B —— VP8 行增量（可选，架构级）

- [ ] 6.1 `Vp8Decoder` 重建/输出平面窗口化（2 宏块行），新增
      `Init / DecodeNextMacroblockRow / EmitReadyRows`。
- [ ] 6.2 逐行 YUV→RGB 入口；`WebpRowSource` 有损行源改走增量路径。
- [ ] 6.3 全矩阵逐像素对拍（奇数宽高、alpha、q30/75/95）+ 长图内存测试。
- 通过条件：与 `DecodeRgba` 完全一致；峰值驻留 ≤ 现状 20%。
- 若阶段 6 内无法达一致：整体回退阶段 6 改动，只交付 4A，并在报告写明
  设计取舍与后续分阶段计划。
- 本轮结论：**不实施 4B**（需重构 `Vp8Decoder` 的 2 宏块行窗口与逐像素对拍，
  属架构级重写），按 spec 允许的回退路径只交付 4A；分阶段方案见
  `docs/PerfReport.md` §6.7 与最终报告。

## 阶段 7：收尾

- [x] 7.1 全量测试（含 JPEG 未受影响）。737 项：736 通过、1 项既有跳过。
- [x] 7.2 完成三项数据表与最终 Markdown 报告。
- [x] 7.3 删除临时测量工程或保留在 `.perf`（不影响交付目录）。保留 `.perf/QualityProbe` 供复测。

## 命令

```
dotnet build src/SharpImageConverter.csproj -c Release
dotnet test SharpImageConverter.Tests/SharpImageConverter.Tests.csproj --filter "FullyQualifiedName~Webp"
dotnet test SharpImageConverter.Tests/SharpImageConverter.Tests.csproj
dotnet run --project .perf/QualityProbe -c Release -- .perf/baseline/SharpImageConverter.dll .perf/quality.csv
```
