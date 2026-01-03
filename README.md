# SharpImageConverter

一个用 C# 编写的图像处理与格式转换库，尽量减少第三方托管依赖（不使用 `System.Drawing`）。支持 JPEG/PNG/BMP/WebP/GIF 格式的相互转换（包含 JPEG 解码与 JPEG 编码输出）。目前主要面向 API 调用；命令行（CLI）已独立为单独项目。

## 功能特性

### JPEG 支持
- 基线（Baseline）与渐进式（Progressive）解码
- Huffman 解码、反量化、IDCT、YCbCr 转 RGB
- 支持 EXIF Orientation 自动旋转/翻转
- 支持将中间 RGB 图像编码输出为基线 JPEG（Baseline，quality 可调）
- 色度上采样优化：4:2:0/4:2:2 专用快速路径，通用 16.16 定点双线性回退

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

### 中间格式
- 引入 `ImageFrame` 作为格式转换的中间数据结构（当前为 `Rgb24`）
- 统一加载为 RGB，再根据输出扩展名选择编码器写回

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

引用命名空间：
```csharp
using SharpImageConverter.Core;
using SharpImageConverter.Processing;
```

常用示例：
- 加载、处理并保存（自动嗅探输入格式；按输出扩展名选择编码器）

```csharp
// 加载为 RGB24
var image = Image.Load("input.jpg"); // 参见 API 入口 [Image](file:///d:/Project/jpeg2bmp/src/Core/Image.cs#L51-L95)

// 处理：缩放到不超过 320x240，转灰度
image.Mutate(ctx => ctx
    .ResizeToFit(320, 240)       // 最近邻或双线性请选用不同 API
    .Grayscale());               // 参见处理管线 [Processing](file:///d:/Project/jpeg2bmp/src/Processing/Processing.cs#L18-L143)

// 保存（根据扩展名选择编码器）
Image.Save(image, "output.png");
```

- RGBA 模式（保留 Alpha 的加载/保存；不支持的目标格式会自动回退为 RGB 保存）

```csharp
// 加载为 RGBA32（优先使用原生 RGBA 解码）
var rgba = Image.LoadRgba32("input.png");
// 保存为支持 Alpha 的格式（如 PNG/WebP/GIF）；格式不支持则回退为 RGB
Image.Save(rgba, "output.webp");
```

- 通过 Stream 加载（自动嗅探文件头）

```csharp
using var fs = File.OpenRead("input.jpg");
var frame = SharpImageConverter.ImageFrame.Load(fs); // 入口 [ImageFrame.Load(Stream)](file:///d:/Project/jpeg2bmp/src/ImageFrame.cs#L69-L114)
var imageFromStream = new Image<Rgb24>(frame.Width, frame.Height, frame.Pixels);
```

- 克隆并缩放用法

```csharp
var original = Image.Load("input.jpg");
var clone = new Image<Rgb24>(original.Width, original.Height, (byte[])original.Buffer.Clone());
clone.Mutate(ctx => ctx.Resize(640, 480));
Image.Save(clone, "resized.png");
```

说明：
- API 入口位于 [Image](file:///d:/Project/jpeg2bmp/src/Core/Image.cs#L51-L95)，其内部通过 [Configuration](file:///d:/Project/jpeg2bmp/src/Core/Configuration.cs#L20-L55) 进行格式嗅探与编码器路由。
- RGB24 与 RGBA32 的保存会根据扩展名选择具体编码器，详见 [Configuration.SaveRgb24](file:///d:/Project/jpeg2bmp/src/Core/Configuration.cs#L77-L93) 与 [Configuration.SaveRgba32](file:///d:/Project/jpeg2bmp/src/Core/Configuration.cs#L127-L146)。
- 处理管线使用扩展方法 `Mutate` 构建上下文，示例见 [ImageExtensions.Mutate](file:///d:/Project/jpeg2bmp/src/Processing/Processing.cs#L148-L160)。

<details>
<summary>命令行用法（CLI，默认折叠；点击展开）</summary>

CLI 项目路径：`Cli/SharpImageConverter.Cli.csproj`

环境要求：
- .NET SDK 8.0+（或更高）
- Windows/Linux/macOS（按平台加载 `runtimes/` 原生库）

运行方式：
```bash
# 在仓库根目录，指定 CLI 项目运行
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- <输入> [输出] [操作] [--quality N]
```

支持的转换：
- JPEG/PNG/BMP/WebP/GIF -> JPEG/PNG/BMP/WebP/GIF

程序会自动根据输入文件扩展名（.jpg/.jpeg/.png/.bmp/.webp/.gif）识别格式，并根据输出文件扩展名选择保存格式。

操作（可选）：
- resize:WxH
- resizefit:WxH
- grayscale

JPEG 参数（可选）：
- --quality N
- --subsample 420/444
- --jpeg-debug

示例：
```bash
# JPEG 转 PNG
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- image.jpg image.png

# PNG 转 BMP
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- icon.png icon.bmp

# BMP 转 PNG
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- screenshot.bmp screenshot.png

# PNG 转 JPEG
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- icon.png icon.jpg

# JPEG 转 JPEG（重编码）
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- image.jpg image_reencode.jpg

# JPEG 转 WebP
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- image.jpg image.webp

# WebP 转 PNG
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- image.webp image.png

# GIF 转 PNG
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- animation.gif frame_0.png

# PNG 转 GIF
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- image.png image.gif

# 转换并缩放到 320x240 内
dotnet run --project Cli/SharpImageConverter.Cli.csproj -- image.jpg out.webp resizefit:320x240
```

</details>

## 已知限制

- 非常规采样率（非 4:2:0 / 4:2:2 / 4:4:4）走通用定点双线性回退路径，性能相对较低。
- PNG 写入仅支持 Truecolor（RGB24），不保留源文件的调色板、元数据与透明通道。
- JPEG 编码目前仅支持 Baseline（非 Progressive），默认使用 4:2:0 子采样，不写入 EXIF 等元数据。
- WebP 编码参数目前较少（质量固定），后续会补齐可配置项。
- GIF 写入仅支持单帧；不写入透明通道、帧延时与循环信息。


## 下一步计划

- 进一步压榨 JPEG 颜色转换与上采样性能（SIMD/Vector 化，块级 2×2 处理）。
- 增强鲁棒性（容错、特殊 JPEG 变体）。
- 增加单元测试与更多示例图片验证。
- 完善 WebP 编码参数（质量/无损/alpha 等）与跨平台运行时布局。


## 故障排查

- “文件不存在”：请检查 `Program.InputPath` 路径是否正确。
- 解析/解码异常：请检查输入文件是否损坏。
- WebP 写入/读取报错 `DllNotFoundException (0x8007007E)`：
  - 确认输出目录存在 `runtimes/win-x64/*.dll`、`runtimes/linux-x64/*.so*` 或 `runtimes/osx-arm64/*.dylib`
  - Windows 推荐至少包含：`libwebp.dll`、`libwebpdecoder.dll`（以及部分版本需要的 `libsharpyuv.dll`）
  - Windows 下部分 `libwebp.dll` 依赖 `libsharpyuv.dll` 与 VC++ 运行时：请将依赖 DLL 放到同目录，或安装 Microsoft Visual C++ 2015-2022 x64 运行库

## 说明

本项目为学习/演示用途，准确性优先于性能。若要支持更广泛的 JPEG 变体或大批量处理，建议逐步引入更高效的 IDCT、采样与缓存策略。
