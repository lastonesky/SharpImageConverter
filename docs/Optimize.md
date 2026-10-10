# 智能有损压缩（`SharpImageConverter.Compression`）

TinyPNG 式的「丢进去就能小很多、肉眼几乎看不出差别」能力。
默认输入 JPG / PNG / GIF / WebP / BMP，输出**同格式**（BMP 例外，转 PNG）的最小版本；
输出扩展名与源格式不同时，自动走「转换即最优」——一步产出目标格式的最小体积（见 §5）。

- 库入口：`ImageOptimizer.Optimize(input, output?, options?)`
- CLI：`--optimize`；与 `--to <格式>` 组合即为「转换 + 智能压缩」
- 源码：`src/Compression/`

---

## 1. 设计要点

### 1.1 为什么需要「感知」画质度量

调色板量化 + 抖动会把误差打散成高频颗粒：逐像素 PSNR 会掉到 30~36 dB，
但人眼看到的（低频、面积平均后的）画面几乎不变。反过来，
单纯看逐像素 PSNR 又会不分青红皂白地把「可接受的有损」也否掉。

因此 `QualityMetrics.Compare` 同时给出两个值：

| 指标 | 含义 | 用途 |
|---|---|---|
| `PerceptualPsnr` | 先做 4×4 块平均再算 PSNR | **主判据**：抹平抖动颗粒，代表「看不看得出来」 |
| `Psnr` | 逐像素 PSNR | **副判据**：拦住「高频细节被抹掉 / 抖动过头」，下限 `MinRawPsnr`（默认 30 dB） |

实测（900×600 合成图）：

| 场景 | 逐像素 PSNR | 感知 PSNR |
|---|---|---|
| PNG 256 色 + 抖动（照片） | 36.6 dB | 48.5 dB |
| PNG 128 色 + 抖动（照片） | 34.4 dB | 43.6 dB |
| PNG 256 色**不**抖动（照片） | 22.1 dB | 29.4 dB |
| JPEG q80（照片） | — | 45.3 dB |

可以看到：抖动让感知分提升近 20 dB（这正是我们要的效果），
而「块平均」对**纯噪点被抹平**这类损伤不敏感，所以再叠一条逐像素下限兜底。

### 1.2 画质下限怎么定

`OptimizeOptions.TargetQuality`（0-100，默认 88）映射为：

```
MinPerceptualPsnr = 33 + 0.14 * TargetQuality
```

即 88 → 45.3 dB，95 → 46.3 dB，75 → 43.5 dB。
标定依据是上面那组实测：均衡档下照片会停在 **256 色 / q80 附近**，
与「放大看才发现有色阶、正常看没区别」的主观感受一致。

想自己调就直接赋值 `MinPerceptualPsnr` / `MinRawPsnr`。

### 1.3 候选搜索

颜色数、JPEG 质量都满足「越激进 → 体积越小、画质越差」的单调关系，因此用二分搜索：
8 档颜色阶梯只需 3~4 次评估，质量区间 40~95 只需 6 次编码 + 解码。
3.84 MP 的 PNG 全流程约 1.1 s（含 4 次候选评估）。

体积并不严格随颜色数单调（中位切分在不同预算下切法不同，会有几个百分点的抖动），
所以最后一步是在**所有达标候选中按体积取最小**，而不是直接取最激进的那个。

---

## 2. 各格式策略

### PNG（收益最大）

1. 颜色数 ≤ 上限时直接**精确取色**（无损），不再做有损尝试。
2. 否则量化到调色板（颜色类型 3，每像素 1 字节而非 3~4 字节）+ Floyd–Steinberg 抖动。
3. 量化不达标时回退**无损真彩色重写**：自适应滤波 + 更慢的 Deflate 级别，
   对渐变类图像尤其有效（实测抖动后索引近乎随机，调色板反而会比真彩色大 19 倍）。
4. 量化收益不足 10% 时也会额外算一次无损重写做对比。

> 实测：照片类 845 KB → 451 KB（-46.7%）；全色域渐变 13.6 KB → 8.3 KB（-38.8%，无损）；
> 6 色图形 3.3 KB → 1.4 KB（-57.4%，无损）。

### JPEG

1. 解码 → 以 4:2:0 重编码，二分搜索「刚好达标」的最低质量。
2. 若 4:2:0 始终不达标（彩色文字、色度高频丰富的图），自动退回 4:4:4 再搜一轮。
3. **按图优化的 Huffman 表**（`JpegEncoderOptions.OptimizeHuffman`，默认开启）：
   编码器的标准 Annex K 表是固定的，与实际符号分布不匹配，空块开销高达 5.38 bits/块
   （DC 2b + EOB 4b）。对大片平坦区域的图（扫描件、截图、大留白照片）这部分开销占比极高。
   两遍编码：先跑一遍「生产 → DCT 量化」只统计符号分布，再据此生成最优表重新编码，
   可再省 20%~40%（143MP 大留白扫描图：2.95 MB → 2.60 MB）。代价是编码耗时约 ×2。
4. 默认丢弃 EXIF（保留 ICC，避免色彩呈现变化）；已解码时方向已生效，不会转错。

> 实测：q100 的 352 KB 照片 → 68 KB（-80.6%）；大留白扫描图 2.95 MB → 2.60 MB（-12.0%）；
> 已经是高质量编码的普通照片可能仍判定「无收益」并保留原图。

**与 TinyPNG 的剩余差距**：TinyPNG 产物是 progressive（SOF2）编码，同画质下比 baseline 再小
5%~10%；我们的编码器目前只支持 baseline（`WriteSof0`）。要进一步追平需要实现 progressive 编码
（多扫描、频谱选择、逐次逼近、EOB 游程），是一项独立的大改动。

### GIF

1. 解码全部帧，**跨帧共享一张全局调色板**（比每帧局部调色板更省）。
2. 二分搜索最少颜色数：颜色越少，LZW 字典命中率越高。
3. 用新增的 `GifEncoder.EncodeIndexed` 直接写「调色板 + 索引」，跳过编码器内部的二次量化，
   否则量化两次会导致画质不可控。
4. 单帧 GIF 保留透明通道（索引 0 预留）；**带透明通道的动画**目前只解码得到 RGB24 帧序列，
   重编码会丢失透明度，因此直接保留原图并给出原因。

> 实测：照片类 GIF 380 KB → 300 KB（-21.1%，64 色 + 抖动）。

### WebP / BMP

- WebP：同样做质量搜索，但搜索下界提到 65（WebP 本身已高效，低质量区间省不了多少体积却明显变糊）。
- BMP：无法压缩，统一输出 PNG。

---

## 3. 实现清单

| 文件 | 作用 |
|---|---|
| `src/Compression/ImageOptimizer.cs` | 入口、格式嗅探、同格式/跨格式路由、二分搜索、收尾写盘 |
| `src/Compression/PngOptimizer.cs` | 调色板量化 + 无损重写回退 |
| `src/Compression/JpegOptimizer.cs` | 质量搜索 + 4:2:0/4:4:4 回退 |
| `src/Compression/GifOptimizer.cs` | 跨帧共享调色板 + 降色 |
| `src/Compression/WebpOptimizer.cs` | WebP 质量搜索（RGBA，保留 alpha） |
| `src/Compression/OptimizeArtifact.cs` | 「纯计算产物」中间结构，供同格式/跨格式两条路径复用 |
| `src/Compression/PaletteQuantizer.cs` | RGBA 中位切分量化器 |
| `src/Compression/ImageQuality.cs` | 感知 / 逐像素画质度量 |
| `src/Compression/OptimizeOptions.cs` | 选项与画质档位 |
| `src/Compression/OptimizationResult.cs` | 结果（体积、方案、画质、是否跨格式转换） |

配套改动：

- `PngWriter` 新增调色板写入（PLTE/tRNS，颜色类型 3）、`PngFilterMode`（Up/Adaptive/None）、Deflate 级别参数。
- `PngAdaptiveFilter`：逐行在 None/Sub/Up/Average/Paeth 中选代价最小者，SIMD 实现。
- `GifEncoder.EncodeIndexed`：调色板 + 索引直写，支持单帧与动画、全局调色板、透明索引。

### PaletteQuantizer 的几个关键取舍

- **精确取色优先**：颜色数不超预算时建无损调色板，走 `Dictionary<uint,int>` 并在超预算时提前退出。
- **直方图**：5 bit/通道 + alpha 5 bit（不透明图只需 32 K 桶），大图抽样，桶只清零用过的部分。
- **中位切分**：按「像素数 × 最大通道跨度」选盒、按累计像素中位数切开；盒的调色板色取加权均值。
- **最近色查找**：逐像素扫 256 色太慢，用「5 bit RGB 粗查表定种子 + 每色 16 近邻短表精修」，
  再叠一层 20 bit 键的结果缓存，实际降到常数级。
- **抖动**：Floyd–Steinberg 只扩散 RGB。alpha 抖动会在半透明区域产生可见噪点，故 alpha 取最近值。

---

## 4. 用法

### API

```csharp
using SharpImageConverter.Compression;

var result = ImageOptimizer.Optimize("photo.png");             // → photo.min.png
Console.WriteLine(result.Method);        // PNG 调色板 256 色 + 抖动
Console.WriteLine(result.SavedRatio);    // 0.467
Console.WriteLine(result.Quality);       // PSNR 36.57dB / 感知 48.51dB / 平均误差 2.54

if (result.KeptOriginal) { /* 无收益，输出是原图副本 */ }
```

| `OptimizeOptions` | 默认 | 说明 |
|---|---|---|
| `TargetQuality` | 88 | 目标画质 0-100，映射到感知 PSNR 下限 |
| `MinPerceptualPsnr` | 由 `TargetQuality` 推导 | 感知 PSNR 下限（dB） |
| `MinRawPsnr` | 30 | 逐像素 PSNR 下限（dB） |
| `MaxColors` | 256 | PNG/GIF 调色板上限 |
| `EnableDithering` | true | Floyd–Steinberg 抖动 |
| `StripMetadata` | true | 丢 EXIF，保留 ICC |
| `JpegQuality` | null | 指定后不做搜索 |
| `JpegMinQuality` | 40 | 搜索下界 |
| `AdaptiveFiltering` | true | PNG 自适应行滤波 |
| `KeepOriginalWhenNoGain` | true | 变大就保留原图 |
| `MinSavingRatio` | 0 | 至少省该比例才采用 |
| `Log` | null | 打印每个候选的体积与画质 |

预设：`OptimizeOptions.Conservative` / `Balanced` / `Aggressive`。

### CLI

```bash
dotnet run -- photo.png --optimize
dotnet run -- photo.jpg photo.small.jpg --optimize --opt-quality 75 --opt-verbose
dotnet run -- d:\site d:\site-min --optimize --recursive --parallel 8
```

---

## 5. 转换 + 智能压缩（一步到位）

### 5.1 动机

优化与格式转换原本是两条互斥路径：`--optimize` 保持原格式，`--to` 只转换。
想把 JPG 转成最小体积的 WebP，得先 `--to webp` 再对产物 `--optimize`。
后者的画质下限是拿**已经降质过的中间产物**当参考，属于二次有损，质量白丢一代。

现在 `--optimize` 与 `--to`（或带不同扩展名的显式输出路径）组合时，
会**解码一次源图，直接按目标格式做质量 / 调色板搜索并写出**，只经过一代编码。

### 5.2 库层语义

`ImageOptimizer.Optimize(input, output, options)` 的路由以**输出扩展名**为准：

| 输出扩展名 | 行为 |
|---|---|
| 与源格式相同 | 同格式压缩（原有行为）：无收益可复制原图，默认输出 `<原名>.min.<原扩展名>` |
| 与源格式不同 | **跨格式转换**：按目标格式搜索最小体积并写出；无论是否比源文件小都写出目标格式 |

跨格式时 `OptimizationResult.Converted == true`。此时 `KeptOriginal` 恒为 false
（「无收益保留原图」在跨格式场景没有意义——原格式 ≠ 目标格式），
`SavedRatio` 是两种格式的体积对比，仅供参考。

CLI 路由条件：显式给了 `--to`，或输出路径带一个与源格式不同的可识别扩展名（`.jpg/.jpeg/.png/.bmp/.webp/.gif`）。
目标扩展名与源一致时仍走同格式压缩，避免误覆盖源文件。

### 5.3 各目标格式

| 目标 | 做法 | 备注 |
|---|---|---|
| `.jpg` / `.jpeg` | 质量二分搜索（4:2:0 → 4:4:4 回退）+ 按图优化 Huffman 表 | alpha 被丢弃（JPEG 不支持） |
| `.png` | 调色板量化二分搜索，必要时回退无损真彩色重写 | 保留 alpha |
| `.webp` | 质量搜索，下界 65 | 保留 alpha（此前同格式优化只走 RGB） |
| `.gif` | 单帧调色板降色搜索 | 静态图；透明像素保留（索引 0 预留） |
| `.bmp` | 无损真彩色直写 | BMP 无压缩，写明「该格式无压缩」 |

> 从 `.png` 转 `.gif` / `.bmp` 等场景下，产物可能比源文件大（容器特性所致），
> 但因为用户显式要求转换，仍会写出目标格式，不会退化成复制原文件。

### 5.4 用法

```bash
# 单文件：JPG 转最小体积 WebP
dotnet run -- photo.jpg --to webp --optimize

# 显式输出路径：扩展名与源不同即自动走转换+压缩
dotnet run -- photo.jpg out.webp --optimize

# 目录批量：整目录转 WebP 并压到最小
dotnet run -- d:\images d:\out --to webp --optimize --recursive --parallel 8

# 调参同样生效
dotnet run -- photo.png --to jpg --optimize --opt-quality 75 --opt-verbose
```

输出示例（实测 240×160 合成照片）：

```text
✅ photo.jpg → photo.webp: 54.8 KB → 12.8 KB | WebP 质量 65 | PSNR 43.12dB / 感知 53.78dB / 平均误差 0.76
✅ photo.png → photo.jpg: 77.2 KB → 4.9 KB | JPEG 质量 68（4:2:0） | PSNR 43.61dB / 感知 45.44dB / 平均误差 1.18
✅ photo.png → photo.gif: 77.2 KB → 73.1 KB | GIF 调色板 32 色 + 抖动 | PSNR 36.78dB / 感知 48.33dB / 平均误差 2.38
✅ photo.png → photo.bmp: 77.2 KB → 791.1 KB | BMP 无损（该格式无压缩） | PSNR 99.00dB / 感知 99.00dB
```

---

## 6. 已知边界

- **带透明通道的动画 GIF** 不做有损优化（解码器只暴露 RGB24 帧序列），会保留原图并说明原因。
- 半透明 PNG 的 alpha 不参与抖动，极平滑的 alpha 渐变可能出现轻微色带（5 bit 桶精度上限）。
- 感知度量基于 4×4 块平均，对「纯噪点纹理被抹平」不敏感，靠 `MinRawPsnr` 兜底；
  若你的素材以高频纹理为主，建议把 `MinRawPsnr` 提到 33~35。
- 已经是高质量编码的产物（如 q92 的 JPEG）通常判为「无收益」，这是预期行为而非失败。
