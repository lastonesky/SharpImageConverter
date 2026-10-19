# 现成 WASM 图像编解码方案调研

> 2026-10-19 · 起因为「SharpImageConverter 要不要自己做 wasm 版」的替代方案评估。
> **本文只做选型对比，不含实测数据**；凡标「未验证」的都是没跑过、不能当结论用的。

## 0. 结论

**有，而且不止一类。** 大致分三档：

| 档位 | 代表 | 与本项目能力重合度 | 代价 |
|---|---|---|---|
| **A. 直接可用**（npm 装就能跑） | `@jsquash/*` + `@jsquash/oxipng` | 格式覆盖**超过**本项目（多 AVIF/JXL/QOI） | 放弃 .NET 代码资产，改前端体系 |
| **B. 应用级成品** | Squoosh（含 CLI）、`rust-image-tools`、`web-image-converter` | 功能接近，含 UI/Worker | 要么只能用现成 UI，要么拆库 |
| **C. 自己做**（现状路线） | 本项目 | 独有 `--optimize` 质量搜索 + 自家 SIMD | 维护两条运行时的产物一致性 |

**最值得认真看的只有 `@jsquash` 一档**——它是 Squoosh 的库化拆分，Apache-2.0，用参考实现
（MozJPEG / libwebp / libavif / OxiPNG），编译时开 wasm SIMD。**它解决的是"编解码器在浏览器里跑"，
不解决"质量搜索优化器"**，这一点决定了它替代不了本项目的 `--optimize`（见 §3）。

---

## 1. A 档：jSquash（Squoosh 的库化拆分）

仓库 `github.com/jamsinclair/jSquash`，Apache-2.0，ESM。

| 包 | 能力 | 底层库 |
|---|---|---|
| `@jsquash/jpeg` | 编 + 解 | **MozJPEG** |
| `@jsquash/png` | 编 + 解 | Rust `png` crate |
| `@jsquash/webp` | 编 + 解 | **libwebp** |
| `@jsquash/avif` | 编 + 解 | libavif |
| `@jsquash/jxl` | 编 + 解 | libjxl |
| `@jsquash/oxipng` | PNG 优化 | OxiPNG |
| `@jsquash/resize` | 缩放 | Rust `resize` + hqx + magic-kernel |
| `@jsquash/qoi` | 编 + 解 | QOI 官方库 |

特性：
- 面向 **Web + Web Worker**；不依赖 `eval`（可在 Cloudflare Workers 跑）
- 不依赖 `TextEncoder/TextDecoder`（可在纯 UTF-8 的 V8 运行时跑）
- 按需懒加载，单个包约 250 KB 量级

**关键比较**：

| 维度 | 本项目（C#） | jSquash |
|---|---|---|
| JPEG 编码器 | 自研（含两遍 Huffman、trellis） | MozJPEG（业界公认同质量下更小） |
| WebP | libwebp P/Invoke（**wasm 下不可用**） | libwebp 编译进 wasm（**可用**） |
| AVIF / JXL | 无 | 有 |
| 质量搜索 `--optimize` | **有**（PSNR/感知 PSNR 搜索） | **无**（只暴露单次编码参数） |
| 流式解码 / 内存预算 | 有（`--stream`、网络流、`requireEoi`） | 无（`ArrayBuffer` 进、`ArrayBuffer` 出） |
| 许可 | 见项目 LICENSE | Apache-2.0 |

**注意 MozJPEG 的编码档位**：`@jsquash/jpeg` 只暴露 quality，不暴露 trellis / 两遍 Huffman 之类的
细粒度开关（Squoosh UI 里那些旋钮并没有全部下沉到库里）。要"极致压榨"仍需回到 C 工具链。

---

## 2. B 档：应用级成品

| 项目 | 说明 | 能否当库用 |
|---|---|---|
| [Squoosh](https://squoosh.app/)（GoogleChromeLabs） | 25k star，wasm 编解码 + 并排对比 UI；CLI 版 `@squoosh/cli` **支持 Butteraugli 自动压质** | UI 不可复用；CLI 是 Node 端，不是浏览器库 |
| [rust-image-tools](https://github.com/codeinaire/rust-image-tools) | `image` crate + `kamadak-exif`，10 种格式，Worker + 零拷贝 | 是完整应用，需自行拆分 |
| [Offline Image Converter](http://mhh.dev/project/2025/09/14/offline-image-convert.html) | 3 天做出来的例子，4 行核心代码 | 参考实现，非库 |
| [web-image-converter](https://github.com/46ki75/web-image-converter) | npm 包，JPEG/PNG/WebP/BMP，MIT | **可以直接装** |
| `img2webp` 一类 | `image` crate + rayon | 偏单格式工具 |

Rust `image` crate 路线的通用短板：**WebP 只解不编**（`image` crate 本身没有 WebP 编码器），
需要另配 `zenwebp` 或 libwebp 绑定。上面的 rust-image-tools 就是"WebP: decode only"。

---

## 3. 本项目 `--optimize` 与这些库的根本差异

这是选型上唯一真正要害的一条。

本项目的 `--optimize` 不是"编码一次"，而是**搜索**：

- JPEG：在 `[min, 95]` 二分质量，每次 `Eval(q)` = 完整编码 → 完整解码 → 全图 PSNR
- PNG：8 档压缩阶梯全枚举 + 调色板量化候选
- WebP：q40 锚点 + 4 点网格 + 二分
- GIF：多档候选 + LZW

单候选成本 ≈ 完整编码 + 完整解码 + 全图画质评估（4.19MP 实测 PNG 单候选 847ms、WebP 478ms）。
**没有任何一个 jSquash 包提供这一层**——它们都是"给我参数，我编码一次"。

所以：
- 若目标是「浏览器里能转换格式」→ **jSquash 直接够用，不必自研**
- 若目标是「浏览器里能复现 `--optimize` 的体积收益」→ 得自己在 jSquash 之上写搜索循环，
  唯一的现成参考是 Squoosh CLI 的 Butteraugli 自动压质（但它也只在 CLI/Node 侧）

值得注意的是：**画质度量这一环反而更适合借用现成库**。本项目已修过
`QualityMetrics.Compare` 的 WebP 通道数缺陷（RGBA 按 3 通道比较导致感知 PSNR 虚高 7dB）；
换成 Butteraugli / SSIM 这类成熟实现能规避同类的自研口径风险。

---

## 4. 另一条路：不用 jSquash，而是「静态链接自己的原生库」

Squoosh 之外还有 `@discere-os/libjpeg-turbo.wasm` 这类：把 libjpeg-turbo 静态编进 wasm，
开 wasm SIMD、SharedArrayBuffer、甚至 WebGPU 做 DCT。这条路对**本项目**的意义在于：

- 本项目的 WebP 卡在 libwebp 的 `[LibraryImport]` + 反向 P/Invoke 回调
  （`Marshal.GetFunctionPointerForDelegate` → `WriteToManagedStream`，wasm 无原生→托管跳板）
- 若改成 Emscripten 静态链接 + `[UnmanagedCallersOnly]` 回调改造，**理论可行但等于自建一套原生构建链**，
  与现在"从 NuGet 拷预编译 dll"的发布模型完全不同

**这是 §2.2 已经写过的结论，本文不改变它**：成本远超收益，不如直接用现成的 jSquash WebP 包。

---

## 5. 建议

按目标分：

1. **只想让浏览器能转格式** → 直接用 `@jsquash/*`，本项目不必做 wasm 化。
   现在仓库里这套 JPEG 单线程改动（`JpegExecutionMode` / `EncodeRgbSequential` /
   `FusedRowSource` 同步模式）的**唯一价值**就只剩「Blazor WASM 场景下复用 .NET 代码」，
   这是个**真实但更窄**的需求。
2. **想保留 `--optimize` 的体积收益** → 前端调 `@jsquash` 做编解码，
   用 Web Worker 跑搜索循环；画质度量换成现成实现（Butteraugli/SSIM），别自研。
3. **纯做 wasm 实验** → jSquash 现成，`npm i @jsquash/jpeg` 半小时能出 demo。

**明确不建议**：为了 wasm 去自建 libwebp 静态链接 + 回调改造。

---

## 6. 一句话

**有现成的，而且比自研的覆盖更全（多 AVIF/JXL）。** `@jsquash`（Apache-2.0、Squoosh 拆分、
MozJPEG/libwebp/libavif 参考实现 + wasm SIMD）是唯一值得比的选项；它的空白正好是本项目唯一的独有资产
——`--optimize` 那种「编码+解码+全图评估」的质量搜索。要不要为这块独有资产维护第二条运行时，
是决策的全部内容。
