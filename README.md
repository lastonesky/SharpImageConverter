# PictureSharp

一个用 C# 编写的图像转换工具，尽量减少第三方托管依赖（不使用 `System.Drawing`）。支持 JPEG/PNG/BMP/WebP/GIF 格式的相互转换（包含 JPEG 解码与 JPEG 编码输出）。

## 功能特性

### JPEG 支持
- 基线（Baseline）与渐进式（Progressive）解码
- Huffman 解码、反量化、IDCT、YCbCr 转 RGB
- 支持 EXIF Orientation 自动旋转/翻转
- 支持将中间 RGB 图像编码输出为基线 JPEG（Baseline，quality 可调）

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

### GIF 支持
- 读取：
  - 支持 GIF87a/GIF89a 格式
  - 支持 LZW 解码
  - 支持全局/局部调色板
  - 支持透明度处理
  - 支持隔行扫描
- 写入：
  - 支持将 RGB 数据编码为 GIF89a
  - 自动 Octree 颜色量化（24-bit RGB -> 8-bit Index）
  - LZW 压缩

### WebP 支持
- 读取/写入 WebP（通过 `runtimes/` 下的原生 `libwebp`）
- 统一解码为 RGB24，再根据输出扩展名选择编码器写回
- 当前 WebP 编码质量固定为 75（后续可扩展为命令行参数/Options）

### 中间格式
- 引入 `ImageFrame` 作为格式转换的中间数据结构（当前为 `Rgb24`）
- 统一加载为 RGB，再根据输出扩展名选择编码器写回

## 目录结构

```
PictureSharp/
├── src/
│  ├── Core/           # Image/Configuration 等基础类型
│  ├── Formats/        # 格式嗅探与 Adapter（JPEG/PNG/BMP/WebP/GIF）
│  ├── Processing/     # Mutate/Resize/Grayscale 等处理管线
│  ├── Metadata/       # 元数据结构（Orientation 等）
│  ├── runtimes/       # WebP 原生库（win-x64/linux-x64）
│  ├── Program.cs      # CLI 程序入口
│  └── ...
└── Jpeg2Bmp.Tests/    # 简单测试工程
```

## 使用方法

环境要求：
- .NET SDK (net10.0 preview)
- Windows/Linux/macOS（WebP 目前内置原生库为 win-x64/linux-x64）

命令行参数：
```bash
dotnet run -- <输入文件路径> [输出文件路径] [操作] [--quality N]
```

支持的转换：
- JPEG/PNG/BMP/WebP/GIF -> JPEG/PNG/BMP/WebP/GIF

程序会自动根据输入文件扩展名（.jpg/.jpeg/.png/.bmp/.webp/.gif）识别格式，并根据输出文件扩展名（.jpg/.jpeg/.png/.bmp/.webp/.gif）选择保存格式。

操作（可选）：
- resize:WxH
- resizefit:WxH
- grayscale

JPEG 相关参数（可选）：
- --quality N
- --subsample 420/444
- --fdct int/float
- --jpeg-debug

示例：
```bash
# JPEG 转 PNG
dotnet run -- image.jpg image.png

# PNG 转 BMP
dotnet run -- icon.png icon.bmp

# BMP 转 PNG
dotnet run -- screenshot.bmp screenshot.png

# PNG 转 JPEG
dotnet run -- icon.png icon.jpg

# JPEG 转 JPEG（重编码）
dotnet run -- image.jpg image_reencode.jpg

# JPEG 转 WebP
dotnet run -- image.jpg image.webp

# WebP 转 PNG
dotnet run -- image.webp image.png

# GIF 转 PNG
dotnet run -- animation.gif frame_0.png

# PNG 转 GIF
dotnet run -- image.png image.gif

# 转换并缩放到 320x240 内
dotnet run -- image.jpg out.webp resizefit:320x240
```

## 已知限制

- JPEG 上采样目前使用最近邻插值。
- PNG 写入目前仅支持 Truecolor 格式，不保留源文件的调色板或元数据。
- JPEG 编码目前仅支持 Baseline（非 Progressive），默认使用 4:2:0 子采样，不写入 EXIF 等元数据。
- WebP 编码参数目前较少（质量固定），后续会补齐可配置项。


## 下一步计划

- 优化上采样算法（实现双线性或双三次插值）。
- 增强鲁棒性（容错、特殊 JPEG 变体）。
- 增加单元测试与更多示例图片验证。
- 完善 WebP 编码参数（质量/无损/alpha 等）与跨平台运行时布局。

## 故障排查

- “文件不存在”：请检查 `Program.InputPath` 路径是否正确。
- 解析/解码异常：请检查输入文件是否损坏。
- WebP 写入/读取报错 `DllNotFoundException (0x8007007E)`：
  - 确认输出目录存在 `runtimes/win-x64/*.dll` 或 `runtimes/linux-x64/*.so*`
  - Windows 推荐至少包含：`libwebp.dll`、`libwebpdecoder.dll`（以及某些版本需要的 `libsharpyuv.dll`）
  - Windows 下部分 `libwebp.dll` 依赖 `libsharpyuv.dll` 与 VC++ 运行时：请将依赖 DLL 放到同目录，或安装 Microsoft Visual C++ 2015-2022 x64 运行库

## 说明

本项目为学习/演示用途，准确性优先于性能。若要支持更广泛的 JPEG 变体或大批量处理，建议逐步引入更高效的 IDCT、采样与缓存策略。


## 优化空间

- JPEG 编码端已启用 4:2:0 子采样 + 整数 FDCT。若要进一步提速：行级向量化是主要方向
