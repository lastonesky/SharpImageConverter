# SharpImageConverter

简体中文 | [English Version](README.en.md)

一个用 C# 编写的图像处理与格式转换库，尽量减少第三方托管依赖（不使用 `System.Drawing`）。支持 JPEG/PNG/BMP/WebP/GIF 格式的相互转换（包含 JPEG 解码与 JPEG 编码输出）。目前主要面向 API 调用；命令行（CLI）已独立为单独项目。

## 开发初衷

本库最初是为了解决在生产环境中使用现有 .NET 图像库时遇到的一些实际问题：

- 需要真正的跨平台支持：我们的服务必须能在 Linux 等非 Windows 环境中稳定运行，因此不能依赖实质上仅在 Windows 上受支持的 `System.Drawing`。在较新的 .NET 版本中继续使用 `System.Drawing` 还会在编译阶段产生大量“只在 Windows 支持”的警告，不利于长期维护。
- 授权与成本的不确定性：不希望引入对企业有营收门槛的组件，例如 ImageSharp 要求年收入超过 100 万美元的公司购买商业授权，这会给后续商业化带来额外的不确定成本。
- 稳定性与可运维性：在托管服务中使用 SkiaSharp 时，我们曾多次遇到非托管层崩溃直接拖垮整个 .NET 进程、导致服务重启的问题，而崩溃原因难以从托管栈追踪。我们希望构建一个完全托管、行为可控、出问题时更容易排查的解决方案。
- 内部系统使用，没有使用并发压力，能正常应对常见的产品图片。

## 功能特性

### JPEG 支持
- 基线（Baseline）与渐进式（Progressive）解码
- Huffman 解码、反量化、整数 IDCT、YCbCr 转 RGB
- 支持 EXIF Orientation 自动旋转/翻转
- 支持将中间 RGB 图像编码输出为基线 JPEG（Baseline，quality 可调）
- 支持常见采样因子（如 4:4:4/4:2:2/4:2:0），色度上采样使用最近邻回采样

### PNG 支持
- 读取：
  - 支持关键块（IHDR, PLTE, IDAT, IEND）
  - 透明度：解析 tRNS 与带 Alpha 的色彩类型（Grayscale+Alpha / Truecolor+Alpha），输出统一为 RGB24，不保留 Alpha
  - 支持所有过滤器（None, Sub, Up, Average, Paeth）
  - 支持 Adam7 隔行扫描
  - 支持灰度、真彩色、索引色；位深覆盖 1/2/4/8/16（转换时缩放到 8-bit）
- 写入：
  - 保存为 Truecolor PNG（RGB24）
  - 使用 Zlib 压缩（Deflate），行过滤固定为 None
  - 不写入调色板或其他元数据

### BMP 支持
- 读写 24-bit RGB BMP
- 支持自动填充对齐

### GIF 支持
- 读取：
  - 支持 GIF87a/GIF89a 格式
  - LZW 解码、全局/局部调色板
  - 透明度：解析透明索引（Graphic Control Extension），支持处置方法 Restore to Background/Restore to Previous 的帧合成
  - 支持隔行扫描；可导出所有帧到 RGB
- 写入：
  - 单帧 GIF89a；Octree 颜色量化（24-bit RGB -> 8-bit Index）
  - LZW 压缩；不写入透明度与动画元数据（延时、循环）

### WebP 支持
- 读取/写入 WebP（通过 `runtimes/` 下的原生 `libwebp`）
- 统一解码为 RGB24，再根据输出扩展名选择编码器写回
- 当前 WebP 编码质量固定为 75（后续可扩展为命令行参数/Options）
- WebP 实现依赖 Google 的 libwebp 及相关组件（BSD-3-Clause License），其版权与许可信息详见 `THIRD-PARTY-NOTICES.md`

### 中间格式
- 引入 `ImageFrame` 作为格式转换的中间数据结构（当前为 `Rgb24`）
- 统一加载为 RGB，再根据输出扩展名选择编码器写回

## What's New / 更新亮点

- PNG：在多数图片上显著降低压缩后体积（例如原先约 110 MB 的 PNG，现在可缩小到约 30 MB，具体效果取决于图像内容），同时略微提升压缩速度。
- JPEG：大幅提升解码速度，并在编码路径上带来小幅性能提升。
- BMP：写出路径经过优化，在同一环境下写出约 400 MB 的 BMP 文件，耗时从约 400 ms 降低到约 270 ms。

## 目录结构

```
SharpImageConverter/
├── src/                         # 库主体（对外 API）
│  ├── Core/                     # Image/Configuration 等基础类型
│  ├── Formats/                  # 格式嗅探与 Adapter（JPEG/PNG/BMP/WebP/GIF）
│  ├── Processing/               # Mutate/Resize/Grayscale 等处理管线
│  ├── Metadata/                 # 元数据结构（Orientation 等）
│  ├── runtimes/                 # WebP 原生库（win-x64/linux-x64/osx-arm64）
│  └── SharpImageConverter.csproj
├── Cli/                         # 独立命令行项目
│  ├── Program.cs
│  └── SharpImageConverter.Cli.csproj
├── SharpImageConverter.Tests/   # 单元测试工程
└── README.md / README.en.md
```

## 使用方式（API）

环境要求：
- .NET SDK 8.0 或更高版本（本库目标框架：`net8.0;net10.0`）
- Windows/Linux/macOS（WebP 对应平台需加载 `runtimes/` 下原生库）

## 安装（NuGet）

```bash
dotnet add package SharpImageConverter --version 0.1.3
```

引用命名空间：
```csharp
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
```

常用示例：
- 加载、处理并保存（自动嗅探输入格式；按输出扩展名选择编码器）

```csharp
// 加载为 RGB24
var image = Image.Load("input.jpg"); // 参见 API 入口 [Image](src/Core/Image.cs)

// 处理：缩放到不超过 320x240，转灰度
image.Mutate(ctx => ctx
    .ResizeToFit(320, 240)       // 最近邻或双线性请选用不同 API
    .Grayscale());               // 参见处理管线 [Processing](src/Processing/Processing.cs)

// 保存（根据扩展名选择编码器）
Image.Save(image, "output.png");
```

- 克隆并处理（不修改原图）

```csharp
// 基于已有图像创建处理后的副本
var processed = image.Clone(ctx => ctx
    .ResizeToFit(320, 240)
    .Grayscale());

// image 保持不变，processed 为处理结果
Image.Save(processed, "output_clone.png");
```

- RGBA 模式（保留 Alpha 的加载/保存；不支持的目标格式会自动回退为 RGB 保存）

```csharp
// 加载为 RGBA32（优先使用原生 RGBA 解码）
var rgba = Image.LoadRgba32("input.png");
// 保存为支持 Alpha 的格式（如 PNG/WebP/GIF）；格式不支持则回退为 RGB
Image.Save(rgba, "output.webp");
```

- 流式操作（Stream）：

```csharp
using SharpImageConverter; // ImageFrame

// 从流加载（自动嗅探格式）
using var input = File.OpenRead("input.jpg");
var frame = ImageFrame.Load(input);

// 如需处理，转换为 Image<Rgb24>
var image = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);
image.Mutate(ctx => ctx.Grayscale());

// 保存到流（需明确指定格式，或封装回 ImageFrame 使用便捷方法）
using var output = new MemoryStream();
// 方式 A: 使用 ImageFrame 便捷方法
new ImageFrame(image.Width, image.Height, image.Buffer).SaveAsPng(output);
// 方式 B: 使用特定编码器
// new SharpImageConverter.Formats.PngEncoderAdapter().EncodeRgb24(output, image);
```

## 命令行工具 (CLI)

位于 `Cli/` 目录下，提供便捷的格式转换与简单处理功能。

### 运行方式

```bash
# 在 Cli 目录下运行
dotnet run -- <输入文件路径> [输出文件路径] [操作] [参数]
```

### 支持格式
- 输入: .jpg/.jpeg/.png/.bmp/.webp/.gif
- 输出: .jpg/.jpeg/.png/.bmp/.webp/.gif

特殊情况：
- GIF → WebP：当输入为动图 GIF、输出扩展名为 `.webp` 时，会编码为动图 WebP（尽量保留帧间隔与循环次数）

### 操作 (Operations)
- `resize:WxH` : 强制缩放到指定宽 (W) 高 (H)
- `resizebilinear:WxH` : 使用双线性插值缩放到指定尺寸
- `resizefit:WxH` : 等比缩放到适应指定矩形框 (W x H)
- `grayscale` : 转为灰度图

### 参数 (Options)
- `--quality N` : 设置 JPEG 编码质量 (0-100)，默认 75
- `--subsample 420|444` : 设置 JPEG 色度采样，默认 420
- `--keep-metadata` : 在重新编码 JPEG 时尽量保留 EXIF/ICC 等基础元数据
- `--jpeg-debug` : 启用 JPEG 解码调试输出
- `--gif-frames` : 将动图 GIF 的每一帧导出为独立图片，方便调试与检查

### 示例

```bash
# 转换格式
dotnet run -- input.png output.jpg

# 调整大小并转换
dotnet run -- input.jpg output.png resize:800x600

# 缩放适应并设置 JPEG 质量
dotnet run -- big.png thumb.jpg resizefit:200x200 --quality 90
```

## 许可证

本项目主要在 AI 辅助下生成，并遵循 MIT 许可证。
