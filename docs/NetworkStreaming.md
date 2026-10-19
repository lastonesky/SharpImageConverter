# 网络传输流式调用的稳定性与健壮性实测

> 生成时间：2026-10-19 11:41　·　运行时 .NET 10.0.13　·　逻辑核 12

测试台：`tools/streamnet`。被测入口：`JpegDecoder.DecodeFromStreamAsync`、`ImageFrame.LoadJpeg/Load(Stream)`、`JpegEncoder/PngWriter/GifEncoder/WebpEncoderAdapter` 的 Stream 写出。
所有输入都经过一个**不可 Seek** 的仿真网络流：固定/抖动分片、逐次延迟、提前 EOF（FIN）、连接重置（RST）、可中途取消。

## 结论摘要

唯一的高危问题是**渐进式 JPEG 在扫描边界被截断时不会报错**：解码器的位读取器把「数据源读尽」当作 EOI 补 0xFF，多扫描 JPEG 只解出前几个扫描也会被 `ValidateScansComplete` 判为完整（它只检查「组件是否解到过系数」），于是返回一张严重失真的图像。默认行为保持宽松（兼容历史），网络场景应显式打开 `requireEoi: true`。

**P0**

- Amish-Noka-Dresser.jpg: 宽松模式在扫描边界截断时静默返回损坏图像 9/149 例，最坏像素差异 93.83 %（最深移除 48084 B，占 85.7%）。
- 5_star_base.jpg: 宽松模式在扫描边界截断时静默返回损坏图像 9/184 例，最坏像素差异 96.92 %（最深移除 247116 B，占 86.2%）。
- progressive.jpg: 宽松模式在扫描边界截断时静默返回损坏图像 6/144 例，最坏像素差异 38.21 %（最深移除 2044117 B，占 66.1%）。

**P2**

- 内存放大：3.09 MB 的 143 MP 渐进 JPEG 在流式解码中仍分配 409 MB（139× 文件大小）；库内没有可配置的像素数/内存预算闸门，网络输入需要调用方自己限制尺寸或字节数。
- 只支持异步读取的流在熵解码阶段抛 `NotSupportedException`：`DecodeFromStreamAsync` 的异步承诺是部分的。
- GIF: 缺失尾部 1 字节（GIF trailer 0x3B）仍返回图像，尾部完整性没有自检。

**未发现问题的维度**：分片交付一致性（1 B ~ 256 KB，§1）、连接重置传播（§3）、取消响应（§4）、限速下的线性开销（§5）、16 路并发无串扰（§7）、写出侧分片确定性与断链后可用性（§8）、PNG/BMP/WebP 截断拒绝（§11）。

## 未修复（需决策）

1. **CLI 退出码恒为 0**：`Program.Main` 是 `void`，解码/编码失败只打印到 stdout。实测 `--stream-strict` 拒绝截断文件后 `exit code` 仍是 0，网络管道里无法判定成败。要改就得先定批量语义（部分失败返回几？），故本轮只记录。
2. **`ImageFrame.LoadJpeg` 没有 `CancellationToken`**：只能下沉到 `JpegDecoder.DecodeFromStreamAsync` 才能取消。
3. **只支持 `ReadAsync` 的流无法解码**：熵解码是同步循环。要么让调用方先把整条流异步读进内存，要么增加一个「异步读满 → 内存解码」的入口；后者会放弃流式的内存优势。
4. **库内没有像素/内存预算闸门**：`MemoryGuard` 只服务 CLI 的文件路径，Stream 入口不经过它。

## 复现方式

```bash
dotnet run --project tools/streamnet -c Release                 # 全量矩阵（约 1 分钟）
dotnet run --project tools/streamnet -c Release -- --only 2     # 只跑第 2 个场景
dotnet test --filter FullyQualifiedName~NetworkStreamingTests   # 回归测试（15 条）
```

## 本轮修复

1. **严格 EOI 模式**（对应 P0）：`JpegDecoder.DecodeFromStreamAsync(..., requireEoi: true)` 与 `ImageFrame.LoadJpeg(..., requireEoi: true)`；CLI 新增 `--stream-strict`。位读取器新增 `ReachedEndOfData`——数据源读尽却未遇到真实 marker 时置位，解码器据此拒绝「扫描边界截断」。默认关闭，保持既有宽松行为不变。
2. **WebP 写出异常保形**（对应 P1）：`WebpCodec` 原先把写出回调里的 `IOException` 包成 `InvalidOperationException`，调用方无法用 `catch (IOException)` 区分「传输中断」与「编码器内部错误」；现在先用 `ExceptionDispatchInfo` 原样抛出回调异常，再判断 `WebPEncode` 返回值。
3. **WebP 解码异常归类**：截断/损坏数据的 `InvalidOperationException` 改为 `InvalidDataException`，与 PNG/JPEG 的「数据问题」口径一致。

宽松/严格两种模式在 4 个样本上的对照见 §2：严格模式把「静默损坏」从 24 例降到 0 例，且完整文件全部照常通过（§1 的分片矩阵全程开启严格模式，无一误拒）。


## 1. 分片交付一致性（分片大小 A/B）

同一份 JPEG 经不可 Seek 的仿真网络流喂给 `JpegDecoder.DecodeFromStreamAsync`，分片从 1 字节到 256 KB 变化，像素必须与内存解码逐字节一致；同时开启严格模式验证正常文件不被误拒。

| 文件 | 字节 | 分片 | Read 次数 | 解码 ms | 像素一致 |
|---|---|---|---|---|---|
| video-001.rgb.jpeg | 6237 | 1 | 6237 | 14.7 | 是 |
| video-001.rgb.jpeg | 6237 | 3 | 2079 | 1.2 | 是 |
| video-001.rgb.jpeg | 6237 | 17 | 367 | 1.0 | 是 |
| video-001.rgb.jpeg | 6237 | 1024 | 7 | 0.9 | 是 |
| video-001.rgb.jpeg | 6237 | 16384 | 1 | 0.9 | 是 |
| video-001.rgb.jpeg | 6237 | 262144 | 1 | 0.9 | 是 |
| Amish-Noka-Dresser.jpg | 56140 | 1 | 56140 | 61.7 | 是 |
| Amish-Noka-Dresser.jpg | 56140 | 3 | 18714 | 54.8 | 是 |
| Amish-Noka-Dresser.jpg | 56140 | 17 | 3303 | 52.6 | 是 |
| Amish-Noka-Dresser.jpg | 56140 | 1024 | 55 | 52.1 | 是 |
| Amish-Noka-Dresser.jpg | 56140 | 16384 | 4 | 62.6 | 是 |
| Amish-Noka-Dresser.jpg | 56140 | 262144 | 4 | 11.7 | 是 |
| 5_star_base.jpg | 286515 | 1 | 286515 | 59.0 | 是 |
| 5_star_base.jpg | 286515 | 3 | 95505 | 35.5 | 是 |
| 5_star_base.jpg | 286515 | 17 | 16854 | 27.3 | 是 |
| 5_star_base.jpg | 286515 | 1024 | 280 | 25.2 | 是 |
| 5_star_base.jpg | 286515 | 16384 | 18 | 25.2 | 是 |
| 5_star_base.jpg | 286515 | 262144 | 18 | 24.5 | 是 |
| progressive.jpg | 3091834 | 1 | - | - | 跳过(过大) |
| progressive.jpg | 3091834 | 3 | 1030612 | 380.4 | 是 |
| progressive.jpg | 3091834 | 17 | 181873 | 337.4 | 是 |
| progressive.jpg | 3091834 | 1024 | 3020 | 325.4 | 是 |
| progressive.jpg | 3091834 | 16384 | 189 | 315.5 | 是 |
| progressive.jpg | 3091834 | 262144 | 189 | 316.2 | 是 |

`ImageFrame.LoadJpeg(useStreamingDecoder: true)` 与 `ImageFrame.Load(byte[])` 一致: **是**（1000x1000, 分片 5 B）。

## 2. 截断 / 提前 EOF（FIN）

在文件的每个比例点与每个 marker 边界截断，用干净 EOF（Read 返回 0）交付。分别用宽松模式（默认）与严格模式（`requireEoi: true`）解码：宽松模式统计「不报错但像素已损坏」的用例，严格模式验证这些用例是否全部被拒。

| 文件 | 字节 | 用例 | 宽松:抛 | 宽松:返回 | 宽松:损坏 | 严格:抛 | 严格:返回 | 严格:损坏 | 最坏差异 |
|---|---|---|---|---|---|---|---|---|---|
| video-001.rgb.jpeg | 6237 | 6237 | 6235 | 2 | 0 | 6237 | 0 | 0 | 0.00 % |
| Amish-Noka-Dresser.jpg | 56140 | 149 | 138 | 11 | 9 | 149 | 0 | 0 | 93.83 % |
| 5_star_base.jpg | 286515 | 184 | 173 | 11 | 9 | 184 | 0 | 0 | 96.92 % |
| progressive.jpg | 3091834 | 144 | 136 | 8 | 6 | 144 | 0 | 0 | 38.21 % |

异常类型分布（宽松+严格合计）: InvalidData=13396。
说明：`严格:返回` 列为严格模式下仍被接受的截断（正常只应剩「截取位置 = 文件长度」这一个对照用例）。

## 3. 连接重置（RST）在任意偏移注入

`Read` 抛 `IOException`。要求异常原样向上传播，不能被吞掉变成「成功返回部分图像」。

| 故障偏移 | 占文件 | 结果 | 耗时 ms |
|---|---|---|---|
| 2 | 0.0% | IO: 模拟网络故障：连接被对端重置 (RST) | 0.1 |
| 10 | 0.0% | IO: 模拟网络故障：连接被对端重置 (RST) | 0.0 |
| 2865 | 1.0% | IO: 模拟网络故障：连接被对端重置 (RST) | 0.0 |
| 57303 | 20.0% | IO: 模拟网络故障：连接被对端重置 (RST) | 3.2 |
| 143257 | 50.0% | IO: 模拟网络故障：连接被对端重置 (RST) | 7.6 |
| 257863 | 90.0% | IO: 模拟网络故障：连接被对端重置 (RST) | 18.3 |


## 4. 传输中途取消

在交付约 25% 字节时取消 `CancellationToken`，期望 `OperationCanceledException`，且取消点不晚于下一个缓冲填充点。

| 场景 | 分片 | 结果 | 耗时 ms | 已交付 |
|---|---|---|---|---|
| 中途取消 | 64 | Canceled | 4.8 | 71680/286515 |
| 中途取消 | 4096 | Canceled | 3.5 | 73728/286515 |
| 预取消 token | - | Canceled | 0 | 0/286515 |

注意：`ImageFrame.LoadJpeg(stream, useStreamingDecoder: true)` 没有 token 参数，高层入口无法取消；只能改用 `JpegDecoder.DecodeFromStreamAsync`。

## 5. 限速 / 高延迟（trickle）

每次 Read 注入固定延迟，检查是否出现忙等、二次读取或超线性开销。「注入延迟」为按 Read 次数累加的理论等待，「墙钟」应约等于注入延迟加解码时间。

| 分片 | 每次延迟 ms | Read 次数 | 注入延迟 ms | 墙钟 ms | 开销 ms | 吞吐 KB/s |
|---|---|---|---|---|---|---|
| 16384 | 20 | 18 | 360 | 386 | 26 | 724.4 |
| 1024 | 2 | 280 | 560 | 606 | 46 | 461.8 |
| 1024 | 0.5 | 280 | 140 | 164 | 24 | 1702.7 |
| 256 | 0.2 | 1120 | 224 | 251 | 27 | 1116.9 |


## 6. 内存开销（流式 vs 整文件）

`GC.GetTotalAllocatedBytes` 增量。流式路径理论上只需 16 KB 池化缓冲 + 系数 + 输出，整文件路径多分配一份与文件等长的字节数组。

| 文件 | 字节 | 路径 | 分配 MB | 放大倍数 | 相对整文件 |
|---|---|---|---|---|---|
| Amish-Noka-Dresser.jpg | 56140 | 整文件 | 2.88 | 53.7× | 基准 |
| Amish-Noka-Dresser.jpg | 56140 | 流式 | 2.90 | 54.1× | +0.02 MB |
| progressive.jpg | 3091834 | 整文件 | 409.11 | 138.7× | 基准 |
| progressive.jpg | 3091834 | 流式 | 409.12 | 138.7× | +0.00 MB |

结论：流式解码省掉的只是「整文件缓冲」这一项（≈文件大小），系数数组与 RGB 输出这两块与整文件路径完全相同，所以当图片本身很大时两者内存几乎无差。

## 7. 并发多流（16 路同时下载解码）

16 个任务同时解码，分片与延迟随机；一半在随机位置提前 EOF。检查死锁、串扰与结果确定性。

墙钟 32 ms；返回图像 8 路（其中与参考一致 8 路），InvalidData 8 路，其他 0 路。

## 8. 写出侧：编码到不可 Seek 的网络响应流

把 JPEG/PNG/GIF/WebP 编码结果分片写入只写流（模拟 HTTP 响应体）：产物应与 `MemoryStream` 逐字节一致；中途断链应抛 `IOException`，且编码器仍可继续使用。

| 格式 | 字节 | 分片写出 | 与内存一致 | 断链@25% | 断链后重编码 |
|---|---|---|---|---|---|
| JPEG | 59725 | OK | 是 | IO | 一致 |
| PNG | 368192 | OK | 是 | IO | 一致 |
| GIF | 218433 | OK | 是 | IO | 一致 |
| WebP | 15216 | OK | 是 | IO | 一致 |


## 9. 仅支持异步读取的流

有些网络流（管道 / 协议栈封装）只实现 `ReadAsync`，同步 `Read` 抛 `NotSupportedException`。`DecodeFromStreamAsync` 的熵解码阶段是同步阻塞读取，这里验证其行为。

失败于 `NotSupportedException`：该流只支持异步读取（模拟）；此时已交付 8192/56140 B（说明头部异步阶段通过，熵解码阶段失败）。

## 10. 头部尺寸炸弹（只改 SOF 宽高）

只改 SOF 的宽高、其余字节不变，子进程以 1 GB 托管堆硬上限运行。第一组 65500×65500 触发的是整型溢出保护；第二组 24000×24000（576 MP）在 int 范围内，用来检查是否存在「内存预算」维度的准入控制。

65500×65500: 子进程退出码 `0`，输出：`THROW InvalidDataException: JPEG component buffer too large.`。
24000×24000: 子进程退出码 `0`，输出：`THROW InvalidDataException: Scan decode failed (mx=969, my=2, componentId=1, bx=1938, by=4, bytesConsumed=7811, bitCount…`。
注意：第二组报 `InvalidData` 是因为截断的熵数据在填充块解完后就失败了，并未真正分配 576 MP 的系数数组，因此本项**不能**证明「大图不会 OOM」。真正的内存风险见 §6：3.09 MB 的文件需要 409 MB，文件大小完全不能作为准入依据。

## 11. 非 JPEG 格式经网络流加载

`ImageFrame.Load(Stream)` 对不可 Seek 流先缓存 12 字节头再拼接尾部。验证分片一致性，以及 50% / 仅差 1 字节截断时的行为。

| 格式 | 字节 | 分片一致 | 截断 50% | 截断 -1B |
|---|---|---|---|---|
| PNG | 368192 | 是 | InvalidData | InvalidData |
| GIF | 218433 | 是 | EndOfStream | 返回图像 1000x1000 |
| WebP | 15216 | 是 | InvalidData | InvalidData |
| BMP | 3000054 | 是 | EndOfStream | EndOfStream |

