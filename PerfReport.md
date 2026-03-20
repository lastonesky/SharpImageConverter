性能问题定位

- JPEG 解码对流直接全量读入并二次拷贝，峰值内存≈输入大小×2，且阻塞式读取不利于大图/网络流场景 JpegDecoder.cs:L20-L72
- JPEG 重建阶段按组件租借平面缓冲，再分量交错到输出，出现多份大缓冲并行存在与全量遍历开销 JpegDecoder.cs:L263-L339
- JPEG 像素格式转 RGB24 全量逐像素循环，缺少 SIMD/并行，高清大图会成为 CPU 热点 JpegImage.cs:L90-L197
- PNG 解码先把 IDAT 全量收集到内存流，再一次性解压到大缓冲，且色彩转换会再分配并二次遍历，内存与带宽消耗偏高 PngDecoder.cs:L80-L186 , PngDecoder.cs:L650-L823
- GIF 多帧解码每帧都会 clone 整张画布，叠加 backBuffer 复制，帧数多时内存与拷贝成本急剧上升 GifDecoder.cs:L184-L205
- 图像处理的缩放与灰度转换为纯 CPU 密集循环，尤其是 Area 缩放的双重嵌套与双精度计算在大图缩小时极易成为热点 Processing.cs:L38-L433 , Processing.cs:L473-L489
- EXIF 方向处理直接新分配并全量拷贝像素，若在 LoadJpeg 后立即执行会造成额外一次整图遍历与内存分配 ImageFrame.cs:L492-L553
- Clone 会无条件复制整张缓冲，在流水线中多次调用会放大内存带宽占用 Processing.cs:L500-L513
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