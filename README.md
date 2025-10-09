# Jpeg2Bmp

一个用 C# 编写的基线 JPEG 解码器与 BMP 写出示例，不依赖第三方库（不使用 `System.Drawing`），实现从 JPEG 到 24-bit BMP 的完整流水线：

- 按 MCU（8×8）顺序读取压缩数据
- 使用 Huffman 表解码 DC/AC 系数
- 反量化（乘以量化表）
- 8×8 的逆离散余弦变换（IDCT）
- YCbCr → RGB 转换
- 写入 24-bit BMP 文件

## 功能与实现

- 段解析（`JpegParser`）
  - 识别 SOI/EOI、DQT（量化表）、SOF0（基线帧）、DHT（霍夫曼表）、SOS（扫描段）、DRI（重启间隔）。
  - 修正 SOS 解析：不重复读取长度；在扫描数据中正确处理 `0xFF00` 填充与 `RSTn`（`FFD0`–`FFD7`）。
  - 记录帧分量（ID、采样因子 H/V、量化表 ID），计算 `MaxH/MaxV`。
- 位流读取（`BitReader`）
  - 处理 `0xFF00` 字节填充（代表数据中的 `0xFF`）。
  - 跳过 `RSTn` 并复位位缓冲（配合 DRI 重启间隔）。
- 霍夫曼解码（`JpegDecoder`）
  - 基于 `CodeLengths` 构建 canonical 表；按位匹配得到符号。
  - DC 使用 ExtendSign 重建，AC 支持 RLE 与 EOB。
- ZigZag 与反量化
  - 将量化表的 ZigZag 序列映射到自然顺序进行反量化。
- IDCT（`Idct`）
  - 朴素浮点 8×8 IDCT，保证正确性（性能非目标）。
- YCbCr → RGB
  - BT.601 转换，输出 BGR 顺序以符合 BMP 格式。
- BMP 写出（`BmpWriter`）
  - 手写 BMP 文件头与像素数据，逐行自底向上写出，按 4 字节对齐。

## 目录结构

```
Jpeg2Bmp/
├── JpegParser.cs     # 段解析与帧信息
├── JpegDecoder.cs     # 霍夫曼解码、反量化、IDCT、组装与颜色转换
├── BitReader.cs       # 位流读取与填充/RSTn 处理
├── Idct.cs            # 8×8 IDCT
├── BmpWriter.cs       # 24-bit BMP 写出
├── Program.cs         # 入口，串联解析→解码→写出
├── JpegHuffmanTable.cs / JpegQuantTable.cs / JpegSegment.cs
└── Jpeg2Bmp.csproj
```

## 使用方法

环境要求：
- .NET SDK（项目当前 `TargetFramework` 为 `net10.0`，属于预览版，构建时会提示预览信息）。
- Windows（PowerShell 终端）。

命令行参数：
- 必填：`<输入JPEG路径>`
- 选填：`[输出BMP路径]`

运行：
```
dotnet build
dotnet run -- <输入JPEG路径> [输出BMP路径]
```

输出路径规则：
- 若未提供输出路径，默认写到与输入文件相同目录、同名但扩展名为 `.bmp`。
- 若目标 `.bmp` 已存在，不会覆盖，而是自动生成不冲突的文件名（例如：`xxx.bmp`、`xxx (1).bmp`、`xxx (2).bmp`）。

## 运行示例（部分日志）

```
Step 5: 解析 JPEG 量化表...
解析到 10 个段。
✅ 图像尺寸: 400 x 559
✅ 量化表数量: 2
...
✅ Huffman 表数量: 4
...
✅ 扫描段数量: 1
Scan: NbChannels=3, DataOffset=1202, DataLength=30088
  Channel 1: DC=0, AC=0
  Channel 2: DC=1, AC=1
  Channel 3: DC=1, AC=1
Step 7 OK: SOS 段解析完成
Step 8: 基线JPEG解码到RGB并写BMP...
✅ BMP 写入完成: d:\out.bmp
```

## 已知限制

- 当前仅支持 4:4:4 采样（`MaxH=MaxV=1`）。
  - 如需支持 4:2:0 或 4:2:2，需要实现分量上采样与 MCU 映射（可后续扩展）。
- 基线 JPEG（8-bit DCT）路径。渐进式（Progressive）未实现。
- 单扫描为主（解析支持多扫描，解码路径目前按单扫描包含所有分量）。
- IDCT 为朴素浮点实现，侧重正确性，性能未优化。

## 下一步计划

- 支持 4:2:0 / 4:2:2 的分量上采样与 MCU 拼接。
- 优化 IDCT（如 AAN 快速整数 IDCT）以提升性能。
- 完善多扫描 / 渐进式 JPEG 支持。
- 增强鲁棒性（容错、特殊 JPEG 变体）。
- 增加单元测试与更多示例图片验证。

## 故障排查

- “文件不存在”：请检查 `Program.InputPath` 路径是否正确。
- 解析/解码异常：请检查输入是否为基线 JPEG，且为 4:4:4 采样；如为 4:2:0/4:2:2，请在需求确认后扩展支持。
- .NET 预览提示：当前 `net10.0` 为预览，属于正常提示。若需稳定版，可调整 `TargetFramework`。
- 警告 CA2022：与 `FileStream.Read` 的精确读取相关，不影响当前演示运行。

## 说明

本项目为学习/演示用途，准确性优先于性能。若要支持更广泛的 JPEG 变体或大批量处理，建议逐步引入更高效的 IDCT、采样与缓存策略。