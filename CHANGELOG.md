## 未发布
### 新增
- **ImageFrame 兼容 BGR24 / BGRA32 中间格式**：`ImagePixelFormat` 新增 `Bgr24`、`Bgra32`，
  `ImageFrame` 构造函数支持以这两种格式直接装载（按每像素字节数自动校验长度）。
- **SIMD 加速的通道转换原语（`SimdHelper`）**：`SwapRgbBgr24`（RGB24⇄BGR24 就地/异处交换）、
  `ConvertRgb24ToBgra32`、`ConvertBgr24ToBgra32`、`ConvertBgra32ToRgb24`、`ConvertBgra32ToBgr24`，
  x86 走 SSSE3 `pshufb`、arm64 走 NEON `tbl`，并带标量回退。
- **ImageFrame 转换 API**：`ToRgb24() / ToBgr24() / ToBgra32()`（非破坏式，非 RGB24 帧保存时自动经 SIMD 规范化为 RGB24），
  以及零额外内存的就地交换 `SwapRgbBgrInPlace()`（仅 24 位格式），便于与 OpenCV / 相机 / GPU 等 BGR/BGRA 数据源互操作。
- 新增单元测试：5 个 SIMD 转换原语的「标量参考对比 + 全长度覆盖」校验，以及 7 个 ImageFrame BGR/BGRA 互操作测试。

### 工具
- 新增 `.github/workflows/release.yml`：推送 `v*` tag 即自动构建 win-x64 / linux-x64 / osx-arm64
  三套 CLI 资产（**每套都做实机冒烟**：`PNG→WebP→PNG` + `PNG→JPEG`，用于证明原生 libwebp 能加载）、
  打包 nupkg/snupkg、生成 `SHA256SUMS.txt` 并创建/更新 GitHub Release。
  手动 `workflow_dispatch` 只接受一个已存在的 tag 且只写**草稿** Release（试跑/补发不会公开发布）。
  runner 选择：`windows-latest` / `ubuntu-latest` / `macos-15`（arm64）——实测 `macos-latest`(=macos-26 arm64)
  会排队超时被取消，`macos-15` 秒级拿到且能实机跑通。
  （`.github/` 在 `.gitignore` 中，新增 workflow 需 `git add -f`。）
- 新增 `tools/build-cli.sh`：构建 CLI 的发布资产（含原生 WebP 库，使用者无需安装 .NET），
  支持 win-x64 / linux-x64 / osx-arm64，产物落在 `.artifacts/release/` 并生成 `SHA256SUMS.txt`。
  脚本放 `tools/` 根目录而非 `tools/release/`：`.gitignore` 的 `[Rr]elease/` 会匹配任意层级的 release 目录。
  已做跨平台适配：GNU tar 才加 `--mode` 强制可执行位（BSD tar/macOS 不需要），校验和优先 `sha256sum`、
  缺省回退 `shasum -a 256`；macOS 分支里的变量紧邻中文一律写成 `${VAR}`（macOS 的 bash 3.2 在非 UTF-8
  locale 下会把中文首字节并入变量名，报 `RID?: unbound variable`）。

### 文档
- 新增 `docs/Release.md`：固化发版口径（版本号 → tag → GitHub Release → 资产清单 → NuGet 自动发布触发），
  含逐步执行清单、回报格式与本机验证手段（win 直接跑、linux 走 WSL、osx-arm64 由 CI 的 macos runner 验证）；
  `docs/README.md` 索引同步更新，`README.md` 的 CLI 章节补充预编译版本的下载入口。

### 修复
- **macOS 上 WebP 原生库加载不了（既有缺陷，影响 NuGet 的 macOS 消费者）**：仓库里的 `osx-arm64/*.dylib`
  带着构建机的 `@rpath`（`/Users/lastonesky/Project/libwebp/build`）与版本化依赖名
  （`@rpath/libsharpyuv.0.dylib`、`@rpath/libwebp.7.dylib`），在别的机器上依赖解析失败。
  实测：无论单文件还是目录式打包，在真实 arm64 macOS 上都报「未能加载 WebP 原生库」。
  - 发布产物侧已修：macOS 改为目录式发布，构建后用 `install_name_tool -add_rpath @loader_path`
    给每个 dylib 补 rpath、按依赖名提供版本化副本（`libwebp.7.dylib` 等）、并对所有 dylib 与主程序
    做 ad-hoc 签名（arm64 上未签名的可执行代码会被直接拒绝加载）；CI 在 `macos-15` 上实机冒烟通过。
  - **尚未修**：仓库里这两个平台上随包发布的原生库本身（`src/runtimes/osx-arm64/native/*.dylib`）
    未改动，因此 NuGet 包在 macOS 上仍会命中同一问题；linux 的 `.so` 也带着失效 RUNPATH
    （`/home/ted/src/libwebp-main/build`，目前实测能加载，属侥幸）。
- 原生库选择由「宿主 OS」改为「按 RID」（`src/SharpImageConverter.csproj` 的 `_SicNativeRid`）：
  此前在 Windows 上发布 linux-x64 会把 Windows 的 `libwebp.dll` 拷进 Linux 产物（反向同理）。
  未指定 `RuntimeIdentifier` 时行为不变（仍按宿主 OS），因此本机开发/测试/AOT 无影响。
- `Cli/SharpImageConverter.Cli.csproj` 不再重复拷贝 `../src/runtimes/**`：原先会把其他平台的原生库
  （linux `.so` / osx `.dylib`）一并带进发布目录，并被单文件 exe 的原生库清单引用（产物已实测可跑）。

## 1.0.0（相对 v0.2.8）
### 规则
- 性能优化准入阈值：**提升不足 7% 的改动算作无效提升，不保留、不提交。**
  该量级的收益与测量噪声无法可靠区分，却会持续增加代码复杂度与维护成本。
  判定取重复测量的**中位数**（`--gif-bench N` 的 median），不取单次、不取 min/max；
  单阶段提速但端到端未达标的同样按无效处理。
  **2026-10-03 修订：不再要求端到端 ≥7%，改为「被修改的部分」自身提速 ≥7% 即可保留；**
  上界分析仍要做，但只用于判断改不改得动该段本身。
- **LZW 产物验收口径（2026-10-03 修订）**：新 LZW 算法不要求与旧算法产物逐字节一致，
  改为要求**往返一致**——`解压(压缩(x))` 必须等于编码前的数据（LZW 前索引的 md5 相同）。
  `LzwRoundTripTests`（8 例）即该口径的落地，断言为逐字节全等。
  **算法改动的验收指标 = LZW 编码耗时**（非压缩率）；据此 lazy matching 与自适应提前 clear
  经封死性上界判负（多付的查表/清表开销 > 收回的出码收益），只分析未实现。

### 改进
- **arm64 全面补齐 SIMD：新增跨架构原语层 `SimdCompat`，JPEG/PNG/GIF/BMP/缩放全链路提速（47% 的项超过 7% 阈值，JPEG 解码最高 5.9x）。**
  起因：库中所有 SIMD 路径都由 `Sse2/Ssse3/Sse41/Avx2/AvxVnni.IsSupported` 守卫，
  而 arm64 上这几个属性**全部为 false** ⇒ 整条向量路径在 ARM 上静默退化为标量死代码
  （14 个文件、约 490 处调用点）。
  - **做法**：新建 `src/Core/SimdCompat.cs` 作为跨架构原语层，每个方法按 `IsSupported` 分派。
    **x86 分支保持逐字不变**（对 x86 零影响），arm64 分支走 NEON。
    映射要点：`UnpackLow/High`→`ZipLow/ZipHigh`；`PackUnsignedSaturate`→`ExtractNarrowingSaturateUnsigned{Lower,Upper}`；
    `pmaddwd`→`MultiplyWideningLower/Upper`+`AddPairwise`；`psadbw`→`uabd`+3×`AddPairwiseWidening`；
    `phaddd`→`AddPairwise`；`pmovzxbd`→两级 `ZeroExtendWidening`；整寄存器移位→`ExtractVector128`。
  - **纠正两处认知**（均有单测固定）：
    1. `tbl` 与 `pshufb` **不完全等价**——`pshufb` 在掩码 bit7=1 时出 0、否则取 `mask & 0x0F`（16..127 会**折叠**），
       而 `tbl` 索引 ≥16 一律出 0。`ShuffleBytes` 统一按 `mask & 0x8F` 归一化后才等价。
    2. NEON `EXT` 方向：`ExtractVector128(v, zero, n)` ≡ `psrldq(v,n)`、`ExtractVector128(zero, v, 16-n)` ≡ `pslldq(v,n)`；
       且 **n=0 时 16-0=16 越界会抛异常**，而 x86 `pslldq x,0` 合法，已单独短路。
  - **新增 `SimdCompatTests`（24 例）**：每个原语按 Intel SDM / ARM ARM **独立推导**标量参考实现后逐 lane 对拍
    （不引用被测代码）。这套测试立即抓出真实 bug——`ShiftLeftBytes(v, 0)` 在 ARM 上抛
    `ArgumentOutOfRangeException` 而 x86 合法。全量单测 189 → **213 通过**。
  - **实测 A/B**（`tools/perf/run-ab.sh`，两侧各 2 轮、跨轮中位数、产物哈希比对）：
    | 链路 | 加速 |
    |---|---|
    | JPEG 解码 large / medium / 渐进式 huge | **5.91x / 4.68x / 3.73x** |
    | RGB→灰度 | 2.04x ~ 3.03x |
    | WebP 解码 | 1.21x ~ 2.50x |
    | 双线性缩放（200% 与 50%） | 1.75x ~ 2.06x |
    | 双三次缩放 200% | 1.61x ~ 1.81x |
    | BMP 编 / 解码 | 1.38x ~ 2.14x |
    | PNG **编码**（Adler32） | +8.4% ~ +9.6% |
    | PNG **解码** | +2%（**无收益**：走 BCL `ZLibStream`，不经本库 Adler32） |
    | GIF 编解码 | +3.4% ~ +14.2%（标量回退本就是查表实现，天花板低） |
    123 项中 58 项（47.2%）超 7% 阈值；118 项产物逐字节一致，
    5 项 JPEG 解码差异为**继承自 x86 的既有差异**（SIMD 用色度复制、标量用双线性上采样）。
    逐项取证与三个存疑项的分析见 `docs/ArmPerfBaseline.md` §7。
  - **Adler32 移植**：`s1 += b[i]; s2 += s1;` 的跨迭代依赖链把标量吞吐锁在 1.62 GB/s；
    改用 `psadbw` 求 s1、`pmaddwd` 求加权 s2、`HorizontalSumInt32`（NEON 单条 `addv`）归约后达 **7.81 GB/s（4.8x）**，
    12 组数据（含 5552 边界与 `Update` 串联）逐位一致。
  - **修掉基准自身三个会骗人的缺陷**（否则会把上面这些收益误判成回归）：
    1. 固定热身 40 次不足以跨过 tier-0→tier-1 阈值（实测 **80~150 次**），而向量路径的 tier-0
       代码体量远大于标量路径 ⇒ tiny/small 全项被系统性判慢，测出 `upscale-200%@tiny 0.271x`
       这种"输出 0.9 MB 比输出 5.6 MB 还慢 2.4 倍"的物理不可能数字。
       **改为在 `SicBench.csproj` 关掉分层编译**（`TieredCompilation=false`）：首次 JIT 即完全优化，
       热身固定 8 次即可（实测 0.58/0.77/0.62 ms，与热身 1000 次的 0.55 ms 一致），
       整轮 A/B 从 ~15 min 降到 **3 min 58 s**。
    2. `Measure` 在**每个计时样本前**强制 compacting GC，GC 尾部开销反而落进计时窗口：
       改成只在循环开始前整理一次（同算子三轮 2.028/0.888/0.847 ms 乱跳 → 1.000/1.070/1.104 ms 稳定）。
    3. `best()` 用 `current*` glob 会命中上一轮的 `*-final-*.csv`，把**不同构建**的行混进一张表，
       导致产物哈希 DIFF 被"更快的那一行"掩盖 ⇒ 改为按 stamp 精确匹配 + **跨轮中位数**选择器。
- **JPEG 编码器新增「按图优化的 Huffman 表」（`JpegEncoderOptions.OptimizeHuffman`）。**
  起因：`progressive.jpg`（143MP，约 2/3 是近纯白背景，TinyPNG 能压 28.5%、我们却判「无收益」）。
  取证：同一量化表下（q72 的表 ≈ 原图表，luma 64 值和 2097 vs 2136）我们出 4.38 MB、原图 3.09 MB（+42%）；
  纯色图探针显示标准 Annex K 表的空块开销高达 **5.38 bits/块**（DC cat0 2b + AC EOB 4b），
  平坦内容占比高时比特流几乎全被空块开销吃掉。
  修复：两遍编码——第一遍复用「生产 → DCT 量化」管线只统计 DC/AC 符号分布（不产出字节），
  第二遍用生成的最优表编码。表生成用显式树 + BFS 求码长（先前的链式合并把 codesize[c2] 整个漏掉，
  8 符号只产出 2 个码长；已由单测拦截），并先把总频率缩到 1000 以内（Fib 上界保证码长 ≤15），
  彻底避开「限长调整只改计数不改码长」的坏表陷阱。生成表 Kraft 和恰为 65536（满树）。
  - 实测（143MP progressive.jpg）：q50 4.04 MB → **2.90 MB**；优化器端到端 2.95 MB → **2.60 MB（省 12.0%）**，
    从「无收益」变为有效压缩；普通照片 photo100.jpg 同画质 76.1 KB → **68.3 KB（-10%）**。
    代价是编码耗时约 ×2（143MP：246 → 465 ms/次），默认只在智能压缩里开启，
    `Image.Save` 的 JPEG 默认路径不变。
  - **修复：优化表不得含「全 1 末码」。** 首版生成的满树在最深组恰好贴满时末码必为全 1
    （如等频 3 符号 → bits=[1,2] → 末码 11），违反 T.81 K.2「no code is allowed to be all
    ones」，被 GDI+/libjpeg 系看图软件以 "Bogus Huffman table definition" 拒绝（本例 4 张表
    全部违规；自家解码器与 Chromium 未做该检查所以能解，差点漏过）。修复与 libjpeg 的
    freq[256] 同思路：加一个频率为 1 的虚拟叶子独占最深组的最后一个码位（全 1 位），
    组装时挖掉——确定性构造，无需迭代，代价是 Kraft 和略小于满树（合法，前缀码无歧义）。
    新增 `IsLegalPrefixCode`（与 jdhuff.c Figure C.2 逐组检查一致）并暴露给测试；
    新增等频 3~17 符号、真实 Annex K 表回归测试；GDI+ 实测打开通过。
    教训：**跨解码器验证不能只信自家往返一致**——自家人可能「一致地错」。
  - 新增 `JpegHuffmanOptimizeTests`（12 例）。全量 189 单测通过。
- **智能有损压缩（TinyPNG 式），`SharpImageConverter.Compression`。**
  `ImageOptimizer.Optimize(input, output?, options?)` / CLI `--optimize`：保持原格式把
  JPG/PNG/GIF/WebP/BMP 压到「肉眼几乎无差别」的最小体积，压不动就保留原图。
  - **画质判据**：`QualityMetrics` 同时给出「4×4 块平均后的感知 PSNR」与「逐像素 PSNR」。
    前者抹平 dithering 颗粒（照片 256 色抖动：逐像素 36.6 dB vs 感知 48.5 dB），后者兜住
    「高频细节被抹平 / 抖动过头」这类块平均看不见的损伤。默认下限由 `TargetQuality`
    推导：`MinPerceptualPsnr = 33 + 0.14*TargetQuality`（均衡档 88 → 45.3 dB），
    另加 `MinRawPsnr = 30 dB`。阈值为 900×600 合成图实测标定。
  - **PNG**：≤256 色时精确取色（无损）；否则量化到调色板（颜色类型 3，每像素 1 字节）
    + Floyd–Steinberg 抖动，颜色数按画质下限二分搜索（8 档只需 3~4 次评估）。
    体积不随颜色数严格单调，故在达标候选里按体积取最小。不达标或收益 <10% 时回退
    无损真彩色重写——渐变类图像抖动后索引近乎随机，实测调色板会比真彩色大 19 倍。
  - **JPEG / WebP**：二分搜索「刚好达标」的最低质量；JPEG 默认 4:2:0，色度高频丰富导致
    不达标时自动退回 4:4:4 再搜一轮。WebP 搜索下界提到 65（低质量区间省不了多少却明显变糊）。
  - **GIF**：跨帧共享全局调色板并降色，动画同样支持；新增 `GifEncoder.EncodeIndexed`
    直写「调色板 + 索引」，跳过编码器内部二次量化（否则画质不可控）。
    带透明通道的动画 GIF 解码只得到 RGB24 帧序列，重编码会丢透明度，故保留原图并说明原因。
  - **PaletteQuantizer**：精确取色优先；5bit/通道 + alpha 5bit 直方图（大图抽样、只清零用过的桶）；
    中位切分按「像素数 × 最大通道跨度」选盒、按累计像素中位数切开；
    最近色查找用「5bit RGB 粗查表定种子 + 每色 16 近邻短表精修」+ 20bit 键结果缓存，
    避免逐像素扫 256 色。抖动只扩散 RGB（alpha 抖动在半透明区会产生可见噪点）。
  - **配套**：`PngWriter` 新增调色板写入（PLTE/tRNS）与 `PngFilterMode`（Up/Adaptive/None）
    及 Deflate 级别参数；新增 `PngAdaptiveFilter`（逐行 5 选 1，SIMD）。
  - 实测（900×600 合成图）：照片 PNG 845 KB→451 KB（-46.7%）、q100 照片 JPEG 352 KB→76 KB（-78.4%）、
    照片 GIF 380 KB→300 KB（-21.1%）、全色域渐变 PNG 13.6 KB→8.3 KB（-38.8%，无损）、
    6 色图形 PNG 3.3 KB→1.4 KB（-57.4%，无损）。3.84 MP PNG 全流程 1.1 s（含 4 次候选评估）。
  - 新增 `CompressionTests`（16 例），含自适应滤波/调色板 PNG 往返一致性、量化器预算与透明槽、
    度量一致性、三种格式端到端优化。全量 177 单测通过。
- **GIF LZW 输出端改为 64bit bit pack writer。**
  位先累积进 64 位累加器、超过 52 位按组吐出 6~8 个整字节，子块还剩 ≥8 字节时一条
  无对齐 8 字节 store 落包，替代原「每码字逐字节 shift + 写包 + 块界检查」的固定开销。
  归因对照（探针跳过全部位打包、只留码宽状态机）：位打包+写出占 LZW 阶段约 12.7%
  （143 MP：422 → 368 ms）；宽写口把这段自身提速约 21%。
  `examples/progressive.jpg` `--gif-bench 5`，10 轮交错 × 4 臂池化中位：
  | 指标 | 基线 | 64bit writer | 提升 |
  |---|---|---|---|
  | LZW 阶段 | 422.1 ms | **410.6 ms** | −2.7%（9/10 轮胜） |
  | 端到端 total | 467.8 ms | **454.2 ms** | −2.9% |
  | 位打包+写出（归因） | 53.8 ms | **42.3 ms** | **−21%**（过「修改部分 ≥7%」门槛） |
  progressive（dithering 开/关）、5_star_base、car 四组产物 md5 逐字节一致，161 单测通过。
- **实测否决：LZW 输入经 64KB 暂存缓冲分块喂入。**
  上界分析：输入是顺序读、已被硬件预取覆盖，暂存只多付一次全量 memcpy；
  实测 LZW 422 → 440 ms（+4.2%，10/10 轮更慢）。结论已写入 `LzwEncoder` 类注释。
  过程教训：消费位必须右移出累加器（基线 `>>=` 语义），用「保留低 r 位」的掩码会把
  尾巴留在 [n*8, n*8+r) 却留下已消费的低位——产物坏在第一次 spill 的末尾字节，
  md5 对比立即拦截（首差出现在 LZW 数据第 6 字节）。
- **实测否决：64KB 批写缓冲（子块 Write 合批）。**
  归因探针（跳过子块 `stream.Write`，`SIC_GIF_NOPKTWRITE`）：子块写出上界仅
  4.75 ms ≈ LZW 阶段的 1.2%（10 轮交错 × `--gif-bench 5` 配对中位，8/10 轮），
  该上界是「调用开销 + memcpy + 扩容」总和，批写只能回收第一项。
  独立微基准（同 23.4 MB 字节量，91725×256B vs 357×64KB，10 轮交错配对）
  实测可回收 **−0.05 ms（胜负 5/10 = 零）**——.NET 小 Write 本就是 memcpy 进内部缓冲，
  每包调用开销已被摊薄；合批反而多付一次全量拼接 memcpy。未实现即否决，
  探针已移除，结论写入 `LzwEncoder` 类注释。
- **JPEG 解码重建（IDCT + YCbCr→RGB）按 MCU 行并行。**
  `TryDecodeInterleavedYCbCrSimd` 的 `my` 行循环改为 `Parallel.For`：每个 MCU 行只写
  `output` 的 `[my*blockH, (my+1)*blockH)` 行区间、互不相交，系数缓冲在熵解码结束后只读，
  量化表共享只读，SIMD 重建内部全是 `static readonly` 常量 ⇒ 线程安全无需同步。
  门控 `60_000 × ProcessorCount + 300_000` 像素（与量化映射同式），小图保持单线程。
  `examples/progressive.jpg`(143 MP) `--jpeg-bench 7` 中位数：
  | 阶段 | 优化前 | 优化后 | 提升 |
  |---|---|---|---|
  | decode total | 440.5 ms | **223~229 ms** | **−49%** |
  | reconstruct | 255.1 ms | **37.5~45.7 ms** | **−82~85%** |
  | entropy（同进程对照组，未改动） | 176.8 ms | 176.8 ms | 0% |
  8 个基线产物（含 143MP 解码/回编码、CMYK/RGB、灰度、q85+444 参数组）**逐字节一致**，152 单测通过。
- **JPEG 编码流水线批大小 `McuBatchSize` 16 → 64。**
  队列容量按「批」计（`clamp(ProcessorCount,2,16)` 批），批=16 时在途 MCU 仅 256 个，
  下游频繁饿等，35K 次入队的信号量唤醒延迟累计可观；批 ≥64 后流水线加深，
  produce-wait 61.8 → ~10ms、huffman-wait 90 → ~21ms。
  同二进制 `SIC_JPEG_BATCH` env 交错 A/B（3 轮，encode total 中位数）：
  批 16 → 292/283/301 ms，批 64 → 204/207 ms，批 128 → 198/203 ms，批 256 → 199 ms
  ——**64 起进入平台（−30%）**，256 因在途工作集变大回落，64 与 128 无显著差异，
  按在途内存（两队列满载 ≈19MB vs ≈38MB）取 64。批大小不影响产物
  （Huffman 按每 MCU 全局 `Sequence` 排序，与分批无关），8 基线产物逐字节一致、152 单测通过。
  小图不受影响：`5_star_base.jpg` 编码 11.5 → 11.4 ms（噪声内）。
- **实测否决：produce（RGB→YCbCr 取样）按行带多生产者并行。**
  `Parallel.ForEach` 多点直接入队：编码 total 303 → 921 ms（dop=∞）/572 ms（dop=4），
  并行度越高越慢、交叉点在单线程。原因：① 多生产者争抢 16 批容量的 sampleQueue 形成波状停顿；
  ② 乱序入队使 Huffman 从 `== expected` 直写快路径退到 pending ring 冷读
  （huffman-busy 218 → 296/397 ms，143MB 系数冷读）。结论已写入 `ProduceRgbSamples` 上方注释；
  若将来重做，正确方向是「并行填充 + 单点按序派发」。
- **`--jpeg-debug` 诊断输出接通控制台。** `JpegEncoder` 的诊断一直写 `Trace.WriteLine`，
  但 CLI 从未注册监听器，输出全部丢失；现在 `--jpeg-debug` 时注册 `ConsoleTraceListener`，
  可看到 `[jpeg-timing] total=… FillMcu420=…`（色彩转换占比）。新增 `--jpeg-bench N`
  分阶段基准与 `SIC_JPEG_STAGE_TIMING=1` 分阶段探针（produce/dct/huffman 及其 wait 槽位、
  解码 entropy/reconstruct/idct-color），测量方法与 `--gif-bench` 对齐。
- **PNG 解码反滤波重构：按滤波器类型分派专用函数，Paeth 用代数化简，并复用已有 SIMD 色彩转换。**
  旧实现把整行先 `CopyTo` 进目标缓冲、再原地改，且逐字节在循环里做 `switch`——
  真实 PNG（libpng/PIL 自适应滤波）里 Paeth 通常占 90% 以上行，逐字节分支同时破坏分支预测与指令缓存。
  新实现：① 一次分派到 `UnfilterSub/Up/Average/Paeth`，从 `src` 直读、写 `dst`，省掉一遍整行拷贝
  （左邻 a 取自 dst、上邻 b 与左上邻 c 取自 prev，三者都不在 src，故语义安全）；
  ② 首 `bpp` 字节单独处理，热循环内不再有 `i >= bpp` 判断；
  ③ Paeth 用代数化简——展开后 `p-a = b-c`、`p-b = a-c`、`p-c = (b-c)+(a-c)`，`p` 本身无需计算，
  且 `pa`/`pb` 可并行求值，依赖链缩短一层。另注：首 bpp 字节的 a、c 均为 0，
  此时 Paeth **退化为 Up**，直接按 Up 处理。
  ④ `UnfilterRgba8ToRgbDirect` / `UnfilterRgb8ToRgbaDirect` 里手写的逐像素标量搬运
  改为复用 `SimdHelper.PackRgbaToRgb` / `ExpandRgbToRgba`（SSSE3 `pshufb`）。
  同一份二进制内用 `SIC_PNG_LEGACY_UNFILTER=1` env 探针交错 A/B，7 轮取中位数：
  | 输入 | 优化前 | 优化后 | 提升 |
  |---|---|---|---|
  | 4000×3000 RGBA（Paeth 占 97% 行） | 194.65 ms | 161.14 ms | **−17.2%** |
  | 2000×1500 RGB | 70.52 ms | 45.40 ms | **−35.6%** |
  | 640×480 RGB | 9.31 ms | 6.31 ms | **−32.2%** |
  | `Amish-Noka-Dresser.png`（几乎无滤波行） | 3.94 ms | 3.88 ms | −1.5% |
  最后一行是预期内的零收益：该图滤波器几乎全为 None，反滤波本就不做事。
  正确性：19 张语料（含 Adam7 隔行、调色板、灰度+Alpha、16 位灰度、1×1/5×3/37×11 奇宽）
  RGB 与 RGBA 解码产物**逐字节一致**，并与 Pillow 逐一对齐；152 个单元测试通过。
- **LZW 编码器字典查找改为两级（过滤缓存 + 散列表），并把槽位从 64 位压到 32 位。**
  编码循环是「算散列 → 载入槽位 → 比较 → 更新 `ent`」的串行依赖链，每像素一次，既不能向量化
  也不能软件流水；二级表 256 KB 必然落在 L2 上，那次加载的十余周期延迟就是整条链的主体。
  新增的一级缓存只有 128 KB、按下标 `fcode & (CSIZE-1)` 直接映射（低 12 位即 `ent`，
  等价于每个前缀 8 个槽），局部性远好于散列表，实测 **87.5%** 的查找在此命中。
  三个已实测的关键点（详见 `LzwEncoder` 类注释）：① 两级加载必须**并行发射**——
  若写成「先判缓存、未命中再查大表」，未命中要串行付两次加载延迟，实测比不用缓存还慢 4 ms；
  ② 缓存下标**不能依赖散列值**——用主表 `h` 当下标时噪声图命中率 53%→61%，但下标要等 imul
  算完才能发出，progressive.jpg 425 → 686 ms；③ 缓存**必须参与插入**，只在命中时回填会更慢。
  命中判定改为取差值 `d = slot - (fcode << 12)`：命中时 `d` 恰为字典编号，一次减法同时完成
  「比较键」与「取编号」，并用 `d - 1u < 4095u` 把空槽（`d == 0`）与键不同的情形一起排除，
  从而不必额外判断空槽就能区分「空槽」与「键为 0 的合法项」。
  （早期版本直接比较 `(slot >> 12) == fcode`，空槽 0 会被误判成 `(c=0, ent=0)` 的命中，
  少输出一个码字且少插一条——表面快 5%，实际产物 md5 已不一致，属**假阳性收益**。）
  `examples/progressive.jpg`(10650×13426, 143 MP) `--gif-bench 5` 中位数：
  | 阶段 | 优化前 | 32 位槽位 | +一级缓存 |
  |---|---|---|---|
  | LZW | 564 ms | 537 ms（−5%） | **420 ms（−25%）** |
  | 编码 total | 622 ms | — | **483 ms（−22%）** |
  缓存槽位 2^12/2^13/2^14/**2^15**/2^16/2^17 → 544/501/458/**430**/429/443 ms（峰值 128 KB）；
  二级表在有缓存后仍取 2^16（2^14/2^15/2^16 → 436/418/414 ms）。
  5 张样例图 × 多组参数（默认/wu 量化器/关抖动/灰度/缩放/2×2 极小图）产物**逐字节一致**。
  **已知代价**：收益取决于缓存命中率，低于约 60% 的图会略微变慢——
  `examples/5_star_base.jpg`（1.2 MP，噪声多，重置密度是 progressive 的 3.3 倍，命中率仅 53%）
  LZW 6.4 → 7.2 ms（+12%）；同目录 `car.png`（81.6%）为 −18%。消融实验确认与清零缓存无关（占 1.9%）。
- 新增八叉树量化 + Bayer 有序抖动（`OctreeQuantizer`），作为 GIF 编码默认量化器；
  原 Wu 量化 + Floyd–Steinberg 误差扩散（`Quantizer`）保留，可通过 `GifEncoder.QuantizerKind`
  / 适配器 `QuantizerKind` / CLI `--gif-quantizer wu` 切回。新方案用 5-bit 直方图喂入深度 5
  八叉树、归约到 ≤256 叶、映射走 5-bit LUT，抖动改用无依赖链的 Bayer 4×4（替代误差扩散串行链），
  量化阶段显著更快；输出**不**与原实现保持 MD5 一致（设计使然，见项目约定）。`DitherStrength` 可调。
- **修正 Bayer 抖动默认值：48 → 8（一个量化步长）。** 原默认值把抖动幅度设成了量化步长的约 5.6 倍
  （偏移 `[-23, +22]`，而 5-bit LUT 的步长为 8），属严重过抖动。实测（平滑渐变 512×512 +
  `examples/Amish-Noka-Dresser.jpg`）：
  | 指标 | S=0(不抖) | **S=8(新默认)** | S=48(旧) | Wu+FS |
  |---|---|---|---|---|
  | 照片总误差 RMS | 1.30 | **2.03** | 12.85 | 1.96 |
  | 照片颗粒 RMS | 0.91 | **1.73** | 11.56 | 1.79 |
  | 照片 1/4 缩放走样 RMS | 10.99 | **11.31** | 22.99 | 11.01 |
  | 渐变最长同值游程(px) | 64 | **3** | 1 | 15 |
  即 S=48 相对 S=8 **颗粒大 6.7 倍、缩小时最近邻走样大 2 倍，而色带抑制没有任何额外收益**
  （块间跳变同为 4.12、最长同值游程已降到 3）。过强的抖动还把 4×4 网格变成强周期图案，
  在看图软件用最近邻采样缩放时被走样成摩尔纹（Wu+Floyd–Steinberg 无周期结构故不受影响）。
  附带收益：索引更规整，143 MP 图编码 total 中位 746.0 → 684.3 ms（**−8.3%**）。
  新增 CLI `--gif-dither N` 与 `GifEncoder.DitherStrength` / 适配器 `DitherStrength` 可调。
- `OctreeQuantizer` 的 Bayer 映射阶段整数化 + SSSE3 向量化。原实现逐像素做 3 次
  「byte→float 转换 + 浮点加 + `Math.Clamp`(两次比较分支) + float→int 截断」，实测占整个量化 78%。
  改写分两步：① 因 v 为整数时 `floor(v+x) = v + floor(x)`，可把 `t*strength` 精确化为整数偏移
  `floor(strength*(2k-15)/32)`（strength=48 时即 `3k-23`），再把「加偏移 + clamp + >>3」
  预计算成 16 相位 × 256 项的 5-bit 表，逐像素只剩 3 次查表；② 进一步用 SSSE3 一次处理 16 像素：
  `pshufb` 三路反交错出 R/G/B 平面，16 位通道内 `paddw + pminsw/pmaxsw` 饱和 + `psrlw 3`，
  合成 16 个 cube 下标。cube→调色板索引的 32768 字节 gather 无法向量化（AVX2 无按字节、
  15 位下标的 gather，dword gather 反而更慢），故保留标量。
  `examples/progressive.jpg`(10650×13426) `--gif-bench 11` 中位数：quantize 553.1 → 215.0（整数化）
  → 115.9 ms（SIMD），编码 total 1164.1 → 719.1 ms；相对原 Wu 量化器 quantize 快 11.4x。
  5 张样例图产物与优化前**逐字节一致**（md5 相同）。
  注：与 `docs/PerfReport.md` 中「量化不可 SIMD」的结论不同——该结论针对 Floyd–Steinberg 的
  **串行误差扩散链**（依赖链受限）；Bayer 逐像素独立、无跨像素依赖，属吞吐受限，故 SIMD 有效。
- `OctreeQuantizer` 映射阶段（cube → 调色板索引）并行化 + 不抖动路径补 SSSE3。
  映射逐像素独立、无跨像素依赖链，此前却一直是串行单线程：143 MP 图上它独占 quantize 的
  ~85 ms，占编码 total 的近 9%。现按 `60_000 × ProcessorCount + 300_000` 像素门控（与直方图同尺度）
  用 `Parallel.For` 按行切片，小图仍走单线程。同时把原先只有抖动路径才有的 SSSE3 版
  移植给不抖动路径（`MapDirectSimd`，去掉偏移表与 pminsw/pmaxsw 即可），
  修掉了「开抖动反而比不开更快」的怪现象（24 核 145.7 vs 120.5 ms）。
  `examples/progressive.jpg` `--gif-bench 11` 中位数（24 核，同二进制 env A/B）：
  抖动开 quantize 127.0 → **42.7 ms**（−66.4%）、total 997.8 → **903.7 ms**（**−9.4%**）；
  抖动关 quantize 158.2 → **42.5 ms**（−73.1%）、total 930.6 → **822.3 ms**（**−11.6%**）。
  LZW 耗时不变（871.7 → 867.9 ms，噪声内），端到端增益全部来自量化阶段。
  5 张样例图 × 抖动开/关共 10 组产物与改动前**逐字节一致**；2 MP 图并行/串行持平，无退化。
- GIF 量化器直方图按图片尺寸选择单/多线程：原实现无条件 `Parallel.For`，每个 worker 都要从 `ArrayPool`
  租借并清零 5 个 35,937 元直方图（约 1.44 MB/线程），最后在 `lock` 内把全部 35,937 个 bin 串行合并回主直方图。
  这个固定成本与图片大小无关，小图上远大于并行省下的扫描时间。现按
  `60_000 × ProcessorCount + 300_000` 像素为界：低于阈值单线程直接累加进主直方图，省掉租借/清零与合并。
  阈值随核数缩放（开销随线程数增长）：实测交叉点 4 核约 0.5 MP、8 核约 1.05 MP、24 核约 1.6 MP。
  24 核 2400×1800→各尺寸 `--gif-bench` 中位数（抖动开）：512×512 10.010→5.991 ms（**−40.1%**）、
  800×600 12.659→9.274（**−26.7%**）、1024×1024 20.208→18.060（**−10.6%**）、1280×720 −8.9%、
  1440×900 −3.3%，≥1.9 MP 回落到并行路径（−1% 以内，属噪声）。抖动关闭时收益更大（小图 −44%~−50%）。
  8 个尺寸 × 抖动开/关共 16 组产物与优化前**逐字节一致**。
- LZW 编码器字典查找重构：原哈希 `h = (c << 4) ^ ent` 因 `c <= 255`、`ent < 4096` 恒落在 `[0, 4095]`，
  8191 个槽位实际只用一半，装载因子为 1.0，探测链严重退化。改为 64 位乘法散列（Fibonacci hashing）
  配 32768 槽位（装载因子约 0.12）+ 线性探测；槽位打包为 `((fcode + 1) << 12) | code`，
  键与值落在同一条 cache line，一次探测一次加载。槽位 0 表示空，键存 `fcode + 1` 避免
  `(c=0, ent=0)` 与空标记冲突。表经 `ArrayPool` 租用，避免动画逐帧触发大对象 GC。
  2400×1800 实测编码 LZW 28.998ms → 21.245ms（1.37x），输出字节与优化前完全一致。
- ~~LZW 解码器改为直写输出~~ **已回退**：该改动实测解码 LZW 12.875ms → 12.025ms（仅 1.07x），
  低于 7% 准入阈值，按规则不保留，解码器已还原至优化前实现。
  （注：该项实测 1.07x = +7%，按当前 7% 阈值已达标，恢复前须重新确认产物 md5 逐字节一致。）
  实验结论（含同轮实测否决的三个改动：一次补 4 字节的位缓冲 -12%、
  prefix/suffix 打包为单个 int -5%、二级跳转表一次展开两像素 -5%）
  已沉淀至 `docs/PerfReport.md` 的「GIF LZW 优化实验记录」，避免重复尝试。
- 新增 GIF 编解码耗时统计：`GifTiming` 按阶段记录 quantize / prepare / header / lzw / render / background
  与未归入阶段的其余时间，并给出占比与吞吐（Mpx/s）。`GifEncoder` / `GifDecoder` 及其适配器新增
  `EnableDiagnostics`、`DiagnosticsLog` 与 `LastTiming`：默认关闭，关闭时热路径不调用 Stopwatch（零开销），
  开启后报表经 `DiagnosticsLog` 输出，未设置时回落到 `Trace`。
  CLI 新增 `--gif-debug`（单次转换的分阶段耗时）与 `--gif-bench N`（重复 N 次取最小/中位/平均，只测量不产出文件）。
- `SimdHelper.AddBytesInPlace` 改为真正的 SIMD：SSE2/AdvSimd 下用 128 位整字节加法（`paddb`，天然 mod 256 回绕），移除原先 Widen/Narrow 的迂回实现。
- 新增 `SimdHelper.GrayscaleRgb24InPlace`：SSSE3 `pshufb` 三路反交错 + 16 位定点加权，每批 16 像素，`Processing.Grayscale()` 已接入。
- 新增 `SimdHelper.ExpandGrayToRgb` / `PackRgbaToRgb` / `ExpandRgbToRgba`，替换 `Configuration` 中的逐像素格式互转，并用 `GC.AllocateUninitializedArray` 避免多余清零。
- `Processing.ResizeBicubicOptimized` 移除 `Vector<float>` 伪 SIMD 脚手架（声明了向量缓冲但内层全为标量），重写为干净的标量实现，并复用行基址减少重复乘法。
- Resize 系列的目标缓冲区改为 `GC.AllocateUninitializedArray`，与池化的索引/权重数组配合降低分配开销。
- JPEG 量化表与整数 DCT reciprocal 表按 quality 缓存（质量已归一化到 `[1,100]`），避免每次编码重建。
- 清理 `JpegEncoder` 中 `// float FDCT removed` 残留注释，改为说明为何只用整数 FDCT；移除 `SimdHelper` 中无主代码调用的 `GetVectorPaddedByteLength` / `AllocateAligned<T>` 泛型重载。
- `Processing.ResizeBilinear` 新增 SSSE3+SSE4.1 路径：一次 8 字节载入同时取到 x0 与 x1 两个像素，
  `pshufb` 拼出 `[a0,b0,a1,b1,...]` 交错 short，`pmaddwd` 用 q/r 拆分精确还原 11 位权重，
  `pmulld` 做垂直插值，每批 4 个像素。数值与标量路径逐位一致。
- `Processing.ResizeArea` 把只依赖 dx / dy 的区间与重叠权重提到循环外预计算，内层只剩乘加累加。
- `ImageFrame.ApplyExifOrientation` 把方向分支提到双层循环外；case 4 改为整行块拷贝；
  case 5-8（转置类）改为 32×32 分块转置，避免目标端按整行跨度写入导致的缓存抖动。
- `ImageFrame.ApplyExifOrientation` 与 `Processing.Clone` 的目标缓冲改用 `GC.AllocateUninitializedArray`。
- `JpegEncoder` 中 `new Vector<int>(span)` 改为 `Vector.LoadUnsafe`，去掉中间拷贝。
- `PngWriter.ApplyUpFilterSimd` 同样改为 `Vector.LoadUnsafe` / `StoreUnsafe`，去掉每步 `Slice(i)` 的
  重复边界检查。滤波函数本身提速 1.36-1.85x，但对 PNG 编码端到端只有 ~0.1%（滤波仅占编码耗时 0.3%）。
- 修正 `Processing.ResizeBilinear` 的 XML 文档注释：此前它被挤到了 SIMD 掩码字段上，导致 warning CS1572。
- `Processing.ResizeBicubicOptimized` 新增 SSSE3+SSE4.1 路径：把 4 像素×4 抽头的 16 次取样压成一次 4 字节载入
  拿到 R/G/B 三条 lane，水平与垂直各做 4 次 `mulps`+`addps`。结合顺序、
  夹取与取整方式都对齐标量实现，输出逐位一致。`examples/progressive.jpg`（10650×13426）
  上实测 1.5x 放大 176.5ms → 49.0ms（3.6x）、2x 放大 326.7ms → 91.1ms（3.6x）。
  SIMD 覆盖不到两个边界情况，均回退标量：抽头夹到最后一个源像素（4 字节载入越界）、
  每行最后一个输出像素（只能按 3 字节写）。
- `Processing.ResizeBilinear` 的 SIMD 循环从每批 4 像素扩到 8 像素：两批之间没有数据依赖，
  两条依赖链可以并行发射。同样条件下实测 2x 放大 133.6ms → 125.4ms（1.07x）、
  4x 放大 537.5ms → 496.9ms（1.08x）；缩小方向受限于写带宽，基本持平（±2%）。
- 把 `ResizeBilinear` 的定点常数（Shift / Scale / RoundingOffset）提到类级别，
  以便抽出的 `BilinearCore4` 辅助方法复用。

### 工具
- 新增 `tools/gif-compare.sh` 与 `tools/gif-timing-master.patch`：把 GIF 计时代码移植到
  基准分支（默认 master）的临时 git 工作树上，让两个分支跑同一套 `--gif-bench` 口径，
  用于量化优化前后的差异。补丁针对 master @ `0bbf1f9`，若该分支的 `GifDecoder` 再有改动需重新移植。

### 文档
- 工程类文档统一收拢到 `docs/`：`PerfReport.md`、`AuditVerification.md`、`goal.md` 移入，
  JPEG 规范原文移入 `docs/reference/`，并新增 `docs/README.md` 作为索引。
  `README.md` / `README.en.md` / `CHANGELOG.md` / `THIRD-PARTY-NOTICES.md` 按生态约定保留在根目录，
  `.trae/rules/project.md` 因 IDE 按固定路径读取而保持原位。
- `goal.md` 中的引用由失效的绝对路径（`file:///d:/...`，盘符本身已错）改为仓库内相对路径。
- 删除根目录误留的 `gcm-diagnose.log`（Git Credential Manager 诊断输出，未被 git 跟踪，
  内容仅含环境变量名与路径，无凭据值）。

### 测试
- 新增 `SharpImageConverter.Tests/SimdPixelOpsTests.cs`，覆盖新增 SIMD 路径与标量实现的逐字节一致性、0..70 长度边界、mod 256 回绕语义，并显式断言本机 SSSE3 可用以免 SIMD 分支漏测。
- 新增 `SharpImageConverter.Tests/ResizeConsistencyTests.cs`，用改动前的原样算法做参考实现，逐位校验
  ResizeBilinear（13 组尺寸 + 4900 组小尺寸穷举）与 ResizeArea。
- 新增 `SharpImageConverter.Tests/ExifOrientationTests.cs`，对 8 个方向在多种尺寸（含跨分块边界）下逐像素校验。
- `ResizeConsistencyTests` 补上此前缺失的双三次覆盖：新增 `RefBicubic` 参考实现（改动前的原样标量算法）、
  13 组尺寸的 `[Theory]` 用例，以及 8×7×8×7 的小尺寸穷举扫描——用于逼出 SIMD 的两个回退边界。
  把 `ResizeBicubicOptimized` 的截断转换改回最近取整即可看到 11 个用例失败，说明确实覆盖了 SIMD 分支。

## 0.2.2（相对 v0.2.1）
### 改进
- JPEG 解码流程重构，统一使用 ImageFrame.LoadJpeg，减少分叉路径与维护成本。
- JPEG IDCT 内核拆分为两阶段，降低运行时计算开销。
- PNG 解码的像素转换使用不安全指针路径优化性能。
- PNG 写入的 Up 过滤器 SIMD 优化，减少内存分配。
- 图像解码与处理整体路径减少内存分配与拷贝。
- JpegImage 移除未使用的缓存字段，清理冗余状态。

### 修复
- JPEG 图像边界处理修复，避免潜在越界访问。
- CLI JPEG 解码修复元数据丢失与方向处理问题。
- BMP 格式检测与读写流程修复并增强健壮性。

## 0.2.1（相对 v0.2.0）
- 版本号：`src/SharpImageConverter.csproj` 从 `0.2.0` 升级为 `0.2.1`。
- 变更范围：共 6 个文件，约 `+397 / -334`（`git diff v0.2.0..HEAD --stat`）。
- 主要方向：JPEG 编解码路径继续收紧内存分配策略（ArrayPool/MemoryPool），并修复测试侧对静态解码 API 的调用方式。

### 代码改动摘要
- `src/Formats/Jpeg/JpegEncoder.cs`
  - 位流写缓冲改为池化租借/归还，减少每次编码时的临时分配。
  - 有序 Huffman 阶段的 pending 容器改为池化扩容与回收，降低 GC 压力。
  - ICC APP2 分片写入改为基于 `MemoryPool<byte>` 的复用缓冲写出。
  - Huffman 表改为静态复用，并修正静态初始化顺序问题。
- `src/Formats/Jpeg/JpegDecoder.cs`
  - `Decode(Stream)` 路径改为池化读取，去掉 `MemoryStream + ToArray` 方式。
  - ICC 收集器改为池化 chunk 管理，并在同步/异步解析完成后统一释放。
- `SharpImageConverter.Tests/*.cs`
  - 测试代码切换到静态 `JpegDecoder` API（`Decode` / `DecodeFromStreamAsync`）。
  - 兼容 `JpegImage` 当前接口，移除不可用成员调用。

## 0.2.0
- 相比 0.1.6，本版本对 JPEG/PNG/GIF/BMP/WebP 全链路进行了较大规模重构与优化，总体聚焦性能、稳定性与流式处理能力。
- JPEG：引入 SIMD 优化的 IDCT 与颜色转换、MCU 批处理与流水线并行编码；新增 APP2 ICC 配置文件支持与可选 CMYK 转换模式；修复多个解码验证问题与资源泄漏问题。
- PNG：解码器采用数组池、定点计算与对齐内存缓冲优化内存与性能，减少不必要颜色转换，并提升网络流读取健壮性。
- GIF：量化器升级为基于直方图的 K-means 实现并改进 LZW 编码，支持抖动开关；补充动画编码能力、并发控制与元数据支持。
- WebP：修复非可查找流（non-seekable stream）解码问题，提升在网络流场景下的兼容性。
- Core/BMP/Processing：新增 SIMD 通用辅助能力，优化 BMP 读写与双线性缩放性能，补充尺寸检查并降低部分路径的内存分配。
- 测试与示例：新增格式检测、元数据、并行处理等测试覆盖，并更新示例脚本与发布流程相关配置。

## 0.1.7
- 修复webp解码错误
## 0.1.6.2-preview
- 优化GIF/PNG的网络流处理能力和图片缩放方法中的内存分配方式
## 0.1.6.1-preview
- 优化缩放处理方法
## 0.1.6
- 重点优化了GIF编码的性能和JPEG编码的性能
## 0.1.5
- JPEG解码添加了CMYK和YCCK的颜色支持 (Added CMYK and YCCK color support in JPEG decoder)
## 0.1.5-preview
- 将各格式的实现，放入对应的子命名空间中 (Moved each format implementation into its own sub-namespace)
- Jpeg解码添加了Stream支持 (Added stream support to JPEG decoder)
## 0.1.4
- 恢复JpegEncoder到重构之前的版本 (Reverted JpegEncoder to the pre-refactor implementation)
- 完善灰度格式之间转换的逻辑 (Improved conversion logic between grayscale formats)

## 0.1.4.1-preview
- 添加对灰度 PNG 和灰度 BMP 格式的支持 (Added support for grayscale PNG and grayscale BMP)

## 0.1.3
- 修复JpegDecoder 解码 /examples/Amish-Noka-Dresser.jpg 错误的问题 (Fixed JpegDecoder decoding error for /examples/Amish-Noka-Dresser.jpg)
- PNG：在多数图片上显著降低压缩后体积（例如原先约 110 MB 的 PNG，现在可缩小到约 30 MB，具体效果取决于图像内容），同时略微提升压缩速度。 (PNG: Significantly reduced output size for many images (e.g., ~110 MB down to ~30 MB, depending on content) with slightly faster compression)
- JPEG：大幅提升解码速度，并在编码路径上带来小幅性能提升。 (JPEG: Greatly improved decode performance and modestly improved encode performance)
- BMP：写出路径经过优化，在同一环境下写出约 400 MB 的 BMP 文件，耗时从约 400 ms 降低到约 270 ms。 (BMP: Optimized write path; writing a ~400 MB BMP now takes ~270 ms instead of ~400 ms on the same machine)
