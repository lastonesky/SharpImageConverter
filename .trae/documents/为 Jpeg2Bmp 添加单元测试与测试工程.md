## 测试目标
- 覆盖格式嗅探、解码、编码的核心路径：`Core.Configuration.LoadRgb24` 与 `SaveRgb24`（d:\Project\jpeg2bmp\Core\Configuration.cs:34、49）、格式匹配器（d:\Project\jpeg2bmp\Formats\Formats.cs:11、23、35）、适配器（d:\Project\jpeg2bmp\Formats\Adapters.cs）。
- 覆盖图像处理 API：`Resize` 与 `Grayscale`（d:\Project\jpeg2bmp\Processing\Processing.cs:15、36），以及 `ImageExtensions.Mutate`（d:\Project\jpeg2bmp\Processing\Processing.cs:56）。
- 覆盖中间层：`ImageFrame.Load/Save` 以及 EXIF 方向应用（d:\Project\jpeg2bmp\ImageFrame.cs:29、71、106）。

## 测试框架
- 新建 `Jpeg2Bmp.Tests` 测试工程，目标框架 `net10.0`。
- 使用 MSTest（官方框架）与 `Microsoft.NET.Test.Sdk`、`MSTest.TestAdapter`、`MSTest.TestFramework`。
- 通过 `dotnet test` 在 Windows/Linux/macOS 上跨平台运行。

## 工程结构
- 新增目录与文件：
  - `Jpeg2Bmp.Tests/Jpeg2Bmp.Tests.csproj`（测试工程，引用主工程 `Jpeg2Bmp.csproj`）。
  - `Jpeg2Bmp.Tests/Helpers/TestImageFactory.cs`（生成小尺寸、可预测的 RGB 测试图像：纯色、棋盘格、渐变）。
  - `Jpeg2Bmp.Tests/Helpers/BufferAssert.cs`（缓冲区断言：精确比较与“误差容忍”比较，用于 JPEG）。
  - `Jpeg2Bmp.Tests/FormatConversionTests.cs`（格式嗅探与编解码往返）。
  - `Jpeg2Bmp.Tests/ProcessingTests.cs`（Resize 与 Grayscale 精确行为测试）。
  - `Jpeg2Bmp.Tests/ImageFrameTests.cs`（`Load/Save` 与 `ApplyExifOrientation` 单元测试）。
  - `Jpeg2Bmp.Tests/BmpPaddingTests.cs`（24-bit BMP 行对齐与读写一致性）。

## 覆盖范围
- 嗅探：对 JPEG/PNG/BMP 头部的最小样本内存流进行 `IsMatch` 验证（d:\Project\jpeg2bmp\Formats\Formats.cs:11、23、35）。
- 解码：通过 `Core.Image.Load(path)` 读取三种格式（d:\Project\jpeg2bmp\Core\Image.cs:29）。
- 编码：通过 `Core.Image.Save(image, path)` 输出三种格式（d:\Project\jpeg2bmp\Core\Image.cs:34、d:\Project\jpeg2bmp\Core\Configuration.cs:49）。
- 处理：`Resize` 最近邻的采样位置映射与 `Grayscale` 亮度计算（d:\Project\jpeg2bmp\Processing\Processing.cs:22、44）。
- 中间层：`ImageFrame.SaveAsBmp/Png/Jpeg` 的选择与尺寸校验，EXIF 方向 1–8 的变换（d:\Project\jpeg2bmp\ImageFrame.cs:91、96、101、113）。

## 具体用例
- FormatConversionTests
  - 生成 2×2 棋盘格 RGB 图，分别写出为 `.bmp`/`.png`/`.jpg`。
  - 读取回来的 `Width/Height` 与像素内容：
    - BMP/PNG：逐字节完全一致。
    - JPEG：允许小的压缩误差，断言 MSE/PSNR 在阈值内（例如 MSE ≤ 2.0）。
  - `Image.Load` 与 `Image.Save` 包装器路径验证（d:\Project\jpeg2bmp\Core\Image.cs:29、34）。
- ProcessingTests
  - Resize：
    - 2×2 到 1×1：像素应等于原图左上角（最近邻）。
    - 3×3 到 6×6：检查采样映射是否重复采样正确（d:\Project\jpeg2bmp\Processing\Processing.cs:22–31）。
  - Grayscale：挑选一个 RGB=(10,200,50)，期望 Y=(77R+150G+29B)>>8，断言三个通道相等（d:\Project\jpeg2bmp\Processing\Processing.cs:44–49）。
- ImageFrameTests
  - SaveAsBmp/SaveAsPng/SaveAsJpeg：小图往返一致性（JPEG 使用较高质量如 90 以减小误差）。
  - ApplyExifOrientation：构造 2×3 有标识像素的图，分别测试 1–8，断言尺寸变化与关键像素位置映射（d:\Project\jpeg2bmp\ImageFrame.cs:113–167）。
- BmpPaddingTests
  - 针对宽度 1、2、3、4、5 的小图写入 BMP 并读取，验证行填充与像素重建一致（d:\Project\jpeg2bmp\BmpWriter.cs / d:\Project\jpeg2bmp\BmpReader.cs）。
- SniffingTests
  - 以内存流构造 `FF D8`（JPEG）、`89 50 4E 47 0D 0A 1A 0A`（PNG）、`BM`（BMP）头，验证 `IsMatch` 为真；随机其它头为假。

## 辅助方法与策略
- `TestImageFactory`
  - `CreateSolid(width,height,(r,g,b))`
  - `CreateChecker(width,height,(r1,g1,b1),(r2,g2,b2))`
  - `CreateGradient(width,height)`（横向或纵向线性渐变）。
- `BufferAssert`
  - `EqualExact(byte[] a, byte[] b)`
  - `EqualWithTolerance(byte[] a, byte[] b, byte perChannelMaxDelta)`
  - `Mse(byte[] a, byte[] b)` + `AssertMseLessThan(threshold)`（用于 JPEG）。
- 临时文件：使用 `Path.GetTempFileName()` / `Path.Combine(Path.GetTempPath(), ...)` 创建，并在 `TestCleanup` 删除。

## 运行方式
- 在解决方案根目录运行：`dotnet test`。
- 所有测试使用小尺寸样本（≤ 8×8），保证快速且稳定。

## 通过标准
- BMP/PNG 往返：尺寸与像素完全一致。
- JPEG 往返：尺寸一致；像素 MSE/PSNR 在设定阈值内（默认质量 75 时 MSE ≤ 2.0，质量 90 时 MSE ≤ 1.0）。
- Resize/Grayscale：按最近邻与公式计算严格匹配。
- EXIF 变换：1–8 的尺寸与关键像素映射均正确。

## 后续扩展
- 增加针对大图的性能测试（非单元测试）：解码耗时、写入耗时统计。
- 引入更多边界用例：异常输入、损坏文件头、奇异尺寸（0/负值防御已在构造器）。
- 适配未来 `net` 版本：如需稳定版可将主工程与测试工程切换到 `net8.0/net9.0`。