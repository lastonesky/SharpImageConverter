# SIMD 实现陷阱与外部审计复核

> 本文件合并两次外部审计（2026-09-24 的 Gitee 审计、2026-10-02 的全库 SIMD 审计）的复核结论与
> **可复用的实现陷阱**（K1–K15）。逐条任务状态不再维护——已落地的在 §2，被否决/暂缓的在 §3，
> 后续进展看 `CHANGELOG.md`。

## 1. 审计结论复核

### 1.1 不成立的指控

| 指控 | 复核结论 |
|---|---|
| 全局无 `Vector256` / AVX2（P0） | ❌ 不成立。`FloatingPointIDCT.cs` 有完整 256 位路径（`TransformVector256`、`StoreRow`、`ToSingle256`），约 20 处。唯一需要补充的语境是 `useFloatingPointIdct` **默认 false**——「默认未开启」与「不存在」是两回事 |
| `AlignedBuffer<T>` 及工厂方法未被使用（P0 死代码） | ❌ 不成立。PNG 解/编码行缓冲 12 处调用（`PngDecoder` 5 处 `Unfilter*`、`PngWriter` 行滤波、单元测试）。`AllocateAlignedBytes` 的「对齐 + 按 `Vector<byte>.Count` 补齐尾部」正是为了让反滤波能整向量读且不越界，删掉 PNG 行滤波会退回逐字节 |
| 审计的「SIMD 覆盖率」表 | ❌ 只统计了 `Core` 与 `Processing`，占 SIMD 代码量最大头的 `Formats/Jpeg` 根本没进表 |

### 1.2 被夸大的指控

- **EXIF orientation 5–8「内存峰值数倍」**：实际是 **2 倍**，全程无中间缓冲（`src` + `dst`）。
  case 2/3/4 已在原数组就地完成，只有 5–8 因源/目标宽高互换必须新分配。
  报告建议的「原地旋转」在非方阵下不可行（需 cycle-following 转置，缓存局部性还更差）。
- **LOH 大数组改 `ArrayPool<byte>.Shared`**：这条建议**有害**。① 这些缓冲最终成为
  `Image<TPixel>.Buffer` / `ImageFrame.Pixels`，是长期持有对象，池化等于「租了不还」或 use-after-return；
  ② Shared 池最大桶 1 MiB，>350K 像素的 RGB 根本进不了池，Rent 直接回落 `new`；
  ③ 大块长寿命像素缓冲本来就该在 LOH。真要做是 `IMemoryOwner<byte>` 抽象，那是破坏性 API 改造。

### 1.3 确认成立并已处理

| 项 | 处置 |
|---|---|
| `SimdHelper.RoundDownToMultiple` 真死代码 | 已删 |
| `IImageProcessor` 无实现（public API） | 保留作扩展点，不删（删除是破坏性变更） |
| Resize 类缺 SIMD | 已补：Bicubic 3.6x、Bilinear 扩到 8 像素（+6~8%） |
| `PackRgbaToRgb` 逐 12 字节三次写出 | 已改：主循环一次 16 字节存储 |
| `AddBytesInPlace` 的 AdvSimd 分支逐行重复 | 已知的跨 ISA 抽象成本，无害 |
| 报告**遗漏**的真实死代码（6 个无引用成员 + `AddBytesInPlace` 的不可达 `Vector<T>` 三级回退） | 已清理 |

> `AddBytesInPlace` 的 `Vector<T>` 三级回退在 x64（必有 SSE2）与 arm64（必有 AdvSimd）上都不可达——
> 这是审计「SIMD 有效率」维度里唯一真正符合无效代码定义的东西，但报告只字未提。

## 2. 已落地的 SIMD 修复

| 类别 | 改动 | 验证 |
|---|---|---|
| SIMD→标量退化 | `PackRgbaToRgb` 每块 3 次标量写 → 1 次 16 字节写（末块单独 8+4 字节，见 K13） | `SimdPixelOpsTests` |
| SIMD→标量退化 | JPEG `ConvertRowYCbCrToRgb`：3 次 `GetElement` + 12 次标量写 → 两条 `pshufb` + 2 次存储 | 新增 `JpegRgbInterleaveTests`：500 组随机 + 已知输入逐字节对拍 |
| 常量提升 | JPEG 编/解码 11 个 `Fix_*`、`half`、`bias128`、`BicubicMaxSum/Round` 提为静态向量 / `const` | golden 一致 |
| 补向量化 | PNG `UnfilterSub`：块内前缀和 + 跨块进位，按 bpp 展开成 `switch` | 新增 `PngUnfilterTests`（bpp 1–8 × 18 种长度 + 回绕 + 跨块进位） |
| 补向量化 | PNG `Adler32`：`psadbw` 求 s1、`pmaddwd` 求加权 s2 | 新增 `Adler32Tests`（2 个标准向量 + 17 种长度 × 4 偏移） |
| 门控统一 | `JpegEncoder.DctQuantizeInPlace` 三档谓词统一；`UnfilterUp` / `AddBytesInPlace` 改为「从最宽到最窄」 | 全量测试 |
| 语义 | `GifDecoder` 调色板展开越界读：读 16 字节 ⇒ 上界取 16（写只需 10） | `GifDecodePathTests` |
| 语义 | `JpegImage` 恒真重言式 → 显式 `true`（**不改行为**，见 K12） | `CmykJpegNoApp14Tests` |
| 性能卫生 | `Crc32` jagged `uint[8][256]` → 扁平 `uint[2048]` + 基址一次取出 | PNG 解码 CRC 校验 |

## 3. 暂缓 / 否决及原因

| 项 | 原因 |
|---|---|
| `OctreeQuantizer` 最近色查找向量化 | 审计建议的「3 张 32 项小表 + `pshufb`」**不成立**（K1）；可行方案要 128 KB 额外表 + 按 AVX2 分档，需先权衡 |
| PNG `UnfilterAverage` / `UnfilterPaeth` 向量化 | 每步都要截断，不是线性前缀和，只能按链长迭代；bpp=1 时 16 次迭代**比标量慢**，须按 `bpp >= 3` 门控并先建基准（K15） |
| `GrayscaleRgb24InPlace` 按整缓冲切分 | 要改 `Processing.cs` 的并行切分契约，与 ARM 回退一起做更合适 |
| JPEG 16 位反量化用 32 位乘 | 8 位 JPEG 下系数落在 int16 内，属「潜在」而非「现实」缺陷；宜与三套 IDCT 契约统一一起做 |
| 三套 IDCT 的舍入 / clamp 契约统一 | `useFloatingPointIdct` 开关**故意**允许不同精度，必须先建三套交叉 golden |
| 阶段 F（PNG 每行 `CopyTo` 往返、JPEG 边界临时缓冲、同一块扫 3 次、zigzag 散写、Adam7 散射、BMP bottom-up） | 都需重构缓冲所有权或先证明是瓶颈，未见封死性上界收益 |

## 4. 实现陷阱（K1–K15）

**K1. `pshufb` 的表查找上限是「每表 16 项且映射可分解」。**
审计说 `OctreeQuantizer._mapLut` 可拆成 3 张 32 项小表——错：15 位下标由 R/G/B 三个 5 位字段
**联合**决定一个字节，不是三个独立映射的拼接。只剩两条路：`int[32768]` 副本 + `Avx2.GatherVector256`，
或改数据结构。

**K2. 定点管线里移动常量偏置前先做代数证明。**
`((a+128)+(b+128)+2)>>2 - 128 == (a+b+2)>>2` 成立，是因为 `+256` 是 4 的倍数（模 2^k 不变性）。

**K3. PNG Sub/Average/Paeth 的可向量化维度是「bpp 条链」，不是「同一条链内的相邻字节」。**
审计把 Paeth 判为不可并行是错的；它与「前缀和类算法把状态留在向量寄存器跨迭代传递」是同一模式。

**K4. `AlignedBuffer` 建了但没接入等于没优化。**
只有 PNG 行缓冲在用，而且用的地方仍走 `LoadUnsafe`（非对齐载入），对齐能力完全没转化为指令选择。
它带 `IDisposable` + 终结器，PNG 解码每行两次 `AlignedAlloc/Free` 本身可能就是净负收益——**先测量再推广**。

**K5. 同一函数内混用手写内在函数与 `Vector<T>` 时，必须显式写出「谁优先、为什么」。**
`DctQuantizeInPlace` 曾出现 SIMD 档看 `Sse2`（128 位）、回退档看 `Vector.IsHardwareAccelerated`（AVX2 下 256 位）
⇒ SIMD 路径反而比回退路径窄。

**K6. `Sse2.IsSupported` 在 x64 恒真 ⇒ 排在它后面的宽档全成死代码。**
分派阶梯必须**从最宽到最窄**，并用 `Vector<byte>.Count > Vector128<byte>.Count` 显式判宽档。

**K7. `pslldq` / `psrldq` 的移位量必须是编译期常量。**
所以 PNG Sub 的向量化必须按 bpp 展开成 `switch`。收敛条件：第 k 步后每个元素累加 2^k 个前驱，
需 `2^k - 1 >= ceil(16/bpp) - 1` ⇒ bpp=1→4 步、2/3→3 步、4~7→2 步、8→1 步。

**K8. 前缀和是线性算子 ⇒ 跨块进位可以直接加在起点。**
`prefix(v + carry) = prefix(v) + prefix(carry)`，省掉一整组广播；首块进位为零向量，
「首 bpp 字节左邻为 0」的边界条件天然满足，行首循环可以直接删。

**K9. `Sse2.SumAbsoluteDifferences` 在 .NET 里返回 `Vector128<ushort>`**（每个 64 位 lane 一个 16 位和），
用 `.AsUInt64().GetElement(0/1)` 取值。

**K10. `Vector128.StoreUnsafe(vec, ptr)` 在 `byte*` 上无法推断类型参数**（CS0411），
改用实例方法 `vec.StoreUnsafe(ref *pDest)`。

**K11. 「删除类」结论必须以编译器为最终裁判。**
审计称 `JpegFrameState.cs:4` 的 `using System.Runtime.Intrinsics.X86` 是死代码，实际 `:812` 用了
`Sse2.IsSupported`，删掉直接编译失败。

**K12. 恒真条件是「意图不明」信号，不能顺手修正。**
`HasAdobeTransform || !HasAdobeTransform` 是重言式，但注释表明「没有 APP14 时一律反相」是**有意**行为。
正确处理：① 读注释确认意图；② 有意 → 改写成显式常量 + 写明理由；③ 确属笔误 → 先建 golden 再改。

**K13. 「写满 16 字节」的前提是「下一块会覆盖尾部」。**
最后一块没有下一块，写 16 字节会越界 4 字节，必须单独用 8+4 字节两次写。
**通式**：任何「靠后续迭代覆写脏字节」的优化，都要单独处理最后一次迭代。

**K14. 对拍测试里的「参考实现」本身也可能有 bug。**
A2 首次失败时直觉是新 SIMD 写错了，实际是参考实现把 8 个像素全塞进只有 8 字节容量的寄存器。
**先怀疑手写展开的位运算部分**——它比被测代码更不可信。

**K15. Average / Paeth 的收益随 bpp 剧烈变化，必须先建基准。**
`chainLen = ceil(16/bpp)`：bpp=4 → 4 次迭代（估 2–3x）、bpp=3 → 6 次（更薄）、
**bpp=1 → 16 次（比标量慢）**。必须按 `bpp >= 3` 门控并在合入前用基准确认收益为正。
