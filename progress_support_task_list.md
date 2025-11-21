
**结论**
- 项目已支持渐进式 JPEG（Progressive，SOF2）基础解码（初始DC/AC）。
- 主要变更：
  - 解析 SOF2 并标记渐进式：`d:\src\jpeg2bmp\JpegParser.cs:66-89, 90-113`
  - 解析并保存 `Ss/Se/Ah/Al`：`d:\src\jpeg2bmp\JpegParser.cs:111-166`
  - 增加渐进式解码分支：`d:\src\jpeg2bmp\JpegDecoder.cs:100-107, 149-226`
  - 系数累积与最终重建：`d:\src\jpeg2bmp\JpegDecoder.cs:177-226, 228-311`
  - 扫描信息打印与路径选择：`d:\src\jpeg2bmp\Program.cs:41-52`

**完成记录**
- [已完成] 在 `JpegParser` 支持 SOF2（`0xFFC2`）并标记帧为渐进式（`JpegParser.cs:90-113`）。
- [已完成] 扩展 `JpegScan` 存储 `Ss/Se/Ah/Al`，在 SOS 时解析并保存（`JpegParser.cs:111-166`, `JpegParser.cs:303-318`）。
- [已完成] 在 `JpegDecoder` 增加渐进式解码分支与状态管理（`JpegDecoder.cs:100-107`, `JpegDecoder.cs:149-226`）。
- [已完成] 为每组件/每块建立 `short[64]` 系数字缓冲，跨扫描累计（`JpegDecoder.cs:170-186`）。
- [已完成] 实现 DC 初始扫描（`Ss=0, Se=0`）与细化位的基础处理（DC 细化位加权）：`JpegDecoder.cs:199-226`。
- [已完成] 实现 AC 初始扫描（`Ss>=1`）含 `EOB`/`ZRL` 与符号位存储（`JpegDecoder.cs:227-311`）。
- [暂不支持] 实现 AC 细化扫描，支持 `EOBRUN`、细化位更新与插入新非零系数（后续可迭代）。
- [已完成] 在重启标记处理里复位 `prevDC` 与位缓冲（`JpegDecoder.cs:214-226`）。
- [已完成] 按 `Scans` 顺序迭代所有扫描，更新系数字缓冲（`JpegDecoder.cs:149-311`）。
- [已完成] 所有扫描完成后执行分块 IDCT，上采样并合成到 Y/Cb/Cr 子平面（`JpegDecoder.cs:312-369`）。
- [已完成] 保持基线路径兼容，依据帧类型（Baseline/Progressive）分支（`JpegDecoder.cs:100-107, 109-148`）。
- [已完成] 复用现有霍夫曼快表，完善边界检查与溢出裁剪（多处）。
- [已完成] 在 `Program` 打印扫描信息含 `Ss/Se/Ah/Al`，并选择正确解码路径（`Program.cs:41-52`）。
- [已完成] 编译类型检查：`dotnet build` 通过。
