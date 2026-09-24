# 文档索引

本目录存放项目的**工程类文档**（性能分析、审计复核、差距清单、规范参考）。
面向使用者的文档（README、更新日志、第三方许可）按生态约定保留在仓库根目录。

| 文档 | 内容 | 状态 |
|---|---|---|
| [PerfReport.md](PerfReport.md) | 性能问题定位与优化建议清单。逐条记录各热点（JPEG 解码/重建/色彩转换、PNG 解压、GIF 多帧、处理管线、EXIF 旋转）的发现与处置结论 | 多数条目已标记【已完成】/【问题确认不存在】 |
| [AuditVerification.md](AuditVerification.md) | 对外部「内存管理 / SIMD 利用率 / SIMD 有效率 / 死代码」审计报告的逐条取证复核，含修复项与实测数据 | 持续更新 |
| [goal.md](goal.md) | 与同类成熟库对比后的差距清单（编码覆盖、并发验证、流式与内存策略、测试语料、发布稳定性、许可合规） | 长期跟踪 |
| [reference/](reference/) | 规范原文等参考资料（如 JPEG File Interchange Format 规范） | 只读存档 |

## 根目录文档

| 文档 | 内容 |
|---|---|
| [../README.md](../README.md) | 中文主文档：功能特性、API、CLI 用法 |
| [../README.en.md](../README.en.md) | 英文主文档 |
| [../CHANGELOG.md](../CHANGELOG.md) | 版本更新日志 |
| [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) | 第三方组件版权与许可声明 |

## 工具专属文档（路径不可移动）

| 路径 | 说明 |
|---|---|
| `.trae/rules/project.md` | Trae IDE 的项目规则，IDE 按固定路径读取 |
| `.deepseek/instructions.md` | DeepSeek TUI 自动生成的目录快照，已被 `.gitignore` 忽略，可随时删除 |
