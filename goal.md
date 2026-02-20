
**差距清单**
- 编码与功能覆盖不足：JPEG 仅基线编码、PNG 写入不含调色板与元数据、GIF 写入仅单帧且不写透明/动画元数据、WebP 编码质量固定等特性仍有限（参考 [README.md:L18-L56](file:///d:/src/SharpImageConverter/README.md#L18-L56)）。
- 并发与线程安全验证不足：项目明确“内部系统使用，没有使用并发压力”，且批量转换对 WebP 并行度有降级说明（参考 [README.md:L9-L14](file:///d:/src/SharpImageConverter/README.md#L9-L14) 与 [README.md:L194-L198](file:///d:/src/SharpImageConverter/README.md#L194-L198)）。
- 流式与内存策略不完整：仅对 JPEG 提供流式解码选项，其他格式回退常规解码（参考 [README.md:L191-L192](file:///d:/src/SharpImageConverter/README.md#L191-L192)）。
- 测试覆盖与兼容性验证不足：现有测试以小尺寸 roundtrip 与少量案例为主，缺少大规模格式语料/跨平台兼容验证与回归门禁（参考 [FormatConversionTests.cs:L18-L119](file:///d:/src/SharpImageConverter/SharpImageConverter.Tests/FormatConversionTests.cs#L18-L119) 与 [ProgressiveJpegTests.cs:L16-L52](file:///d:/src/SharpImageConverter/SharpImageConverter.Tests/ProgressiveJpegTests.cs#L16-L52)）。
- 商用发布稳定性不足：当前版本为 preview（参考 [SharpImageConverter.csproj:L12-L16](file:///d:/src/SharpImageConverter/src/SharpImageConverter.csproj#L12-L16)）。
- 许可与第三方合规落地：WebP 依赖原生库且需随分发保留许可声明（参考 [README.md:L52-L56](file:///d:/src/SharpImageConverter/README.md#L52-L56) 与 [THIRD-PARTY-NOTICES.md:L1-L39](file:///d:/src/SharpImageConverter/THIRD-PARTY-NOTICES.md#L1-L39)）。