# SharpImageConverter 做 WebAssembly 的可行性分析

> 2026-10-19 · 基于**实测**而非推测。测试台 `E:\tmp\wasmprobe`（Blazor WASM + 直接引用 `src/SharpImageConverter.csproj`），
> 桌面基线 `E:\tmp\deskprobe`，两者跑同一份源码、同一组样本。

## 0. 结论

**能跑，但不是"改个 csproj 就能上线"的量级。**

| 格式 | 单线程 wasm | wasm+Threads | 结论 |
|---|---|---|---|
| BMP 编/解 | ✅ 往返逐字节一致 | ✅ | 直接可用 |
| PNG 编/解 | ✅ 往返逐字节一致 | ✅ | 直接可用 |
| GIF 编/解 | ✅ | ✅ | 直接可用 |
| JPEG **解码** | ✅ 1000×1000 正常 | ✅ | 直接可用（慢，见 §3） |
| JPEG **编码** | ⚠️ 原始代码炸在 `Cannot wait on monitors`；**单线程路径已补齐**，见 §6 | ✅ 1597B 正常 | 已修 |
| WebP（全部） | ❌ 原生库 + 反向 P/Invoke 不支持 | ❌ 同左 | **需重写 or 换托管实现** |
| `--optimize`（质量搜索） | ❌ 依赖 `Parallel.For` | ⚠️ 需核 | 见 §4 |

不开线程时，库里 **33k 行源码全部通过编译**，只在运行时炸在 3 个点上。这说明移植面很小——问题不在"能不能编译"，
而在**运行时能力模型**和**原生依赖**。

---

## 1. 实测环境与结果

### 1.1 环境探测（Blazor WASM 内 `RuntimeInformation` 实打实读出来的）

```
ProcessorCount=1           ← 单线程运行时
Arch=Wasm; OS=Browser
Sse2=False Ssse3=False Sse41=False Avx2=False AdvSimd=False
Vector128.IsHardwareAccelerated=True      ← 关键
Vector256.IsHardwareAccelerated=False
Vector.IsHardwareAccelerated=True
```

开 `WasmEnableThreads=true` 后 `ProcessorCount=12`（本机 12 逻辑核），其余不变。

### 1.2 功能矩阵

| 用例 | 单线程 | 多线程 | 产物一致性 |
|---|---|---|---|
| BMP 128² 往返 | ✅ 49206 B | ✅ | identical=True |
| PNG 128² 往返 | ✅ 929 B / 14 ms | ✅ 24 ms | identical=True |
| GIF 128² 编码 | ✅ 8794 B / 49 ms | ✅ 69 ms | — |
| JPEG 1000² 解码（Amish, 56KB） | ✅ 1051 ms | ✅ 1009 ms | — |
| PNG 600×450 解码（car.png） | ✅ 23 ms | ✅ 29 ms | — |
| PNG 1000² 编码 | ✅ 16851 B / 186 ms | ✅ 235 ms | 与桌面同为 16851 B |
| JPEG 128² 编码 q80 | ❌ 见 §2.1 | ✅ 1597 B / 61 ms | 与桌面同为 1597 B |
| WebP 128² 编码 q75 | ❌ 见 §2.2 | ❌ 同左 | 桌面 374 B（对比用） |

**产物体积与桌面逐字节同值**（16851 / 1597 / 8794 / 49206），说明编解码在 wasm 上的输出是可信的，
不是"能跑但结果不对"。

### 1.3 桌面基线（同机 i7-10750H，12 逻辑核，3 次中位数）

| 用例 | 桌面 | wasm 单线程 | 倍数 |
|---|---|---|---|
| JPEG 1000² 解码 | 64 ms | 1009 ms | **15.8×** |
| PNG 1000² 编码 | 8 ms | 186 ms | **23×** |
| PNG 600×450 解码 | 2 ms | 23 ms | 11× |
| GIF 128² 编码 | 2 ms | 49 ms | 24× |
| JPEG 128² 编码 | 1 ms | 61 ms（多线程） | 60× |

倍数**不具跨机意义**（桌面是 AOT/JIT 后的原生，wasm 是 Mono 解释/LLVM 化代码），但量级说明问题：
小图上是几十倍，大图上是十几倍。这个比例对"批量处理"是致命的，对"单张交互"还能忍。

---

## 2. 三个硬阻塞点

### 2.1 JPEG 编码：`Cannot wait on monitors on this runtime`

```
AggregateException: Cannot wait on monitors on this runtime.
 inner: PlatformNotSupportedException
```

根因是结构性的，不是 bug：`JpegEncoder.EncodeRgbPipeline`（`JpegEncoder.cs:1004`）是**无条件**的三段流水线：

```csharp
var t1 = Task.Run(() => RunStage(() => ProduceRgbSamples(...)));
for (int i = 0; i < dctWorkers; i++)
    dctTasks[i] = Task.Run(() => RunStage(() => ProcessDct(...)));
var t4 = Task.Run(() => RunStage(() => ProcessHuffmanOrdered(...)));
Task.WaitAll(dctTasks);
Task.WaitAll(t1, t4);
```

其中 `PipeQueue.Enqueue/Dequeue` 用 `SemaphoreSlim.Wait()`（`JpegEncoder.cs:303`、`:319`）阻塞等待。
单线程 wasm 运行时里 `Monitor.Wait` 明确抛 `PlatformNotSupportedException`（设计如此，避免死锁整页）。

**注意**：这条路径没有"小图走串行"的退化分支——任何 JPEG 编码都会走到这里。
所以修复不是"加个 `if` 而已"，得补一条真正的单线程编码路径。工作量在 `EncodeRgbPipeline` /
`ProcessDct` / `ProcessHuffmanOrdered` 三个函数的串行版本，以及 `JpegBaselineFusedRowSource`
里 `_producer.Wait(2000)`（`:206`）这类阻塞点。

### 2.2 WebP：原生库 + 反向 P/Invoke

```
TypeInitializationException: TypeInitialization_Type, WebpCodec
 inner: PlatformNotSupportedException: No native to managed transition for method
 'int WebpCodec:WriteToManagedStream (byte*,uintptr,intptr)', missing [UnmanagedCallersOnly] attribute.
```

两层问题叠在一起：

1. **链接层**：`WebpCodec` 有 26 处 `[LibraryImport("libwebp"/"libwebpdecoder"/"libwebpmux")]`。
   wasm 里没有 dlopen，这些 `DllImport` 必须重新链接到静态编译进 wasm 模块的 libwebp。理论上
   Emscripten 能做（`libwebp` 有现成的 `-sEXPORTED_FUNCTIONS` 方案），但等于自建一套原生构建链，
   与现在"从 NuGet 拷预编译 dll"的发布模型完全不同。
2. **回调层**（更麻烦）：`WebpCodec.cs:118` 用 `Marshal.GetFunctionPointerForDelegate(WriterCallback)`
   把托管委托变成函数指针交给 libwebp 的回调。wasm 是**单地址空间**，没有"原生→托管"的跳板，
   这个调用必须改成 `[UnmanagedCallersOnly]` 静态方法 + 全局状态传递。这不是改一行特性的事：
   `WriteToManagedStream` 依赖实例字段（`WebPWriterState.Stream`），要重构成 ThreadStatic/AsyncLocal 上下文。

**结论**：WebP 在 wasm 里要么重写绑定层，要么换纯托管实现（如自研 VP8L 编码器），代价远超其它格式。

### 2.3 SIMD 分派全部落空——但**没有性能崩塌**

`SimdCompat.cs` 的分派是 `Ssse3.IsSupported` / `Sse41.IsSupported` / `AdvSimd.*.IsSupported` 三重判定
（见 `SimdCompat.cs:70-77`、`SimdHelper.cs:400-743`）。wasm 上**三个全是 false**，所以：

- `ByteShuffleSupported` → `!ForceScalar && (false || false)` → **false**
- 所有向量分支退回标量算术

但 `Vector128.IsHardwareAccelerated == true` —— .NET 10 的 WASM 有 **PackedSimd**（wasm simd128）。
也就是说：**可用的 SIMD 能力存在，只是这个库的守卫一个都没识别到它**。

这是本次分析里最有价值的一条：如果目标是让库在浏览器里跑得动，补一条
`PackedSimd.IsSupported` 分支到 `SimdCompat`（参照 ARM64 那次的移植手法，NEON 分支是逐条验证过语义等价的），
理论收益是对齐现有 x86/NEON 路径，而不是从标量重新发明。实际倍数**未实测**——本次没做，
不做无根据数字。

---

## 3. 交付形态与体积

### 3.1 部署体积

`dotnet publish` 后 `wwwroot` 共 **73 MB**，其中 `_framework` 的 `.gz` 合计 **21 MB**。
单看库自己：`SharpImageConverter.wasm` 485 KB（gzip 212 KB）。
运行时占大头：`dotnet.native.wasm` 3.09 MB（gzip 1.23 MB）。

对"图像处理工具站"来说 21 MB 首屏不可接受，需要 `IL trimming + AOT`；但 **AOT 与
`WasmEnableThreads` 目前不共存**（.NET 10 状态），这会和 §2.1 的 JPEG 编码冲突——两者必须二选一或
让 JPEG 编码走单线程路径。这是本次分析发现的**最尖锐的矛盾**。

### 3.2 多线程不是"开个开关"

开线程要满足全部条件：

1. `<WasmEnableThreads>true</WasmEnableThreads>`
2. COOP/COEP 响应头（DevServer 会自动加 `Cross-Origin-Embedder-Policy: require-corp` +
   `Cross-Origin-Opener-Policy: same-origin`；**正式托管环境要自己配**，且要求所有跨域资源带 CORP 头）
3. 浏览器支持 SharedArrayBuffer

另外实测踩到一个坑：**SDK 11.0.100-rc.1 构建出的多线程产物在启动时直接崩**
（`MONO_WASM: Can't find method JavaScriptExports.InstallMainSynchronizationContext`
→ `mono_wasm_start_deputy_thread_async() failed`）。固定到 SDK 10.0.402（`global.json`）后正常。
这是工具链问题，不是库的问题，但会浪费排查时间。

值得注意的是：多线程下 `png-encode`、`gif-encode` 反而**更慢**（186→235 ms、49→69 ms）。
小图上线程调度开销盖过收益；JPEG 编码从"不可用"变成"可用"是唯一实打实的收益。

---

## 4. `--optimize` 路径：未验证，但风险明确

`ImageOptimizer.SearchQualityParallel`（`ImageOptimizer.cs:655`）用
`Parallel.For(..., MaxDegreeOfParallelism: dop)`；`QualityMetrics.Compare` 内层
（`ImageQuality.cs:414`）同样 `Parallel.For`。单线程运行时下 `Parallel.For` 通常是**串行降级**而非抛异常，
但 `Environment.ProcessorCount == 1` 时外层 `CandidateParallelism` 会把 degree 夹到 1，直接走
`SearchQuality` 串行分支——所以**大概率能跑**。

本次没用真实样本跑 `Optimize` API（`ImageOptimizer.Optimize` 只接**文件路径**，
wasm 里没有文件系统，得先加 byte[]/Stream 入口才谈得上验证）。这是明确的**待办缺口**，不是结论。

---

## 5. 建议路线（按投入排序）

### 路线 A：托管库 + 自己包一层 JS 胶水（推荐先做可行性验证）
不做 wasm 打包，把库编译成**普通 DLL** 交给宿主应用（Blazor WASM / 自己的 wasm 宿主）。
- 优点：零架构改动，§2.1/§2.2 的阻塞原样暴露，但都只是"该格式不可用"，不影响 PNG/GIF/BMP
- 风险：JPEG 编码在此形态下不可用（除非开线程）

### 路线 B：补齐单线程路径（必做，无论哪条路线）——**已落地**
1. ✅ `EncodeRgbPipeline` / `EncodeGrayPipeline` 加串行实现（`JpegEncoder.EncodeRgbSequential`），
   判定集中在 `JpegExecutionMode.IsSequential`
2. ✅ `JpegBaselineFusedRowSource` 加同步模式（就地解一行），不再需要 `_producer` 线程
3. ⬜ `SimdCompat` 补 `PackedSimd` 分支（收益待测，但结构上对齐 ARM64 那次的成功经验）
4. ⬜ WebP 单独决策：wasm 目标下直接不注册该格式，`IsMatch` 返回 false

落地细节与实测见 §6。

---

## 6. 单线程路径的落地结果（2026-10-19）

### 6.1 为什么"加一条单线程支路"就能编码

wasm 单线程运行时里 **`Monitor.Wait` / `SemaphoreSlim.Wait` 直接抛
`PlatformNotSupportedException`**——这是设计行为（阻塞会锁死唯一那根线程，整页死掉）。
原来的 `EncodeRgbPipeline` 是**无条件**三段流水线：

```
Fill(线程1) ──PipeQueue──> DCT(线程2..n) ──PipeQueue──> Huffman(线程1)
```

两个 `PipeQueue` 的入队/出队都用 `SemaphoreSlim.Wait()` 阻塞。所以问题不是"并行度不够"，
而是**只要建了这两条队列，编码就一定炸**，跟图片大小无关。

单线程支路把这三个阶段**顺序串起来**：同一个线程依次做 填充 → DCT量化 → Huffman，
队列与信号量整个不存在，于是没有任何阻塞点。**这才是"加了就能跑"的真正原因**——
不是绕开了限制，而是消除了产生阻塞的数据结构。

关键约束是 **Huffman 阶段必须全局按栅格顺序**（DC 预测依赖前一块）。并行版靠
`ProcessHuffmanOrdered` + 待定环形重排来保证顺序；串行版天然就是顺序，所以结果**逐字节相同**。

### 6.2 覆盖范围

| 入口 | 串行实现 | 说明 |
|---|---|---|
| `EncodeRgbPipeline` | `EncodeRgbSequential` | 420 / 444 均覆盖 |
| `EncodeGrayPipeline` | `EncodeGraySequential` | 灰度 |
| `CollectHuffmanHistograms` | 串行直通 | `optimizeHuffman` 的第二遍统计 |
| `JpegSampleCache.Capture` | 串行 `for` | 采样缓存填充 |
| `JpegBaselineFusedRowSource` | 同步模式 | 解码侧逐 MCU 行 |

统一判定在 `JpegExecutionMode.IsSequential`，可用环境变量 `SIC_JPEG_PIPELINE=0|1` 强制覆盖
（同一份二进制内做 A/B 用）。

### 6.3 实测

**135 例逐字节一致性**（9 种尺寸 × 420/444/灰度 × optimizeHuffman × trellis）：
串行与并行的 SHA256 **全部相同**。CLI 端到端三组（`--optimize` / `--quality 70 --jpeg-subsampling 444` /
`--jpeg-progressive`）产出 md5 一致；解码侧 `--to bmp`（走 fused 行级路径）md5 一致。

**串行开销**（1000×1000，同一二进制内 `SIC_JPEG_PIPELINE` 交替，5 轮取中位）：

| 模式 | med |
|---|---|
| 串行 | 32 / 67 / 36 / 60 / 56 ms |
| 并行 | 24 / 27 / 24 / 25 / 26 ms |

串行约为并行的 1.4~2.5 倍。单核 `DOTNET_PROCESSOR_COUNT=1` 跑全量 **390 项测试全通过**，
多核同样 390 全通过。

### 6.4 踩过的两个坑（都已修，写在这里防复发）

1. **`PrepareBaselineRowScan` 拆分时把表绑定放进了 `_syncMode` 分支**。原本它同时做
   「校验 + `AssignTables` + `ResetPredictors`」，拆成两半后 `AssignTables` 只在同步分支调用，
   并行分支从未绑表 ⇒ 连 `my=0` 都解不出来。现在绑表 + 重置预测器都在构造函数里无条件执行。
2. **`_bitOffset` 记成了绝对位置**。同步模式每行重建 `JpegBitReader`，它以
   `_data.AsSpan(_entropyOffset)` 为原点，所以偏移必须从 0 起算；写成 `entropyOffset`
   会让首行从错位 623 字节处起解。

还有一个被推翻的**错误前提**，值得单独记：曾假设「MCU 行结束时位流必然字节对齐」，据此只用
`BytesConsumed` 重建位读器。实测行尾 `bitCount` 常为 2/6/12——只有扫描起点和 RST 边界才做
`AlignToByte + DiscardBufferedBits`，普通行尾**不对齐**。现在改为保存
「偏移 + 已消费字节 + 位缓冲原文 + 残位数 + 待定 marker + 填充字节数 + 是否读尽」整套标量。

---

## 7. 一句话回答
**库本身移植面很小（33k 行全编过，只在 3 个运行时能力点上炸），但"能跑"和"好用"之间隔着
JPEG 单线程路径、WebP 原生依赖、以及 AOT/Threads 互斥三件事。** PNG/GIF/BMP 现在就能在浏览器里
原样用；JPEG 编码要么开线程要么改结构；WebP 建议在 wasm 目标下直接砍掉。
