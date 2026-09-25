## 未发布
### 规则
- 性能优化准入阈值：**提升不足 7% 的改动算作无效提升，不保留、不提交。**
  该量级的收益与测量噪声无法可靠区分，却会持续增加代码复杂度与维护成本。
  判定取重复测量的**中位数**（`--gif-bench N` 的 median），不取单次、不取 min/max；
  单阶段提速但端到端未达标的同样按无效处理。

### 改进
- 新增八叉树量化 + Bayer 有序抖动（`OctreeQuantizer`），作为 GIF 编码默认量化器；
  原 Wu 量化 + Floyd–Steinberg 误差扩散（`Quantizer`）保留，可通过 `GifEncoder.QuantizerKind`
  / 适配器 `QuantizerKind` / CLI `--gif-quantizer wu` 切回。新方案用 5-bit 直方图喂入深度 5
  八叉树、归约到 ≤256 叶、映射走 5-bit LUT，抖动改用无依赖链的 Bayer 4×4（替代误差扩散串行链），
  量化阶段显著更快；输出**不**与原实现保持 MD5 一致（设计使然，见项目约定）。`DitherStrength` 可调。
- **修正 Bayer 抖动默认值：48 → 8（一个量化步长）。** 原默认值把抖动幅度设成了量化步长的约 5.6 倍
  （偏移 `[-23, +22]`，而 5-bit LUT 的步长为 8），属严重过抖动。实测（平滑渐变 512×512 +
  `examples/Amish-Noka-Dresser.jpg`）：
  | 指标 | S=0(不抖) | **S=8(新默认)** | S=48(旧) | Wu+FS |
  |---|---|---|---|---|
  | 照片总误差 RMS | 1.30 | **2.03** | 12.85 | 1.96 |
  | 照片颗粒 RMS | 0.91 | **1.73** | 11.56 | 1.79 |
  | 照片 1/4 缩放走样 RMS | 10.99 | **11.31** | 22.99 | 11.01 |
  | 渐变最长同值游程(px) | 64 | **3** | 1 | 15 |
  即 S=48 相对 S=8 **颗粒大 6.7 倍、缩小时最近邻走样大 2 倍，而色带抑制没有任何额外收益**
  （块间跳变同为 4.12、最长同值游程已降到 3）。过强的抖动还把 4×4 网格变成强周期图案，
  在看图软件用最近邻采样缩放时被走样成摩尔纹（Wu+Floyd–Steinberg 无周期结构故不受影响）。
  附带收益：索引更规整，143 MP 图编码 total 中位 746.0 → 684.3 ms（**−8.3%**）。
  新增 CLI `--gif-dither N` 与 `GifEncoder.DitherStrength` / 适配器 `DitherStrength` 可调。
- `OctreeQuantizer` 的 Bayer 映射阶段整数化 + SSSE3 向量化。原实现逐像素做 3 次
  「byte→float 转换 + 浮点加 + `Math.Clamp`(两次比较分支) + float→int 截断」，实测占整个量化 78%。
  改写分两步：① 因 v 为整数时 `floor(v+x) = v + floor(x)`，可把 `t*strength` 精确化为整数偏移
  `floor(strength*(2k-15)/32)`（strength=48 时即 `3k-23`），再把「加偏移 + clamp + >>3」
  预计算成 16 相位 × 256 项的 5-bit 表，逐像素只剩 3 次查表；② 进一步用 SSSE3 一次处理 16 像素：
  `pshufb` 三路反交错出 R/G/B 平面，16 位通道内 `paddw + pminsw/pmaxsw` 饱和 + `psrlw 3`，
  合成 16 个 cube 下标。cube→调色板索引的 32768 字节 gather 无法向量化（AVX2 无按字节、
  15 位下标的 gather，dword gather 反而更慢），故保留标量。
  `examples/progressive.jpg`(10650×13426) `--gif-bench 11` 中位数：quantize 553.1 → 215.0（整数化）
  → 115.9 ms（SIMD），编码 total 1164.1 → 719.1 ms；相对原 Wu 量化器 quantize 快 11.4x。
  5 张样例图产物与优化前**逐字节一致**（md5 相同）。
  注：与 `docs/PerfReport.md` 中「量化不可 SIMD」的结论不同——该结论针对 Floyd–Steinberg 的
  **串行误差扩散链**（依赖链受限）；Bayer 逐像素独立、无跨像素依赖，属吞吐受限，故 SIMD 有效。
- GIF 量化器直方图按图片尺寸选择单/多线程：原实现无条件 `Parallel.For`，每个 worker 都要从 `ArrayPool`
  租借并清零 5 个 35,937 元直方图（约 1.44 MB/线程），最后在 `lock` 内把全部 35,937 个 bin 串行合并回主直方图。
  这个固定成本与图片大小无关，小图上远大于并行省下的扫描时间。现按
  `60_000 × ProcessorCount + 300_000` 像素为界：低于阈值单线程直接累加进主直方图，省掉租借/清零与合并。
  阈值随核数缩放（开销随线程数增长）：实测交叉点 4 核约 0.5 MP、8 核约 1.05 MP、24 核约 1.6 MP。
  24 核 2400×1800→各尺寸 `--gif-bench` 中位数（抖动开）：512×512 10.010→5.991 ms（**−40.1%**）、
  800×600 12.659→9.274（**−26.7%**）、1024×1024 20.208→18.060（**−10.6%**）、1280×720 −8.9%、
  1440×900 −3.3%，≥1.9 MP 回落到并行路径（−1% 以内，属噪声）。抖动关闭时收益更大（小图 −44%~−50%）。
  8 个尺寸 × 抖动开/关共 16 组产物与优化前**逐字节一致**。
- LZW 编码器字典查找重构：原哈希 `h = (c << 4) ^ ent` 因 `c <= 255`、`ent < 4096` 恒落在 `[0, 4095]`，
  8191 个槽位实际只用一半，装载因子为 1.0，探测链严重退化。改为 64 位乘法散列（Fibonacci hashing）
  配 32768 槽位（装载因子约 0.12）+ 线性探测；槽位打包为 `((fcode + 1) << 12) | code`，
  键与值落在同一条 cache line，一次探测一次加载。槽位 0 表示空，键存 `fcode + 1` 避免
  `(c=0, ent=0)` 与空标记冲突。表经 `ArrayPool` 租用，避免动画逐帧触发大对象 GC。
  2400×1800 实测编码 LZW 28.998ms → 21.245ms（1.37x），输出字节与优化前完全一致。
- ~~LZW 解码器改为直写输出~~ **已回退**：该改动实测解码 LZW 12.875ms → 12.025ms（仅 1.07x），
  低于 7% 准入阈值，按规则不保留，解码器已还原至优化前实现。
  （注：该项实测 1.07x = +7%，按当前 7% 阈值已达标，恢复前须重新确认产物 md5 逐字节一致。）
  实验结论（含同轮实测否决的三个改动：一次补 4 字节的位缓冲 -12%、
  prefix/suffix 打包为单个 int -5%、二级跳转表一次展开两像素 -5%）
  已沉淀至 `docs/PerfReport.md` 的「GIF LZW 优化实验记录」，避免重复尝试。
- 新增 GIF 编解码耗时统计：`GifTiming` 按阶段记录 quantize / prepare / header / lzw / render / background
  与未归入阶段的其余时间，并给出占比与吞吐（Mpx/s）。`GifEncoder` / `GifDecoder` 及其适配器新增
  `EnableDiagnostics`、`DiagnosticsLog` 与 `LastTiming`：默认关闭，关闭时热路径不调用 Stopwatch（零开销），
  开启后报表经 `DiagnosticsLog` 输出，未设置时回落到 `Trace`。
  CLI 新增 `--gif-debug`（单次转换的分阶段耗时）与 `--gif-bench N`（重复 N 次取最小/中位/平均，只测量不产出文件）。
- `SimdHelper.AddBytesInPlace` 改为真正的 SIMD：SSE2/AdvSimd 下用 128 位整字节加法（`paddb`，天然 mod 256 回绕），移除原先 Widen/Narrow 的迂回实现。
- 新增 `SimdHelper.GrayscaleRgb24InPlace`：SSSE3 `pshufb` 三路反交错 + 16 位定点加权，每批 16 像素，`Processing.Grayscale()` 已接入。
- 新增 `SimdHelper.ExpandGrayToRgb` / `PackRgbaToRgb` / `ExpandRgbToRgba`，替换 `Configuration` 中的逐像素格式互转，并用 `GC.AllocateUninitializedArray` 避免多余清零。
- `Processing.ResizeBicubicOptimized` 移除 `Vector<float>` 伪 SIMD 脚手架（声明了向量缓冲但内层全为标量），重写为干净的标量实现，并复用行基址减少重复乘法。
- Resize 系列的目标缓冲区改为 `GC.AllocateUninitializedArray`，与池化的索引/权重数组配合降低分配开销。
- JPEG 量化表与整数 DCT reciprocal 表按 quality 缓存（质量已归一化到 `[1,100]`），避免每次编码重建。
- 清理 `JpegEncoder` 中 `// float FDCT removed` 残留注释，改为说明为何只用整数 FDCT；移除 `SimdHelper` 中无主代码调用的 `GetVectorPaddedByteLength` / `AllocateAligned<T>` 泛型重载。
- `Processing.ResizeBilinear` 新增 SSSE3+SSE4.1 路径：一次 8 字节载入同时取到 x0 与 x1 两个像素，
  `pshufb` 拼出 `[a0,b0,a1,b1,...]` 交错 short，`pmaddwd` 用 q/r 拆分精确还原 11 位权重，
  `pmulld` 做垂直插值，每批 4 个像素。数值与标量路径逐位一致。
- `Processing.ResizeArea` 把只依赖 dx / dy 的区间与重叠权重提到循环外预计算，内层只剩乘加累加。
- `ImageFrame.ApplyExifOrientation` 把方向分支提到双层循环外；case 4 改为整行块拷贝；
  case 5-8（转置类）改为 32×32 分块转置，避免目标端按整行跨度写入导致的缓存抖动。
- `ImageFrame.ApplyExifOrientation` 与 `Processing.Clone` 的目标缓冲改用 `GC.AllocateUninitializedArray`。
- `JpegEncoder` 中 `new Vector<int>(span)` 改为 `Vector.LoadUnsafe`，去掉中间拷贝。
- `PngWriter.ApplyUpFilterSimd` 同样改为 `Vector.LoadUnsafe` / `StoreUnsafe`，去掉每步 `Slice(i)` 的
  重复边界检查。滤波函数本身提速 1.36-1.85x，但对 PNG 编码端到端只有 ~0.1%（滤波仅占编码耗时 0.3%）。
- 修正 `Processing.ResizeBilinear` 的 XML 文档注释：此前它被挤到了 SIMD 掩码字段上，导致 warning CS1572。
- `Processing.ResizeBicubicOptimized` 新增 SSSE3+SSE4.1 路径：把 4 像素×4 抽头的 16 次取样压成一次 4 字节载入
  拿到 R/G/B 三条 lane，水平与垂直各做 4 次 `mulps`+`addps`。结合顺序、
  夹取与取整方式都对齐标量实现，输出逐位一致。`examples/progressive.jpg`（10650×13426）
  上实测 1.5x 放大 176.5ms → 49.0ms（3.6x）、2x 放大 326.7ms → 91.1ms（3.6x）。
  SIMD 覆盖不到两个边界情况，均回退标量：抽头夹到最后一个源像素（4 字节载入越界）、
  每行最后一个输出像素（只能按 3 字节写）。
- `Processing.ResizeBilinear` 的 SIMD 循环从每批 4 像素扩到 8 像素：两批之间没有数据依赖，
  两条依赖链可以并行发射。同样条件下实测 2x 放大 133.6ms → 125.4ms（1.07x）、
  4x 放大 537.5ms → 496.9ms（1.08x）；缩小方向受限于写带宽，基本持平（±2%）。
- 把 `ResizeBilinear` 的定点常数（Shift / Scale / RoundingOffset）提到类级别，
  以便抽出的 `BilinearCore4` 辅助方法复用。

### 工具
- 新增 `tools/gif-compare.sh` 与 `tools/gif-timing-master.patch`：把 GIF 计时代码移植到
  基准分支（默认 master）的临时 git 工作树上，让两个分支跑同一套 `--gif-bench` 口径，
  用于量化优化前后的差异。补丁针对 master @ `0bbf1f9`，若该分支的 `GifDecoder` 再有改动需重新移植。

### 文档
- 工程类文档统一收拢到 `docs/`：`PerfReport.md`、`AuditVerification.md`、`goal.md` 移入，
  JPEG 规范原文移入 `docs/reference/`，并新增 `docs/README.md` 作为索引。
  `README.md` / `README.en.md` / `CHANGELOG.md` / `THIRD-PARTY-NOTICES.md` 按生态约定保留在根目录，
  `.trae/rules/project.md` 因 IDE 按固定路径读取而保持原位。
- `goal.md` 中的引用由失效的绝对路径（`file:///d:/...`，盘符本身已错）改为仓库内相对路径。
- 删除根目录误留的 `gcm-diagnose.log`（Git Credential Manager 诊断输出，未被 git 跟踪，
  内容仅含环境变量名与路径，无凭据值）。

### 测试
- 新增 `SharpImageConverter.Tests/SimdPixelOpsTests.cs`，覆盖新增 SIMD 路径与标量实现的逐字节一致性、0..70 长度边界、mod 256 回绕语义，并显式断言本机 SSSE3 可用以免 SIMD 分支漏测。
- 新增 `SharpImageConverter.Tests/ResizeConsistencyTests.cs`，用改动前的原样算法做参考实现，逐位校验
  ResizeBilinear（13 组尺寸 + 4900 组小尺寸穷举）与 ResizeArea。
- 新增 `SharpImageConverter.Tests/ExifOrientationTests.cs`，对 8 个方向在多种尺寸（含跨分块边界）下逐像素校验。
- `ResizeConsistencyTests` 补上此前缺失的双三次覆盖：新增 `RefBicubic` 参考实现（改动前的原样标量算法）、
  13 组尺寸的 `[Theory]` 用例，以及 8×7×8×7 的小尺寸穷举扫描——用于逼出 SIMD 的两个回退边界。
  把 `ResizeBicubicOptimized` 的截断转换改回最近取整即可看到 11 个用例失败，说明确实覆盖了 SIMD 分支。

## 0.2.2（相对 v0.2.1）
### 改进
- JPEG 解码流程重构，统一使用 ImageFrame.LoadJpeg，减少分叉路径与维护成本。
- JPEG IDCT 内核拆分为两阶段，降低运行时计算开销。
- PNG 解码的像素转换使用不安全指针路径优化性能。
- PNG 写入的 Up 过滤器 SIMD 优化，减少内存分配。
- 图像解码与处理整体路径减少内存分配与拷贝。
- JpegImage 移除未使用的缓存字段，清理冗余状态。

### 修复
- JPEG 图像边界处理修复，避免潜在越界访问。
- CLI JPEG 解码修复元数据丢失与方向处理问题。
- BMP 格式检测与读写流程修复并增强健壮性。

## 0.2.1（相对 v0.2.0）
- 版本号：`src/SharpImageConverter.csproj` 从 `0.2.0` 升级为 `0.2.1`。
- 变更范围：共 6 个文件，约 `+397 / -334`（`git diff v0.2.0..HEAD --stat`）。
- 主要方向：JPEG 编解码路径继续收紧内存分配策略（ArrayPool/MemoryPool），并修复测试侧对静态解码 API 的调用方式。

### 代码改动摘要
- `src/Formats/Jpeg/JpegEncoder.cs`
  - 位流写缓冲改为池化租借/归还，减少每次编码时的临时分配。
  - 有序 Huffman 阶段的 pending 容器改为池化扩容与回收，降低 GC 压力。
  - ICC APP2 分片写入改为基于 `MemoryPool<byte>` 的复用缓冲写出。
  - Huffman 表改为静态复用，并修正静态初始化顺序问题。
- `src/Formats/Jpeg/JpegDecoder.cs`
  - `Decode(Stream)` 路径改为池化读取，去掉 `MemoryStream + ToArray` 方式。
  - ICC 收集器改为池化 chunk 管理，并在同步/异步解析完成后统一释放。
- `SharpImageConverter.Tests/*.cs`
  - 测试代码切换到静态 `JpegDecoder` API（`Decode` / `DecodeFromStreamAsync`）。
  - 兼容 `JpegImage` 当前接口，移除不可用成员调用。

## 0.2.0
- 相比 0.1.6，本版本对 JPEG/PNG/GIF/BMP/WebP 全链路进行了较大规模重构与优化，总体聚焦性能、稳定性与流式处理能力。
- JPEG：引入 SIMD 优化的 IDCT 与颜色转换、MCU 批处理与流水线并行编码；新增 APP2 ICC 配置文件支持与可选 CMYK 转换模式；修复多个解码验证问题与资源泄漏问题。
- PNG：解码器采用数组池、定点计算与对齐内存缓冲优化内存与性能，减少不必要颜色转换，并提升网络流读取健壮性。
- GIF：量化器升级为基于直方图的 K-means 实现并改进 LZW 编码，支持抖动开关；补充动画编码能力、并发控制与元数据支持。
- WebP：修复非可查找流（non-seekable stream）解码问题，提升在网络流场景下的兼容性。
- Core/BMP/Processing：新增 SIMD 通用辅助能力，优化 BMP 读写与双线性缩放性能，补充尺寸检查并降低部分路径的内存分配。
- 测试与示例：新增格式检测、元数据、并行处理等测试覆盖，并更新示例脚本与发布流程相关配置。

## 0.1.7
- 修复webp解码错误
## 0.1.6.2-preview
- 优化GIF/PNG的网络流处理能力和图片缩放方法中的内存分配方式
## 0.1.6.1-preview
- 优化缩放处理方法
## 0.1.6
- 重点优化了GIF编码的性能和JPEG编码的性能
## 0.1.5
- JPEG解码添加了CMYK和YCCK的颜色支持 (Added CMYK and YCCK color support in JPEG decoder)
## 0.1.5-preview
- 将各格式的实现，放入对应的子命名空间中 (Moved each format implementation into its own sub-namespace)
- Jpeg解码添加了Stream支持 (Added stream support to JPEG decoder)
## 0.1.4
- 恢复JpegEncoder到重构之前的版本 (Reverted JpegEncoder to the pre-refactor implementation)
- 完善灰度格式之间转换的逻辑 (Improved conversion logic between grayscale formats)

## 0.1.4.1-preview
- 添加对灰度 PNG 和灰度 BMP 格式的支持 (Added support for grayscale PNG and grayscale BMP)

## 0.1.3
- 修复JpegDecoder 解码 /examples/Amish-Noka-Dresser.jpg 错误的问题 (Fixed JpegDecoder decoding error for /examples/Amish-Noka-Dresser.jpg)
- PNG：在多数图片上显著降低压缩后体积（例如原先约 110 MB 的 PNG，现在可缩小到约 30 MB，具体效果取决于图像内容），同时略微提升压缩速度。 (PNG: Significantly reduced output size for many images (e.g., ~110 MB down to ~30 MB, depending on content) with slightly faster compression)
- JPEG：大幅提升解码速度，并在编码路径上带来小幅性能提升。 (JPEG: Greatly improved decode performance and modestly improved encode performance)
- BMP：写出路径经过优化，在同一环境下写出约 400 MB 的 BMP 文件，耗时从约 400 ms 降低到约 270 ms。 (BMP: Optimized write path; writing a ~400 MB BMP now takes ~270 ms instead of ~400 ms on the same machine)
