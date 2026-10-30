# 文档索引

本目录存放项目的**工程类文档**（性能记录、算法分析、平台移植、实现陷阱、发布口径）。
面向使用者的文档（README、更新日志、第三方许可）按生态约定保留在仓库根目录。

| 文档 | 内容 |
|---|---|
| [PerfReport.md](PerfReport.md) | **性能实测记录与已否决方向**：验收/测量口径、半年整体对比、各格式（JPEG / PNG / BMP / 缩放）的实测与封死性上界，以及「勿重复尝试」汇总表（含 AVX-VNNI 结论） |
| [Optimize.md](Optimize.md) | 智能有损压缩（TinyPNG 式）的设计与用法：感知画质度量与阈值标定、各格式策略、量化器取舍、API 与 CLI |
| [OptimizeAudit.md](OptimizeAudit.md) | `--optimize` 方向性审计：画质判据、WebP 通道口径、GIF 动画、PNG 产出阶段等问题的取证与结论 |
| [OptimizePlan.md](OptimizePlan.md) | 依据审计结论制定的逐条改进计划（P0~P3），含验收标准与执行记录 |
| [JpegAudit.md](JpegAudit.md) | **JPEG 代码审计**：可删除的死代码清单、需要决策的半成品/重复实现、以及编码/解码/optimize 三条链路的剩余性能空间（含 `ParallelPixelThreshold` 定错一个数量级的实测） |
| [GifFindings.md](GifFindings.md) | GIF 算法与性能记录：Octree/Wu 两条量化路径、LZW 编解码、热点归因与已否决方向 |
| [ArmFindings.md](ArmFindings.md) | ARM（Apple M4）基线与专属指令实测：x86 SIMD 失效清单、NEON 语义等价、A/B 结果、LD3/RDM/CRC 三条判决 |
| [SimdPitfalls.md](SimdPitfalls.md) | 外部审计复核结论 + SIMD 实现陷阱（K1–K15）与暂缓/否决清单 |
| [Release.md](Release.md) | **发布流程约定**：版本号/tag/Release 资产/NuGet 自动发布（trusted publishing）的口径、执行清单与踩坑记录 |
| [Review-2026-10-30.md](Review-2026-10-30.md) | **整体改进评审（快照）**：公共 API / 数据结构 / 行为规范化 / 代码组织 / 工程门禁 / 格式覆盖的问题清单，含实测证据与建议的落地顺序 |
| [reference/](reference/) | 规范原文等参考资料（如 JPEG File Interchange Format 规范），只读存档 |

## 根目录文档

| 文档 | 内容 |
|---|---|
| [../README.md](../README.md) | 中文主文档：功能特性、API、CLI 用法 |
| [../README.en.md](../README.en.md) | 英文主文档 |
| [../CHANGELOG.md](../CHANGELOG.md) | 版本更新日志（含每次改动的判定依据） |
| [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | 第三方组件版权与许可声明 |

## 工具

| 路径 | 说明 |
|---|---|
| [../tools/build-cli.sh](../tools/build-cli.sh) | 构建 CLI 单文件自包含可执行文件（win/linux/osx，用于 Release 资产） |
| [../tools/gif-compare.sh](../tools/gif-compare.sh) | GIF 优化前后同口径的 `--gif-bench` 对比 |
| [../tools/perf/](../tools/perf/) | `SicBench` 基准与 `run-ab.sh` / `run-bench.ps1` / `compare.ps1`；`run-bench.ps1` 会生成 `PerfCompare-<stamp>.md`（可再生成，不入库） |

## 路径不可移动的文档

`.trae/rules/project.md` 是 Trae IDE 的项目规则，IDE 按固定路径读取，不能移到 `docs/`。
