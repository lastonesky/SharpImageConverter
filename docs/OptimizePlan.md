# `--optimize` 改进执行计划

> 依据：[OptimizeAudit.md](OptimizeAudit.md)。
> 用法：**逐条执行，完成一项就把对应复选框改成 `[x]`，并在文末「执行记录」追加一条
> （改了什么、怎么验证、实测数据、产物是否变化）。**
> 未完成的条目不写“预期已完成”，避免把计划当成结果。

---

## 0. 执行规则

1. **一次只做一项**（或一项内明确的小步）。每项开始前先确认基线：
   `dotnet test` 全绿；至少用 `tools/perf/SicBench` 在该格式语料上跑一次 optimize，记录产物 md5 与耗时。
2. **产物一致性优先**：标了「行为不变」的项，验收必须包含“同二进制 A/B 产物 md5 逐字节一致”。
   标了「行为变更」的项，必须给出语料级前后对比（体积、PSNR/感知分、选中档位），并更新
   `docs/Optimize.md` 的实测表。
3. **性能结论必须同二进制 A/B**：跨二进制对比有 ±2% 系统偏差，不作为验收依据。
4. 每项完成后：
   - 勾选本文件复选框，填写「完成日期」；
   - 在 §7 执行记录追加一条；
   - 若改变了产品行为，同步 `CHANGELOG.md`「未发布」与 `docs/Optimize.md`；
   - 若引入了新的已知限制，追加到 `docs/Optimize.md` 的「已知问题」。
5. 依赖关系：
   - `P0-1` 必须早于 `P1-5`、`P1-6`、`P2-1`（判据修正后所有格式的选档都要重跑）。
   - `P1-6` 已完成：`WebpMinQuality=20` 只对小图生效；>20 MP 默认 `WebpLargeImageMinQuality=40`。
   - `P1-1` / `P1-2` / `P1-3` 互相独立，可任意顺序。
   - `P1-4`（progressive）内部按「无损重封装 → 收益门槛 → progressive」三步走。

---

## 1. 状态总览

| 编号 | 事项 | 优先级 | 性质 | 状态 | 完成日期 |
|---|---|---|---|---|---|
| P0-1 | WebP 画质比较改回 4 通道，统一固定质量分支与产出复评 | P0 | 正确性 + 行为变更 | [x] 已完成 | 2026-10-17 |
| P0-2 | 16-bit PNG / APNG / 动画 WebP 检测后拒绝优化 | P0 | 正确性 | [x] 已完成 | 2026-10-17 |
| P0-3 | 跨格式透明图合成背景 / 明确告警 | P0 | 正确性 | [x] 已完成 | 2026-10-17 |
| P1-1 | PNG 赢家按 zlib 9（或 zopfli）补编一次 | P1 | 体积 | [x] 已完成 | 2026-10-17 |
| P1-2 | PNG 调色板位深 1/2/4 + 灰度颜色类型 | P1 | 体积 | [x] 已完成 | 2026-10-17 |
| P1-3 | PNG 抖动/不抖动双候选 | P1 | 体积 | [x] 已完成 | 2026-10-17 |
| P1-4a | JPEG 无损重封装（阶段 1：去元数据） | P1 | 体积/正确性 | [x] 已完成 | 2026-10-17 |
| P1-4a-2 | JPEG 无损重封装（阶段 2：Huffman 表重排） | P1 | 体积 | [x] 已完成 | 2026-10-17 |
| P1-4b | JPEG/WebP 有损再压缩的最小收益门槛 | P1 | 正确性 | [x] 已完成 | 2026-10-17 |
| P1-4c | JPEG progressive 编码 | P1 | 体积 | [x] 已完成 | 2026-10-17 |
| P1-5 | WebP 按像素数选 method + lossless 候选 | P1 | 体积 | [x] 已完成 | 2026-10-17 |
| P1-6 | WebP 大图搜索可扩展化（锚点 + 小网格 + 二分） | P1 | 速度/体积 | [x] 已完成 | 2026-10-17 |
| P2-1 | 画质判据升级（Y-SSIM + 尺寸感知 + 最差 1% 块） | P2 | 方向 | [x] 已完成 | 2026-10-17 |
| P2-1b | 外部度量后端钩子 + 预乘 alpha | P2 | 方向 | [x] 已完成 | 2026-10-17 |
| P2-2a | GIF 动画：帧间差分 + disposal=1 | P2 | 体积 | [x] 已完成 | 2026-10-17 |
| P2-2b | GIF 动画：透明动画支持 | P2 | 能力 | [x] 已完成 | 2026-10-17 |
| P2-2c | GIF 动画：局部调色板 / crop / 帧去重 | P2 | 体积 | [x] 已完成 | 2026-10-17 |
| P2-3 | PNG 调色板滤波实测（None/Up/Adaptive）+ 调色板重排 | P2 | 体积 | [x] 已完成 | 2026-10-17 |
| P3-1a | JPEG 4:2:0 vs 4:4:4 全候选比较 | P3 | 体积 | [x] 已完成 | 2026-10-17 |
| P3-1b | JPEG 4:2:2 编码支持 | P3 | 体积 | [x] 已完成 | 2026-10-17 |
| P3-1c | JPEG trellis 量化评估 | P3 | 体积 | [x] 已完成（默认关闭） | 2026-10-18 |
| P3-2 | WebP 参数搜索（alpha_quality / filter / sns / segments / preprocessing） | P3 | 体积 | [x] 已完成后移除（收益不足） | 2026-10-18 |
| P3-3 | 调色板量化 k-means/Lloyd 精修 | P3 | 体积/画质 | [x] 已完成 | 2026-10-17 |

---

## 2. P0：先修正确性

### P0-1 WebP 画质比较改回 4 通道（已完成）

**问题**：`ImageOptimizer.SearchQualityParallel` / `SearchQuality` 对 WebP 的 RGBA 缓冲
按 `bytesPerPixel=3` 比较，感知 PSNR 虚高约 8 dB（143 MP q40：53.71 vs 45.74）。
`JpegQuality` 固定档却是 4 通道；`WebpOptimizer` 固定质量分支还误用 `SearchMethod`
作为最终 method。

**实际改动**（见 §7 记录）：
1. `SearchQualityParallel` / `SearchQuality` 增加 `bytesPerPixel` 参数，WebP 传 4。
2. 固定质量分支改用 `OutputMethod`，并统一 4 通道。
3. 产出后对最终字节解码一次、重新计算画质，`Quality` / `MeetsBar` 用最终产物口径。
4. 新增 `OptimizeOptions.WebpMinQuality`（默认暂保持 40）与 CLI `--opt-webp-min`；
   **下界降到 20 依赖 P1-6**：P1-6 已把大图默认下界保持 40（小图 20），
   143 MP 想拿 −7.8% 需显式 `--opt-webp-min 20 --opt-webp-large-min 20`。

**验收结果**：267 个单测全绿；语料对比见 §7。

### P0-2 16-bit / 动画输入保护（已完成）

**问题**：16-bit PNG 只取高字节后被标记“无损”；APNG / 动画 WebP 可能只解第一帧。

**实际改动**：
1. `ImageOptimizer.DetectSourceTraits` 在路由前只读文件头/关键块：
   - PNG：`IHDR` bit depth = 16；第一个 `IDAT` 之前存在 `acTL`（APNG）。
   - WebP：`VP8X` flags bit1；`ANIM`/`ANMF` 兜底。
   - 只按块长度跳过数据，不整文件读入；使用 `ReadExactly` 处理短读。
2. 同格式 optimize 命中时走 `Finish(..., reason)`，直接复制原图并给出中文原因。
3. 跨格式转换照常执行，`OptimizationResult.Method` 追加“（源为 16-bit/动画，已降级）”。
4. 新增 `SourceTraits` 值类型与 3 个单测（16-bit PNG、APNG、动画 WebP）。

**验收结果**：3 个新单测通过，全套 270/270；CLI 手工验证三种文件同格式 optimize 输出与输入
逐字节相同（`.artifacts/traits/`）。

### P0-3 跨格式透明图合成背景（已完成）

**问题**：透明 PNG → JPEG/BMP 时直接丢掉 alpha，透明区通常变黑。

**实际改动**：
1. `OptimizeOptions.MatteColor`（`(byte R, byte G, byte B)`，默认白色）。
2. 跨格式到 JPEG / BMP 时先按 matte 合成；目标为 PNG / WebP / 单帧 GIF 时保持原样
   （GIF 本身支持二值透明）。
3. 发生合成时在 `OptimizationResult.Method` 追加“（透明通道已合成到背景 #RRGGBB）”。
4. `EncodePlain` 兜底路径的 JPEG / BMP 也走同一合成逻辑。
5. 新增单测：透明 PNG → JPEG 的透明角接近白色、中心保留原色；→ BMP 的透明角精确等于 255。

**验收结果**：新单测通过，全套 271/271；不透明输入的合成路径走 `PackRgbaToRgb` 快分支，行为不变。

---

## 3. P1：产出阶段与候选集合

### P1-1 PNG 赢家按 zlib 9（或 zopfli）补编一次（已完成）

**问题**：`PngOptimizer.Encode` 全程用 `CompressionLevel.Optimal`（6），
搜索期降档是对的，但产出期从未升级。

**实际改动**：
1. 新增 `PngWriter.RecompressIdat(png, level)`：只把 IDAT 解压后按新级别重压，
   **不重新滤波、不重新量化**，像素逐字节不变；多个 IDAT 合并、其他块原样保留。
2. `PngOptimizer` 在选出赢家后按 `SmallestSize`（zlib 9）重压一次，取更小者。
3. 新增 `OptimizeOptions.PngMaxDeflateOnWinner`（默认 true）与
   `PngMaxDeflatePixelThreshold`（默认 8 MP）。zlib 6→9 是 7~10 倍成本，
   143 MP 上会多花几十秒，故大图默认跳过；需要极限压缩可调大阈值。
4. 新增 CLI `--opt-no-png-deflate9` 用于同二进制 A/B。
5. 新增单测：同一输入 in/off 两种设置的产物体积不增、解码像素完全一致。

**验收结果**（同二进制 A/B）：
- `car.png`（0.27 MP，图形）：60,521 → **54,549 B（−9.9%）**，147 → 210 ms；
- `Amish-Noka-Dresser.png`（1.0 MP，照片）：140,614 → **137,402 B（−2.3%）**，196 → 243 ms；
- `large.png`（12.6 MP > 8 MP 阈值）：不重压，体积与耗时不变。
- 全套 272/272 单测通过（后续再跑）。

### P1-2 PNG 调色板位深 + 灰度（已完成）

**问题**：`PngWriter.CreateIHDRPalette` 写死 bit depth 8；`WriteGray` 从未在 optimize 使用。

**实际改动**：
1. `PngWriter.WritePalette` 增加 `byte bitDepth`（1/2/4/8）与 `PackIndices`：
   高位在前的行内打包；位深 <8 时把打包后的 rowBytes 当作“宽度”传给现有滤波实现
   （PNG 规范中 bpp = 1，滤波按字节处理）。
2. `PngOptimizer` 对赢家按实际颜色数选最小合法位深（≤2→1、≤4→2、≤16→4），
   重新量化后与 8-bit 版本按实际体积取小。
3. 灰度检测：不透明且 R=G=B 时尝试 `PngWriter.WriteGray`（8-bit，颜色类型 0），
   与调色板候选按体积竞争。
4. 16-bit 灰度仍由 P0-2 拦住；带 alpha 的灰度暂不处理（需要颜色类型 4）。
5. 新增 4 个单测：1/2/4 位调色板 round-trip、16 色图 optimize 后 IHDR 为 4-bit 且像素不变。

**验收结果**（合成语料，同二进制）：
- 16 色 256×256 图形：1,214 → **343 B（−71.7%）**，IHDR bit depth=4、color type=3；
- 32 级灰度 256×256：1,001 → **583 B（−41.8%）**，color type=0；
- 已有 256 色/照片语料不劣化（位深选择仅在 ≤16 色时触发）。

### P1-3 PNG 抖动/不抖动双候选（已完成）

**问题**：GIF 已把抖动作为搜索维度（同图 92 KB vs 17 KB），PNG 没有。

**实际改动**：
1. `PngOptimizer` 的阶梯搜索改为 `(颜色数, 抖动)` 二维候选；精确取色路径仍只评一次。
2. 二分判定改为「该色数下任一抖动变体达标」，最终在**所有达标的 (色数, 抖动) 候选**里按体积取最小。
3. 候选并行度按 `色数档数 × 变体数` 计算，内存预算不变；`--opt-verbose` 会分别打印两个变体。
4. 保留「最高档不达标即收尾」的短路：照片在最高色数下两个变体都不达标时仍然只算 2 个候选
   （而不是整条阶梯）。

**验收结果**（同二进制 A/B，开启 P1-1 的 zlib 9 补压）：
- `Amish-Noka-Dresser.png`：137,402 → **111,025 B（−19.2%）**；
- `.perf/corpus/large.png`（12.6 MP）：7,334,753 → **2,716,630 B（−63.0%）**；
- `car.png`：54,549 B 不变（精确取色路径）。
- 新增「双候选体积 ≤ 强制不抖动」单测。

### P1-4a JPEG 无损重封装（阶段 1：去元数据，已完成）

**实际改动**：
1. 新增 `JpegLosslessOptimizer.StripMetadata`：解析 JPEG 段，删除 APP1（EXIF/XMP）、
   APP13（IPTC/Photoshop）与 COM，保留 APP0/APP2（ICC）/APP14、SOF/SOS/DQT/DHT 与熵编码数据。
2. **EXIF Orientation ≠ 1 时不删 APP1**，避免无损重封装把显示方向改掉。
3. `JpegOptimizer.Run` 先算无损产物，再与有损结果按规则选：
   有损必须比 `min(原图, 无损产物)` 再小 ≥ `MinLossyGainRatio`（默认 10%）且达标才采用；
   否则采用无损产物（如果更小）；否则保留原图。
4. 新增 CLI `--opt-lossy-min-gain N`（百分比）用于调整/验证门槛。

**验收结果**：带 EXIF+COM 的 30 KB JPEG：30,623 → 30,569 B（去除 54 B 元数据），
像素逐字节一致；Orientation=6 的样本保留 APP1。新增 3 个单测，全套 292/292。

> 仍未做：Huffman 表重排（jpegtran -optimize，约 5%~10%），已拆为 `P1-4a-2`。

### P1-4a-2 JPEG 无损重封装（阶段 2：Huffman 表重排，已完成）

**实际改动**：
1. `JpegDecoder.ParseForTranscode`：复用现有解析器熵解码到系数，不重建像素、不释放资源。
2. `JpegFrameState` 暴露 `IsProgressive` / `Frame` / `Components` / `RestartInterval` / `LastScan`。
3. 新增 `JpegHuffmanOptimizer`：
   - 按原 MCU/块顺序统计 DC/AC 符号频率（含 restart 边界与 DC 预测器复位）；
   - 用 `JpegEncoder.BuildOptimalHuffmanTable` 生成新表，重写 DHT；
   - 用新表重新熵编码系数，保留 DRI/重启标记、SOF/DQT/SOS 与其他段；
   - 同步执行元数据剥离（APP1/APP13/COM，Orientation≠1 时保留 APP1）。
4. 仅支持**单扫描基线 JPEG**；渐进式/多次扫描返回 null，自动回退到阶段 1。

**实测**：
- `.perf/corpus/large.jpg`：626,239 → **565,492 B（−9.7%）**，像素 BMP 逐字节一致；
- 带 EXIF 的 30 KB JPEG：30,623 → 30,557 B（Huffman + 去元数据），像素一致；
- 灰度/4:2:0/4:4:4 样本均验证像素一致；渐进式样本回退到元数据剥离。

**验收结果**：全套 292/292；基线 JPEG 无损再降约 5%~10%。

### P1-4b 有损再压缩的最小收益门槛（已完成）

**实际改动**：
1. 新增 `OptimizeOptions.MinLossyGainRatio`（默认 0.10）。
2. `ImageOptimizer.Finish` 增加 `minGainRatio` 参数；JPEG / WebP 的同格式 optimize 传入该值。
   PNG / GIF 是无损重编，不传（保持 0）。
3. 生效时取 `max(MinSavingRatio, MinLossyGainRatio)`；因门槛不足而保留原图时，
   `Reason` 写明“仅节省 X%，低于有损再压缩的最低收益 10%”。
4. 跨格式转换不适用（必须写出目标格式）。

**验收结果**：新增单测（`MinLossyGainRatio=0.99` 时 JPEG 保留原图、字节一致、`Reason` 可读），
全套 278/278 通过（后续再跑）。

### P1-4c JPEG progressive 编码（已完成）

**实际改动**：
1. 新增 `JpegProgressiveTranscoder`：不重新量化，直接复用 baseline 已算出的系数，
   把 baseline JPEG 重写成 progressive（SOF2）。
2. 扫描组织：1 条 DC 扫描（全分量）+ 每个分量 1 条 AC 扫描（1..63），
   只做 spectral selection，不做 successive approximation（Ah/Al=0）。
3. AC 扫描实现 EOBRUN 聚合（含 0x7FFF 上限）；每条扫描单独统计并写最优 Huffman 表。
4. 丢弃原 DHT/DRI 与熵数据，保留 DQT/APP/SOF 段（SOF0→SOF2）。
5. `JpegOptimizer.Produce` 在 baseline 产出后尝试 progressive，按体积取小；
   新增 `OptimizeOptions.JpegProgressive`（默认 true）与 CLI `--opt-no-jpeg-progressive`。

**实测（同 q，像素逐字节一致）**：
| 样本 | baseline（优化 Huffman） | progressive | 变化 |
|---|---|---|---|
| `.perf/corpus/large.jpg`（q57） | 490,224 B | **463,598 B** | −5.4% |
| `.perf/corpus/medium.jpg`（q57） | 103,765 B | **98,857 B** | −4.7% |
| `Amish-Noka-Dresser.jpg`（q42） | 28,140 B | **26,206 B** | −6.9% |

**验收结果**：4:2:0 / 4:4:4 的单测均验证 baseline 与 progressive 解码像素完全一致；
全套 294/294。

### P1-5 WebP method 按尺寸 + lossless 候选（已完成）

**前置**：P0-1 完成（判据修正后重跑性能结论）。

**前置验证（2026-10-17，`D:/progressive.jpg`，143 MP，libwebp 1.6.0）**：
| method | q | 耗时 | 体积 |
|---|---|---|---|
| 3 | 40 | 8.0 s | 1,080,478 B |
| 4 | 40 | 8.0 s | 1,082,492 B |
| 4 | 65 | 7.8 s | 1,434,324 B |
| 3 | 75 | 7.8 s | 1,597,784 B |
| 4 | 75 | **26.3 s** | **1,655,814 B**（更大） |
| 5 | 75 | 8.4 s | 1,646,612 B |
| 6 | 75 | 8.7 s | 1,603,088 B |

**实际改动**：
1. 产出档按像素数：≤20 MP 用 `method 6`（实测 0.3~12.6 MP 上体积均小于 m3，耗时 +5%~14%），
   >20 MP 用 `method 3`（143 MP 上 m6 反而更大）。
2. 无损候选：不透明、≤20 MP、唯一色数 ≤4096 时，编码一次无损 WebP 与有损结果按体积取小。
3. **无损必须走 `WebPEncodeLosslessRGBA` 直通**：通过 `WebPConfig.lossless=1` 的配置路径
   实测仍会产生 1~5 的像素误差（ffmpeg 解码同样如此），不是真正无损；直通入口像素严格一致。
4. m6 与 m2/m3 画质不完全一致：边界上若 m6 产物不达标，先在同一 q 退回 m3 试一次，
   再进入升 q 重试，避免为了 0.01 dB 白升 2 档。

**实测**（同二进制）：
| 样本 | MP | 旧结果 | 新结果 | 变化 |
|---|---|---|---|---|
| `car.png` | 0.3 | q40 / 1,720 B | q23 / **1,394 B** | −19.0% |
| `5_star_base.jpg` | 1.2 | q81 / 205,192 B（m3） | q81 / **199,036 B**（m6） | −3.0% |
| `video-001.rgb.jpg` | 0.02 | q91 / 4,718 B | q91 / **4,570 B** | −3.1% |
| 256×256 16 色图 | 0.07 | 有损 ~1.0 KB | **无损 814 B** | 无损且像素一致 |
| 256×256 32 级灰度 | 0.07 | 有损 | **无损 162 B** | 无损且像素一致 |
| `Amish-Noka-Dresser.png` | 1.0 | q40 / 9,864 B（实际 45.31 dB，低于 45.32 门槛 0.01） | q43 / 10,246 B | +3.9%（修正了旧产物的“差一点达标”） |

**M4 保护**：`WebpEncoderOptions` 在 `quality >= 75` 时把 `method 4` 强制降为 3，
CLI 显式指定 m4 也生效（q75 实测 26.3 s → 7.8 s）。复现时可设
`SIC_WEBP_ALLOW_M4_HIGHQ=1` 绕过保护。

**M4 悬崖的触发条件（2026-10-17 追加实验）**：
- q72：9.9 s；q74.9：7.8 s；**q75 / q76 / q80：26~35 s** ⇒ 阈值是质量 75，
  不是“所有高质量档都随机触发”。
- 同样 10650×13426 (143 MP)，把原图重编码一次（3.78 MB）或高斯模糊后：q75 m4 均为 **~9.7 s**；
  141 MP 的裁剪图也是 ~7.6 s ⇒ **内容/像素组合决定是否触发**，不是单纯尺寸。
- 源码层面（libwebp 1.6.0）：m4 = `RD_OPT_BASIC + full-SNS` 且没有 m3 的 fast-probe；
  `frame_enc.c:645-660` 在 `size_p0 > PARTITION0_SIZE_LIMIT` 时会 **把 max_i4_header_bits 减半并
  重跑整幅分析 pass**。m4 在 q≥75 时 SNS/tlambda 变强，原始 JPEG 的块状/振铃噪声会改变模式选择、
  让 partition0 膨胀，从而触发整幅重跑；耗时 7.8 s → 26~35 s 正好接近 3 个 pass。
  m3 没有 full-SNS、m5/m6 走 trellis，实测都不触发。
- 结论：这是 **libwebp m4 + 特定内容 + q≥75** 的交互，不是 WebP 容器或本项目解码路径的问题。

**验收结果**：`car.png` −19.0%、`5_star_base` −3.0%、`video-001` −3.1%；
低色数 256×256 图无损 814 B / 162 B，像素逐字节一致；143 MP 保持 m3 不退化。

### P1-6 WebP 大图搜索可扩展化（锚点 + 小网格 + 二分）（已完成）

**问题**：下界从 40 降到 20 后，143 MP 的网格+窗口会评估 19 个候选（~27 s），
收益只有 −7.8%。全图降采样搜索又会把 q34 估成 q52，选档偏保守。

**实际改动**：
1. 新增 `SearchQualityAnchored`：先探 **q40 锚点**（历史默认下界，绝大多数照片已达标）。
   - 锚点达标：只在 `(min, 40]` 里找；先探 `min`，不达标就做小网格 + 二分。
   - 锚点不达标：先探 95，再在 `(40, 95]` 里做小网格 + 二分。
2. 小网格固定 4 个内部点（吃多核），剩余区间二分（log2），把 143 MP 的候选从 19 压到 8。
3. 默认下界：`WebpMinQuality=20` 只对小图生效；>20 MP 默认用
   `WebpLargeImageMinQuality=40`，避免为 −7.8% 体积多花 ~15 s。
   想给大图极限体积时显式设 `--opt-webp-min 20 --opt-webp-large-min 20`。
4. 去掉“降采样搜 q”的方案（实测 143 MP 上 q34 被估成 q52，产物反而更大）。

**实测（143 MP，`D:/progressive.jpg`）**：
| 配置 | 选中 q | 体积 | 搜索 | 端到端 |
|---|---|---|---|---|
| 默认（大图下界 40） | 40 | 1,080,478 B | 3.4 s / 1 候选 | **15.4 s** |
| 显式下界 20 | 34 | **996,350 B（−7.8%）** | 19.6 s / 8 候选 | 31.2 s |
| 旧实现（下界 20，网格+窗口） | 34 | 996,350 B | 18.9 s / 19 候选 | 30.4 s |

⇒ 搜索算法把候选数减半，但 143 MP 的编码本身仍贵；默认选择“保速度”，极限体积走显式选项。

---

## 4. P2：判据与 GIF 动画

### P2-1 画质判据升级（已完成）

**实际改动**：
1. `ImageQuality` 新增 `Ssim`（Y 通道 SSIM 均值）与 `WorstSsim`（最差 1% 块的 SSIM）。
2. `QualityMetrics.Compare` 在旧 PSNR 之外计算 V2：
   - 在 Y 通道上算局部 SSIM（C1/C2 标准常数）；
   - 块边长尺寸感知：`clamp(round(sqrt(w*h)/500), 4, 16)`（1 MP 用 4、12 MP 约 6、143 MP 用 16）；
   - 用 256 桶直方图求最差 1% 块，抓局部糊掉/块效应；
   - 带 alpha 的图暂不跑 V2（透明像素 RGB 无显示意义），沿用旧口径。
3. `OptimizeOptions` 新增 `MinSsim`（默认 0.95）与 `MinWorstSsim`（默认 0.75），
   `Accept` 同时要求旧指标与 V2 达标；新增 CLI `--opt-min-ssim` / `--opt-min-worst-ssim`（百分比）。
4. 阈值来自标定语料：当前合格产物 SSIM 均值 0.984~0.991、最差 1% 0.827~0.957；
   取 0.95 / 0.75 留出余量。人工 4×4 棋盘格（旧感知 PSNR 54 dB、SSIM 0.004）现在会被拒绝。

**实测**：语料上的既有产物在默认阈值下全部保持（体积、选中档位不变）；
143 MP WebP 搜索/输出耗时无明显变化；新增 2 个 V2 单测，全套 299/299。

**未做**：SSIMULACRA2/Butteraugli 原生后端、平坦区色带专项惩罚（外部钩子已提供，见 P2-1b）。

### P2-1b 外部度量后端钩子 + 预乘 alpha（已完成）

1. `QualityMetrics.ExternalMetric`：进程级委托钩子，可接入 SSIMULACRA2 / Butteraugli 等原生实现；
   设置后 `Compare` 完全交给外部后端，返回的 `ImageQuality` 直接进入 `Accept`。
   **没有内置**这些原生算法，避免引入未验证的第三方实现。
2. RGBA 比较改为预乘 alpha：`RGB * A / 255`，完全透明像素 RGB 归零、半透明按 alpha 缩放；
   修复透明 GIF / PNG 中不可见 RGB 误伤画质的问题。
3. 新增单测：透明区不同 RGB 但 alpha 相同 → 判定无损；alpha 不同 → 被扣分；
   外部后端钩子返回的分数被正确采用。

### P2-2a GIF 动画：帧间差分 + disposal=1（已完成）

**实际改动**：
1. `GifEncoder.EncodeIndexed` 增加 `disposalMode` 参数；帧间差分传 `1`（保留上一帧），
   既有透明单帧/动画继续走默认 `2`（恢复背景）。
2. 动画候选量化时预留 1 个透明索引（`MaxColors-1`），编码前把透明槽追加到全局调色板。
3. 首帧保留完整索引；后续帧与上一帧索引相同的像素写成透明索引，只编码变化区域。
4. **修复** `PaletteQuantizer` 精确调色板的 `KeyNotFoundException`：动画只用采样帧建精确表时，
   非采样帧可能出现表中没有的颜色；现在退回最近色而不是抛异常。

**实测**（真实动画语料）：
- `.perf/a3a/final.gif`：23,390,056 → **17,779,514 B（−24.0%）**；
- `.perf/corpus/large.gif`：2,420,888 → 2,341,406 B（−3.3%）；
- 新增单测：disposal=1 时透明像素显示上一帧；4 色三帧动画 optimize 后逐帧像素一致且体积更小。

**仍未做**：透明动画（源图带 alpha）仍直接保留原图；局部调色板、crop/帧去重也尚未做。

### P2-2b GIF 动画：透明动画支持（已完成）

**实际改动**：
1. `GifDecoder.DecodeAnimationRgba32`：新增 `AnimationRgba` 解码模式，逐帧保留 alpha 并做 disposal 合成；
   `GifAnimation` 新增 `RgbaFrames`，同时提供 RGB 帧以兼容既有调用方。
2. `GifOptimizer` 对透明动画走「整帧 + 全局调色板 + 二值透明」：透明槽由量化器预留，
   `transparentIndex=0`、`disposal=2`；不做帧间差分（单个透明索引无法同时表达“与上一帧相同”和“透明”）。
3. 不透明动画仍走 P2-2a 的帧间差分 + disposal=1。
4. 修复 `PaletteQuantizer.Assemble` 的索引偏移 bug：精确取色把颜色存在 `[offset..]`，
   `Assemble` 仍从 `[0..]` 读，导致保留透明槽时调色板整体错位（透明动画表现为颜色全错）。
5. 画质比较：双方 alpha=0 时 RGB 不参与计分，避免透明像素的不可见 RGB 误伤画质。

**实测**：
- `.perf/corpus/large.gif`：2,420,888 → **2,142,409 B（−11.5%）**；
- `.perf/a3a/final.gif`：23,390,056 → 17,779,514 B（−24.0%，与 P2-2a 持平）；
- 新增透明动画单测：8×8 两帧、红/透明 + 红/绿，optimize 后逐帧 alpha 与可见 RGB 一致。

**仍未做**：透明动画的帧间差分（需要两个透明语义）、局部调色板、crop/帧去重。

### P2-2c GIF 动画：局部调色板 / crop / 帧去重（已完成）

**实际改动**：
1. `GifEncoder.EncodeIndexedFrames`：支持每帧局部调色板、帧矩形（left/top/width/height）、
   各自位深；`globalPalette=null` 时不写全局表，纯局部表编码。
2. `GifOptimizer` 在全局调色板候选之外，再对赢家做一次「每帧独立量化 + 局部表 + 整帧」候选，
   按体积取小；`GifLocalPalettes` 默认开启。局部表与帧间差分互斥，因此是独立候选。
3. **帧去重**：连续完全相同的帧合并为一条，时长相加（纯无损）。
4. **crop**：不透明动画的差分帧只编码变化区域的包围盒，矩形外的像素完全不写；
   无变化帧写成 1×1 透明像素。
5. 修复 `DecodeAnimationRgba32` 的合成语义：差分帧的透明像素现在保留上一帧 RGBA，
   而不是写成 alpha=0（否则解码后的「显示帧」会丢掉上一帧内容）。

**实测**：
- `.perf/corpus/large.gif`：2,420,888 → **2,141,895 B（−11.5%）**；
- `.perf/a3a/final.gif`：23,390,056 → **17,651,187 B（−24.5%）**；
- 新增单测：局部调色板无 GCT 解码、连续相同帧去重、差分帧 round-trip；全套 307/307。

**说明**：局部调色板/帧去重在真实语料上不一定胜出（全局表 + 差分已经很强），
因此都按体积竞争，不强行替换。

### P2-3 PNG 调色板滤波 + 调色板重排（已完成）

**实际改动**：
1. 赢家精修时实测 `None` / `Up` / `Adaptive` 三种滤波；`AdaptiveFiltering=false` 时只试 None/Up。
2. 同时实测最小位深与 8-bit，按实际体积取小。
3. 调色板按索引使用频率降序重排，并同步重映射索引；再用胜出的滤波/位深编码一次比体积。
4. 只对 ≤8 MP 或 ≤16 色的图做，避免大图多付几轮 deflate。
5. 顺带修复：灰度候选原先在 zlib 6 下与已 zlib 9 的调色板产物比较，导致灰色渐变被误判；
   现在灰度候选也先 zlib 9 再比较。

**实测**：
- `Amish-Noka-Dresser.png`：114,720（zlib6）→ 90,123（None）→ 88,654（重排）→ **85,867 B（zlib9）**；
  最终 32 色、感知 45.76 dB，比 P1-3 后的 111,025 B 再降 22.6%。
- 16 色 256² 图仍为 343 B；32 级灰度仍为 583 B（修复后不再被调色板误判）；
- `car.png` 54,549 B、12.6 MP `large.png` 2,716,630 B 不变。

**验收结果**：全套 291/291；调色板图 IDAT 再降 1%~23%（视内容）。

---

## 5. P3：编码器能力

### P3-1a JPEG 4:2:0 vs 4:4:4 全候选比较（已完成）

**实际改动**：
1. `JpegOptimizer` 在 4:2:0 达标后仍搜索 4:4:4（可用 `JpegCompareSubsampling=false` 关闭）。
2. 两者都达标时按体积取小；平局优先 4:4:4（色度更完整）。
3. 4:2:0 不达标时仍回退 4:4:4（原行为）；都不达标时取感知分更高者。

**实测**：`.perf/corpus/large.jpg`（色度丰富）：
- 4:2:0：q57 / 463,598 B / 感知 45.43 dB；
- 4:4:4：q46 / **373,443 B（−19.5%）** / 感知 **46.49 dB**。
新增单测：开启比较后的产物体积不大于仅 4:2:0；全套 304/304。

### P3-1b JPEG 4:2:2 编码支持（已完成）

**实际改动**：
1. 新增 `JpegChromaSubsampling`（444/422/420），`JpegEncoderOptions` 增加枚举构造；
   旧的 `bool subsample420` API 保留并映射到枚举。
2. 编码器支持 4:2:2：Y H=2/V=1、Cb/Cr H=1/V=1，MCU 16×8，4 个块（2Y+1Cb+1Cr）；
   新增 `RgbToYCbCr422` SIMD 快路径与带边界 clamp 的标量回退。
3. `JpegSampleCache` / `JpegCoefficientSink` / `EncodeRgbPipeline` / `EncodeFromSamples`
   全部支持 422 的几何与块顺序。
4. `JpegOptimizer` 在 4:2:0 之外同时搜索 4:2:2 / 4:4:4，所有达标候选按体积取小。
5. CLI `--subsample 420/422/444` 支持普通转换。

**实测**：`.perf/corpus/large.jpg`
| 候选 | 质量 | 体积 | 感知 PSNR |
|---|---|---|---|
| 4:2:0 | q57 | 463,598 B | 45.43 dB |
| **4:2:2** | **q46** | **364,7xx B** | **46.02 dB** |
| 4:4:4 | q46 | 373,443 B | 46.49 dB |

优化器最终选择 4:2:2（体积最小且达标）。新增 4:2:2 常规尺寸/奇数尺寸 round-trip 单测；
全套 311/311。

### P3-1c JPEG trellis 量化评估（已完成，默认关闭）

**实现**：新增 `JpegTrellisQuantizer` 与 `JpegEncoderOptions.TrellisQuantization` /
`OptimizeOptions.JpegTrellisQuantization`（默认 false），CLI `--opt-jpeg-trellis`。
每个 AC 系数在 `{0, 最近值, 最近值±1}` 中用 DP 选「失真 + λ×码率」最小组合；
失真用系数域平方误差（Parseval 等价于像素 SSE），码率用标准 Huffman 表码长估计，
λ 随量化步长 Q² 缩放（`λ ≈ 60·(scale/100)²`，可用 `SIC_JPEG_TRELLIS_LAMBDA` 覆盖）。
trellis 需要量化前 DCT 系数，因此走标量 FDCT（默认 SIMD 融合路径不适用）。

**实测（PNG→JPEG，`--optimize`，同机）**：

| 样本 | 基线体积 / 时间 | trellis 体积 / 时间 | 体积 | 感知 PSNR | 最差 1% SSIM |
|---|---:|---:|---:|---|---|
| `small.png` 0.48 MP | 29,111 B / 0.6 s | 27,920 B / 1.3 s | −4.1% | 45.47 → 45.71 | 0.753 → 0.757 |
| `medium.png` 1.57 MP | 82,724 B / 1.4 s | 68,755 B / 4.2 s | −16.9% | 45.39 → 45.40 | 0.812 → 0.761 |
| `Amish-Noka-Dresser.png` 1.0 MP | 27,399 B / 0.9 s | 21,382 B / 2.5 s | −22.0% | 45.37 → 45.43 | 0.835 → 0.800 |
| `car.png` 0.27 MP | 3,901 B / 0.4 s | 3,005 B / 1.2 s | −23.0% | 46.25 → 46.10 | 0.871 → 0.780 |
| `large.png` 12.6 MP | 377,105 B / 3.2 s | 285,690 B / 23.0 s | −24.2% | 45.45 → 45.35 | 0.937 → 0.890 |

**决定：默认不启用。** 体积收益 4%~24% 显著，但代价是 2~7 倍编码时间，
且最差 1% 块 SSIM 明显下降（逼近 0.75 门槛）——它是「在画质门槛内按体积重新分配误差」，
不是无代价的同质量压缩。保留 opt-in（`--opt-jpeg-trellis`）用于离线评估与特殊素材，
不进入默认 optimize。完整数据见 docs/Optimize.md §2「JPEG trellis」。

### P3-2 WebP 参数搜索（已移除）

曾实现 `WebpEncoderOptions` 的 5 个调参字段 + `WebpOptimizer` 小图坐标下降，
语料实测只有 **0~1.3%** 体积收益，代价是 **2~4 倍**搜索时间（`car.png` −1.2%、
`5_star_base.jpg` −1.3%，其余样本 0%）。判定「不划算」后已从代码库整体删除
（`WebpParamSearch` / `WebpParamSearchMaxPixels` / `--opt-webp-params` /
`tools/perf/WebpParamProbe`），不再保留 opt-in。

### P3-3 调色板 k-means 精修（已完成）

**实际改动**：
1. 中位切分后增加 1 轮 Lloyd 迭代（`PaletteQuantizer.RefinePalette`）：
   按 5-bit 直方图桶的权重把每个桶归到最近调色板色，再用桶加权均值更新颜色；
   不增加颜色数、不改变索引，只降低同色数下的量化误差。
2. `QuantizeOptions.EnableKMeansRefinement` 默认开启，可关闭用于 A/B。
3. 精确取色路径不跑精修（本来就是无损）。
4. 新增单测：同一图 16 色无抖动下，开启精修的 PSNR/SSIM 不低于关闭。

**实测**：
- `Amish-Noka-Dresser.png`：85,867 → **85,819 B**（−0.06%）；
- `.perf/a3a/final.gif`：17,779,514 → **17,651,187 B**（−0.72%）；
- `car.png`、`large.gif` 基本不变。收益不大但方向正确，耗时增加可忽略。

**验收结果**：同色数画质不降、体积不增；全套 305/305。

---

## 6. 横切项（已完成，2026-10-18）

- **全候选取最小**：PNG / GIF 不再用 `ReplayLadderVisits` 限制参与体积比较的候选集合。
  最高档达标时把整条固定网格（颜色数阶梯 × 抖动变体）评估完，在所有达标候选里按体积取最小；
  最高档不达标仍只算最高档。候选集合与核数/内存无关，结果确定。
  JPEG / WebP 在最低达标 q 附近多看 1~2 档；WebP 另外在**已评估**候选中按搜索档 m2 体积取最小。
- **边界多看 1~2 档**：`SearchQualityByScore` / `SearchQualityAnchored` 在收敛后向下再探 2 档，
  命中达标就采用更低 q（体积通常更小，且能兜住量化表取整造成的非单调）。
- **RGBA 预乘 alpha**：已在 P2-1b 完成。
- **CandidateParallelism 用当前可用内存**：从 `TotalAvailableMemoryBytes / 4` 改为
  `(总量 − 已用于托管堆) / 4`，下界 64 MB；并新增 `ForEachCandidate`：候选并行遇到 OOM
  时 GC 后以并行度 1 重跑同一批候选（PNG / GIF / WebP 均已接入）。
- **跨格式目标格式建议**：`OptimizationResult.Suggestion`，按「是否带 alpha / 唯一色数 /
  目标格式」给出方向明确的建议（JPEG 带透明 → WebP/PNG；照片转 PNG/GIF → WebP；
  低色数转 JPEG → PNG/WebP；BMP → PNG）；CLI 会打印 `💡 目标格式建议`。
  只做启发式、不额外编码。
- **内存预检 + 逐行流式（2026-10-18 追加）**：新增 `MemoryGuard`，解码前读文件头宽高并按格式估算峰值。
  optimize 超预算直接拒绝（同格式保留原图、跨格式抛 `InsufficientMemoryException`）；
  非 optimize 转换超预算时：BMP/PNG/GIF/WebP/JPEG → BMP/PNG/GIF/JPEG 走 `StreamingConversion` 管线
  （PNG zlib 逐行、BMP 行读写、GIF 首帧/单帧缓冲、WebP libwebp 增量解码、JPEG MCU 行 IDCT/编码）；
  目标 WebP 仍需整帧，流式后不足则拒绝。`--memory-limit` / `--no-memory-guard` 可覆盖。

---

## 7. 执行记录

> 每完成一项追加一条。格式：
> `- YYYY-MM-DD · P0-1 · 完成：<改了什么>；验证：<命令/数据>；产物：<md5/体积变化>。`

- 2026-10-18 · 内存预检 + 逐行流式 · **完成**：新增 `MemoryGuard`（五格式头探测 +
  格式经验估算）与 `MemoryLimitBytes` / `DisableMemoryGuard` / CLI `--memory-limit`；
  optimize 超预算拒绝；非 optimize 超预算时 BMP/PNG/GIF/WebP → BMP/PNG 走
  `StreamingConversion`（BMP/PNG 行编解码、PNG 逐行 zlib、GIF 首帧/单帧缓冲、
  WebP 增量解码、JPEG 基线 MCU 行熵解码+SIMD IDCT/行级编码），目标 WebP 仍拒绝。
  143 MP 基线 JPEG→BMP：854 MB/641 ms → **65 MB/775 ms**（像素一致）；progressive
  458 MB/1039 ms（像素一致）。流式 BMP 改为 top-down 顺序写。新增 18 个单测，全套 335/335。
- 2026-10-18 · 横切项 · **完成**：PNG/GIF 全网格候选取最小（去掉 Replay 限制）；
  JPEG/WebP 边界多看 1~2 档；`CandidateParallelism` 改用当前可用内存（下界 64 MB）
  并加 OOM 串行回退；新增跨格式 `Suggestion` 建议。验证：全套 317/317；
  small.png PNG 292 ms / medium.png 568 ms / large.gif 1279 ms，无静默退化。
- 2026-10-18 · P3-2 · **完成后移除**：WebP 五参数小网格搜索 + 单参数消融/组合/坐标下降
  （car.png −1.2%、5_star −1.3%、其余 0；耗时 2~4 倍）。因收益过小，相关代码与
  `--opt-webp-params` / `WebpParamSearch` / `tools/perf/WebpParamProbe` 已删除。
- 2026-10-18 · P3-1c · **完成（默认关闭）**：新增 `JpegTrellisQuantizer`（块内 DP，λ 随 Q² 缩放）
  与 `--opt-jpeg-trellis`。small −4.1%、medium −16.9%、Amish −22.0%、car −23.0%、
  large −24.2%，耗时 2~7 倍，最差 1% SSIM 下降；默认不启用。

- 2026-10-17 · P3-3 · **完成**：中位切分后增加 1 轮 Lloyd/k-means 精修，
  `QuantizeOptions.EnableKMeansRefinement` 默认开启。Amish.png −0.06%、
  final.gif −0.72%；精修后同色数 PSNR/SSIM 不降。全套 305/305。
- 2026-10-17 · P3-1b · **完成**：新增 `JpegChromaSubsampling` 枚举与 4:2:2 编码
  （16×8 MCU、2Y+1Cb+1Cr、SIMD+标量回退）；优化器同时搜索 420/422/444 按体积取小；
  CLI `--subsample 422`。large.jpg 选择 4:2:2 q46 / 364.7 KB（感知 46.02 dB）。
  新增 4 个单测，全套 311/311。
- 2026-10-17 · P3-1a · **完成**：`JpegOptimizer` 在 4:2:0 达标后仍搜索 4:4:4，按体积取小；
  新增 `JpegCompareSubsampling`（默认 true）与 CLI `--opt-no-jpeg-subsample-compare`。
  large.jpg 463,598 → 373,443 B（−19.5%），感知 45.43 → 46.49 dB。全套 304/304。
- 2026-10-17 · P2-1b · **完成**：新增 `QualityMetrics.ExternalMetric` 外部后端钩子；
  RGBA 比较改为预乘 alpha；透明区不可见 RGB 不再误伤画质。新增 2 个单测，全套 303/303。
  未内置 SSIMULACRA2/Butteraugli 原生实现（只提供接入点）。
- 2026-10-17 · P2-2c · **完成**：新增 `EncodeIndexedFrames`（局部调色板/帧矩形/无 GCT）；
  GIF 动画增加局部表候选、连续帧去重、差分帧 crop；修复动画 RGBA 透明像素应保留上一帧的合成语义。
  实测 large.gif −11.5%、final.gif −24.5%；新增 3 个单测，全套 307/307。
- 2026-10-17 · P2-2b · **完成**：新增 `DecodeAnimationRgba32` + `GifAnimation.RgbaFrames`；
  透明动画走整帧 + 全局调色板 + 二值透明（disposal=2），不透明动画保持帧间差分。
  修复精确调色板 `Assemble` 的 offset 错位；画质比较忽略双方全透明像素的 RGB。
  实测 large.gif 2.42 MB → 2.14 MB（−11.5%）；新增透明动画单测；全套 301/301。
- 2026-10-17 · P2-1 · **完成**：`ImageQuality`/`QualityMetrics` 增加 Y-SSIM 与最差 1% 块，
  块边长按像素数放大；`OptimizeOptions.MinSsim=0.95` / `MinWorstSsim=0.75` 默认生效；
  新增 CLI `--opt-min-ssim` / `--opt-min-worst-ssim`。棋盘格案例旧 PSNR 54 dB、SSIM 0.004，
  新指标可拦截；语料既有产物保持不变。全套 299/299。
- 2026-10-17 · P2-2a · **完成**：GIF 动画帧间差分 + disposal=1；`EncodeIndexed` 增加
  `disposalMode`；动画候选预留透明索引；修复精确调色板对非采样色抛 KeyNotFoundException。
  实测 final.gif 23.39 MB → 17.78 MB（−24.0%）；新增 disposal=1 与三帧动画像素回归单测。
  全套 297/297。
- 2026-10-17 · P1-4c · **完成**：新增 `JpegProgressiveTranscoder`，复用 baseline 系数重写为
  SOF2（DC 扫描 + 每分量 AC 扫描 + EOBRUN），不重新量化；`JpegOptimizer` 按体积取小。
  实测 large.jpg −5.4%、medium.jpg −4.7%、Amish q42 −6.9%，像素一致；全套 294/294。
- 2026-10-17 · P1-4a-2 · **完成**：新增 `JpegHuffmanOptimizer`（基线单扫描），
  复用解码器系数统计频率、重建最优 Huffman 表并重写熵编码段；`JpegDecoder.ParseForTranscode`
  暴露仅到系数的解析路径。`large.jpg` 626,239 → 565,492 B（−9.7%），像素 BMP 一致；
  渐进式回退阶段 1。全套 292/292。
- 2026-10-17 · P2-3 · **完成**：PNG 赢家精修增加 None/Up/Adaptive 滤波实测 + 调色板频率重排，
  并修正灰度候选与 zlib 9 的口径不一致。Amish.png 111,025 → 85,867 B（−22.6%）；
  16 色图 343 B、灰度 583 B 保持；全套 291/291。
- 2026-10-17 · P1-4a · **完成（阶段 1）**：新增 `JpegLosslessOptimizer.StripMetadata`，
  删除 APP1/APP13/COM、保留 ICC 与熵数据；Orientation≠1 时保留 APP1；
  `JpegOptimizer.Run` 在「原图/无损/有损」间按 10% 门槛选择。新增 CLI `--opt-lossy-min-gain`。
  验证：30,623 → 30,569 B、像素一致；Orientation=6 保留 APP1；新增 2 个单测。
- 2026-10-17 · P1-6 · **完成**：新增 `SearchQualityAnchored`（q40 锚点 + 4 点小网格 + 二分），
  143 MP 候选数 19 → 8；默认大图下界保持 40（`WebpLargeImageMinQuality`），小图默认 20。
  实测 143 MP：默认 q40/1,080,478 B/15.4 s；显式下界 20 得 q34/996,350 B（−7.8%）/31.2 s。
  另确认“降采样搜 q”不可用（q34 被估成 q52），已移除。
- 2026-10-17 · P1-5 · **完成**：产出档按尺寸选 m6（≤20 MP）/m3（>20 MP）；
  低色数不透明图增加无损 WebP 候选；无损必须走 `WebPEncodeLosslessRGBA` 直通
  （config.lossless 路径实测有 1~5 像素误差，非真无损）；m6 边界失败时先同 q 退回 m3。
  实测 car −19.0%、5_star −3.0%、video −3.1%，256² 低色数图无损 814 B/162 B；全套 289/289。
- 2026-10-17 · P1-4b · **完成**：新增 `OptimizeOptions.MinLossyGainRatio`（默认 0.10）；
  `Finish` 增加 `minGainRatio`，JPEG/WebP 同格式 optimize 传入；收益不足时保留原图并写入
  `Reason`，跨格式不受影响。验证：`MinLossyGainRatio=0.99` 的 JPEG 单测通过，
  全套 278/278。
- 2026-10-17 · P1-2 · **完成**：`PngWriter.WritePalette` 支持 1/2/4 位索引打包，
  `PngOptimizer` 对 ≤16 色的赢家降到最小位深；不透明灰度图尝试颜色类型 0。
  验证：16 色 256² 图 1,214 → 343 B（−71.7%，4-bit）；32 级灰度 1,001 → 583 B（−41.8%，灰度）；
  新增 1/2/4 位 round-trip 与 16 色 optimize 单测，全套 277/277 通过（当前累计 289/289）。
- 2026-10-17 · P1-3 · **完成**：`PngOptimizer` 的候选从一维颜色阶梯改成 `(色数, 抖动)` 二维；
  二分判定取该色数下任一变体达标，最终在所有达标候选里按体积取最小；并行度按候选数×2。
  验证（同二进制 A/B）：Amish.png 137,402 → 111,025 B（−19.2%）；12.6 MP large.png
  7,334,753 → 2,716,630 B（−63.0%）；car.png 不变。新增双候选体积单测，全套 273/273 通过。
- 2026-10-17 · P1-1 · **完成**：新增 `PngWriter.RecompressIdat`（只重压 IDAT，像素不变）；
  `PngOptimizer` 对赢家按 zlib 9 重压一次；新增 `PngMaxDeflateOnWinner` /
  `PngMaxDeflatePixelThreshold`（默认 8 MP）与 CLI `--opt-no-png-deflate9`。
  验证（同二进制 A/B）：car.png 60,521 → 54,549 B（−9.9%）、Amish.png 140,614 → 137,402 B（−2.3%）；
  12.6 MP 的 large.png 因超过阈值不重压。新增像素一致性单测。
- 2026-10-17 · P0-3 · **完成**：新增 `OptimizeOptions.MatteColor`（默认白）；
  跨格式到 JPEG/BMP 前做 alpha 合成（PNG/WebP/GIF 保持原样），并在 `Method` 标注背景色；
  `EncodePlain` 兜底路径同步。验证：新增透明 PNG→JPEG/BMP 单测，全套 271/271 通过。
- 2026-10-17 · P0-2 · **完成**：新增 `ImageOptimizer.DetectSourceTraits`，识别 16-bit PNG
  （IHDR bit depth）、APNG（`acTL`）与动画 WebP（`VP8X` flags / `ANIM`/`ANMF`）。
  同格式 optimize 命中时保留原图并给出 `Reason`；跨格式转换在 `Method` 追加降级提示。
  验证：3 个新单测 + 全套 270/270 通过；手工构造的 16-bit PNG / APNG / 动画 WebP
  同格式 optimize 输出与输入逐字节相同。
- 2026-10-17 · P0-1 · **完成**：WebP 画质比较从 `bytesPerPixel=3` 改为 4；
  `WebpOptimizer` 固定质量分支从 `SearchMethod` 改为 `OutputMethod`；
  产出后重新解码最终字节计算 `Quality`/`MeetsBar`；新增 `OptimizeOptions.WebpMinQuality`
  与 CLI `--opt-webp-min`（默认 40，降到 20 留到 P1-6）。
  验证：`dotnet test` 267/267 通过。语料（PNG/JPG → WebP，`--opt-webp-min 40` 与旧二进制对比）：
  | 样本 | MP | 旧 q/体积 | 新 q/体积 | 变化 |
  |---|---|---|---|---|
  | Amish-Noka-Dresser.png | 1.0 | 40 / 9,864 B | 40 / 9,864 B | 0 |
  | car.png | 0.3 | 40 / 1,720 B | 40 / 1,720 B | 0 |
  | progressive.jpg | 143 | 40 / 1,080,478 B | 40 / 1,080,478 B | 0（耗时 11.3 s → 12.1 s） |
  | 5_star_base.jpg | 1.2 | 52 / 121,164 B | **81 / 205,192 B** | +69.4%（旧口径实际未达 45.3 dB） |
  | video-001.rgb.jpg | 0.02 | 95 / 5,934 B | **91 / 4,718 B** | −20.5% |
  另测 `--opt-webp-min 20`：progressive 选到 q34 / 996,350 B（−7.8%），
  但搜索 18 个候选、端到端 27.0 s，因此不设为默认。
  产物变化属预期（行为变更），已写入 CHANGELOG / docs/Optimize.md 的已知口径说明。
