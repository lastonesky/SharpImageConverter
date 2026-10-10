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
- 可选按图优化的 Huffman 表（`OptimizeHuffman`）：两遍编码，平坦内容多的图可再省 20%~40%
- 支持常见采样因子（如 4:4:4/4:2:2/4:2:0），色度上采样使用最近邻回采样

### PNG 支持
- 读取：
  - 支持关键块（IHDR, PLTE, IDAT, IEND）
  - 透明度：解析 tRNS 与带 Alpha 的色彩类型（Grayscale+Alpha / Truecolor+Alpha），支持 RGB24 与 RGBA32 输出（使用 RGBA 接口以保留 Alpha）
  - 支持所有过滤器（None, Sub, Up, Average, Paeth）
  - 支持 Adam7 隔行扫描
  - 支持灰度、真彩色、索引色；位深覆盖 1/2/4/8/16（转换时缩放到 8-bit）
- 写入：
  - 保存为 Truecolor PNG（RGB24）与 Truecolor+Alpha PNG（RGBA32）
  - 使用 Zlib 压缩（Deflate），行过滤使用 Up（逐行差分），并在 Up 过滤过程里使用 SIMD 加速
  - 可写入 EXIF/iCCP/sRGB 元数据

### BMP 支持
- 读取：支持无压缩的 8/24/32 位 BMP（统一输出为 RGB24）
- 写入：24-bit RGB BMP
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
- WebP 编码质量与并发策略可配置
- WebP 实现依赖 Google 的 libwebp 及相关组件（BSD-3-Clause License），其版权与许可信息详见 `THIRD-PARTY-NOTICES.md`

### 中间格式
- 引入 `ImageFrame` 作为格式转换的中间数据结构，支持 `Rgb24`（默认）、`Bgr24`、`Bgra32`、`Rgba32` 四种像素格式
- 统一加载为 RGB，再根据输出扩展名选择编码器写回；非 RGB24 帧在保存时会通过 SIMD 自动规范化为 RGB24
- 提供 `ToRgb24() / ToBgr24() / ToBgra32() / ToRgba32()` 非破坏式转换，以及零额外内存的就地交换 `SwapRgbBgrInPlace()`（24 位与 32 位均可，同一位深内交换 R/B），便于与 OpenCV / 相机 / GPU 等 BGR/BGRA 数据源互操作

### 智能有损压缩（TinyPNG 式）
- 一行 API / 一条命令把 JPG、PNG、GIF、WebP、BMP 压到「肉眼几乎无差别」的最小体积，输出格式与输入一致
- 画质由**感知度量**（4×4 块平均 PSNR）把关：先抖动再评估，因此能容忍 dithering 颗粒、却拦得住真正的画质劣化
- PNG：量化到 ≤256 色调色板（每像素 1 字节）+ Floyd–Steinberg 抖动；颜色数按画质下限二分搜索；不达标时自动回退无损真彩色重写
- JPEG / WebP：二分搜索「刚好达标」的最低编码质量，JPEG 默认 4:2:0，色度高频丰富时自动退回 4:4:4
- GIF：跨帧共享全局调色板并降色，动画同样支持（带透明通道的动画除外）
- BMP 无法压缩，统一转为 PNG 输出
- 详见 [docs/Optimize.md](docs/Optimize.md)

## What's New / 更新亮点

- 详细更新日志请见 [CHANGELOG](CHANGELOG.md)。

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
├── docs/                        # 工程类文档（索引见 docs/README.md）
│  ├── PerfReport.md             # 性能问题定位与优化状态
│  ├── AuditVerification.md      # 外部审计报告复核
│  ├── goal.md                   # 与成熟库的差距清单
│  └── reference/                # 规范原文等参考资料
└── README.md / README.en.md
```

工程类文档（性能分析、审计复核、差距清单）统一放在 `docs/`，入口见 [docs/README.md](docs/README.md)。

## 使用方式（API）

环境要求：
- .NET SDK 8.0 或更高版本（本库目标框架：`net8.0;net10.0`）
- Windows/Linux/macOS（WebP 对应平台需加载 `runtimes/` 下原生库）

## 安装（NuGet）

```bash
dotnet add package SharpImageConverter --version 0.1.6.1-preview
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

- 智能有损压缩（TinyPNG 式，保持原格式）：

```csharp
using SharpImageConverter.Compression;

// 一行搞定：输出 <原名>.min.<原扩展名>
var result = ImageOptimizer.Optimize("photo.png");
Console.WriteLine(result); 
// photo.png: 845.4 KB → 450.6 KB（省 46.7%）| PNG 调色板 256 色 + 抖动 | PSNR 36.57dB / 感知 48.51dB

// 指定输出路径与画质档位
ImageOptimizer.Optimize("photo.jpg", "photo.small.jpg", OptimizeOptions.Aggressive);

// 精细控制
var options = new OptimizeOptions
{
    TargetQuality = 92,        // 目标画质 0-100，越高越保守
    MaxColors = 128,           // PNG/GIF 调色板上限
    EnableDithering = true,
    StripMetadata = true,      // 丢弃 EXIF，保留 ICC
    MinSavingRatio = 0.05,     // 至少省 5% 才采用，否则保留原图
};
var r = ImageOptimizer.Optimize("banner.gif", "banner.min.gif", options);
Console.WriteLine($"{r.SavedRatio:P1} / {r.Quality.PerceptualPsnr:F2} dB");
```

## 命令行工具 (CLI)

位于 `Cli/` 目录下，提供便捷的格式转换与简单处理功能。

### 运行方式

不想自己编译的话，可直接到 [Releases](https://github.com/lastonesky/SharpImageConverter/releases) 下载预编译版本：
Windows 为单文件 `.exe`，Linux 为 `.tar.gz`，两者都是自包含的，**无需安装 .NET**，也无需额外放置原生 WebP 库。

```bash
# 在 Cli 目录下运行
dotnet run -- <输入文件或文件夹路径> [输出文件或文件夹路径] [操作] [参数]
```

### 支持格式
- 输入: .jpg/.jpeg/.png/.bmp/.webp/.gif
- 输出: .jpg/.jpeg/.png/.bmp/.webp/.gif

特殊情况：
- GIF → WebP：当输入为动图 GIF、输出扩展名为 `.webp` 时，会编码为动图 WebP（尽量保留帧间隔与循环次数）

- 基本用法：`dotnet run -- <输入文件或文件夹> [输出路径] [操作] [选项]`
- 图像操作参数：
  - `resize:WxH`：缩放到指定尺寸（默认缩放实现）。
  - `resizebilinear:WxH`：双线性缩放。
  - `resizefit:WxH`：等比缩放并适配目标框。
  - `grayscale`：处理管线中转灰度。
- 编解码与质量参数：
  - `--quality N` / `-q N` / `--quality=N`：JPEG/WebP 质量（默认 75）。
  - `--subsample 420|444` / `--subsample=420|444`：JPEG 子采样（默认 420）。
  - `--keep-metadata`：JPEG 重编码保留元数据（EXIF/ICC）。
  - `--idct int|float` / `--idct=int|float`：JPEG IDCT 实现选择。
  - `--stream`：JPEG 优先走流式解码路径（非 JPEG 自动回退常规解码）。注意：仅 marker/segment 头解析为异步读取，熵编码数据解码阶段为同步阻塞读取（受限于解码器结构，见 `JpegDecoder.DecodeFromStreamAsync` 的文档注释）。
  - `--jpeg-debug`：打印 JPEG 编码配置与耗时信息。
- GIF/灰度相关：
  - `--gif-frames`：GIF 拆帧导出。
  - `--gif-debug`：打印 GIF 编解码的分阶段耗时（量化、LZW、像素展开等）与吞吐（Mpx/s）。
  - `--gif-bench N`：对 GIF 编解码重复 N 次（默认 5）并输出各阶段的最小/中位/平均耗时，用于对比优化前后的差异；该选项只做测量、不产出文件。
  - `--gray`：输出阶段按灰度保存（BMP/PNG/WebP/JPEG 路径生效）。
  - `--dithering on|off`：GIF 编码抖动开关（默认 on）。
- 目录批处理参数：
  - `--recursive`：递归处理子目录。
  - `--to ext` / `--to=ext`（同义：`--out-ext`）：指定输出后缀（bmp/png/jpg/jpeg/webp/gif；单文件同样生效）。
  - `--parallel N`：并行度（目录模式；WebP 输出强制串行）。
  - `--skip-existing`：目标文件存在时跳过。
- 智能压缩参数（配合 `--optimize`）：
  - `--opt-quality N`：目标画质 0-100（默认 88）。95≈保守、88≈均衡、75≈激进。
  - `--max-colors N`：PNG/GIF 调色板颜色上限（默认 256）。
  - `--no-dither`：关闭 Floyd–Steinberg 抖动（体积更小，但渐变容易出色带）。
  - `--min-saving N`：至少省 N%（如 `--min-saving 10`）才采用，否则保留原图（仅同格式压缩生效）。
  - `--opt-verbose`：打印每个候选方案的体积与画质，便于调参。
  - `--keep-metadata`：保留元数据（默认丢弃 EXIF、保留 ICC）。

> `--optimize` 与 `--to` 组合即为「转换 + 智能压缩」：转成目标格式时**直接产出该格式下体积最小的版本**，
> 无需先转换再单独 optimize 一遍。输出扩展名与源格式不同的显式输出路径（如 `photo.jpg out.webp --optimize`）会自动走同一路径。
> 画质下限以**原始图像**为参考，只经过一代编码，因此比「先转换、再对转换产物 optimize」的两次有损链质量更高。

### 文件夹批量转换
- 递归：`--recursive`（遍历子目录）
- 指定输出格式：`--to bmp|png|jpg|webp` 或 `--out-ext .bmp|.png|.jpg|.webp`
- 并行：`--parallel N`（默认使用逻辑 CPU 数；输出为 `.webp` 时为确保线程安全自动降为 1）
- 跳过已存在：`--skip-existing`（目标文件已存在则跳过）
- 输出位置：第二个参数为文件夹时，保持源目录的相对结构；未指定时输出到源文件所在目录
- 默认扩展名：无操作默认 `.png`，存在操作默认 `.bmp`；显式指定优先生效

### 示例

```bash
# 转换格式
dotnet run -- input.png output.jpg

# 调整大小并转换
dotnet run -- input.jpg output.png resize:800x600

# 缩放适应并设置 JPEG 质量
dotnet run -- big.png thumb.jpg resizefit:200x200 --quality 90

# 批量：将整个文件夹转为 PNG（默认）
dotnet run -- d:\images

# 批量：指定输出文件夹与递归
dotnet run -- d:\images d:\out --recursive

# 批量：指定目标格式为 BMP，并设置并行度
dotnet run -- d:\images d:\out --to bmp --parallel 8

# 批量：跳过已存在文件
dotnet run -- d:\images d:\out --skip-existing

# 智能压缩：输出 photo.min.png
dotnet run -- photo.png --optimize

# 智能压缩：指定输出路径 + 激进档 + 打印候选过程
dotnet run -- photo.jpg photo.small.jpg --optimize --opt-quality 75 --opt-verbose

# 智能压缩：整站图片批量压（指定输出目录，保持文件名与目录结构）
dotnet run -- d:\site d:\site-min --optimize --recursive --parallel 8

# 转换 + 压缩：JPEG 转 WebP，直接输出该格式下最小体积（无需再 optimize 一遍）
dotnet run -- photo.jpg --to webp --optimize

# 转换 + 压缩：整目录批量转 WebP 并压到最小
dotnet run -- d:\images d:\out --to webp --optimize --recursive
```

### 智能压缩输出示例

```text
✅ photo.png: 845.4 KB → 450.6 KB（省 46.7%）| PNG 调色板 256 色 + 抖动 | PSNR 36.57dB / 感知 48.51dB
✅ photo100.jpg: 352.3 KB → 76.1 KB（省 78.4%）| JPEG 质量 87（4:4:4） | PSNR 40.00dB / 感知 45.43dB
✅ photo.gif: 380.3 KB → 299.9 KB（省 21.1%）| GIF 调色板 64 色 + 抖动 | PSNR 33.97dB / 感知 45.49dB
➖ photo.jpg: 无收益，保留原图（78.2 KB）
```

## 许可证

本项目主要在 AI 辅助下生成，采用 [MIT 许可证](LICENSE)。

> 立场声明：以下公司及其关联公司，**不被欢迎**使用本项目：腾讯（Tencent）、华为（Huawei）、阿里巴巴（Alibaba）、字节跳动（ByteDance）、百度（Baidu）、美团（Meituan）、京东（JD.com / JD）、小米（Xiaomi）、新浪（Sina）、优酷（Youku）、爱奇艺（iQIYI）。这是作者的个人立场表达，不构成法律上的使用限制——MIT 许可证对所有人开放，作者亦无精力去取证或追究任何侵权行为。
