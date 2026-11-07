# VP8 有损行级流式解码实施方案（WebP 迭代 2 · 任务 4B）

## 0. 状态与审批

- 本文档是 `spec.md` §5.3（4B）的**可执行细化稿**。**状态：已按 P0–P3 实施并验收**，
  全部 §9.5 阈值达标，未触发回退；实现记录见 `docs/PerfReport.md` §6.7 与
  `CHANGELOG.md`，任务清单见 `tasks.md` 阶段 8。
- 实现按 §10 的 P0–P4 串行推进，每阶段独立构建/测试通过后才进入下一阶段。
- 本文档对 §5.3 的两处**设计修正**（均为写实分析后的结论）：
  1. 重建窗口不是"2 个完整宏块行（32 行）"，而是 **当前 16 行 + 上一宏块行最后 1 行**（Y 17 行、UV 9 行）。
     依据：VP8 帧内预测最多只读上方 1 行；环内滤波在**输出平面**上执行，不读重建窗口。
     若 P1 对拍发现任何超出 1 行的读取，回退到保守的 32/16 行窗口（内存代价见 §5.3）。
  2. 逐行 RGB 不是"单行独立转换"，而是**行对转换 + 2 行 RGBA 暂存**。依据：fancy upsampling
     的偶数行用 bottom 公式，直接复用 `UpsampleRgbaLinePair` 才能保证逐位一致；暂存只多 2 行。
- 公开 API 目标：**零变化**。`WebpRowSource`、`IRowImageSource`、`Vp8Decoder.Decode` 的签名与语义不变。
- 动图仍显式拒绝；本轮不触碰 `WebpDecoder` 的动图合成路径。

## 1. 目标与非目标

### 1.1 目标

1. `WebpRowSource` 的有损 VP8 分支不再构造整帧 RGBA / 整帧 YUV，改为**按宏块行推进、按输出行交付**。
2. 输出与现有整帧路径（`Vp8Decoder.Decode` → `Vp8YuvToRgb.ConvertToRgba` → ALPH 覆盖 A）
   **逐像素 bit-exact**，包括奇数宽高、非 16 倍数尺寸、1×1、alpha、simple/complex 滤波。
3. 常驻解码内存与图像**高度**无关，只与宽度和两个宏块行相关；显著低于整帧路径。
4. 保持既有的短读/EOF/截断/错误包装/并发隔离语义。

### 1.2 非目标

- 不支持 VP8 非关键帧（WebP 合法输入只有关键帧，现有 `Vp8Decoder` 已显式拒绝）。
- 不支持动图逐帧流式解码（继续 `WebpUnsupportedException`）。
- 不改变 VP8 压缩载荷的"整 chunk 缓冲"策略：bool 解码器随机访问分区字节，增量喂字节属另一次
  架构改造（见 §5.4）。
- 不新增公开 API、不改 JPEG/其他格式。

## 2. 现有实现写实（本方案的事实基础）

### 2.1 `Vp8Decoder` 的重建/输出平面

`src/Formats/Webp/Vp8Decoder.cs`：

- `AllocatePlanes`（L423–479）：
  - `Pad = 8`；`_strideReconY = Pad*2 + mbW*16`、`_strideReconUv = Pad*2 + mbW*8`；
  - 重建平面四周留 padding：上方 `Pad` 行填 **127**，左边界第 `Pad-1` 列填 **129**；
  - 输出平面无 padding：`_strideOutY = mbW*16`、`_strideOutUv = mbW*8`；
  - 逐行上下文数组（`_mbNz/_mbNzDc/_topModes/_segmentOfMb/_skipOfMb/_isI4X4OfMb/_imodes/
    `_uvModeOfMb/_nonZeroY/_nonZeroUv/_fInnerOfMb`）均按 `mbW` 分配，跨宏块行复用。
- `DecodeFrame`（L539–567）每宏块行的固定顺序：
  `ParseIntraModeRow` → `DecodeMacroblock`×mbW → 清左侧上下文（`_leftNz/_leftNzDc/_leftModes`）
  → `CopyRowToOutput`（L993–1009）→ `FilterRow`（L1016–1078）。
- `ReconstructMacroblock`（L870–913）的平面偏移是**绝对宏块行**：
  `yOff = (Pad + mbY*16)*strideY + Pad + mbX*16`，`uvOff` 同理；
  帧内预测读取的都是**未滤波重建值**。
- `PrepareTopRight`（L920–961）：i4x4 的右上 4 样本取自**上一宏块行同一行**（`yOff - strideY + 16`）；
  最右宏块改用本宏块上一行第 15 列；并把 4 样本写回上一行右侧 padding 与
  当前宏块第 3/7/11 行右侧（`+16..+19`）。
- `CropOutput`（L1080–1097）最后才把宏块网格裁剪到真实宽高。

### 2.2 宏块解码与上/左邻居

- 行内按 `mbX = 0..mbW-1` 从左到右；左邻居来自**当前行已重建宏块**（或图像左边界 129）。
- 上邻居来自**上一宏块行**（首个宏块行为 127）。
- `CheckMode`（L855–864）只把 `mbX==0 / mbY==0` 的"无左/无上"退化为 DC 变体；
  `mbY` 是**图像绝对行号**，窗口化后必须继续使用绝对 `mbY` 判断。
- `_mbNz` 同时充当"上方非零上下文"（跨行保留）；`_leftNz` 每行清零。

### 2.3 环内滤波的跨宏块行依赖

`src/Formats/Webp/Vp8LoopFilter.cs`：

- `HFilter16/HFilter8` 处理**左边界**（垂直边界），仅当 `mbX>0`；只改行内列，不跨宏块行。
- `VFilter16/VFilter8` 处理**上边界**（水平边界），仅当 `mbY>0`；会改写边界**上方最多 4 行**
  与下方若干行。
- `VFilter16i/HFilter16i`（及 `8i`）处理宏块内部边界，均在当前宏块行内。
- `FilterRow`（L1016–1078）在 `_out*` 平面上原位滤波；上方行已是上一行滤波结果，
  与 libwebp 的 `cache_y` 语义等价。
- 结论：**输出平面必须保留上一宏块行**；重建平面只需保留上一行（预测用）。

### 2.4 `WebpRowSource` 现有契约

`src/Formats/Webp/WebpRowSource.cs`：

- 内部实现 `IRowImageSource`；公开成员 `Width/Height/Channels/HasAlpha/ReadRow/Dispose`；
  `IsSupported(path)` 为内部静态。
- `ReadRow`（L103–167）：锁内检查 `_disposed`/行号/缓冲长度（不足抛 `ArgumentException`）；
  VP8L 走 `Vp8LRowDecoder.TryReadArgbRow`，VP8 走 `_rgba` 切片；读完返回 `false`；`_nextRow++`。
- `ParseStaticImage`（L173–311）：
  - VP8L 分支（L260–280）直接构造 `Vp8LRowDecoder`；
  - **VP8 分支（L282–299）当前是 `CopyChunkToCapture` + `CopyRemaining` + `WebpDecoder.DecodeRgba(full)`**，
    即整帧回退；
  - ALPH 数据被写入 capture，供整帧解码读取；VP8X 画布与图像尺寸不一致抛
    `WebpBitstreamException`；ANIM/ANMF/动画标志抛 `WebpUnsupportedException`。
- 构造期异常包装：`WebpUnsupportedException` 透传；其余容器/位流/参数异常包装
  `InvalidDataException`，并释放自有流。
- `Dispose`（L394–402）在锁内置 `_disposed`，幂等，只释放自有流。

### 2.5 alpha 与动图

- `AlphDecoder.Decode`（L27–78）返回 `byte[width*height]`（1 B/px），
  但 `CompressionVp8L` 分支内部调用 `Vp8LDecoder.DecodeBareStream` 得到**整幅 `uint[]`（4 B/px）**，
  然后取 GREEN 字节；`ApplyInverseFilter`（L83–155）按行做 horizontal/vertical/gradient 逆滤波。
- `WebpDecoder` 的 alpha 合并只**覆盖 A 字节**（`WebpDecoder.cs` L387–394），不做预乘；
  动图合成另有 `BlendPixelNonPremult`（L548 起），与静态行源无关。
- 因此流式路径只需按行取得 alpha 并写 A，RGB 不受 alpha 影响。

## 3. 架构与状态机

### 3.1 组件划分

| 组件 | 文件 | 职责 |
|---|---|---|
| `Vp8Decoder`（改造） | `src/Formats/Webp/Vp8Decoder.cs` | 头解析、宏块解码、重建、滤波。新增"窗口模式"平面布局与"固定当前行基址"；静态 `Decode` 仍走整帧布局 |
| `Vp8RowDecoder`（新增，internal） | `src/Formats/Webp/Vp8RowDecoder.cs` | 行级状态机：`Init / DecodeNextMacroblockRow / EmitReadyRows`、行对 RGB 暂存、行交付 |
| `Vp8YuvToRgb`（小改） | `src/Formats/Webp/Vp8YuvToRgb.cs` | 暴露 internal 的 `ConvertLinePair` 包装（复用现有私有 `UpsampleRgbaLinePair`，不改算法） |
| `AlphRowSource`（新增，internal，P3） | `src/Formats/Webp/AlphRowSource.cs` | ALPH 逐行解码 + 逐行逆滤波；压缩=VP8L 时复用 `Vp8LRowDecoder` 核心 |
| `WebpRowSource`（改造） | `src/Formats/Webp/WebpRowSource.cs` | VP8 分支改为构造 `Vp8RowDecoder`；按行合并 alpha；生命周期不变 |

`Vp8Decoder` 不新增公开 API；`Vp8RowDecoder` 与 `AlphRowSource` 均为 `internal`。

### 3.2 状态机与推进边界

```
S0 Init
  ├─ 解析帧标签/关键帧图像头/profile/partition0 长度
  ├─ 解析分段/滤波/分区/量化/概率头
  ├─ 分配窗口、初始化边界、分配逐行上下文
  └─ 不预解任何宏块行
S1 RowLoop（对 mbY = 0..mbH-1）
  ├─ ParseIntraModeRow(mbY)（partition0）
  ├─ 对 mbX = 0..mbW-1：DecodeMacroblock(tokenBr, mbX, mbY)
  ├─ 清左侧上下文
  ├─ CopyRowToOutput()：重建当前行 → 输出窗口当前半区
  ├─ FilterRow(mbY)：H 边界 + 内部边界 + V 上边界（沿用现有调用顺序）
  ├─ finalizedThrough = 16*mbY - 1（亮度行，见 §3.5）
  ├─ EmitReadyRows()：把已最终确定的行对转换/交付
  └─ Slide：重建窗口上一行 ← 当前最后一行；输出窗口当前半区 ← 上一半区
S2 Flush
  ├─ 若还有未交付行（末宏块行 + 偶数高镜像行），转换并交付
  └─ HasMoreRows = false
S3 Done      ReadRow 返回 false
S4 Failed    首个异常被缓存；后续 ReadRow 继续抛同一 InvalidDataException
```

推进边界：

- `DecodeNextMacroblockRow()` 只能在 `S0/S1` 调用；每调用一次恰好解码一个宏块行并完成滤波。
- `EmitReadyRows()` 只在 `S1` 的滤波之后调用；不得早于 `FilterRow`，否则会交付未最终确定的行。
- `Flush()` 等价于 `EmitReadyRows(final=true)`，只允许在 `mbY == mbH` 后调用一次。
- 只有当某一输出行 `r` 已"最终"且它所在的行对两行都可用时，才允许 `ReadRow` 返回该行；
  否则先内部推进 `DecodeNextMacroblockRow()`。

### 3.3 重建窗口：17 行 / 9 行，为什么

```
Y  窗口: 17 × _strideReconY ( = Pad*2 + mbW*16 )
  row 0        上一宏块行的最后一行（mbY==0 时整行填 127）
  row 1..16    当前宏块行（绝对 mbY）的 16 行重建
UV 窗口:  9 × _strideReconUv ( = Pad*2 + mbW*8 )
  row 0        上一宏块行最后一行色度
  row 1..8     当前宏块行
```

- 为什么是"1 行"而不是"16 行"：`Vp8IntraPred.Pred16/Pred4/Pred8` 的所有模式最多读取
  **上方 1 行**（`off - stride`，含 `off - stride - 1` 的 top-left 与 `off - stride + 16..19`
  的 i4x4 top-right）；`PrepareTopRight` 也只读这 1 行。滤波在输出平面进行，不读重建窗口。
- 为什么仍要 `Pad*2` 列：i4x4 的 top-right 写回/读取落在右侧 padding（`+16..+19`）；
  左边界 129 位于 `Pad-1` 列。
- 初始化：
  - `mbY==0`：row 0 整行（含 padding）填 127；`PrepareTopRight` 的 `mbY==0` 分支继续用显式 127。
  - 左边界：窗口所有行（Y 与 UV）的 `Pad-1` 列填 129；重建不写该列，滑行复制会保留它。
- 滑动（每宏块行结束一次）：
  - `Array.Copy(_reconY, (1+15)*_strideReconY, _reconY, 0, _strideReconY)`；
  - UV 同理把第 8 行复制到第 0 行；
  - **必须复制整行 stride**（含左右 padding），保证 `PrepareTopRight` 的右 padding 语义与
    左边界 129 延续；复制量 = `S_y + 2*S_uv` 字节/宏块行（1080p 约 3.9 KB，整帧累计约 0.26 MB）。
- 保守回退：若对拍发现任何读取超出上方 1 行，改用 32/16 行窗口（当前+上一完整宏块行），
  代码结构不变，仅分配高度与滑行复制量变化（内存见 §5.3）。

### 3.4 输出窗口：2 个宏块行

```
Y  窗口: 32 × _strideOutY，row 0..15 上一宏块行（已滤波），row 16..31 当前
UV 窗口: 16 × _strideOutUv，row 0..7  上一，row 8..15 当前
```

- 需要上一完整宏块行：
  1. `VFilter16/8` 在滤波当前行上边界时要读取并改写上一行最后 4 行；
  2. `EmitReadyRows` 在本次滤波后交付**上一整宏块行**，需要其全部 16/8 行。
- 当前行基址固定：`_curOutYBase = 16*_strideOutY`、`_curOutUvBase = 8*_strideOutUv`；
  `FilterRow` 内 `mbY>0` 判断继续使用**绝对 mbY**，`mbX>0` 不变。
- 每行结束后把当前半区复制到上一半区（16 行 Y + 8 行 U + 8 行 V，整 stride）；
  复制量 1080p 约 46 KB/宏块行，整帧约 3.1 MB，相对解码成本可忽略。

### 3.5 滤波对输出的延迟模型

由 §2.3 的调用顺序可得：

- `FilterRow(k)` 中，`VFilter*` 的**上边界**（绝对行 `16k`）改写上方最多 4 行，
  即亮度行 `16k-4 .. 16k-1`；色度行 `8k-4 .. 8k-1`。
- 因此 `FilterRow(k)` 返回后：
  - 亮度 `[0, 16k-1]` 已收到**所有会触碰它们的滤波**（下一行的上边界从 `16k+12` 起）；
  - 色度 `[0, 8k-1]` 同理；当前行首色度行 `8k` 已收到上边界下方侧的滤波。
- 发射策略（保守、与实现解耦）：
  - `finalizedLumaThrough = 16*mbY - 1`，`finalizedChromaThrough = 8*mbY - 1`；
  - 亮度行 `r` 的 fancy upsampling 需要色度行 `uTop=(r-1)/2`、`uCur=uTop+1`；
    `r = 16*mbY-1` 需要 `uCur = 8*mbY`，该行在本次 `FilterRow(mbY)` 后已最终，因此可交付。
- **行对约束**：输出必须按行对转换（§4.1）。`r = 16*mbY-1` 与 `r+1 = 16*mbY` 属于同一行对，
  后者要等 `FilterRow(mbY+1)` 才最终，所以**实际延迟再多 1 行**：
  - `FilterRow(mbY)` 后可交付的行对终点为 `16*mbY-2`（偶数）；
  - 奇数行 `16*mbY-1` 延后到下一次滤波后交付。
- 量化：**最大延迟 = 1 个宏块行 + 1 亮度行（≤17 行）+ 当前行解码时间**。
  这就是环形窗口只需要 2 个输出宏块行、且行源必须按宏块行推进的原因。
- 不再尝试"4 行精确足迹"的激进发射：它依赖每个滤波函数的精确写入范围（simple/complex、
  亮度/色度不同），收益只是少 12 行延迟，风险不成比例。

## 4. 输出路径

### 4.1 逐行 YUV→RGB 的时机与一致性

- 时机：只在 `FilterRow` 完成、且目标行对两行都最终后转换；转换使用**输出窗口**（已滤波）的
  Y/U/V，不读重建窗口。
- 一致性保证：复用现有 `Vp8YuvToRgb.UpsampleRgbaLinePair`（L249–404）与 `ChunkPair16/ChunkSingle16`，
  以与整图 `ConvertToRgba`（L440–498）**完全相同的 top/bottom 行与 UV 行映射**成对转换，
  因此逐位一致，无需重写 SIMD。
- 新增 internal 包装（不改私有函数体）：

```csharp
internal static void ConvertLinePair(
    byte[] y, int topYOff, int bottomYOff,
    byte[] u, byte[] v, int topUvOff, int curUvOff,
    byte[] dst, int topDstOff, int bottomDstOff, int len)
```

- 暂存：`_rgbaPair = new byte[2 * width * 4]`，`topDstOff=0`、`bottomDstOff=width*4`。
  一次转换产出两行，`ReadRow` 从暂存按行交付；总转换工作量与整帧路径相同（不是 2 倍）。
- 行映射（与 `ConvertToRgba` 的分支逐条对齐）：

| 输出行 `r` | 处理 | luma `topYOff` | `topUvOff` | `curUvOff` | bottom |
|---|---|---|---|---|---|
| `r = 0` | 首行镜像 | 0 | 0 | 0 | `-1` |
| `r` 奇数且非"偶数高末行" | 行对上半 | `r` | `(r-1)/2` | `(r-1)/2 + 1` | `r+1` |
| `r` 偶数且 `r>=2` | 行对下半 | `r-1` | `r/2 - 1` | `r/2` | 与上半同对 |
| `height` 偶数且 `r = height-1` | 末行镜像 | `height-1` | `chromaH-1` | `chromaH-1` | `-1` |

  - `height` 为奇数时最后一行是偶数行，由 `(height-2, height-1)` 行对覆盖，无需镜像。
  - `height == 1` 只走首行；`height == 2` 走首行 + 末行镜像。
- 窗口偏移换算（`FilterRow(mbY)` 之后：上一半区 = `mbY-1`，当前半区 = `mbY`）：

```text
LumaOff(r)   = r < 16*mbY ? (r - 16*(mbY-1))*S_outY : (16 + r - 16*mbY)*S_outY
ChromaOff(c) = c < 8*mbY  ? (c - 8*(mbY-1))*S_outUv : (8 + c - 8*mbY)*S_outUv
```

  - `Flush` 时所有剩余行都在当前半区，直接用 `r - 16*(mbH-1)` / `c - 8*(mbH-1)`。
  - 行对可能跨越两个半区（`16mbY-1` 在上一半区、`16mbY` 在当前半区），上表公式已覆盖。

### 4.2 stride 与缓冲区衔接

- 输出平面 stride 为 `mbW*16` / `mbW*8`（宏块网格，无 padding）；窗口每行 stride 不变，
  只是行基址不同。目标行只写有效宽度 `width`，`len = width`。
- `WebpRowSource.ReadRow` 保持"行缓冲 ≥ `Width*Channels`，不足抛 `ArgumentException`"。
- `Vp8RowDecoder.TryReadRgbaRow(Span<byte> dst)` 要求 `dst.Length >= Width*4`，写 RGBA（A=255）。
  `WebpRowSource` 若 `Channels==3` 再剥掉 A（与现有 VP8L 路径的转换循环一致），不新增分配。
- 暂存与窗口都在构造期一次性分配；`ReadRow` 稳态零堆分配（验收见 §9.4）。

### 4.3 alpha 合并

- 语义与 `WebpDecoder.ApplyAlphaChunk` 一致：**只覆盖 A 字节**，RGB 不变（非预乘）。
- P2（先可用）：`WebpRowSource` 用现有 `AlphDecoder.Decode` 得到整幅 1 B/px alpha，
  按行取用；仅对中小图满足内存验收（1080p 约 2 MB，4K 约 8.3 MB）。
- P3（大图达标）：接入 `AlphRowSource`，逐行产出 alpha：
  - `compression=0`：直接按行拷贝负载，1 B/px 行缓冲；
  - `compression=1`：复用 `Vp8LRowDecoder` 的 VP8L 裸流核心，每行取 GREEN 字节；
  - 逆滤波逐行执行：horizontal 需要上一行首像素；vertical 需要上一行；gradient 需要上一行与左上，
    状态只有"上一行 alpha（`width` 字节）" + 当前行。
  - 内存 = VP8L 环形历史（约 4.2 MB）+ 2 行 alpha，与高度无关。
- alpha 是否启用沿用现有规则：仅当存在 VP8X 且其 Alpha 标志置位（`hasAlpha`）时合并；
  与当前 `WebpDecoder.DecodeRgba` + `hasAlpha` 的输出一致。

### 4.4 动图

- `WebpRowSource` 继续在 VP8X 动画标志、ANIM、ANMF 处抛 `WebpUnsupportedException`；
  `WebpDecoder` 的动图帧合成（画布、blend/dispose）完全不改。
- 静态有损 + ALPH 与动图的 alpha 合成互不影响。

## 5. 内存

### 5.1 公式

记 `mbW = ceil(width/16)`、`mbH = ceil(height/16)`：

| 项 | 公式（字节） |
|---|---|
| 重建 Y（17 行） | `(Pad*2 + 16*mbW) * 17` |
| 重建 U + V（各 9 行） | `2 * (Pad*2 + 8*mbW) * 9` |
| 输出 Y（32 行） | `16*mbW * 32` |
| 输出 U + V（各 16 行） | `2 * (8*mbW) * 16` |
| RGB 行对暂存 | `2 * width * 4` |
| 逐行上下文 | 约 `41 * mbW` + 数组头 |
| 压缩载荷（不变项） | VP8 chunk 长度（+ ALPH chunk 长度） |
| alpha（P2 / P3） | `width*height`（1 B/px，另有 VP8L 4 B/px 瞬时峰值）/ 约 4.2 MB 环形 + 2 行 |

合计（不含载荷与 alpha）：

`R = (16 + 16mbW)*17 + 2*(16 + 8mbW)*9`
`O = 768*mbW`
`Total ≈ R + O + 128*mbW (暂存) + 41*mbW`

### 5.2 典型/最坏值（Pad=8）

| 图像 | mbW×mbH | 重建窗口 | 输出窗口 | 暂存+上下文 | 合计 | 整帧路径平面 | 占比 |
|---|---|---|---|---|---|---|---|
| 1920×1080 | 120×68 | 50,480 B | 92,160 B | 20,280 B | **约 163 KB** | 6,363,904 B（6.36 MB） | 2.6% |
| 3840×2160 | 240×135 | 100,400 B | 184,320 B | 40,560 B | **约 325 KB** | 25,075,968 B（25.1 MB） | 1.3% |
| 4096×4096 | 256×256 | 107,056 B | 196,608 B | 43,264 B | **约 347 KB** | 50,594,560 B（50.6 MB） | 0.7% |
| 16383×16383（VP8 最大） | 1024×1024 | 426,544 B | 786,432 B | 173,056 B | **约 1.39 MB** | 806,355,712 B（806 MB），另有整帧 RGBA 1.07 GB | 0.17% |

- 最大合法 VP8 尺寸就是 16383（14 位宽高），`mbW=mbH=1024`；1.39 MB 与高度无关。
- 若 §3.3 的回退触发（32/16 行重建窗口），上表重建项变为：
  1080p 93,184 B、4K 185,344 B、16383² 787,456 B，合计分别约 206 KB / 410 KB / 1.75 MB，
  结论不变。

### 5.3 与整帧路径对比

- 整帧路径常驻：重建 3 平面 + 输出 3 平面（`Vp8Decoder`）+ 整帧 RGBA（`WebpRowSource` 的 `_rgba`）。
  16383² 峰值可达约 1.9 GB 量级；行级后为 **1.39 MB + VP8 载荷**。
- 既有的 `20000×64` 长图（VP8）：
  - 现状（整帧 RGBA 5.12 MB + 两套 YUV 4.48 MB）约 9.6 MB；
  - 行级：重建 520,560 B + 输出 960,000 B + 暂存 160,000 B + 上下文 51,250 B ≈ **1.69 MB**（约 17.6%）；
  - `64×20000` 约 **6 KB**，相对现状 9.6 MB 下降 >99.9%。
  - 宽而矮的图像是内存占比最不利的形态（窗口随宽度增长、整帧只随高度增长），
    因此 §9.3 把 `20000×64` 作为硬性验收用例。

### 5.4 压缩载荷与 alpha 的边界

- VP8 partition0 与 token 分区仍**整 chunk 一次读入**（`Vp8BoolDecoder` 需要随机访问）；
  典型 1080p 数百 KB、4K 约 1–2 MB、16383² 可达数十 MB。它远小于整帧平面/RGBA，
  但会出现在进程私有字节中，报告中需分开列出。
- 若未来要求进一步压低载荷内存，需把 bool 解码器改为按分区的按需喂字节；
  这会改变 `Vp8BoolDecoder` 的边界条件语义，**不在本轮**。
- alpha：P2 的 1 B/px 整幅在 4K 为 8.3 MB、16383² 为 268 MB，且 `DecodeBareStream`
  还有 4 B/px 瞬时峰值，因此 **P3 是 4K/大图 alpha 的强制阶段**，不是可选优化。

## 6. 接口与兼容

### 6.1 内部签名

```csharp
internal sealed class Vp8RowDecoder : IDisposable
{
    internal Vp8RowDecoder(byte[] payload);              // 只解析头部 + 分配窗口
    internal int Width { get; }
    internal int Height { get; }
    internal bool HasMoreRows { get; }
    internal bool TryReadRgbaRow(Span<byte> dst);        // width*4，A=255
    internal void Dispose();                             // 幂等，释放窗口与载荷引用
}
```

`Vp8Decoder` 内部新增（不新增公开成员）：

```csharp
private void InitWindowed();                    // 窗口分配/边界初始化
private void DecodeNextMacroblockRow();         // S1 的一次推进
private void CopyRowToOutput();                 // 以 _curOut*Base 为当前行
private void FilterRow(int mbY);                // 逻辑不变，偏移改用 _curOut*Base
private void SlideWindows();                    // §3.3/§3.4 的两次行复制
```

关键改动点（仅偏移）：

- `ReconstructMacroblock` / `PrepareTopRight` 的 `yOff/uvOff`
  由 `(Pad + mbY*16)*stride + ...` 改为 `_curReconYBase + Pad + mbX*16`；
  整帧模式把 `_curReconYBase` 设为 `(Pad + mbY*16)*stride`，**行为完全不变**。
- `CopyRowToOutput` 的源基址同理改用 `_curRecon*Base + Pad`，目标基址用 `_curOut*Base`。
- `FilterRow` 的 `yBase/uvBase` 改用 `_curOutYBase/_curOutUvBase`。
- `CheckMode(mbX, mbY, ...)`、分区选择 `mbY & mask`、`_mbNz` 跨行上下文继续使用**绝对 mbY**。

### 6.2 公开 API

- `Vp8Decoder.Decode(ReadOnlySpan<byte>)` 签名/返回值不变；整帧测试全部保留。
- `WebpRowSource` 的构造函数、`Width/Height/Channels/HasAlpha/ReadRow/Dispose/IsSupported`
  不变；不新增公开类型。
- `IRowImageSource`、JPEG 与其他格式零改动。

### 6.3 RowSource 生命周期

- 构造：顺序读到 VP8 chunk，读满该 chunk（含 padding），构造 `Vp8RowDecoder`；
  **不再** `CopyRemaining` 读整个文件尾部。非 seekable 流同样适用。
- 提前 `Dispose`（未读完）：允许，无异常、不做收尾/不校验剩余行；释放自有流与解码器引用。
- 部分消费：调用方可在任意行停止；再次 `ReadRow` 从停止处继续；读完返回 `false`。
- `Dispose` 后调用 `ReadRow`：`ObjectDisposedException`（与现状一致）。
- 行为差异（需在 CHANGELOG 注明）：
  1. VP8 的尾部 chunk（EXIF/XMP 等）不再被消费；不影响像素与元数据 API（元数据由独立容器 API 读取）。
  2. 某些损坏位流（宏块/token 层）的错误从构造函数推迟到首次/中间 `ReadRow`，仍包装为
     `InvalidDataException`。

### 6.4 兼容性判据

- 现有 `WebpStreamingDecodeTests`、VP8 解码 golden/对拍测试、全量测试全部通过。
- `WebpRowSource` 对 VP8L 的行为保持不变（同一容器骨架与锁/游标逻辑）。

## 7. 异常与并发

### 7.1 短读 / EOF / 截断

- 构造期（唯一 I/O 阶段）：
  - RIFF 头短读 → `WebpContainerException` → `InvalidDataException`；
  - chunk 头不足 8 字节、chunk 数据不足、RIFF 声明长度不符 → 同上；
  - VP8 帧头/profile/关键帧/起始码非法 → `WebpBitstreamException` 或 `WebpUnsupportedException`
    （后者透传），包装规则沿用现有 `ParseStaticImage` 的 catch 块。
- 行解码期（纯 CPU，无 I/O）：
  - `_partition0.Eof`（intra 模式行截断）与 `tokenBr.Eof`（token 分区截断）在每宏块行/宏块处检查，
    抛 `WebpBitstreamException`；
  - `Vp8RowDecoder` 在 `WebpRowSource` 内统一包装为 `InvalidDataException("无效或不完整的 WebP 数据流", ex)`。
- 与 VP8L 行流式不同，VP8 没有"跨行保存位流快照"的需要：bool 解码器状态天然驻留在实例中，
  分区字节全在内存。

### 7.2 已产出行的语义

- `ReadRow` 返回 `true` 即表示该行**已最终确定且不可撤回**；之后任何解码错误都不影响已返回的行。
- 错误发生后：行源进入 `S4 Failed`，当前及后续 `ReadRow` 抛第一个异常（包装后），不再产出新行。
- 构造期完成的 VP8X 画布/尺寸校验不变；`Width/Height` 在构造期即确定，错误不会发生在"尺寸未知"状态。

### 7.3 并发

- 每个 `WebpRowSource`/`Vp8RowDecoder` 实例私有窗口与上下文，单实例由 `_lock` 串行化。
- 共享只读：`Vp8Tables`、概率默认表、`Vp8LoopFilter` 的静态钳制表（CLR 保证静态构造线程安全）、
  `Vp8YuvToRgb` 的静态向量常量、`Vp8IntraPred/Vp8Idct` 纯函数。
- 禁止跨实例共享 `byte[] payload`（构造期传入的数组由行源持有；调用方若复用需自行确保不再修改）。

## 8. 与 VP8L 行流式的差异与可复用部分

| 维度 | VP8L（已落地 4A） | VP8（本方案 4B） |
|---|---|---|
| 依赖结构 | LZ77 反向引用 + predictor 上一行 | 仅帧内：上 1 行 + 左邻居 + 上一宏块行滤波 |
| 常驻内存 | 约 4.2 MB 环形历史 + 2 行 | 约 1.39 MB 窗口（16383²）+ 行对暂存 |
| 跨调用状态 | `LsbBitReaderSnapshot` 保存/恢复位流位置 | bool 解码器与分区字节常驻；按宏块行顺序推进，无需快照 |
| 推进单位 | 输出行（逐像素/逐行） | 宏块行（16 亮度行）+ 行对交付 |
| 输出延迟 | 0 行 | 1 宏块行 + 1 行（滤波/行对） |
| 主要风险 | 逆变换顺序、拷贝距离边界 | 环内滤波跨行足迹、行对映射、窗口 padding |
| 可复用 | `WebpRowSource` 容器骨架、行游标/锁/Dispose、`CountingStream`/内存测量、`Vp8LRowDecoder` 环形历史状态机（用于 ALPH） | 同左 + `Vp8LoopFilter`、`Vp8YuvToRgb`（行对内核）、`Vp8IntraPred`、`Vp8Idct`、`Vp8Decoder` 的解析/重建函数 |

复用原则：**只复用不改变语义的函数**（滤波、预测、IDCT、YUV→RGB 行对、容器读取）。
VP8L 的行状态机不能直接用于 VP8（依赖模型不同），但 ALPH（压缩=VP8L）可以复用它。

## 9. 测试与验收

### 9.1 逐像素对拍矩阵

基准：同一输入分别走
`Vp8Decoder.Decode` + `Vp8YuvToRgb.ConvertToRgba` + `AlphDecoder.Decode`（整帧）与
`Vp8RowDecoder`（流式），要求 **RGBA 全通道差值 = 0**（无 ≤1 容差）。

| 维度 | 取值 |
|---|---|
| 尺寸 | 1×1、1×3、3×1、15×17、17×15、16×16、17×16、16×17、17×17、31×33、33×31、100×37、1920×1080、3840×2160（内存允许时）、16383×1 |
| 宽高奇偶 | 奇数宽/奇数高、奇数宽/偶数高、偶数宽/奇数高、偶数宽/偶数高 |
| 质量 | q ∈ {10, 30, 50, 75, 95} |
| 滤波 | simple / complex；level=0 与高 level；sharpness 由测试码流覆盖 |
| 宏块形态 | mbW=1、mbH=1、mbW=1&mbH>1、mbH=1&mbW>1；首/末宏块；i4x4 与 i16x16；skip 宏块 |
| 分区 | num_parts = 1、2、4、8 |
| alpha | 无 ALPH；ALPH filter=none/horizontal/vertical/gradient；compression=none/VP8L；preprocessing 0/1 |
| 容器 | 简单 VP8、VP8+ALPH、VP8X+ALPH+VP8；ALPH 在 VP8 前/后 |

### 9.2 loop filter 与边界专项

- `mbH=1`（无 VFilter）与 `mbW=1`（无 HFilter）。
- 最后 4 行跨边界改写：构造边缘强梯度图（竖直条纹 / 单像素线），强制高 `filter_level`。
- 奇数高/宽时的裁剪：末宏块行/列部分无效像素参与滤波但不得进入输出。
- 行对边界：交付行 `16k-1` 与 `16k` 必须来自同一次转换，不得使用未滤波的 `16k`。
- 1×1、1×2、2×1、2×2：验证首行/末行镜像与无 VFilter/VFilter 分支。

### 9.3 内存测量方法

- 同进程、同顺序对比：先测量现状整帧路径峰值，再测量流式路径；每 8 行采样
  `GC.GetTotalMemory(false)`，同时记录进程私有字节；重复 5 次取中位。
- 用例：`4096×4096`（无 alpha）、`1920×1080`、`3840×2160`、`20000×64`、`64×20000`；
  alpha 用例：`3840×2160` + ALPH（P3 后）。
- 0 分配验证：`GC.GetAllocatedBytesForCurrentThread()` 在第一次 `ReadRow` 前与最后一行后取样，
  稳态 `ReadRow` 循环的总分配量 ≤ 32 KB（不含构造期）。

### 9.4 性能基线

- 用 `.perf` 的 `AssemblyLoadContext` 方式加载改动前的 `SharpImageConverter.dll`，
  同进程交替跑 1080p/4K q75 的"读完整图"计时（3 次取中位）。
- 记录 rows/s 与总耗时；同时记录峰值内存，避免只比较 CPU。

### 9.5 量化阈值与回退条件

| 项 | 通过阈值 | 不通过的处理 |
|---|---|---|
| 逐像素一致 | 全矩阵 diff = 0 | 任一 diff ≠ 0：**不接线**，回退 `WebpRowSource` VP8 分支为整帧路径，报告差异样本 |
| 内存：4096² 无 alpha | 峰值增量 ≤ 整帧路径 5%（硬上限 20%，符合 spec §2.3） | 检查窗口/暂存/上下文；仍 >20% 则回退 |
| 内存：16383² 无 alpha | 平面窗口合计 ≤ 2 MB | 回退到 32/16 行窗口仍超限则放弃 4B |
| 内存：20000×64 / 64×20000 | 相对现状峰值下降 ≥ 80% | 见 §5.3；不达标先检查输出窗口是否可缩为 20/12 行，再回退 |
| 内存：4K alpha | 额外 ≤ 4.5 MB（P3 后） | P3 未完成则 4K alpha 不纳入 4B 交付，报告说明 |
| 稳态分配 | 每图 ≤ 32 KB（`ReadRow` 阶段） | 去除任何逐行 `new`；不达标回退 |
| 吞吐 | ≥ 现状 0.8×（1080p/4K q75） | 优化滑行复制/行对暂存；低于 0.7× 回退 |
| 既有测试 | WebP 过滤测试与全量测试全绿、0 新警告 | 任一失败即停止并回退 |

**整体回退方式**：`Vp8Decoder` 的窗口模式与新增 `Vp8RowDecoder` 不接线；
`WebpRowSource` VP8 分支恢复为 `CopyChunkToCapture + CopyRemaining + WebpDecoder.DecodeRgba`。
不保留"半截"实现。

## 10. 分阶段实施计划

| 阶段 | 产出 | 可独立验证的判据 | 风险/备注 |
|---|---|---|---|
| **P0** 核心窗口化（不改行为） | `Vp8Decoder` 新增窗口布局与固定基址：`_curRecon*Base/_curOut*Base`、`InitWindowed`、`DecodeNextMacroblockRow`、`SlideWindows`；整帧模式仍走旧路径 | 现有所有 WebP 测试全绿；临时测试用同一码流分别跑整帧/窗口模式，比较重建/输出平面逐字节一致 | 偏移改写要覆盖 `ReconstructMacroblock/PrepareTopRight/CopyRowToOutput/FilterRow` 全部绝对 `mbY` 依赖；`CheckMode` 保持绝对 mbY |
| **P1** 行级状态机（YUV） | 新增 `Vp8RowDecoder`：`Init/DecodeNextMacroblockRow/EmitReadyRows/Flush`；`Vp8YuvToRgb.ConvertLinePair` 包装；行对暂存 | 不接 `WebpRowSource`，测试直接驱动行源，9.1 矩阵（无 alpha）diff = 0；含 1×1/奇数/末行镜像 | 滤波延迟与跨半区偏移是主要风险；行对映射必须与 `ConvertToRgba` 逐分支一致 |
| **P2** 接线 RowSource（无 alpha / 小图 alpha） | `WebpRowSource` VP8 分支改构造 `Vp8RowDecoder`；ALPH 暂用 `AlphDecoder.Decode` 整幅 1 B/px；alpha 按行覆盖 A | 流式测试与整帧对拍全绿；4096²/长图内存达标；1080p alpha 达标 | 构造期不再 `CopyRemaining`，需更新"尾部截断"相关测试预期；错误从构造期后移到 `ReadRow` |
| **P3** ALPH 行增量 | 新增 `AlphRowSource`；从 `Vp8LDecoder` 拆出裸流状态初始化，复用 `Vp8LRowDecoder`；逐行逆滤波 | alpha 全矩阵 diff = 0；4K alpha 额外内存 ≤4.5 MB；16383² alpha 不触发整幅分配 | 需要动 `Vp8LDecoder` 的裸流入口（本次最大重构点之一）；不能改变 VP8L 既有行为 |
| **P4** 性能与收尾 | 消除可避免的复制/分配，更新 `docs/WebpEncoder.md`、`docs/PerfReport.md`、`CHANGELOG.md`；跑全量 | 吞吐 ≥0.8×；稳态分配达标；全量测试全绿；报告给出所有实测字节/耗时 | 若吞吐不足，优先检查行对暂存与滑行复制，不改变解码语义 |

## 11. 风险与回退（按严重度）

1. **环内滤波的跨行足迹与发射时机不一致**（最高风险）
   - 现象：上一宏块行最后 4 行（亮度/色度）在下一行上边界滤波中被改写；若提前交付会出现
     单行/数行像素差，且只在特定图/quality 出现。
   - 控制：保守的"整宏块行"延迟；9.1/9.2 的边缘强梯度专项；任何 diff ≠ 0 即整体回退。
2. **ALPH 大图内存**（高）
   - 现象：`DecodeBareStream` 的 4 B/px 瞬时峰值使 4K/16383² 无法达标。
   - 控制：P3 强制；否则 4B 交付范围限定为"无 alpha 或 ≤1080p alpha"，并在报告写明。
3. **行对映射/首末行镜像与整图分叉**（中高）
   - 现象：`height` 为偶数末行镜像、`height==1/2`、奇数高末行、行对跨窗口半区。
   - 控制：行对转换直接复用 `UpsampleRgbaLinePair`；1×1、1×2、2×1、2×2、奇数高专项对拍。
4. **窗口 padding 语义漂移**（中）
   - 现象：`PrepareTopRight` 写回上一行右 padding、左边界 129、滑行复制只复制部分 stride。
   - 控制：滑行复制整 stride；P0 平面级逐字节对拍先于 RGB 对拍。
5. **载荷内存不可压缩**（低，已知）
   - 现象：VP8 chunk 全缓冲；超大图载荷数十 MB。
   - 控制：报告中与平面窗口分开列出；不作为 4B 的失败条件。

## 12. 实施伪代码（供 P0–P2 对照）

```text
Vp8RowDecoder.TryReadRgbaRow(dst):
  if failed: throw cached
  while !stagingHas(nextOutRow):
      if !DecodeNextMacroblockRow():
          Flush()                       // 只允许一次
          break
  CopyStagingRowTo(dst)                 // 剥/留 A 由 WebpRowSource 处理
  nextOutRow++
  return nextOutRow <= Height

DecodeNextMacroblockRow():
  mbY = nextMbY
  ParseIntraModeRow(mbY)
  for mbX in 0..mbW-1: DecodeMacroblock(tokenBr[mbY & mask], mbX, mbY)
  reset left contexts
  CopyRowToOutput()                     // 当前窗口基址
  FilterRow(mbY)                        // 绝对 mbY 判断 mbY>0 / mbX>0
  EmitReadyRows(through = 16*mbY-2)      // 行对终点，奇数尾行延后
  SlideWindows()                        // recon 最后一行→row0；out 当前半区→上一半区
  nextMbY++
  return nextMbY < mbH

EmitReadyRows(through):
  while nextOutRow <= min(through, Height-1):
      pair = PairFor(nextOutRow)        // 0 首行 / 奇数起点 / 偶数回退一行 / 末行镜像
      if pair.bottom >= Height: pair.bottom = -1
      ConvertLinePair(pair)             // 写 _rgbaPair 两行
      stagingRows = {pair.top, pair.bottom}
```
