# AVX-VNNI 在 Resize 上的落地实验与结论

> 状态：【实验完成，代码已撤销】。结论：在当前架构下，INT8/INT16 量化 VNNI 路径**不快于**已有的浮点 SIMD 路径，按项目「提升不足 7% 视为无效」铁律不应保留。
> 关联：本次实验的完整代码改动已 `git checkout` 撤销并删除，未合入任何分支。

## 1. 背景与问题

用户提出：让 `ResizeBilinear` / `ResizeBicubicOptimized` 真正吃上 AVX-VNNI（`vpdpbusd` / `vpdpwssd`），并用 `examples` 同款的 `progressive.jpg`（143 MP，10650×13426）做缩小→放大实测，对比用/不用 VNNI 的速度。

前置结论（已先行确认）：resize 两个函数是**浮点插值**（`Sse2/Ssse3/Sse41` 的 `float` 路径），而 VNNI 是「整型点积」指令。直接套会改算法语义，输出不再逐位一致，因此必须走**量化**路径（源像素→整数、权重量化到整数、整点点积），且需临时关闭 md5/逐字节一致性硬判（该一致性闸门已一并在本次撤销中恢复）。

## 2. 实现方案

- 新增量化版 `ResizeBilinearQuantized` / `ResizeBicubicOptimized` 的 VNNI 分支：源像素本就是 `u8`，零扩展成 `s16` 直接喂 VNNI；权重量化（bilinear 组合权重 `>>8`、bicubic `wx*wy ×16384`），量化误差 <0.2 级。
- 点积内核 `DotU8S16`：用 `AvxVnni.MultiplyWideningAndAdd(int, short, short)`（即 **vpdpwssd**，u8×s16→int32 点积累加）+ 标量回退；由 `UseQuantizedVnni` 静态开关路由，默认关、浮点行为不变。
- 纯点积基准（与 `vnni/` 下 C 演示同法）：N=16384 的 INT8 点积，本机 `AvxVnni.IsSupported=True`，**标量 0.5 GMAC/s → VNNI 11.6 GMAC/s，约 24×**——说明「点积本身」VNNI 确实极快。

## 3. 实验方法

- 解码 `progressive.jpg` → 缩小 4×（2662×3356）→ 放大回原尺寸（10650×13426）。
- 浮点路径 vs VNNI 路径分别用 `Stopwatch` 计时，**只计 resize、不计入解码与保存**。
- 4 张对比图保存到 `D:\`（缩小/放大的 float 与 vnni 各一张），供人工核验正确性。

## 4. 实测结果

| 操作 | 浮点 SIMD | VNNI（量化） | 相对速度 |
|---|---|---|---|
| 缩小 bilinear（4×） | 140 ms | 556 ms | **0.25×**（VNNI 慢 4×） |
| 放大 bicubic（回原尺寸） | 952 ms | 1705 ms | **0.56×**（VNNI 慢约 1.8×） |

**质量**（VNNI 与浮点最大逐字节差，0~255 值域）：缩小 **1**、放大 **2** → 量化 VNNI 与浮点**视觉几乎一致**。

## 5. 为什么 VNNI 反而慢

点积本身（vpdpwssd）很快（见 §2 的 24× 基准），但 resize 是**内存/带宽密集型**任务，瓶颈不在「乘加」这一步：

- 当前 VNNI 实现是「**逐像素标量 gather** 源像素 + **每像素重算插值权重**（bilinear 4 次 / bicubic 16 次乘加 + round + clamp）」，这些标量开销盖过了点积那一步的 VNNI 收益。
- 已有的浮点 SIMD 路径早已按「**4 像素/批 + 权重预计算**」充分优化，整点乘加本来就不是它的热点。
- 图像 resize 数据访问是 gather 型（源像素按非连续偏移取点），受 L1/L2 缓存与内存带宽约束；VNNI 的吞吐优势被带宽封顶，无法线性放大。

## 6. 什么方式可能让 VNNI 快（及预期）

要让 VNNI 真正胜出，需把 §5 的标量开销批量化：

1. **一次处理 N 个输出像素**（向量化最外层循环），而非逐像素。
2. **`pshufb` 转置式 gather**：把分散的源像素重排进连续 SIMD 寄存器，消除逐像素标量 gather。
3. **权重瓦片预取**：把每行/列的插值系数预计算成可复用瓦片，避免每像素 4/16 次重算。

预期与代价：

- 即使做到位，因 §5 的内存带宽封顶，对**大图 resize** 的提速大概率在「可感知但有限」区间（例如放大 bicubic 从 0.56× 翻到 ~1.0–1.3×），**不太可能拿到「巨量」（数倍）提升**。
- 工程量大（需重写插值核心、引入转置 gather 与权重瓦片、维护 INT8/INT16 两套量化与回退），且引入量化误差需重新定义一致性验收（容差而非逐字节）。
- 结论：**投入产出比低，本次不继续**，代码已撤销。

## 7. 结论与处置

- 按 `PerfReport.md` 的准入阈值（**提升不足 7% 视为无效，不保留**），本次 VNNI 改动为负提升，依规不保留、不提交。
- 代码全部撤销：`Processing.cs` 与 `ResizeConsistencyTests.cs` 已 `git checkout` 还原；3 个新增文件（`src/Core/VnniIntrinsics.cs`、`SharpImageConverter.Tests/VnniIntrinsicsTests.cs`、`VnniResizeExperimentTests.cs`）已删除；md5 一致性硬判已恢复为开启。
- VNNI 对 resize 的「正确但更慢」结论留存于本文档，供后续评估。若未来出现内存带宽不再是瓶颈的场景（如极小图高频 resize、或权重可完全预计算的固定缩放比流水线），可重新评估本条路线。

## 8. 附录：.NET 调用 VNNI 的坑（net8 实测）

- `System.Runtime.Intrinsics.X86.AvxVnni` 自 .NET 6 起可用；本机 `AvxVnni.IsSupported=True`。
- net8.0 引用集把 `AvxVnni` 标了 `[RequiresPreviewFeatures]`，触发 CA2252（当错误处理）；仅可在文件级 `#pragma warning disable CA2252` 局部抑制，**不开启工程级预览特性**。
- net8 仅暴露 **128 位** `MultiplyWideningAndAdd`（`vpdpbusd`/`vpdpwssd` 的 xmm 形式）；256 位 ymm 形式需更新的 .NET 版本。
- 必须判 `IsSupported` 并提供标量回退，否则不支持的机器上抛 `PlatformNotSupportedException`。
