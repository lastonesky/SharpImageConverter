I'll analyze the graphics library. Let me first load the relevant skills and explore the codebase structure.
已完成全库审计（51 个源文件，覆盖 JPEG 编解码、GIF 量化/LZW、PNG 滤波、BMP、WebP、Processing 重采样）。以下为发现清单。

---

# 一、总体结论

| 维度 | 结论 |
|---|---|
| SIMD 覆盖面 | 极不均衡：JPEG 的 FDCT/IDCT、GIF 映射、PNG 的 Up 滤波已向量化；**Paeth/Sub/Average 滤波、Adler32、最近色查找、区域平均缩放、直方图、灰度 JPEG 全部未向量化** |
| 位宽 | **全库除 `Processing.cs` 双三次外没有任何 256 位路径**，无一处 AVX-512；JPEG/GIF/PNG/BMP 核心锁死在 128 位 |
| ARM 回退 | 库内已建立 `AdvSimd`/`Vector<T>` 惯例（`SimdHelper.AddBytesInPlace`、`PngDecoder`），但 **JPEG 全链路、GIF 全目录、BMP、SimdHelper 的像素格式转换、Processing 全部缺失** |
| 标量/SIMD 混用 | **存在 6 处明确的错误混用**（见第三节），其中 3 处直接抵消 SIMD 全部收益 |

---

# 二、未向量化的热点函数/循环

## P0（占比最大）

| 位置 | 问题 |
|---|---|
| `Processing/Processing.cs:482-522` `ResizeArea.ProcessRow` | 缩小路径默认实现，**纯 `double` 标量三重循环**；511 行每输出像素一次除法 `1.0/totalArea`。全库最大的未向量化热点 |
| `Formats/Gif/Quantizer.cs:331-350` | 最近色查找：35937 立方 × ≤256 调色板色的 **float 平方距离，约 9.2M 次标量迭代**，是 GIF 编码器最大热点。未用 `Widen + MultiplyAddAdjacent` 一次比多候选；唯一提前退出是浮点 `dist == 0` |
| `Formats/Png/PngDecoder.cs:1022-1052` `UnfilterPaeth` | PNG 解码最热行类型，**1 字节/迭代全标量** |
| `Formats/Png/PngDecoder.cs:908-925` / `978-999` | `UnfilterSub` / `UnfilterAverage` 同样全标量。**5 个滤波器里只有 Up 有 SIMD** —— 同一算法族内 SIMD/标量割裂 |
| `Formats/Png/Adler32.cs:41-54` | 逐字节标量，被 `ZlibHelper.cs:59/112/237` 对**每一个**压缩/解压字节调用 |
| `Core/SimdHelper.cs:103-165` `GrayscaleRgb24InPlace` | 只有 128 位 + 标量尾；`Processing.cs:1505-1509` 按行并行切分导致**每行都产生一次标量尾** |

## P1

| 位置 | 问题 |
|---|---|
| `Formats/Jpeg/JpegEncoder.cs:1676-1694` | **灰度编码全链无 SIMD**（`FillBlockGray8ToY`、`ProduceGraySamples:901`、`ProcessDctGray:1026`） |
| `Formats/Gif/OctreeQuantizer.cs:224-269` | 直方图构建纯标量 + 4 路随机散射；并行分支只是切像素区间，内部仍标量 |
| `Formats/Gif/Quantizer.cs:162-174/219-223` | Wu 直方图 5 路随机散射；`:173/223` 的 `_m2` 用 **double** 累加 |
| `Formats/Bmp/BmpReader.cs:410-417` | 24 位 BGR→RGB 全标量，而同文件 `:480-495` 的 32 位路径有 SSSE3 shuffle → **同类转换两种能力不一致** |
| `Formats/Bmp/BmpReader.cs:562-563` / `BmpWriter.cs:48,177` | 热路径整数除法（`(v*255+max/2)/max`）、行填充用除法而非按位对齐 |
| `Formats/Gif/GifEncoder.cs:156,162-165` | 透明通道扫描、RGBA→RGB 跨步转换纯标量（已有 `SimdHelper.PackRgbaToRgb` 未调用） |
| `Formats/Webp/WebpAdapter.cs:65-70, 219-225` | RGBA↔RGB 逐像素标量循环，`SimdHelper.cs:224/261` 已有 SSSE3 实现**未复用**，且多一次全缓冲读写往返 |
| `Formats/Jpeg/HuffmanDecodingTable.cs:120-138` | 慢路径逐位解码；`JpegBitReader.cs:133-216` 逐字节填充 |
| `ImageFrame.cs:619-631, 646-738, 751-810` | EXIF 方向变换逐像素/逐字节标量；`:800-805` 逐字节 swap 本可整行向量交换 |

**合理未向量化（已排除）**：LZW 编码/解码的串行依赖链、Floyd–Steinberg 误差扩散的串行误差链、八叉树插入/归约的指针追踪、直方图散射。

---

# 三、标量 / SIMD 错误混用（核心问题）

## 3.1 SIMD 结果被标量逐元素后处理 —— 收益完全抵消

| 位置 | 错误类型 |
|---|---|
| `Formats/Gif/OctreeQuantizer.cs:612-615`<br>`:756-759` | **最严重**。16 路 `pshufb` 算出 16 个 cube 下标后 `Sse2.Store` 到 `stackalloc`，再 `for (j=0..16) _mapLut[buf[j]]` 做 **16 次标量随机 gather + 16 次单字节散写**。前面的解交织/移位/合成全部白做，且这是 GIF 最热的映射阶段。`:578` 注释自认"AVX2 下没有可用 gather"，但 32KB 表可拆成 3 张 32 项小表用 `pshufb` 留在向量域 |
| `Formats/Jpeg/SimdJpegPipeline.cs:312-341` `ConvertRowYCbCrToRgb` | `rgLow.AsInt64().GetElement(0/1)`、`b.AsInt64().GetElement(0)` 把向量拆成 3 个 `ulong` 标量，再用 **12 次手工展开的 `*(ushort*)`/`*(byte*)` 标量写**完成 RGB 交织。交织本应一条 `pshufb` 完成 |
| `Core/SimdHelper.cs:242-244` `PackRgbaToRgb` | shuffle 之后 `AsUInt32().GetElement(0/1/2)` 三次标量取出 + 三次 `WriteUnaligned`，把一次 16 字节 shuffle 拆成 3 次标量 store |
| `Processing/Processing.cs:1247-1249`<br>`1415-1417 / 1439-1441` | `px.GetElement(0..2)` 三次标量写回，SIMD 结果被标量后处理 |

## 3.2 向量 → 标量 → 向量往返（冗余数据转换）

| 位置 | 错误类型 |
|---|---|
| `Processing/Processing.cs:1153-1156` `BicubicCoreVnni`<br>`:1203-1206` `BicubicCoreVnni2` | 在**向量内核内部**用 `wH.GetElement(i)` 逐 lane 取回标量，再 `Vector128.Create(...)` 广播回去（每像素 4~8 次）。`:1320-1328` 预计算成 `Vector256<short>[]` 的向量化成果被彻底浪费 |
| `Processing/Processing.cs:667-681` | `ResizeBicubicOptimized` 主循环内每像素重建权重向量；`:678-681` 垂直权重 `wy0..wy3` **每行恒定**却在每像素重建，与 `:317-319` 已按行提升的写法不一致 |
| `Processing/Processing.cs:1051-1052, 1169-1170, 271-272` | `BicubicMaxSum/BicubicRound/0.25f/128.5f` 等**编译期常量**在循环内 `Vector.Create`，而 `:130-131` 同类已提升为 `static readonly` —— 同文件内策略不一致 |
| `Processing/Processing.cs:223-226, 264-273` | 同一套权重准备 **6 个数组**（`x0/x1/wx0/wx1` + `q/r`）：SIMD 段只用 q/r，标量段只用 wx0/wx1+x1，**同一数据两套表示**；且为全 width 构建 q/r，实际只用到 `simdXEnd` |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:325-326` | `cbLo/cbHi` 在 `Convert8Pixels` 内先 `+128`，调用处再 `-CConst128` —— **多余偏置往返** |

## 3.3 同一算法多套实现并存（冗余 + 结果漂移风险）

| 位置 | 错误类型 |
|---|---|
| `Formats/Jpeg/JpegEncoder.cs:1387-1435` `DctQuantizeInPlace` | **量化有 3 套实现**：SIMD 融合版（`:1392`，SSE2 128 位）、`Vector<int>` 版（`:1398-1419`，可变宽）、纯标量版（`:1429-1433`）。更关键的是**门控条件不同**：SIMD 版看 `Sse2.IsSupported`，回退版看 `Vector.IsHardwareAccelerated` → **AVX2 机器上"SIMD 路径"反而比"回退路径"窄** |
| `Formats/Jpeg/JpegEncoder.cs:1396-1434` | 标量路径**先把 FDCT 写回 `block[]`，再单独一轮遍历做量化**；SIMD 路径是融合的。同一 64 系数在标量侧被读-改-写两遍 → 阶段割裂 |
| `Processing/Processing.cs`<br>`155-201` ↔ `835-875`；`1062-1078` ↔ `1084-1117`；`1147-1172` ↔ `1196-1229` | 双线性有 `BilinearCore4` / `BilinearCore4Vnni` 两份近乎逐行重复的核心；双三次有 `BicubicCoreVnniV/V2`、`BicubicCoreVnni/Vnni2` 四份两两重复。**`BilinearCore4Vnni` 内部不含任何 VNNI 指令**（`:829-832` 自述），是纯冗余实现 |
| `Formats/Gif/OctreeQuantizer.cs`<br>`560-572` ↔ `583-624`；`635-664` ↔ `714-773` | 4 份映射实现，`:580` 注释自认"结构完全一致"。且 `:672` 的 `DitherOffset` double 公式在标量与 SIMD 侧各实现一次 |
| 死代码 | `SimdJpegEncodePipeline.cs:73` `ForwardDct8x8` 生产零调用（仅测试用）却与融合版双份维护；`SimdJpegPipeline.cs:163-169` `StoreRowSse2`、`:345-349` `ConvertRowYCbCrToRgb(Span)` 重载无调用点；`JpegFrameState.cs:4` 死 `using` |

## 3.4 结果不一致（位宽 / 饱和 / 舍入 / clamp 语义分歧）

| 位置 | 错误类型 |
|---|---|
| `Formats/Jpeg/SimdJpegPipeline.cs:262-270` | 反量化用 **`Vector128<short> * Vector128<short>`（16 位乘）**，而 `FastIDCT.cs:96-103` 用 int、`FloatingPointIDCT.cs:97` 用 float。系数×量化表超 int16 时 SIMD **静默回绕**，标量不会 |
| `Formats/Jpeg/SimdJpegPipeline.cs:80-112` | 蝶形中间量是 **int32**（`(z3o+z4o)*9633` 可达 ~1.3e9），而 `FastIDCT.cs:105-231` 全程 **long**。高码率下 SIMD 溢出 |
| `Formats/Jpeg/SimdJpegPipeline.cs:41-68` | 每趟 16 次 `Widen` + 8 次 `Narrow`，第一趟结果 `Narrow` 回 int16 截断/饱和；`FastIDCT.cs:59` 用 int32 工作区 |
| `Formats/Jpeg/SimdJpegPipeline.cs:302-304` | **两级窄化往返**：`PackSignedSaturate`(int32→int16) 紧接 `PackUnsignedSaturate`(int16→byte)，越界像素在第一级就被饱和到 ±32767，与 `FastIDCT.cs:248` 的逐像素 byte clamp 结果不同。且打包时 `Vector128<short>.Zero` 使**高 8 字节全被丢弃**，浪费一半 pack 吞吐 |
| 三套 IDCT 的 DC-only 捷径 | `FastIDCT.cs:26-57` `Descale(dc,3)+128`（round-half-up）；`FloatingPointIDCT.cs:40-64` `(int)(dc*0.125f+128.5f)`（向零截断）；`SimdJpegPipeline` **无此捷径** → 三种 DC-only 结果 |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:142, 199-206` | `LoadRow` 用 `Vector128.Narrow`（**带符号饱和** packssdw）压 int32→int16，标量 FDCT 全程 int32 **不 clamp**；融合路径最终系数也先饱和再量化。当前 8-bit 安全但无断言保护 |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:169` vs `JpegEncoder.cs:1794-1797` | 量化乘数位宽不对称：SIMD `abs * recip` 是 32 位，标量 `(long)v * recip` 是 64 位（`recip` 上界 131072） |
| 色度上采样算法分歧 | `SimdJpegPipeline.cs:244-247`（最近邻复制）vs `JpegReconstruct.cs:490-493`（双线性插值）→ **同一张 4:2:0 图在 Sse2 可用/不可用或 `useFloatingPointIdct` 开关下像素值不同** |
| `Processing/Processing.cs:1260` vs `:684/1050/1218` | 标量路径用 `if/else if` 分支夹取 [0,255]，SIMD 路径用 `Min/Max` 无分支 —— 同一算法两套语义 |
| `Formats/Gif/OctreeQuantizer.cs:448-450, 465-473` | LUT 回退值取「第一个非空子节点的叶子」而非几何最近色，注释自认约 0.24% 像素用错色 |

**已核对一致（无问题）**：JPEG 编码的定点系数（2446/3196/4433/6270/7373/9633/12299/15137/16069/16819/20995/25172）与标量逐项相同；`Descale` 偏移与算术右移一致；色彩系数 77/150/29、-43/-85/128、128/-107/-21 一致；PNG 滤波的 mod 256 回绕语义在标量与 SIMD 两侧均正确（无饱和运算误用）；Paeth 已做代数化简。

---

# 四、SIMD/AVX 指令选择的正确性与适用性

## 4.1 位宽不足（最突出）

| 位置 | 问题 |
|---|---|
| `Formats/Jpeg/SimdJpegPipeline.cs:275, 280` | **每块 2 次 `Transpose8x8`，各 24 条 unpack**。根因是 int32 在 128 位只有 4 lane，8 元素行装不下 → 只能靠转置把"行"换成"跨寄存器"。升到 AVX2（8×int32 恰好一行）可**完全消除两次转置** |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:179-197` | 同样问题：128 位 Widen 成 int32 后只剩 4 lane，8 点变换核心被迫**每 pass 调用 4 次**（lo/hi 各 2 次） |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs` 全文 | 只有 SSE2/SSSE3 128 位，**无任何 AVX2 路径** |
| `Formats/Png/PngDecoder.cs:937-968` | `Sse2.IsSupported` 在 x64 **恒为真**，导致 `:959` 的 `Vector<T>` 分支（可 256/512 位）**永不可达** → Up 滤波被锁死在 128 位，同时是死代码 |
| `Processing/Processing.cs:1019-1020` | 注释承认"本机 `AvxVnni.MultiplyWideningAndAdd` 仍降级为 pmaddwd（2 抽头）"，但 `:1023-1028` 仍按"一条 vpdpwssd 完成 12 个 MAC"设计去交错掩码 —— **注释与实测指令不符**，收益模型失真 |
| 全库 | 无 `Vector512` / AVX-512 任何路径 |

## 4.2 对齐

| 位置 | 问题 |
|---|---|
| `Core/SimdHelper.cs:361-452` `AlignedBuffer` | 已建 64 字节对齐基础设施，但**仅 PNG 行缓冲使用**（`PngDecoder.cs:732/760/786/812/841`、`PngWriter.cs:225`）。JPEG/GIF/BMP/Processing 全部走 GC 堆非对齐缓冲 |
| `PngDecoder.cs:937-946` | 行缓冲已 64 字节对齐，但 SIMD 侧仍用 `Vector128.LoadUnsafe`（**非对齐载入**）→ 已建对齐能力未接入指令选择，未用 `LoadAligned` |
| `Core/SimdHelper.cs:377, 435-451` | `AlignedBuffer` 实现 `IDisposable` + 终结器，PNG 解码**每行两次** `NativeMemory.AlignedAlloc/AlignedFree`；池化缓冲实现 `IDisposable` 是反模式 |
| `Processing/Processing.cs:164-174, 839-855, 1034` | 全部 `Unsafe.ReadUnaligned` / `LoadUnsafe`，含 `Vector256` 载入，**无任何对齐保证或对齐快速路径** |

## 4.3 分支规避与掩码

| 位置 | 问题 |
|---|---|
| `Processing/Processing.cs:691` | `if (x + 1 < width)` 位于 SIMD 主循环内且**恒为真**（`:620` `simdEnd = width - 1`，`:623-626` 只会减小）→ `:696-701` 是死代码，却每像素付一次分支 |
| `Processing/Processing.cs:1243` | `if (useSimdBase && q3 + 4 <= srcBytes)` 被逐像素循环调用，每像素两次条件判断 |
| `Processing/Processing.cs:183, 858` | `for (int c = 0; c < 3; c++)` 在核心内部，每次按 `c` 索引静态数组（带边界检查的载入） |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:233-242, 368-387` | `if (evenLeft)` / `if (evenRow)` 数据相关分支在 FDCT 核心与 16 行向量循环体内；`:346-347` 每行三元分支 |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:229-259` | FDCT 常量未提升为 `static readonly`，在核心内现场 `Vector128.Create`（11 个 × 4 次 core × 2 pass ≈ **88 次广播/块**）；`:244` 的 `Create(-Fix_...)` 还多一次运行时取负。同类 `QuantBias:70`/`C2:40`/`CConst128:41` 已提升 —— **同文件内不一致** |
| `Formats/Jpeg/SimdJpegPipeline.cs:271, 277, 288` | 每块/每行构造 `half`、`bias128` 常量 |
| `Formats/Png/PngDecoder.cs:1471-1475, 1850-1853, 1342/1351`<br>`BmpReader.cs:305, 562`<br>`JpegImage.cs:195/219/253/...` | 逐像素循环内 `switch (ColorType)`、调色板越界分支、循环不变 `if (invert)`/`if (accurate)` 分支 |
| `Formats/Jpeg/JpegImage.cs:179, 237, 479, 511` | **逻辑错误**：`HasAdobeTransform || !HasAdobeTransform` 恒为真 |

## 4.4 掩码与越界

| 位置 | 问题 |
|---|---|
| `Formats/Gif/GifDecoder.cs:535, 554` | `Vector128.LoadUnsafe` 一次读 **16 字节**，但循环条件只保证剩余 8（RGBA）/10（RGB）像素 → **尾部最多越界读 8 字节**（依赖 `ArrayPool.Rent` 的富余，非契约保证） |
| `Formats/Jpeg/SimdJpegEncodePipeline.cs:400-401` | `Convert8Pixels` 读 `src` 和 `src+8` 共 24 字节；420 路径 `rowPtr+24` 需 48 字节可读，完全靠"调用方保证整块落在图像内"（`:310/331` 注释） |
| `Processing/Processing.cs:694, 1112-1116, 1228` | 4/8 字节写顺带覆盖下一像素的 R，靠下一轮覆写 → 制造跨迭代的**写后覆盖假依赖**，阻碍并行与向量化 |
| `Formats/Gif/GifDecoder.cs:552` | RGB 主循环条件 `i + 10 <= pixelCount` 与"每次处理 8 像素"不匹配，每轮多留最多 9 像素走标量尾 |
| `Core/SimdHelper.cs:100-101, 1034, 1130-1131` / `Processing.cs:100-101` | `Unsafe.ReadUnaligned<uint/ulong>` 后按"低字节 = 第一个通道"做 shuffle —— **隐式小端假设**，大端平台 R/G/B 落错 lane |

---

# 五、内存访问模式阻碍向量化

| 位置 | 问题 |
|---|---|
| `Formats/Gif/OctreeQuantizer.cs:615, 759` | `_mapLut[buf[j]]` 15 位下标、32KB 表的**逐元素随机 gather**，每次可能跨 cache line |
| `Formats/Gif/OctreeQuantizer.cs:229-232, 265-268`<br>`Quantizer.cs:169-173, 219-223` | 直方图按 bin **随机散射**，4~5 路并行写放大 cache line 争用；每线程约 896KB（4 路 SoA），24 线程 ~21MB 全进 LOH |
| `Formats/Png/PngDecoder.cs:1063-1114, 655-668, 1349-1355` | Adam7 按 (dx,dy) **跨步散射写**；调色板路径逐像素间接 gather |
| `Formats/Png/PngDecoder.cs:748, 775, 801` | 每行 `curSpan.CopyTo(output)` —— **每行一次额外整行 memcpy 往返**（应直接重建进 output，仅交换 prev 指针） |
| `Formats/Jpeg/JpegReconstruct.cs:170-179, 204-213` | 边界 MCU：SIMD 写入 `stackalloc`(192B/768B) 后再**标量逐行 `CopyTo` 拷回** |
| `Formats/Jpeg/JpegReconstruct.cs:64-76`<br>`JpegFrameState.cs:854, 858` | 平面路径：IDCT 写整平面 → `InterleaveComponents` 再**标量扫一遍全部平面**，两遍全图访问 |
| `Formats/Jpeg/JpegFrameState.cs:549` | `block[zigzag[k]] = coef` 逐系数 zigzag 散射写 |
| `Formats/Bmp/BmpReader.cs:299-300/339-340/406-408/475-477`<br>`BmpWriter.cs:242-276` | bottom-up **反序遍历**：目标地址按整行跨度反向跳跃，cache/TLB 不友好 |
| `Formats/Png/Crc32.cs:79, 121-128` | 8×256 的 jagged `uint[][]`（8KB），每次迭代 8 次**二级指针跳转的非连续 gather**，应为扁平 `uint[8*256]` |
| `Processing/Processing.cs:1320` | `new Vector256<short>[width]` 裸分配，与 `:1282` 已 rent 的 `xWeight` 内容重复 |

---

# 六、编译器自动向量化 vs 手写内在函数的冲突/重复

| 位置 | 冲突类型 |
|---|---|
| `Formats/Jpeg/JpegEncoder.cs:1389-1427` | 同一函数内：手写 `Sse2` 内在函数（128 位，门控 `Sse2.IsSupported`）与跨平台 `Vector<int>`（JIT 自选，AVX2 下 256 位，门控 `Vector.IsHardwareAccelerated`）**并存**。两套向量系统的宽度倒挂，且门控谓词不同 |
| `Formats/Png/PngDecoder.cs:937-968` | `Sse2.IsSupported`（恒真）排在 `Vector<T>` 之前 → **JIT 自动向量化路径被手写内在函数完全屏蔽**，成为死代码 |
| `Processing/Processing.cs` 全文 | 同一算法混用 `Vector<T>`（`PngWriter.cs:251-265`）、`Sse2/Ssse3/Sse41`、`Avx2`、`AvxVnni` 四套，且**无任何 `Vector<T>`/`AdvSimd` 兜底** → ARM 上 `useSimd=false`、`simdEnd=0`，resize **100% 落到标量**（`:348-369, 704-757, 989-1001, 1253-1262`） |
| `Formats/Jpeg/SimdJpegPipeline.cs` 全文 | 直接调用 `Sse2.UnpackLow/High`、`Sse2.PackUnsignedSaturate`（**x86 专有内在函数**）而非跨平台 `Vector128` 等价 API → 无 ARM 回退；且**自身无 `Sse2.IsSupported` 门控**，完全依赖 `JpegReconstruct.cs:89` 的外部检查，门控脆弱 |
| `Formats/Jpeg/SimdJpegPipeline.cs:16-34` vs `FastIDCT.cs:10-21` | 同一批定点常量：前者声明为 `static readonly int`（运行时静态初始化 + 每次访问间接取），后者 `const int`（编译期折叠）→ 常量策略不一致，前者无法参与常量传播 |
| `Core/SimdHelper.cs:229, 266`<br>`BmpReader.cs:468` / `BmpWriter.cs:224`<br>`GifDecoder.cs:533, 548`<br>`LzwDecoder.cs:201-211`<br>`OctreeQuantizer.cs:516` | 只有 `Ssse3.IsSupported` / `Avx2` 单一门槛，**缺 `AdvSimd` 与 `Vector<T>` 回退**。对比 `SimdHelper.cs:42-55`、`PngDecoder.cs:948-961` 已有回退 —— 库内惯例不一致 |

---

# 七、优先优化方向（按收益排序）

1. **消除 SIMD→标量退化**：`OctreeQuantizer.cs:615/759`（改 3 张 32 项小表 + `pshufb` 全程留向量域）、`SimdJpegPipeline.cs:312-341`（`pshufb` 一次完成 RGB 交织）、`SimdHelper.cs:242-244`。
2. **升 256 位**：JPEG IDCT/FDCT 双管线用 AVX2 后，int32 恰好 8 lane 一行，**两次 `Transpose8x8` 与 lo/hi 拆分可整体消除**；同时补齐 `Processing.cs` 双线性/双三次的 256 位档。
3. **补标量热点**：Paeth/Sub/Average 滤波（PNG 解码最热，且 `:1017-1019` "不可并行"的判断是错的 —— bpp 条链互不依赖，可用 16 位 lane 一次算多字节）、Adler32、最近色查找（`Widen + MultiplyAddAdjacent`）、`ResizeArea.ProcessRow`。
4. **收敛多套实现**：JPEG 量化 3 套 → 1 套；`Processing.cs` 双线性 2 套 / 双三次 4 套 → 按宽度分档的单实现；删除 `ForwardDct8x8`、`StoreRowSse2`、`BilinearCore4Vnni` 冗余分支。
5. **统一门控与回退**：全库按「Avx2 → Sse2/SSSE3 → AdvSimd → Vector\<T\> → 标量」单一阶梯分派，并让 `Vector<T>` 档真正可达（修正 `PngDecoder.cs:937` 的判定顺序）；补 JPEG/GIF/BMP/Processing 的 ARM 路径。
6. **对齐与常量**：把 `AlignedBuffer` 接入 JPEG/GIF/Processing 主缓冲并改用 `LoadAligned`；`SimdJpegEncodePipeline.cs:229-259` 的 11 个 FDCT 常量、`Processing.cs` 的 `BicubicMaxSum/Round` 提升为 `static readonly`。
7. **消除冗余遍历**：PNG 每行 `CopyTo` 往返、JPEG 平面交织的第二遍全图扫描、`ComputeLastNz` + `HuffmanWriteBlock` 对同一块的两次 zigzag 扫描（应与量化融合为一趟）。
8. **修正语义分歧**：`SimdJpegPipeline.cs:262-270` 的 16 位反量化乘、`:302-304` 的两级饱和 pack、三套 IDCT 的舍入与 clamp 契约，以及 `JpegImage.cs:179/237/479/511` 的恒真条件。