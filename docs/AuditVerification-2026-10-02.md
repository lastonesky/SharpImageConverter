# SharpImageConverter SIMD / AVX 优化落地进度

> 来源：2026-10-02 全库 SIMD 审计（51 个源文件）。
> 本文档把审计结论重构为**可逐步落实的任务清单**，每条记录：位置、错误类型、**怎么改**、状态、实测结果。
> 文末「发现的问题与知识」记录过程中踩到的坑与可复用结论。
>
> 状态图例：`⬜ 待办` `🟨 进行中` `✅ 已完成` `⏭️ 暂缓（含原因）` `❌ 放弃`
>
> **基线**：构建 0 错误；测试 152 通过 / 0 失败。
> **当前**：构建 0 错误；测试 **161 通过 / 0 失败**（新增 9 项 golden 对拍测试）。

---

## 全局改动原则（所有任务通用）

1. **正确性优先**：每处 SIMD 改动必须有"SIMD 开 / SIMD 关"两条路径的逐字节对比，不接受"看起来差不多"。
2. **不改数值语义**：除非任务目标就是修正语义分歧（E 组），否则改动必须保持输出逐字节不变。
3. **门控阶梯统一**：`最宽 Vector<T> → Sse2/Ssse3 → AdvSimd → 标量`，**从最宽到最窄排列**（见 K6）。
4. **常量一律 `static readonly Vector128/256<T>` 或 `const int`**，不在循环体内 `Vector.Create`。
5. **不在向量内核内 `GetElement`**：取回标量再广播 = 向量→标量→向量往返，一律消除。

---

## 本次完成清单（速览）

| 任务 | 文件 | 性质 | 验证 |
|---|---|---|---|
| B1 | `Jpeg/SimdJpegEncodePipeline.cs` | FDCT 常量提升为静态向量（去掉 88 次/块广播 + 运行时取负） | 编码 golden 一致 |
| B2 | `Jpeg/SimdJpegPipeline.cs` | `static readonly int` → `const int`；`half`/`bias128` 提升 | 解码测试 |
| B4 | `Processing/Processing.cs` | `BicubicMaxSum/Round` 向量常量提升 | `ResizeConsistencyTests` |
| D3 | `Jpeg/SimdJpegPipeline.cs` | 删除死代码 `StoreRowSse2`、`ConvertRowYCbCrToRgb(Span)` | 编译 |
| E4 | `Png/Crc32.cs` | jagged `uint[8][256]` → 扁平 `uint[2048]`；字节序判断外提 | PNG 解码 CRC 校验 |
| C6 | `Bmp/BmpReader.cs`、`BmpWriter.cs` | 行填充除法 → 按位对齐 | `BmpPaddingTests` |
| C7 | `Webp/WebpAdapter.cs` | 复用 `SimdHelper.PackRgbaToRgb` / `ExpandRgbToRgba` | WebP 往返 |
| A1 | `Core/SimdHelper.cs` | `PackRgbaToRgb` 每块 3 次标量写 → 1 次 16 字节写 | `SimdPixelOpsTests` |
| A2 | `Jpeg/SimdJpegPipeline.cs` | RGB24 交织：3 次 GetElement + 12 次标量写 → 两次 `pshufb` + 2 次存储 | **新增对拍测试**（500 组随机 + 已知输入） |
| C1 | `Png/PngDecoder.cs` | Sub 滤波向量化：块内前缀和 + 跨块进位 | **新增对拍测试**（bpp 1–8 × 18 种长度） |
| C4 | `Png/Adler32.cs` | `psadbw` 求 s1、`pmaddwd` 求加权和 | **新增测试**（2 个标准向量 + 参考实现 × 17 种长度） |
| D1 | `Jpeg/JpegEncoder.cs` | 量化三档门控谓词统一 | `JpegEncoderSimdTests` |
| D2 | `Png/PngDecoder.cs`、`Core/SimdHelper.cs` | 门控阶梯改"从最宽到最窄"，`Vector<T>` 档不再不可达 | 161 测试 |
| E3 | `Gif/GifDecoder.cs` | 调色板展开越界读修正（读 16 字节 → 上界取 16 而非 8/10） | `GifDecodePathTests` |
| E1 | `Jpeg/JpegImage.cs` | 恒真重言式 → 显式 `true`（**不改行为**，见 K12） | `CmykJpegNoApp14Tests` |
| — | `Jpeg/JpegFrameState.cs` | 审计"死 using"**误报**，已回退（见 K11） | 编译 |

---

# 阶段 A —— 消除 SIMD→标量退化

### A1. `Core/SimdHelper.cs` `PackRgbaToRgb` ✅

- **错误类型**：SIMD 结果被标量逐元素后处理（一次 16 字节 shuffle 拆成 3 次标量 store）
- **怎么改**：主循环改为一次 `Vector128.StoreUnsafe` 写满 16 字节（高 4 字节是掩码置零的垃圾，落在本缓冲区内且被下一块覆盖）；**最后一块**没有"下一块"可覆盖，改用 `WriteUnaligned<ulong>` + `WriteUnaligned<uint>` 写真实 12 字节。
- **结果**：每块 3 次存储 → 1 次（末块 2 次）；尾部像素仍走标量（每调用一次）。
- **⚠️ 坑**：不能对所有块都写 16 字节——末块会越界 4 字节（见 K13）。

### A2. `Formats/Jpeg/SimdJpegPipeline.cs` `ConvertRowYCbCrToRgb` ✅

- **错误类型**：SIMD 结果被标量逐元素后处理（3 次 `GetElement` 拆向量 + 12 次手工展开标量写完成 RGB 交织）
- **怎么改**：抽出 `InterleaveRgb24`（并额外提供 `Span<byte>` 安全重载作为测试缝），用两条 `pshufb` 掩码 + `Or` 在向量域完成 RGB24 交织；`out0` 写 16 字节、`out1` 写 8 字节。门控同步加上 `Ssse3.IsSupported`（`JpegReconstruct.cs:91`）。
- **结果**：12 次标量写 → 2 次存储；3 次 `GetElement` 消除。
- **验证**：新增 `JpegRgbInterleaveTests`——500 组随机输入与"旧标量拼装"参考实现逐字节对拍 + 已知输入的通道顺序断言。**参考实现第一版写错了**（见 K14）。

### A3. `Formats/Gif/OctreeQuantizer.cs:615/759` ⏭️

- **错误类型**：SIMD 收益被标量完全抵消（16 路向量算出下标后 store 到 `stackalloc`，再 16 次标量随机 gather + 16 次单字节散写）
- **怎么改**：
  - A3-a（低风险，未做）：16 个结果字节先攒进 `Vector128<byte>`，末尾一次 16 字节 store，消除 16 次单字节散写。
  - A3-b：另建 `int[32768]` 副本，用 `Avx2.GatherVector256` 一次取 8 个，两轮覆盖 16 个下标，再 pack 成字节。
- **⏭️ 暂缓原因**：审计原文建议的"3 张 32 项小表 + `pshufb`"**不成立**（见 K1），重新设计需要 128 KB 额外表 + 按 `Avx2` 分档，改动面与内存代价都需要先权衡。

---

# 阶段 B —— 常量提升与分支规避

### B1. `Formats/Jpeg/SimdJpegEncodePipeline.cs:229-259` ✅

- **怎么改**：11 个 `Fix_*` 提为 `static readonly Vector128<int>`；负系数单独预置（去掉 `:244` 的运行时取负）。
- **结果**：与 `QuantBias`/`C2`/`CConst128` 策略统一；数值不变，仅传播方式变。

### B2. `Formats/Jpeg/SimdJpegPipeline.cs` ✅

- **怎么改**：`static readonly int Fix_*` → `const int`（与 `FastIDCT.cs` 一致，可参与编译期折叠）；`half` 两档提为 `HalfPass1/HalfPass2`，`bias128` 提为 `Bias128`。

### B3. `Formats/Jpeg/SimdJpegEncodePipeline.cs:325-326` ⏭️

- **错误类型**：冗余偏置往返（`Convert8Pixels` 内 `+128`，调用处再 `-128`）
- **怎么改/⏭️ 暂缓**：数学上 `((a+128)+(b+128)+2)>>2 - 128 == (a+b+2)>>2`（`+256` 是 4 的倍数），等价成立；但改动要同时动 `Convert8Pixels` 的输出契约与 420 的累加式，收益（每块 32 次向量加减）相对风险偏低，留待与 D 组一起做。

### B4. `Processing/Processing.cs` ✅

- **怎么改**：`BicubicMaxSum` / `BicubicRound` 提为 `BicubicMaxSum256/Round256`、`BicubicMaxSum128/Round128`，替换 3 处现场 `Vector.Create`。

### B5. `Processing/Processing.cs:1153-1156, 1203-1206, 1320-1328` ⏭️

- **错误类型**：向量→标量→向量往返（`wH.GetElement(i)` 取回再 `Vector.Create` 广播）
- **怎么改**：把权重瓦片布局改成"广播友好"的 `Vector128<int>[]` / `Vector256<int>[]`，内核零转换取用；`new Vector256<short>[width]` 改 `ArrayPool`。
- **⏭️ 暂缓原因**：需整段重写权重瓦片的构造与消费两端，且 4 份 bicubic 核心（`BicubicCoreVnniV/V2`、`BicubicCoreVnni/Vnni2`）尚未收敛，先收敛再改布局更划算。

### B6. `Processing/Processing.cs:691` 恒真分支 ⏭️

- **怎么改**：删除 `if (x + 1 < width)` 及其 else（据审计 `:620 simdEnd = width - 1` 使其恒真）。
- **⏭️ 暂缓原因**：该结论来自子代理分析，未逐行复核；删除死分支前必须确认 `simdEnd` 的所有修改点，否则会引入越界写。留待建立 resize 边界用例后再动。

### B7. `Formats/Jpeg/SimdJpegEncodePipeline.cs:233-242, 346-347, 368-387` ⏭️

- **怎么改**：`evenLeft` 是编译期可知的，拆成两个特化核心让 JIT 消除分支；`evenRow` 改"每两行一对"展开。
- **⏭️ 暂缓原因**：JIT 很可能已通过常量传播消除 `evenLeft`；收益需反汇编确认，不做无凭据的改动。

---

# 阶段 C —— 补标量热点向量化

### C1. `Formats/Png/PngDecoder.cs` `UnfilterSub` ✅

- **错误类型**：未向量化（同类 `UnfilterUp` 已有 SIMD，算法族内割裂）
- **怎么改**：Sub 是"步长 bpp 的加法链"，链内串行但 **bpp 条链互不依赖**——并行维度是"链"。
  块内用前缀和的对数步：`s += s << m`，m 依次取 `bpp, 2·bpp, 4·bpp…`（第 k 步后累加 2^k 个前驱），链长 `ceil(16/bpp)` 决定步数。
  跨块进位用 `ShiftRightLogical128BitLane(prev, 16-bpp)` 取上一块末 bpp 字节；因为前缀和是**线性算子**，进位直接加在起点一起参与累加即可。
  首块进位为零向量 ⇒ "首 bpp 字节左邻为 0" 的边界条件天然满足，不再需要单独的行首循环。
- **结果**：16 字节/块，1–4 次 `paddb` + 1–4 次 `pslldq` 取代 16 次标量迭代。
- **验证**：新增 `PngUnfilterTests`——bpp ∈ {1..8} × len ∈ {1,2,3,bpp,bpp+1,15,16,17,31,32,33,47,48,49,100,255,256,1000} 与规范定义逐字节对拍；另有回绕（非饱和）断言与跨块进位断言。
- **⚠️ 坑**：`pslldq`/`psrldq` 的移位量**必须是编译期常量**，因此按 bpp 展开成 `switch`，不能用循环变量（见 K7）。

### C2. `Png/PngDecoder.cs` `UnfilterAverage` ⏭️（设计完成，未落地）

- **怎么改**：`dst[i] = src[i] + ((dst[i-bpp] + prev[i]) >> 1)`。逐元素 `(a+b)>>1` 在 8 位会溢出，须用 `pavgb` 加修正：`avg = pavgb(a,b) - ((a^b) & 1)`（见 S2）。
  因每步都要截断，它**不是**线性前缀和，只能按链长迭代 `chainLen` 次；每次迭代 `a = Or(ShiftLeft(d, bpp), carryFirst)` 后重算。
- **⏭️ 暂缓原因**：`chainLen = ceil(16/bpp)`——bpp=4 时 4 次迭代（可能约 2–3×），但 **bpp=1 时 16 次迭代反而比标量慢**。必须按 `bpp >= 3` 门控，且需要基准数据确认收益为正后才能合入（见 K15）。

### C3. `Png/PngDecoder.cs` `UnfilterPaeth` ⏭️（设计完成，未落地）

- **怎么改**：审计原文的"Paeth 不能靠 SIMD 跨像素并行"判断**是错的**（见 K3）。做法同 C2 的链迭代框架：`pa/pb/pc` 在 16 位 lane 内用 `Abs`（`AsSByte→Abs→AsByte` 保留 0x80 幅值）求值，三路比较用 `BlendVariable` 链无分支选出 pred。
- **⏭️ 暂缓原因**：与 C2 同——收益随 bpp 变化剧烈，需先建基准。好消息是 C1 已经验证了该"块内前缀和 + 跨块进位 + 按 bpp 展开"框架的正确性，C2/C3 可直接复用。

### C4. `Formats/Png/Adler32.cs` ✅

- **错误类型**：未向量化（逐字节标量，命中每一个 zlib 字节）
- **怎么改**：
  - `s1`：`psadbw`（`Sse2.SumAbsoluteDifferences`）一次求 8 字节的和，两个 64 位 lane 相加得 16 字节总和。
  - `s2`：由 `s2 增量 = 16·s1_0 + Σ(16-i)·b_i` 可知是 `b` 与 `[16..1]` 的点积；`Widen` 到 16 位后用 `pmaddwd`（`MultiplyAddAdjacent`）一次完成 8 个乘加，两级 `pshufd`（0x4E / 0xB1）归约。
  - 保留 `NMAX=5552` 分块（增量与标量一致，上界仍成立）与 `% 65521`。
- **结果**：每个 16 字节块只剩 1 次 `s2` 更新，而不是 16 次。
- **验证**：新增 `Adler32Tests`——`"Wikipedia"` → `0x11E60398`、`"The quick brown fox..."` → `0x5BDC0FDA` 两个标准向量；加 17 种长度 × 4 种偏移（含跨 NMAX 边界）与参考实现对比；加分两段 `Update` 等价性。
- **⚠️ 坑**：`psadbw` 在 .NET 里返回 `Vector128<ushort>` 而不是 `Vector128<byte>`（见 K9）。

### C5. `Core/SimdHelper.cs` `GrayscaleRgb24InPlace` ⏭️

- **怎么改**：按整个连续缓冲切分（而非按行），只在整幅图末尾留一次标量尾；补 `AdvSimd` 回退。
- **⏭️ 暂缓原因**：需要改 `Processing.cs:1505-1509` 的并行切分契约，与 D4（ARM 回退）一起做更合适。

### C6. BMP 行填充按位对齐 ✅（部分）

- **已完成**：`BmpReader.cs:126` `((bits+31)/32)*4` → `((bits+31)>>5)<<2`；`BmpWriter.cs:48,177` `((n+3)/4)*4` → `(n+3) & ~3`。
- **⏭️ 未完成**：24 位 BGR→RGB 的 SSSE3 化（`BmpReader.cs:410-417`）。原因：需要新增跨缓冲的 3 通道置换 helper，且 16 位/bitfields 路径也各有标量实现，宜与 D4 一起统一。

### C7. `Formats/Webp/WebpAdapter.cs` ✅

- **怎么改**：RGBA→RGB 与 RGB→RGBA 两处逐像素标量循环改为调用 `SimdHelper.PackRgbaToRgb` / `ExpandRgbToRgba`。

### C8. `Formats/Gif/Quantizer.cs:331-350` 最近色查找 ⏭️

- **怎么改**：`Widen` → `MultiplyAddAdjacent` 一次比 4 个候选，保持 32 位累加**不 pack**；加"距离已大于当前最优则跳过"的掩码剪枝。
- **⏭️ 暂缓原因**：约 9.2M 次迭代确实是 GIF 最大热点，但从 float 改定点会改变并列候选的排序，必须先建立调色板 golden 才能保证"改速不改质"。

---

# 阶段 D —— 收敛多套实现与门控统一

### D1. `Formats/Jpeg/JpegEncoder.cs` `DctQuantizeInPlace` ✅

- **怎么改**：回退档门控去掉冗余的 `Sse2.IsSupported || AdvSimd.IsSupported`（`Vector<T>` 本身已是可移植抽象），统一为 `Vector.IsHardwareAccelerated && Vector<int>.Count >= 4`；补长注释说明"宽度倒挂是故意的"——档 1 虽只有 128 位但把 FDCT 也向量化了，不要因"档 2 更宽"就调换顺序。
- **结果**：三档门控谓词不再互相矛盾，意图可维护。

### D2. 门控阶梯顺序 ✅

- **怎么改**：`PngDecoder.UnfilterUp` 与 `SimdHelper.AddBytesInPlace` 都改成"先判 `Vector<byte>.Count > 16` 走最宽档，再 `Sse2` → `AdvSimd` → `Vector<T>` 兜底 → 标量尾"。
- **结果**：`Vector<T>` 档在 AVX2（32 字节）/ AVX-512（64 字节）机器上真正可达，不再是死代码。

### D3. 死代码清理 ✅（部分）

- **已完成**：删除 `SimdJpegPipeline.StoreRowSse2`（无调用点）、`ConvertRowYCbCrToRgb(Span)` 重载（无调用点）、`:197` 注释残留。
- **`ForwardDct8x8` 保留**：生产路径不用，但 `JpegEncoderSimdTests` 依赖它做"不带量化"的逐系数对拍；已加注释说明它与融合版共用 `FdctTransform`，不存在双份维护。

### D4. 补 `AdvSimd` 回退 ⏭️

涉及 `SimdHelper.PackRgbaToRgb/ExpandRgbToRgba`、`BmpReader/BmpWriter`、`GifDecoder`、`LzwDecoder`、`OctreeQuantizer`、`Processing`、`SimdJpegPipeline`。
- **⏭️ 暂缓原因**：开发环境无 ARM 机器，`AdvSimd` 路径只能保证"编译通过 + 逻辑等价"，无法实测。当前策略是**先保证 x86 路径正确**，ARM 回退作为独立批次（需 ARM CI 或真机）再做。

---

# 阶段 E —— 语义分歧修正

### E1. `Formats/Jpeg/JpegImage.cs` 恒真条件 ✅（显式化，未改行为）

- **错误类型**：`ColorInfo.HasAdobeTransform || !ColorInfo.HasAdobeTransform` 恒为 true
- **结论**：`:487-488` 的注释表明"没有 APP14 时是否反相取决于编码器，本库沿用一律反相"——**行为是有意的**，不是笔误。
- **怎么改**：4 处恒真式改为 `bool invert = true;` 并写明理由与"若要改成分支需先建 golden"的警告。
- **⚠️ 教训**：恒真条件是"意图不明"信号，**不能顺手修正**——直接改成 `HasAdobeTransform` 会静默改变所有 CMYK/YCCK 输出（见 K12）。

### E2. `Formats/Jpeg/SimdJpegPipeline.cs:262-270` 16 位反量化乘 ⏭️

- **怎么改**：系数与量化表 `Widen` 到 32 位再 `MultiplyLow`，与 `FastIDCT`（int/long）语义对齐；同时消除 `ushort*`→`short*` 强转。
- **⏭️ 暂缓原因**：8 位 JPEG 下反量化后的系数落在 int16 内，当前不会触发回绕，属于"潜在"而非"现实"缺陷；改动涉及 IDCT 数据流的位宽，宜与 E5（三套 IDCT 契约统一）一起做。

### E3. `Formats/Gif/GifDecoder.cs` 越界读 ✅

- **怎么改**：RGBA 主循环上界 `i + 8 <= pixelCount` → `i + 16 <= pixelCount`；RGB 主循环 `i + 10` → `i + 16`。
- **理由**：写侧确实只需 `i+10`，但**读侧** `Vector128.LoadUnsafe` 是一次读 16 字节索引，必须取两者中更严的一个。
- **代价**：末段最多 15 个像素改走标量尾（相对整幅图可忽略）。若要保住这部分覆盖，需要把剩余索引拷进零填充的 16 字节栈缓冲再走一次 SIMD——留作后续。

### E4. `Formats/Png/Crc32.cs` ✅

- **怎么改**：`uint[8][256]` → 扁平 `uint[8*256]`（`T0..T7` 编译期偏移常量）+ `MemoryMarshal.GetArrayDataReference` 一次性取基址；`BitConverter.IsLittleEndian` 提到 `static readonly` 字段。
- **结果**：消除每次迭代 8 次二级指针跳转的非连续 gather。

### E5. 三套 IDCT 的舍入 / clamp 契约 ⏭️

- **⏭️ 暂缓原因**：跨 `SimdJpegPipeline` / `FastIDCT` / `FloatingPointIDCT` 三个文件的大改，且 `useFloatingPointIdct` 开关目前**故意**允许不同精度。必须先建立三套交叉 golden 才能动。

---

# 阶段 F —— 内存访问与冗余遍历 ⏭️

| 任务 | 位置 | 状态 / 原因 |
|---|---|---|
| F1 | `PngDecoder.cs:748,775,801` 每行 `CopyTo` 往返 | ⏭️ 需重构双行缓冲的所有者关系 |
| F2 | `JpegReconstruct.cs:170-179,204-213` 边界 MCU 临时缓冲 | ⏭️ 需改边界写入方式 |
| F3 | `JpegEncoder.cs:1438-1488` 同一块被扫 3 次 | ⏭️ 需先确认 zigzag gather 是否真是瓶颈 |
| — | `PngDecoder.cs:1063-1114` Adam7 跨步散射 | ⏭️ 隔行数据是天然的跨步写，收益有限 |
| — | `JpegFrameState.cs:549` zigzag 散射写 | ⏭️ 同 F3 |
| — | BMP bottom-up 反序遍历 | ⏭️ 缓存友好性改动，需基准支撑 |

---

# 执行记录

## 批次 1 —— 常量、死代码、低风险清理（B1 / B2 / B4 / D3 / E4 / C6 / C7）

- 构建 0 错误，测试 152 → 154（新增 2 项）。
- **发现问题**：审计"JpegFrameState.cs:4 是死 using"是**误报**——`:812` 的融合路径门控确实使用 `Sse2.IsSupported`，删除后编译失败。已回退并加注释（K11）。

## 批次 2 —— SIMD→标量退化（A1 / A2）

- A1 一次通过。
- A2 首次编译失败：`Vector128.StoreUnsafe(out0, pDest)` 无法推断类型参数 → 改用实例方法 `out0.StoreUnsafe(ref *pDest)`（K10）。
- A2 首次测试失败：新增的**参考实现**写错了（把 8 个像素都塞进只有 8 字节容量的 `rg0`，且把 `rg1` 置 0）。修正为 `rg0` 装像素 0–3、`rg1` 装像素 4–7 后通过（K14）。
- 测试 154 → 156。

## 批次 3 —— 热点向量化（C1 / C4）

- C1 一次通过与参考实现的全 bpp × 全长度对拍。
- C4 首次编译失败：`Sse2.SumAbsoluteDifferences` 在 .NET 中返回 `Vector128<ushort>` 而非 `Vector128<byte>`（K9），修正后通过。
- 测试 156 → 161。

## 批次 4 —— 门控与语义（D1 / D2 / E1 / E3）

- 全部一次通过。
- E1 的关键判断：查注释后确认"一律反相"是**有意**行为，因此只做显式化、不改语义（K12）。

---

# 发现的问题与知识

## K1. "3 张 32 项小表 + pshufb"方案不成立（修正审计原文）

审计原文建议 `OctreeQuantizer` 的 `_mapLut` 可"拆成 3 张 32 项小表用 `pshufb` 留在向量域"。**该建议错误**：`_mapLut[cube]` 的 15 位下标由 R/G/B 三个 5 位字段**联合**决定一个字节，不是三个独立映射的拼接，因此不可分离。
真正可行的向量化手段只有：① 建 `int[32768]` 副本 + `Avx2.GatherVector256`；② 改变数据结构本身（算法层改动）。
**教训**：`pshufb` 做表查找的上限是"每表 16 项（字节表）"，且要求映射可分解；对不可分解的大表只能 gather 或改数据结构。

## K2. 偏置移动不是无条件等价（B3 风险点）

`((a+128)+(b+128)+2)>>2 - 128` 与 `(a+b+2)>>2` 是否等价，必须用"模 2^k 不变性"论证（此处 `+256` 是 4 的倍数，`>>2` 后正好 `-128`，**确实等价**）。
**教训**：定点管线里移动常量偏置前先做代数证明，不要凭直觉。

## K3. PNG Sub/Average/Paeth 的可向量化维度是 bpp 条链

审计原文把 Paeth 判为"不可并行"是错的。三个滤波器真正的并行维度不是"同一条链内的相邻字节"（那是真串行），而是 **bpp 条互不依赖的链**。这与"前缀和类算法把状态留在向量寄存器跨迭代传递"是同一模式。C1 已实证该框架。

## K4. `AlignedBuffer` 已建但未接入

`SimdHelper` 提供 64 字节对齐分配器，只有 PNG 行缓冲在用；而且用它的地方仍走 `LoadUnsafe`（非对齐载入），对齐能力完全没转化为指令选择。另外它实现 `IDisposable` + 终结器，PNG 解码每行两次 `AlignedAlloc/AlignedFree`，本身可能是净负收益——**先测量再推广**。

## K5. 门控谓词不一致会导致"SIMD 路径比回退路径窄"

`JpegEncoder.DctQuantizeInPlace` 里 SIMD 档看 `Sse2.IsSupported`（128 位），回退档看 `Vector.IsHardwareAccelerated`（AVX2 下 256 位）。
**教训**：同一函数内混用手写内在函数与 `Vector<T>` 时，必须显式写出"谁优先、为什么"，不能各用各的谓词。

## K6. `Sse2.IsSupported` 在 x64 恒真 → 后面的宽档全成死代码

`PngDecoder.UnfilterUp` 与 `SimdHelper.AddBytesInPlace` 把 `Sse2` 排在 `Vector<T>` 之前，导致宽档永不可达。
**规则**：分派阶梯必须"从最宽到最窄"排列，并用 `Vector<byte>.Count > Vector128<byte>.Count` 显式判断宽档是否真的更宽。

## K7. `pslldq` / `psrldq` 的移位量必须是编译期常量

`Sse2.ShiftLeftLogical128BitLane(v, n)` / `ShiftRightLogical128BitLane(v, n)` 的 `n` 必须是常量，不能是循环变量。因此 PNG Sub 的向量化必须按 bpp 展开成 `switch`（1–8 各一份常量移位序列）。
收敛条件：第 k 步后每个元素累加 2^k 个前驱，故需 `2^k - 1 >= ceil(16/bpp) - 1`——bpp=1→4 步、2→3 步、3→3 步、4→2 步、5/6/7→2 步、8→1 步。

## K8. 前缀和是线性算子 ⇒ 跨块进位可以直接加在起点

块间进位不必单独"复制到各链"，只要把它加在块首（`[0, bpp)`），再和块内数据一起走同一套移位累加即可：`prefix(v + carry) = prefix(v) + prefix(carry)`。这省掉了一整组"广播进位"的操作。
**对称性**：首块进位为零向量 ⇒ "首 bpp 字节左邻为 0" 的边界条件天然满足，行首循环可以直接删掉。

## K9. `psadbw` 在 .NET 里返回 `Vector128<ushort>`

`Sse2.SumAbsoluteDifferences(Vector128<byte>, Vector128<byte>)` 的返回类型是 `Vector128<ushort>`——因为每个 64 位 lane 装的是一个 16 位和。用 `.AsUInt64().GetElement(0/1)` 即可取出两组 8 字节的和。
（我第一次按 `Vector128<byte>` 声明，编译报错。）

## K10. `Vector128.StoreUnsafe(vec, ptr)` 在 `byte*` 上无法推断类型参数

`Vector128.StoreUnsafe(out0, pDest)` 报 CS0411；改用实例方法形式 `out0.StoreUnsafe(ref *pDest)` 即可。

## K11. 审计结论必须回查——"死 using"是误报

审计称 `JpegFrameState.cs:4` 的 `using System.Runtime.Intrinsics.X86` 是死代码，实际 `:812` 使用 `Sse2.IsSupported`，删除后编译失败。
**教训**：子代理/静态扫描给出的"删除类"结论，删除前必须以编译器为最终裁判。

## K12. 恒真条件是"意图不明"信号，不能顺手修正

`JpegImage.cs` 中 `HasAdobeTransform || !HasAdobeTransform` 确实是重言式，但注释表明"一律反相"是**有意**的产品选择。直接"修正"成 `HasAdobeTransform` 会静默改变所有 CMYK/YCCK 输出。
**正确处理**：① 读注释确认意图；② 若行为有意 → 改写成显式常量 + 写明理由；③ 若确属笔误 → 建 golden 再改。

## K13. "写满 16 字节"的前提是"下一块会覆盖尾部"

`PackRgbaToRgb` 想用一次 16 字节存储替代 3 次标量写，前提是高 4 字节垃圾会被下一块覆盖。**最后一块没有下一块**，若也写 16 字节会越界 4 字节——必须单独用 8+4 字节两次写。
**通式**：任何"靠后续迭代覆写脏字节"的优化，都要单独处理最后一次迭代。

## K14. 对拍测试里"参考实现"本身也可能有 bug

A2 第一次跑失败时，直觉是新 SIMD 写错了；实际是我把参考实现里 8 个像素全塞进只有 8 字节容量的 `rg0`（`rg0` 只装像素 0–3，`rg1` 装像素 4–7）。
**教训**：先怀疑新写的参考实现，尤其是手工展开的位运算部分——它比被测代码更不可信。

## K15. Average / Paeth 的向量化收益随 bpp 剧烈变化，必须先建基准

两者都不是线性前缀和，只能按链长 `chainLen = ceil(16/bpp)` 迭代：
- bpp=4 → 4 次迭代（16 字节），估计约 2–3×；
- bpp=3 → 6 次迭代，收益更薄；
- **bpp=1 → 16 次迭代，会比标量更慢**。

因此必须按 `bpp >= 3` 门控，并且在合入前用基准确认收益为正。C1 已经验证了"块内前缀和 + 跨块进位 + 按 bpp 展开"这一框架的正确性，C2/C3 可直接复用，只差基准数据。
