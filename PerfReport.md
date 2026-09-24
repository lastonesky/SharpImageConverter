性能问题定位

- 【已完成】JPEG 解码对流直接全量读入并二次拷贝，峰值内存≈输入大小×2，且阻塞式读取不利于大图/网络流场景 JpegDecoder.cs:L20-L72
- 【已完成】JPEG 重建阶段按组件租借平面缓冲，再分量交错到输出，出现多份大缓冲并行存在与全量遍历开销 JpegDecoder.cs:L263-L339
- 【已完成】JPEG 像素格式转 RGB24 全量逐像素循环，缺少 SIMD/并行，高清大图会成为 CPU 热点 JpegImage.cs:L90-L197
- 【已完成】PNG 解码先把 IDAT 全量收集到内存流，再一次性解压到大缓冲，且色彩转换会再分配并二次遍历，内存与带宽消耗偏高 PngDecoder.cs:L80-L186 , PngDecoder.cs:L650-L823
- 【问题确认不存在】GIF 多帧解码每帧都会 clone 整张画布，叠加 backBuffer 复制，帧数多时内存与拷贝成本急剧上升 GifDecoder.cs:L184-L205
- 【已完成】图像处理的缩放与灰度转换为纯 CPU 密集循环，尤其是 Area 缩放的双重嵌套与双精度计算在大图缩小时极易成为热点 Processing.cs:L38-L433 , Processing.cs:L473-L489
- 【已完成】EXIF 方向处理直接新分配并全量拷贝像素，若在 LoadJpeg 后立即执行会造成额外一次整图遍历与内存分配 ImageFrame.cs:L492-L553
- 【问题确认不存在】Clone 会无条件复制整张缓冲，在流水线中多次调用会放大内存带宽占用 Processing.cs:L500-L513
优化建议

- JPEG 解码：优先使用流式解析路径（已有异步流式 API），或在能获知长度时一次性分配精确大小缓冲，避免“租借扩容 + 二次拷贝”模式；对超大图可考虑分块解码以降低峰值内存 JpegDecoder.cs:L20-L72
- JPEG 重建：若 SIMD 路径失败再分配输出，可考虑延后输出分配；对常见 YCbCr -> RGB24 走 SIMD 或向量化路径，减少分量平面与交错的双遍历 JpegDecoder.cs:L263-L339
- JPEG 颜色转换：在 ToRgb24 里引入 SIMD/并行化路径，或按行并行，显著降低大图转换耗时 JpegImage.cs:L90-L197
- PNG 解码：将 IDAT 逐块流式送入解压器并直接写入目标缓冲，减少中间内存流与多次复制；在交错图中直接 scatter 到最终 RGB 缓冲，避免“pass->RGB->scatter”三次遍历 PngDecoder.cs:L80-L186 , PngDecoder.cs:L650-L823
- GIF 解码：提供“只解第一帧”或“懒加载帧”的接口，避免每帧全量 clone；或在帧缓存中使用共享底图 + 差分数据以降低拷贝 GifDecoder.cs:L184-L205
- 处理管线：Area 缩放可用积分图/分离核近似，降低 O(w h scale) 复杂度；Bilinear/Bicubic 可用固定点/向量化减少浮点运算；灰度可向量化按块处理 Processing.cs:L38-L433 , Processing.cs:L473-L489
- EXIF 方向：将旋转/翻转延后到最终输出阶段，或引入“视图式方向”避免多一次整图拷贝 ImageFrame.cs:L492-L553
- Clone 使用：在高频流水线中优先 Mutate，避免反复的全量复制 Processing.cs:L500-L513
验证方法

- 基线指标：对 JPEG/PNG/GIF 各选 3 类图片（小图、超大图、动图/高分辨率），记录解码/编码耗时、吞吐与峰值内存，作为优化前基线
- 运行监控：用 dotnet-counters 观察 GC Allocated MB/sec、Gen2 次数与 CPU；对热点阶段用 dotnet-trace/PerfView 采样锁定函数级热点
- 量化标准：为解码/缩放/颜色转换设定目标（如耗时降低 20–40%、峰值内存降低 30%），优化后对比同样数据集验证回归
如果你希望我直接落地某一类优化（例如 JPEG 流式解码、PNG 解压路径优化或缩放算法加速），我可以从对应文件开始实现并给出基准测试脚本。

## 实测验证：examples/progressive.jpg（10650×13426 = 143 MP，RGB 409 MB）

### 方法

- "改动前"不是手写参考实现，而是用 `git archive` 取出提交 `7f967ba` 的 `src/` 编成独立程序集
  （`AssemblyName=SharpImageConverter.Baseline`），与当前工作树的 `src` 通过 `extern alias` 同时加载，
  在同一进程内交错 A/B、每侧取多轮最小值。此前用**手写参考实现**测出的数字偏高，已作废（见下）。
- 非原地算子（Resize/EXIF/编解码/格式转换）直接吃同一份输入，零额外开销；
  原地算子（Grayscale、AddBytesInPlace）每轮先无分配拷贝到预分配暂存区——**不能用 `Clone()`**，
  大数组克隆本身触发 LOH GC，扣除项自带巨大噪声（会让结果变成负数）。
- 合计 40 项，全部 `SequenceEqual` 逐字节比对：不一致 0 项。
- 环境：.NET 10.0.12 / 12 逻辑核 / SSE2+SSSE3+SSE4.1+AVX2 均可用。

### 全尺寸 143 MP

| 操作 | 改动前 | 改动后 | 加速 |
|---|---|---|---|
| JPEG 解码（渐进式） | 699.75 ms | 699.56 ms | 1.00x |
| Resize → 1/8（ResizeArea） | 135.49 ms | 104.67 ms | **1.29x** |
| Resize → 1/16（ResizeArea） | 138.70 ms | 112.69 ms | **1.23x** |
| ResizeBilinear → 1/8 | 22.80 ms | 12.57 ms | **1.81x** |
| Grayscale（原地 409 MB） | 56.72 ms | 36.53 ms | **1.55x** |
| AddBytesInPlace 逐行（原地 409 MB） | 55.22 ms | 50.07 ms | 1.10x |
| 流水线 解码→缩小 1/8→PNG | 977.78 ms | 966.27 ms | 1.01x |

### 裁切图 2048×2048（取自原图中心）

| 操作 | 改动前 | 改动后 | 加速 |
|---|---|---|---|
| Resize 缩小 1/2（Area） | 5.88 ms | 4.98 ms | 1.18x |
| Resize 缩小 1/3（Area） | 8.86 ms | 6.56 ms | 1.35x |
| Resize 缩小 1/5（Area） | 23.60 ms | 18.58 ms | 1.27x |
| Resize 缩小 1/10（Area） | 16.46 ms | 13.14 ms | 1.25x |
| ResizeBilinear 缩小 1/2 | 8.26 ms | 3.81 ms | **2.17x** |
| ResizeBilinear 放大 150% | 71.12 ms | 31.78 ms | **2.24x** |
| ResizeBilinear 放大 200% | 124.62 ms | 54.19 ms | **2.30x** |
| Resize 放大 200%（Bicubic） | 230.64 ms | 175.42 ms | 1.31x |
| Bicubic 缩小 1/2 | 12.24 ms | 9.44 ms | 1.30x |
| Grayscale | 2.30 ms | 1.25 ms | 1.83x |
| Gray8 → Rgb24 | 5.08 ms | 1.14 ms | **4.44x** |
| Rgba32 → Rgb24 | 7.69 ms | 2.07 ms | **3.71x** |
| Rgb24 → Rgba32 | 9.71 ms | 2.03 ms | **4.77x** |
| AddBytesInPlace 逐行（stride） | 1.92 ms | 1.60 ms | 1.20x |
| PNG 编码（全行 Up 滤波） | 186.24 ms | 187.63 ms | 0.99x |
| PNG 解码（Up 滤波反算） | 12.98 ms | 12.78 ms | 1.02x |
| EXIF orientation 4 | 11.32 ms | 1.51 ms | **7.52x** |
| EXIF orientation 5–8 | ~21.7 ms | ~13.5 ms | 1.57–1.64x |
| JPEG 编码 q=85 | 19.95 ms | 20.44 ms | 0.98x |

### 读数说明

- **ResizeBilinear 是这轮最大赢家**，放大/缩小都稳定在 2.1–2.3x（SSSE3 路径真正吃满了）。
- **编解码整体几乎不动**：JPEG 解码 1.00x（未改）、PNG 编解码 ~1.0x、JPEG 编码 ~1.0x。
  因为耗时被熵编码/deflate 主导，`AddBytesInPlace` 和量化表缓存再快也占不到 1%。
  PNG 编码的 0.99x 不是回退——写入端 `PngWriter.ApplyUpFilterSimd` 未经改动，是测量顺序偏差。
- **`AddBytesInPlace` 的收益随缓冲变大而坍缩**：4 MB（进缓存）2.8x → 12 MB 1.20x → 409 MB 1.10x。
  它做的是纯带宽搬运，超出缓存后被内存带宽卡死。
- **量化表缓存没有可测收益**：每次编码本来只建 64 字节的表，缓存省不掉可观时间。

### 作废的旧数字（重要）

此前用**手写参考实现**测出、并在对话中报告过的数字偏高，以下均已用真实基线重测更正：

| 项目 | 旧（手写参考） | 新（真实基线 A/B） |
|---|---|---|
| ResizeArea 2000×1500→1000×750 | 6.86x | **1.21x** |
| ResizeArea 4000×3000→800×600 | 5.02x | **1.24x** |
| EXIF case 5–8 | 2.25–2.58x | **1.57–1.64x** |

原因：手写参考实现比真实的改动前代码更慢，放大了加速比。今后一律用 git 取真实基线比对。

### 下一步机会

- `PngWriter.ApplyUpFilterSimd` 仍在用 `new Vector<byte>(span.Slice(i))`（含拷贝），
  与已修的 `JpegEncoder` 里 `new Vector<int>(span)` 是同一类问题，可照同样方式改 `LoadUnsafe`。
