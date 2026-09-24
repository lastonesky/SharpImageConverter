
**差距清单**
- 编码与功能覆盖不足：JPEG 仅基线编码、PNG 写入不含调色板与元数据、GIF 写入仅单帧且不写透明/动画元数据、WebP 编码质量固定等特性仍有限（参考 [README.md](../README.md)）。
- 并发与线程安全验证不足：项目明确“内部系统使用，没有使用并发压力”，且批量转换对 WebP 并行度有降级说明（参考 [README.md](../README.md)）。
- 流式与内存策略不完整：仅对 JPEG 提供流式解码选项，其他格式回退常规解码（参考 [README.md](../README.md)）。
- 测试覆盖与兼容性验证不足：现有测试以小尺寸 roundtrip 与少量案例为主，缺少大规模格式语料/跨平台兼容验证与回归门禁（参考 [FormatConversionTests.cs](../SharpImageConverter.Tests/FormatConversionTests.cs) 与 [ProgressiveJpegTests.cs](../SharpImageConverter.Tests/ProgressiveJpegTests.cs)）。
- 商用发布稳定性不足：当前版本为 preview（参考 [SharpImageConverter.csproj](../src/SharpImageConverter.csproj)）。
- 许可与第三方合规落地：WebP 依赖原生库且需随分发保留许可声明（参考 [README.md](../README.md) 与 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)）。