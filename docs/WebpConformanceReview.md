# WebP 实现与规范对照报告

对照日期：2026-11-03。对照对象与依据：

- `docs/WebPLoseless.md`（Google 官方 VP8L 无损位流规范，2023-03-09 版）→ 对照
  `src/Formats/Webp/` 下 VP8L 编解码实现（`Vp8LEncoder.cs`、`Vp8LDecoder.cs`、
  `Vp8LTransforms.cs`、`Vp8LHuffman*.cs`、`HuffmanTable.cs`、`Vp8LDistance.cs`、
  `Vp8LColorCache.cs`、`LsbBitReader/Writer.cs`、`WebpEncoder.cs` 容器封装）；
- `docs/WebpEncoder.md`（内部有损编码器现状文档）→ 核对文档陈述与
  `Vp8Encoder.cs` 当前代码是否漂移。

审查方式：按规范条款逐条对照源码（条款 → 结论 → file:line 证据），重点关注
此前已知易错点（最右列 TR 替换、颜色变换的 red 更新顺序、ClampAddSubtractHalf
公式、距离映射全表、max_symbol 语义）。

## 1. VP8L 无损规范对照结论

总体结论：**实现与规范高度一致**。逐条对照未发现会导致「合法位流解错」或
「非法位流被编出」的实质性错误；发现 1 个边界尺寸偏差（已修复）、2 个信息性
字面差异、2 个与 libwebp 一致的宽松解码口径。

### 1.1 图像头（规范 §3）

| 条款 | 结论 | 证据 |
|---|---|---|
| 签名 0x2F；编码写出、解码校验 | 符合 | `Vp8LEncoder.cs:354`、`Vp8LDecoder.cs:93` |
| 宽高 `ReadBits(14)+1`，上限 16384 | **偏差（已修复）** | 编码端一直允许 16384（`Vp8LEncoder.cs:97`），解码端 `MaxDimension` 误为 16383（有损 VP8 的上限），会拒绝自己产出的合法文件。已改为 16384 并加 16384×1 / 1×16384 往返回归测试（含 libwebp 交叉验证） |
| `alpha_is_used` 仅提示位，全 255 时写 0 | 符合 | `Vp8LEncoder.cs:450`、`Vp8LDecoder.cs:102` |
| `version_number` 3 位须为 0，非 0 拒绝 | 符合 | `Vp8LEncoder.cs:358`、`Vp8LDecoder.cs:103` |
| RIFF 封装（'RIFF'+size+'WEBP'+'VP8L'+size+payload，奇数补齐） | 符合 | `WebpEncoder.cs:234-260`、`WebpChunkId.cs:11` |

### 1.2 变换框架（规范 §4、§7.2）

| 条款 | 结论 | 证据 |
|---|---|---|
| `while(ReadBits(1))` 循环 + 2 位类型 | 符合 | `Vp8LDecoder.cs:153`、编码侧各写流路径 |
| 每种变换至多一次 | 符合 | `Vp8LDecoder.cs:156`（位掩码查重） |
| 逆变换按读入逆序应用 | 符合 | `Vp8LDecoder.cs:318`；编码正向运算顺序与写流顺序互逆自洽 |
| 子分辨率图无变换位、无 meta prefix | 符合 | `Vp8LDecoder.cs:150,236`（`isLevel0` 门控）；`Vp8LEncoder.cs:437,582` |

### 1.3 预测器变换（规范 §4.1）

14 种模式（0=0xff000000 黑、1=L、2=T、3=TR、4=TL、5–10=Average2 组合、
11=Select、12=ClampAddSubtractFull、13=ClampAddSubtractHalf）逐一与规范公式
对照**全部符合**（`Vp8LTransforms.cs:76-89` 及标量/SIMD 展开；编码端
`Vp8LEncoder.cs:2330-2345` 等）。此前最担心的三点核实无误：

- 边界规则：最左上像素恒黑、顶行用 L、最左列用 T，编码/解码两侧一致
  （`Vp8LTransforms.cs:296-312`；编码端 tile 模式选择刻意排除边界行列）；
- **最右列 TR 用本行最左像素替换**：编码端取到的是尚未变换的本行首原始值，
  解码端取到的是已重建值——无损变换下两者相等，与规范等价（无损的关键前提）；
- 残差逐通道 mod 256 加减（byte 自然回绕），不 clamp。

### 1.4 颜色变换（规范 §4.2）

CTE 子图打包（blue=green_to_red、green=green_to_blue、red=red_to_blue）、
`ColorTransformDelta=(t*c)>>5` 的 int8×int8 语义、以及**正向用原始 red、
逆向用已加 green_to_red 之后的 `tmp_red & 0xff`** 这一不对称顺序，两侧均与
规范严格一致（`Vp8LEncoder.cs:1985-1989`、`Vp8LTransforms.cs:426-430`）。

### 1.5 减绿变换（规范 §4.3）

无数据位；正向 R−=G、B−=G，逆向相加，mod 256。符合
（`Vp8LEncoder.cs:414`、`Vp8LTransforms.cs:481-512`）。

### 1.6 调色板变换（规范 §4.4）

`color_table_size=ReadBits(8)+1`（≤256）、调色板表按 1×N 子图编码且减法编码、
索引取 green 通道、越界索引输出 0x00000000、width_bits 分档（≤2→3、≤4→2、
≤16→1、否则 0）、LSB-first 打包、打包后宽度按 `DIV_ROUND_UP` 传递给后续
变换/熵图——**全部符合**（`Vp8LEncoder.cs:1769-1813`、`Vp8LDecoder.cs:183-202`、
`Vp8LTransforms.cs:543-660`）。

### 1.7 图像数据 / LZ77 / 颜色缓存（规范 §5）

| 条款 | 结论 | 证据 |
|---|---|---|
| 五前缀码用途与顺序、字母表大小（G=280+cache、ARB=256、距离=40） | 符合 | `Vp8LDecoder.cs:21-25,293`、`Vp8LEncoder.cs:573,604` |
| 长度前缀码 ≤24（长度 ≤4096） | 符合 | `Vp8LEncoder.cs:99,1507` |
| 前缀码→值展开公式 | 与规范伪码逐行一致 | `Vp8LDistance.cs:31-39` |
| 距离映射 120 项近邻表 | **全表逐项核对无误**（含易错的 (±2,5)/(±5,2) 与尾部 (8,5)(7,7)(-7,7)(8,6)(8,7) 区段） | `Vp8LDistance.cs:11-23` |
| 码 >120 → dist=code−120；dist<1 钳为 1 | 符合 | `Vp8LDistance.cs:50-57` |
| 颜色缓存 1bit 标志 + 4bit（1..11，非法报错）、哈希 `0x1e35a7bd`、每像素插入、初始全零 | 符合 | `Vp8LDecoder.cs:218-222`、`Vp8LColorCache.cs:23-56`、编解码插入路径齐全 |

### 1.8 熵编码（规范 §6）

| 条款 | 结论 | 证据 |
|---|---|---|
| Simple code length code（1/2 符号、首符号 1 或 8 位、次符号恒 8 位） | 符合 | `Vp8LHuffman.cs:32-46`、编码端 `Vp8LHuffmanCode.cs:224-252` |
| Normal code：`4+ReadBits(4)`、`kCodeLengthCodeOrder` 19 项逐值一致、3 位码长 | 符合 | `Vp8LHuffman.cs:12-15,52-61` |
| 可选 max_symbol：`length_nbits=2+2·ReadBits(3)`、`max_symbol=2+ReadBits(n)`、超字母表报错 | 符合（含解码端校验） | `Vp8LHuffman.cs:69-76` |
| code 16（重复 3+2 位，初始值 8）/ 17（3+3 位零）/ 18（11+7 位零） | 符合，编码端 RLE 对称 | `Vp8LHuffman.cs:81-102`、`Vp8LHuffmanCode.cs:258-298` |
| 完全树约束（Kraft==1）+ 单叶例外；限长 15 | 编码端构造保证 + 数值校验；解码端 Kraft 校验 | `Vp8LHuffmanCode.cs:89-195`、`HuffmanTable.cs:62-88` |
| Meta prefix：1bit 标志、`prefix_bits=ReadBits(3)+2`、`(pixel>>8)&0xffff`、组数=max+1、子图禁用 | 符合 | `Vp8LDecoder.cs:236-304`、`Vp8LEncoder.cs:581-594,924` |
| LSB-first 位序（含 Huffman 码字 MSB-first 经反转写出） | 符合 | `LsbBitReader.cs:104-128`、`LsbBitWriter.cs:21-46` |

### 1.9 偏差与宽松口径清单

1. **【已修复】解码端拒绝 16384 边长**——见 §1.1；`Vp8LDecoder.cs` 已改 16384，
   新增 `StageB_Full_MaxDimension16384_RoundTrip_BitExact` 回归测试（我方 +
   libwebp 双解码器 bit-exact）。
2. **【信息性·合法】调色板索引像素 alpha 写 0 而非规范字面的 255**——逆变换按
   整像素替换、alpha 不参与解释，逐像素结果不变（`Vp8LEncoder.cs:1796`）。
3. **【信息性·合法】CTE 子图像素 alpha 写 0 而非规范字面的 255**——解码端只解释
   低 24 位，结果不变（`Vp8LEncoder.cs:1845,1924`）。
4. **【宽松·与 libwebp 一致】simple code 符号越界静默忽略**而非报错
   （`Vp8LHuffman.cs:40-46`）；两符号均越界时才因全零码长报错。
5. **【宽松·与 libwebp 一致】单激活符号树不校验码长必须为 1**，按 0-bit 单符号
   表解码（`HuffmanTable.cs:73-88,181-184`）。
6. **【备注】predictor 模式 14/15 兜底为 left**（`Vp8LTransforms.cs:90`），注释称
   「按 libwebp」，但 libwebp 哨兵实为 Predictor0（黑）；仅影响损坏位流的容错
   表现，编码端只写 0..13，不构成规范偏差。
7. **【备注·规范措辞歧义】** `max_symbol` 的计数单位实现为「元符号」（16/17/18
   各计 1 次），与 libwebp `ReadHuffmanCodeLengths` 一致；规范原文「code lengths」
   措辞与参考实现存在歧义，如需与其他解码器互操作时留意。
8. **【编码器特性·合法】子图一律 literal-only、无颜色缓存**（`Vp8LEncoder.cs:437`），
   规范允许子图用 LZ77/缓存，此为压缩率取舍而非合规问题。

## 2. `WebpEncoder.md`（有损编码器现状文档）漂移核对

| 文档陈述 | 与代码对照结果 |
|---|---|
| §1「熵编码：标准 Huffman 表 + 两遍法」 | **表述错误**。VP8 有损熵编码是布尔算术编码（`Vp8BoolEncoder`，移植自 libwebp `VP8BitWriter`）；两遍法统计的是 1056 个 token 概率节点的 0/1 频次，与 Huffman 无关。建议改为「布尔算术编码 + 两遍概率统计」 |
| §1 缺少大图降级能力 | **文档滞后**。本次新增 partition0 超限自动降级（关闭分段 + 强制 16×16 + 自适应 skip 概率），见 Vp8Encoder.cs 类注释 |
| §3.3 λ=300 | 与代码一致（`LambdaScale = 300`，`Vp8Encoder.cs:187`） |
| §4 缺口 1 系数 trellis | 仍成立（无 trellis 相关代码） |
| §4 缺口 2 滤波强度不参与 RD | 仍成立（`EstimateFilterLevel(q)` 查表定死，`Vp8Encoder.cs:403`） |
| §4 缺口 3 无损多 pass | **部分过时**。meta-Huffman 的 tile 直方图 k-means 聚类已实现（`Vp8LEncoder.cs:769,1026`，按估计码长+码表开销选组数）；「用最终码长重算 Huffman」与「长窗 LZ77」（距离已支持至 2²⁰ 全图、长度 4096 为规范上限）表述需按当前实现改写 |
| §4 缺口 4 动图逐帧 `canvas.ToArray()` | 仍成立（`WebpDecoder.cs:402`） |
| §2/§3 实测数据表 | 历史数据，本次未复测，不在核对范围 |

## 3. 建议后续动作

1. （已随本报告完成）解码端 16384 上限修复 + 回归测试。
2. 修订 `WebpEncoder.md` §1 熵编码表述、补录大图降级、按现状改写缺口 3。
3. （可选）把 §1.9 第 6 条的兜底注释/行为与 libwebp 对齐（Predictor0 而非 left）。
4. （可选）§1.9 第 4/5 条宽松口径如需严格化，加显式报错路径；当前与 libwebp
   行为一致，不动也不影响互操作。
