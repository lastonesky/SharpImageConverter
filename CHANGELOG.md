## 未发布
### 改进
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
