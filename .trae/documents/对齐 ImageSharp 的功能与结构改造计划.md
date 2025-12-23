## 目标
- 在不引入第三方库、尽量避免 unsafe 的前提下，使项目在“结构与API形态”上尽量对齐 SixLabors.ImageSharp，并逐步扩充“功能覆盖”。
- 保持跨平台与 AnyCPU；优先 net8.0（或继续 net10.0）作为目标框架。

## 总体架构
- 引入分层与命名空间：
  - `Core`：像素类型、图像容器、缓冲管理、配置与注册机制。
  - `Formats`：`JPEG/PNG/BMP` 的 `IImageDecoder/IImageEncoder` 实现与格式嗅探器。
  - `Processing`：图像处理算子与 `Mutate/Clone` 管线。
  - `Metadata`：`ImageMetadata`、`ExifProfile`、`IccProfile`（先实现最小 sRGB）。
  - `Color`：像素与色彩空间转换（RGB/YCbCr/Gray）。

## 核心抽象（对齐 ImageSharp 风格）
- `Image<TPixel>`：通用图像容器，持有 `Width/Height`、像素缓冲（`Memory<TPixel>`），支持多帧（`ImageFrame<TPixel>`）。
- `IPixel` 与若干常用像素类型：`Rgb24`、`Rgba32`、`Gray8`（先做 3 个）。
- `IImageFormat`、`IImageDecoder`、`IImageEncoder`、`IImageInfo`：统一解码/编码入口与格式信息。
- `Configuration`：注册所有已支持的 `IImageFormat`、编解码器与嗅探器。
- `Image.Load/Save` 与 `DecoderOptions/EncoderOptions`：面向用户的统一 API。

## 嗅探与IO统一
- `FormatDetector`：基于文件头（magic numbers）与部分元数据的嗅探，实现 `TryDetectFormat(stream)`。
- 统一入口：`Image.Load(stream|path)` 调用 `Configuration` 中注册的解码器完成解析；`image.Save(path, options)` 选择合适编码器。

## 现有代码映射与改造
- 现有 `JpegParser/JpegDecoder/Idct/JpegQuantTable/JpegHuffmanTable/BitReader` 移入 `Formats.Jpeg`，做成 `JpegDecoder : IImageDecoder`；`JpegEncoder : IImageEncoder`。
- `PngDecoder/PngWriter` 移入 `Formats.Png`；`BmpReader/BmpWriter` 移入 `Formats.Bmp`。
- 现有 `ImageFrame` 升级/重命名为 `Image<TPixel>` 与 `ImageFrame<TPixel>`；现阶段默认解码到 `Rgb24`，后续扩展到 `Rgba32/Gray8`。

## 处理管线与算子
- 引入 `IImageProcessor` 与 `ImageProcessingContext`，实现 `image.Mutate(ctx => ctx.Resize(...).Grayscale().Rotate(90))` 风格。
- 第一批算子：
  - Resize（Nearest、Bilinear、Bicubic）
  - Crop、Rotate/Flip
  - Grayscale、Brightness/Contrast
  - Blur/Sharpen（盒滤波/高斯的简化实现）
- 算子以 `TPixel` 泛型实现，必要时通过 `PixelOperations` 完成通道映射与向量化（优先 `System.Numerics.Vector<T>`，不依赖架构专用指令）。

## 元数据与色彩
- `ImageMetadata`：尺寸、分辨率、色彩空间引用等；
- `ExifProfile`：解析/应用 Orientation（已在现有路径上支持，升级到通用容器）；
- `IccProfile`：最小实现读取与 sRGB 应用占位（后续扩展至实际转换）。

## 性能与正确性专项
- JPEG：
  - 将 FDCT/IDCT 优化为 AAN 整数版本（保留 double 版本作参考），统一 ZigZag（已修复），缓存 Huffman 快表，完善 Restart 处理。
  - 编码端加入 4:2:0 子采样选项、量化表自适应（quality 1–100），逐步支持 Progressive 编码（后置）。
- PNG：
  - 解码端块级缓冲与过滤器优化；写入端支持 Alpha/调色板与更多过滤器策略。
- 内存：
  - 引入 `ArrayPool<T>` 做行/块级临时缓冲；避免过度分配。
- ARM 适配：
  - 优先 `System.Numerics`，避免 `System.Runtime.Intrinsics`；确保 AnyCPU。

## 测试与基准
- 单元测试：格式嗅探、像素一致性、算子正确性、EXIF 方向；
- Golden 图像：对比参考输出（哈希/PSNR 约束）；
- Fuzz：对解码器做异常与边界的健壮性验证；
- Benchmark：关键路径（IDCT、Resize、解码循环）。

## API 对齐示例
- `var image = Image.Load("a.jpg");`
- `image.Mutate(x => x.Resize(800, 600).Grayscale());`
- `image.Save("out.png", new PngEncoderOptions { CompressionLevel = 6 });`
- `Configuration.Default.TryAddFormat(new WebpFormat(), new WebpDecoder(), new WebpEncoder());`（示例占位）

## 渐进式里程碑
1. 搭建 `Core` 抽象与 `Image<TPixel>`，把现有解码输出对齐到 `Image<Rgb24>`。
2. 完成 `Configuration/FormatDetector` 与三种格式的 `IImageDecoder/IImageEncoder` 封装；`Image.Load/Save` 跑通。
3. 引入处理管线与基础算子（Resize/Crop/Rotate/Grayscale）。
4. 元数据容器与 EXIF 统一应用；ICC 占位。
5. JPEG AAN 整数 IDCT/FDCT 与 4:2:0 子采样；PNG Alpha/调色板增强。
6. 测试与基准全面补齐；CLI 示例迁移到新 API。

## 风险与取舍
- 完全对齐 ImageSharp 的所有功能需要较长周期（多格式、多像素类型、完整 ICC）；本计划采取“API 形态先对齐、功能分阶段拓展”。
- 保持零第三方依赖与 AnyCPU 将限制某些极致性能优化（SIMD 专属指令），但不影响正确性与通用性。

## 输出与验收
- 提供新 API 的最小可用版本（Load/Mutate/Save），三种格式全通。
- 附带测试报告与示例。
- 持续对齐：补充像素类型、算子、元数据、性能优化。