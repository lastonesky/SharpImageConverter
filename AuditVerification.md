# Gitee 代码审计报告复核

复核对象：`SharpImageConverter` 仓库（工作分支），针对外部审计报告在「内存管理 / SIMD 利用率 / SIMD 有效率 / 死代码」四方面的结论逐条取证。

复核方式：全仓符号级检索 + 逐文件精读 + Release 构建验证（基线 `0 错误 1 警告`，警告仅为 SourceLink 无 SCM 信息）。

---

## 0. 结论速览

| 报告条目 | 报告判定 | 复核结果 | 说明 |
|---|---|---|---|
| 全局无 `Vector256` / AVX2 | P0 证据 | ❌ **不成立** | `FloatingPointIDCT.cs` 有完整 AVX2 路径 |
| `AlignedBuffer<T>` 未使用 | P0 死代码 | ❌ **不成立** | PNG 解码/编码热路径主力，12 处调用 |
| `AllocateAlignedBytes` / `CopyToAlignedBytes` / `GetPaddedLength` / `RoundUpToMultiple` 未使用 | P0 死代码 | ❌ **不成立** | 均有生产代码或单元测试调用 |
| `IsAligned` / `RoundDownToMultiple` 未使用 | P0 死代码 | ⚠️ **部分成立** | 前者仅测试用；后者确为真死代码 |
| EXIF 5-8 内存峰值「数倍」 | 风险点 | ⚠️ **夸大** | 实际 2 倍，无中间缓冲 |
| LOH 大数组改 `ArrayPool` | 建议 | ⚠️ **建议有误** | 该场景下池化反而有害，见 §3.2 |
| ResizeBicubic / ResizeArea 无 SIMD | P1 | ✅ **成立** | 确为纯标量（ResizeArea 已部分优化） |
| ResizeBilinear 批量仅 4 像素 | P2 | ✅ **成立** | `simdXEnd &= ~3`，每次 12 字节 |
| `PackRgbaToRgb` 逐 uint32 写出 | P1 | ✅ **成立（受限）** | 12 字节产出无法对齐到 16 |
| `IImageProcessor` 无实现 | P2 | ✅ **成立** | 全仓仅接口定义一处，无实现/无引用 |
| `AddBytesInPlace` AdvSimd 分支重复 | P2 | ✅ **成立（无害）** | 纯维护性问题 |

**核心判断：报告的两条 P0 都建立在错误的事实依据上（把存在的 AVX2 说成没有，把主力路径的类说成死代码）。这两条若按建议“移除/[Obsolete]”，会直接打掉 PNG 编解码的性能基础。** 真正的有效结论集中在 P1/P2，且报告漏掉了若干真实死代码（见 §4）。

---

## 1. 不成立的指控（含反证）

### 1.1 AVX2 / Vector256「结果为空」——不成立

`src/Formats/Jpeg/FloatingPointIDCT.cs` 存在完整的 256 位路径：

| 位置 | 内容 |
|---|---|
| L66 | `if (Vector256.IsHardwareAccelerated)` 分发 |
| L82 | 注释明确写着「AVX2 路径，一行 8 个元素正好一个 256 位向量」 |
| L85–L138 | `TransformVector256`，含 `Vector256.Load` / `Store` / `Cu[v]` 预缩放 / 双趟一维变换 |
| L259–L264 | `StoreRow`：`Vector256.ConvertToInt32` → `Vector128.Narrow` → clamp |
| L289–L301 | `ToSingle256` 两条重载（short / ushort） |

该文件 `Vector256` 出现约 20 处。**唯一需要补充的语境**：`useFloatingPointIdct` 默认为 `false`（`JpegDecoder.Decode` L9、`ImageFrame.LoadJpeg` L318），所以 AVX2 路径是**按需启用**而非默认走；报告把它说成「不存在」，与「默认未开启」是两回事。

此外报告完全忽略了 JPEG 侧的另一套 SIMD 栈：
- `SimdJpegPipeline.cs`（362 行）：跨平台 `Vector128<short>` 双趟整数 IDCT + YCbCr→RGB
- `SimdJpegEncodePipeline.cs`（448 行）：FDCT+量化、RGB→YCbCr 4:2:0 / 4:4:4，在 `JpegEncoder` L1299 / L1420 / L1537 实际调用
- 默认路径：`JpegFrameState` L795 `if (!handled && colorSpace == YCbCr && !useFloatingPointIdct && Sse2.IsSupported)`

即报告的「SIMD 覆盖率」表**只统计了 `Core` 和 `Processing` 两个目录，占 SIMD 代码量最大头的 JPEG 根本没进表**。

### 1.2 `AlignedBuffer<T>` 及其工厂方法「未被使用」——不成立

实际调用点：

| 调用点 | 文件:行 |
|---|---|
| `Unfilter` 双行缓冲 | `PngDecoder.cs:727-728` |
| `UnfilterRgb8Direct` | `PngDecoder.cs:755-756` |
| `UnfilterRgba8Direct` | `PngDecoder.cs:781-782` |
| `UnfilterRgba8ToRgbDirect` | `PngDecoder.cs:807-808` |
| `UnfilterRgb8ToRgbaDirect` | `PngDecoder.cs:843-844` |
| PNG 写入行滤波 | `PngWriter.cs:225` |
| 单元测试 | `ProcessingTests.cs:118` |

语义也很明确：`AllocateAlignedBytes(stride, alignment: 64, padToMultiple: Vector<byte>.Count)` —— 对齐 + 按 `Vector<byte>.Count` 补齐尾部，正是为了让 `UnfilterScanline` 里的 `Vector.LoadUnsafe` / `AddBytesInPlace`（`PngDecoder.cs:884`、`914`）能整向量读且不越界。**删掉它，PNG 行滤波会退回逐字节，这是 PNG 解码最热的路径之一。**

同理：
- `GetPaddedLength` → 被 `AllocateAlignedBytes` / `CopyToAlignedBytes` 内部调用，**非死代码**
- `RoundUpToMultiple` → 被 `GetPaddedLength` 调用 + 测试调用，**非死代码**
- `IsAligned` → 被单元测试 `ProcessingTests.cs:119/130` 调用，**弱存活**（仅测试用到）
- `NormalizeAlignment` → 被 `AlignedBuffer.Allocate` 和 `IsAligned` 调用，**非死代码**

---

## 2. 被夸大的指控

### 2.1 EXIF Orientation 5-8 的「内存峰值数倍」

`ImageFrame.ApplyExifOrientation`（`ImageFrame.cs:586`）的峰值是 **2 倍**，不是数倍：

- 分配：`dst = GC.AllocateUninitializedArray<byte>(newW * newH * 3)`（L608），`newW*newH == width*height`，与原图等大
- 全程**没有任何中间缓冲区**，只有 `src` + `dst`
- 调用方 `LoadJpeg`（L330）走 `ApplyExifOrientationInPlace`，其中 case 2/3/4 已在原数组上就地完成（L749-810），只有 5-8 落到 `default` 分支才新建数组

5-8 是转置类操作，源/目标宽高互换，非方阵无法原地完成（需要 cycle-following 转置算法，实现复杂度高且缓存局部性差于现有 32×32 分块方案）。**结论：这是必要的 2 倍峰值，不是缺陷。** 报告建议的「原地旋转」在非方阵场景下不可行。

### 2.2 `GC.AllocateUninitializedArray` → `ArrayPool<byte>.Shared` 的替换建议

这条建议在本仓库语境下是**有害**的：

1. 这些数组最终会被交给 `Image<TPixel>.Buffer`（`Processing.cs:285/449/596`）或 `ImageFrame.Pixels`，成为**长期持有对象**。用池意味着「租了不还」或「还了之后被别人拿到」，前者退化 XMTP 式的池污染，后者是 use-after-return 数据竞争。
2. `ArrayPool<byte>.Shared` 的最大桶是 1 MiB（1024×1024）。大图（>~350K 像素 RGB）根本进不了池，Rent 会直接回落 `new`，建议无效。
3. LOH 的代价是 GC  compaction 成本，但这类大块、长寿命的像素缓冲本来就该在 LOH 上——这正是 LOH 的设计目标。

真正值得做（若有大图场景收益诉求）的方向是报告 P1-5 提到的 `IMemoryOwner<byte>` 抽象，但那是**破坏性 API 改造**，不是换一个分配函数。

---

## 3. 确认成立的问题

### 3.1 真死代码：`SimdHelper.RoundDownToMultiple`（`SimdHelper.cs:316-322`）

全仓检索仅命中定义行，无生产代码、无测试引用。**可安全删除**（`internal static class`，无 API 破坏风险）。

> 顺带说明：CHANGELOG L9 已记录上一轮清理了同族的 `GetVectorPaddedByteLength` / `AllocateAligned<T>` 泛型重载，`RoundDownToMultiple` 是那次清理的遗漏项。

### 3.2 `IImageProcessor` 无实现（确认，但处理方式应与报告不同）

`Processing.cs:15-22` 定义 `public interface IImageProcessor { void Execute(Image<Rgb24> image); }`，全仓检索 `IImageProcessor` **仅此一处**。确认成立。

但它是 `public` API：删除 = 破坏性变更。建议 `[Obsolete("预留扩展点，暂无实现；...")]` 或直接保留作扩展点，而不是按报告说的「补充实现或标记」。

### 3.3 Resize 类确实缺 SIMD（成立）

| 方法 | 现状 | 位置 |
|---|---|---|
| `ResizeBilinear` | SSSE3+SSE4.1，每批 4 像素（12 字节） | `Processing.cs:208-250` |
| `ResizeBicubicOptimized` | 纯标量，`Parallel.For` 逐像素 | `Processing.cs:459-613` |
| `ResizeArea` | 纯标量；权重已提到循环外预计算 | `Processing.cs:289-451` |

报告判断准确。补充一点：`ResizeBilinear` 的 SIMD 还带一个额外限制——`x1Index[x] != x0Index[x] + 3` 时提前中断（L144），因为右边缘把 `x1` 夹到 `x0` 后 8 字节载入拿不到相邻像素。这意味着**每行尾部必然退标量**，且 `simdXEnd &= ~3` 再砍 0-3 像素。

### 3.4 `PackRgbaToRgb` 写出效率低（成立，但受格式限制）

`SimdHelper.cs:242-244` 确实分三次 `WriteUnaligned` 写 uint32。根因是 16 字节输入→12 字节产出，无法直接 `Store`。可行的改进是按报告说的「两次加载 32 字节（8 个 RGBA）→ 产出 24 字节」，用 `Ssse3.Alignr` 拼合后一次写 16 字节 + 一次写 8 字节，把写次数从 6 次降到 2 次。**收益有限**（该函数在 `Configuration.SaveRgba32` 回退路径上使用，不在编码主路径），优先级低于报告给的 P1。

### 3.5 `AddBytesInPlace` AdvSimd 分支重复（成立，无害）

`SimdHelper.cs:31-52`，`Sse2` 与 `AdvSimd` 两段循环体逐行相同。属于「跨 ISA 抽象成本」，可用跨平台 `Vector128<byte>` 单段替代（`Vector128.Add` 在两种 ISA 上都映射为最窄原生指令），但需要一个小心的事：`Sse2.Add` 与 `Vector128.Add` 的语义一致性需回归测试验证（`SimdPixelOpsTests.cs` 已覆盖）。

---

## 4. 报告遗漏的真实死代码

复核过程中发现报告漏掉的、确无引用的成员（均为 `internal`，删除无 API 破坏）：

| 成员 | 位置 | 复核证据 |
|---|---|---|
| `NativeBufferOwner<T>.FromSpan` | `NativeBufferOwner.cs:81` | 全仓仅定义处命中；所有调用均为 `Allocate` |
| `NativeBufferOwner<T>.ReadOnlySpan` | `NativeBufferOwner.cs:50` | 无调用点 |
| `AlignedBuffer<T>.Clear()` | `SimdHelper.cs:428` | 无调用点（`Allocate(clear:)` 已覆盖该语义） |
| `AlignedBuffer<T>.Bytes` | `SimdHelper.cs:397` | 无 `.Bytes` 调用点 |
| `AlignedBuffer<T>.ByteLength` | `SimdHelper.cs:384` | 无调用点 |
| `AlignedBuffer<T>.Alignment` | `SimdHelper.cs:383` | 无调用点 |
| `AddBytesInPlace` 的 `Vector<T>` 三级回退 | `SimdHelper.cs:53-63` | x64 必有 SSE2、ARM64 必有 AdvSimd，**该分支在两个目标平台上都不可达** |

最后一条尤其值得注意：它是报告「SIMD 有效率」维度里唯一真正符合「无效代码」定义的东西，但报告只字未提，反而去追 SECTION 里活跃的类。

---

## 5. 修复可行性评估

| # | 事项 | 是否可修 | 难度 | 风险 | 实际收益 |
|---|---|---|---|---|---|
| 1 | 删除 `RoundDownToMultiple` | ✅ | 极低 | 无 | 无性能收益，纯卫生 |
| 2 | 删除 §4 表中 6 个无引用成员 | ✅ | 极低 | 无 | 同上；有 `Nullable`/ analyzer 噪声收益 |
| 3 | 删除 `AddBytesInPlace` 的 `Vector<T>` 不可达分支 | ✅ | 低 | 低（需保留非 SSE2/非 AdvSimd 目标的理论安全网） | 无性能收益 |
| 4 | `AddBytesInPlace` 合并 SSE2/AdvSimd 重复分支 | ✅ | 低 | 中（跨 ISA 语义一致需回归验证） | 无性能收益，降维护成本 |
| 5 | `IImageProcessor` 标 `[Obsolete]` | ✅ | 低 | 低（public API，仅加注解） | 无性能收益 |
| 6 | `PackRgbaToRgba` 写出改为 2 次 | ✅ | 中 | 中（边界处理易错，需穷举测试） | 小幅；非主路径 |
| 7 | `ResizeBilinear` 批次扩到 8 像素 | ✅ | 中高 | 中 | 需实测确认，可能受寄存器压力反噬 |
| 8 | `ResizeBicubicOptimized` 加 SIMD | ✅ | 高 | 中高 | **最值得做**，位于 `Resize()` 的放大路径（`Processing.cs:50`） |
| 9 | `ResizeArea` 加 SIMD | ✅ | 高 | 高 | 当前为 `double` 累加，向量化会改变**求和顺序 → 结果不再逐位一致**，需先决定是否接受数值漂移 |
| 10 | `Image<TPixel>.Buffer` 改 `IMemoryOwner<byte>` | ⚠️ 可行但代价大 | 很高 | 高 | 破坏性重构，全仓调用点改造；收益主要是超大图场景 |
| 11 | ❌ 移除 `[Obsolete]` `AlignedBuffer<T>` 家族 | **不要做** | — | **极高** | 会打掉 PNG 行滤波性能，见 §1.2 |
| 12 | ❌ LOH 大数组改 `ArrayPool` | **不要做** | — | 高 | 见 §2.2，池化长寿命像素缓冲语义错误 |

### 建议执行顺序

1. **立即可做（无风险）**：#1、#2、#3、#5 —— 纯死代码清理与注解，一次 PR，跑现有测试即可放行。
2. **需要谨慎（需先补测试）**：#4、#6 —— 行为等价性依赖 `SimdPixelOpsTests`，建议先加长度 0..70 的穷举字节比对再改。
3. **需要 benchmark 驱动**：#7、#8 —— 先加 BenchmarkDotNet 用例（`SharpImageConverter.Benchmarks` 已存在）拿到基线，再决定 `#7` 是否值得；`#8`（Bicubic SIMD）是唯一预期有明显端到端收益的项。
4. **需要先决策数值容差**：#9 —— 若要求与现状逐位一致，则不可做。
5. **暂缓**：#10 —— 与 `goal.md` 中列出的其他差距项相比，ROI 偏低。

---

## 附：复核基线

```
dotnet build src/SharpImageConverter.csproj -c Release
→ 已成功生成。1 个警告（SourceLink: 源代码管理信息不可用），0 个错误。
```

本报告未修改任何源代码。

---

## 后续：§5 中第 7、8 项已实施（见 CHANGELOG「未发布」）

按建议 #8（Bicubic SIMD）与 #7（Bilinear 8 像素）实际落地后的结果：

| 场景（`examples/progressive.jpg`） | 改动前 | 改动后 | 加速 |
|---|---|---|---|
| `ResizeBicubicOptimized` 1.5x 放大（2662×3356 → 3993×5034） | 176.5 ms | 49.0 ms | **3.6x** |
| `ResizeBicubicOptimized` 2x 放大（2662×3356 → 5324×6712） | 326.7 ms | 91.1 ms | **3.6x** |
| `ResizeBilinear` 2x 放大（2662×3356 → 5324×6712） | 133.6 ms | 125.4 ms | 1.07x |
| `ResizeBilinear` 4x 放大（2662×3356 → 10648×13424） | 537.5 ms | 496.9 ms | 1.08x |
| `ResizeBilinear` 0.25x 缩小（全图 → 2662×3356） | 39.9 ms | 39.1 ms | ≈1.02x |
| `ResizeBilinear` 0.1x 缩小（全图 → 1065×1342） | 9.4 ms | 8.8 ms | 1.06x（噪声量级） |

两者均与改动前的标量输出**逐位一致**（全尺寸 golden 逐字节比对通过）。

与复核时预判的对照：
- **Bicubic 的 3.6x 超出预判**（原估 1.3-1.4x）。真正省下的不只是算术量：一次 4 字节载入同时喂给 3 个通道，
  每个输出像素的取样次数从 48 次降到 16 次，省的是**载入与地址计算**——这部分在原估中被严重低估了。
- **Bilinear 的 8 像素批次只拿到 6-8%**，且仅在放大方向；缩小方向受写带宽限制基本不动。
  这印证了「配对两个 4 像素核心只换来并行发射空间、换不来指令数下降」的判断。
  要继续提升，必须在 x 方向做真正的指令重用（一次载入 + shuffle 供相邻输出像素共用），而不是继续扩批。
