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
