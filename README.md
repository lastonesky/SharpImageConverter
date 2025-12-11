# Jpeg2Bmp

一个用 C# 编写的图像转换工具，不依赖第三方库（不使用 `System.Drawing`），支持 JPEG/PNG/BMP 格式的相互转换。

## 功能特性

### JPEG 支持
- 基线（Baseline）与渐进式（Progressive）解码
- Huffman 解码、反量化、IDCT、YCbCr 转 RGB
- 支持 EXIF Orientation 自动旋转/翻转

### PNG 支持
- 读取：
  - 支持关键块（IHDR, PLTE, IDAT, IEND）及透明度（tRNS）
  - 支持所有过滤器（None, Sub, Up, Average, Paeth）
  - 支持 Adam7 隔行扫描
  - 支持灰度、真彩色、索引色及带 Alpha 通道的格式
- 写入：
  - 支持将 RGB 数据保存为 Truecolor PNG
  - 使用 Zlib 压缩（Deflate）

### BMP 支持
- 读写 24-bit RGB BMP
- 支持自动填充对齐

## 目录结构

```
Jpeg2Bmp/
├── JpegParser.cs      # JPEG 段解析
├── JpegDecoder.cs     # JPEG 解码核心
├── PngDecoder.cs      # PNG 解码器
├── PngWriter.cs       # PNG 编码器
├── BmpReader.cs       # BMP 读取器
├── BmpWriter.cs       # BMP 写入器
├── ZlibHelper.cs      # Zlib/Deflate 封装
├── Crc32.cs/Adler32.cs# 校验算法
├── Program.cs         # 程序入口
└── ...
```

## 使用方法

环境要求：
- .NET SDK (net10.0 preview)
- Windows/Linux/macOS (只要支持 .NET)

命令行参数：
```bash
dotnet run -- <输入文件> [输出文件]
```

支持的转换：
- JPEG -> BMP/PNG
- PNG -> BMP/PNG (重编码)
- BMP -> PNG/BMP (重写)

程序会自动根据输入文件扩展名（.jpg, .png, .bmp）识别格式，并根据输出文件扩展名选择保存格式。若未指定输出文件，默认转换为 BMP（对于 JPEG）或 PNG（对于 BMP）。

示例：
```bash
# JPEG 转 PNG
dotnet run -- image.jpg image.png

# PNG 转 BMP
dotnet run -- icon.png icon.bmp

# BMP 转 PNG
dotnet run -- screenshot.bmp screenshot.png
```

## 已知限制

- JPEG 上采样目前使用最近邻插值。
- PNG 写入目前仅支持 Truecolor 格式，不保留源文件的调色板或元数据。


## 下一步计划

- 优化上采样算法（实现双线性或双三次插值）。
- 优化 IDCT（如 AAN 快速整数 IDCT）以提升性能。
- 增强鲁棒性（容错、特殊 JPEG 变体）。
- 增加单元测试与更多示例图片验证。

## 故障排查

- “文件不存在”：请检查 `Program.InputPath` 路径是否正确。
- 解析/解码异常：请检查输入文件是否损坏。
- .NET 预览提示：当前 `net10.0` 为预览，属于正常提示。若需稳定版，可调整 `TargetFramework`。
- 警告 CA2022：与 `FileStream.Read` 的精确读取相关，不影响当前演示运行。

## 说明

本项目为学习/演示用途，准确性优先于性能。若要支持更广泛的 JPEG 变体或大批量处理，建议逐步引入更高效的 IDCT、采样与缓存策略。