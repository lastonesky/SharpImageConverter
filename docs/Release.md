# 发布流程（Release）

本文是**发版的唯一操作口径**：版本号、tag、GitHub Release、Release 资产、NuGet 发布都由这里定义。

## 一、呼唤方式（约定）

需要发版时，直接对我说：

```
发版 1.0.1
```

可选补充：

- `发版 1.0.1，带 linux` —— 连 linux-x64 CLI 资产一起发
- `发版 1.0.1-rc.1` —— 预发布版本
- `只打 tag 不发 NuGet` —— 极少用，仅在你明确要求时

我会按下面的《执行清单》做完所有动作，并按《回报格式》汇报。**你只需要提供目标版本号。**
版本号不合法（不是 `X.Y.Z` / 该版本已被 NuGet 占用 / 工作树不干净）我会先停下来问，不会擅自换号。

## 二、唯一事实来源

| 项 | 位置 | 规则 |
|---|---|---|
| 包版本 | `src/SharpImageConverter.csproj` 的 `<Version>` | 三段式 `X.Y.Z`（NuGet 不接受 `1.0` 这种两段式） |
| git tag | `vX.Y.Z` | 与 `<Version>` 一一对应，附注 tag |
| NuGet 发布 | `.github/workflows/nuget-publish.yml` | 推 tag 自动触发（OIDC trusted publishing），**不要手动 `dotnet nuget push`** |
| GitHub Release | 由我创建 | 资产见第五节 |

## 三、执行清单（收到「发版 X.Y.Z」后）

0. **前置检查**：工作树干净、`master` 与两个远端一致、`X.Y.Z` 未被 NuGet 占用（探测命令见第四节坑 2）
1. **改版本号**：`src/SharpImageConverter.csproj` → `<Version>X.Y.Z</Version>`
2. **改 CHANGELOG**：把顶部 `## 未发布` 落成 `## X.Y.Z（相对 v<上一版>）`，并在最上面补一个空的 `## 未发布`
3. **提交并推 master**：`chore(release): bump version to X.Y.Z` → gitee + github
4. **打 tag 并推**：`vX.Y.Z` 推到两个远端（推 github 即触发 NuGet 自动发布）
5. **构建 CLI 资产**：`tools/build-cli.sh win-x64`（需要时追加 `linux-x64`）
6. **取回 NuGet 上的同一份包**：从 `www.nuget.org` 下载 CI 刚发布的 nupkg/snupkg，保证 Release 资产与已发布包一致；连同 CLI 资产生成 `SHA256SUMS.txt`
7. **创建 Release**：`gh release create vX.Y.Z`，写入人话版说明 + 上传全部资产
8. 确认 CI（nuget-publish）运行成功、Release 不是 Draft

**顺序很重要**：先改版本号再打 tag。反过来做会踩第四节坑 1。

## 四、必须避开的坑（都是踩过的）

1. **不要先打 tag 再改版本号。** 删除已推送的 tag 会让对应的 GitHub Release 自动变回 **Draft**（需要 `gh release edit <tag> --draft=false` 重新发布，并确认 target commit）。改了版本号就必然要移动 tag —— 所以版本号必须在打 tag 之前就位。
2. **NuGet 版本号不可复用。** 重复推同一版本会被 `--skip-duplicate` 静默跳过：CI 显示 success，实际什么都没发。发版前探测：
   ```bash
   curl -sL -o /dev/null -w '%{http_code}\n' \
     https://www.nuget.org/api/v2/package/SharpImageConverter/X.Y.Z   # 200=已占用, 404=可发
   ```
3. **`api.nuget.org` 的 flatcontainer 索引在国内可能命中滞后镜像**（会被 302 到 `nuget.azure.cn`，看不到刚发布的版本）。判断版本是否存在请用上面的 `www.nuget.org/api/v2/package/...`。
4. **`.github/` 被 `.gitignore` 忽略**（见 `.gitignore` 末尾）：新增或修改 workflow 必须 `git add -f`。
5. **NuGet 认证是 OIDC trusted publishing**（`NuGet/login@v1` + `id-token: write` + environment `production`），不要改成长期 API Key。
6. **Windows 文件系统不保存 Linux 可执行位**：打包 linux/osx 的 `tar.gz` 必须显式 `--mode`，否则用户解压后 `Permission denied`（脚本已处理）。
7. **原生 WebP 库按 RID 选择**：`src/SharpImageConverter.csproj` 的 `_SicNativeRid` 决定拷贝哪套原生库。指定 `RuntimeIdentifier` 时以 RID 为准（跨平台发布不会混入宿主平台的原生库）；未指定时按宿主 OS。CLI 项目不再重复拷贝 `runtimes/**`。

## 五、Release 资产约定

| 资产 | 说明 |
|---|---|
| `SharpImageConverter.X.Y.Z.nupkg` | 与 nuget.org 上 CI 构建的**同一份**（从 nuget.org 下载后上传） |
| `SharpImageConverter.X.Y.Z.snupkg` | 同上（符号包） |
| `SharpImageConverter.Cli-X.Y.Z-win-x64.exe` | 单文件自包含（原生库内嵌，运行时自解压），**无需安装 .NET** |
| `SharpImageConverter.Cli-X.Y.Z-linux-x64.tar.gz` | 同上 |
| `SHA256SUMS.txt` | 覆盖以上全部资产 |

CLI 资产由 `tools/build-cli.sh` 生成（版本号自动取自 csproj，产物落在被 gitignore 的 `.artifacts/release/`）：

```bash
tools/build-cli.sh win-x64                     # 默认
tools/build-cli.sh win-x64 linux-x64           # 多平台
```

发布参数：`-c Release -r <rid> --self-contained true -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
-p:DebugType=none -p:GenerateDocumentationFile=false`。

## 六、发布前验证（必做）

| RID | 验证方式 | 本机可行性 |
|---|---|---|
| win-x64 | 直接跑一次 `PNG→WebP→PNG` 往返（覆盖原生 libwebp）+ `PNG→JPEG` | ✅ |
| linux-x64 | WSL 里解包后跑同一套往返：`wsl.exe -e bash -lc '...'` | ✅ |
| osx-arm64 | 需在 macOS 上实测 | ❌ 未纳入默认资产 |

**osx-arm64 是脚本支持的第三个 RID，但在本机无法验证运行**，因此默认不发。需要在 macOS 上实测通过（尤其是原生 libwebp 能否加载）后，再把它加进发版参数。

## 七、回报格式

每次发版我会回：

- tag 与指向的 commit（两个远端各一行 `git ls-remote` 证据）
- 两个远端的 CI 运行链接（nuget-publish 状态）
- GitHub Release URL + 资产清单（含大小、状态）
- `SHA256SUMS.txt` 内容
- nuget.org 版本页链接
- 已知残留风险（例如「osx 未验证」「某平台未发」）

## 八、版本号规范

- 正式版：`<Version>X.Y.Z</Version>` + tag `vX.Y.Z`
- 预发布：`<Version>X.Y.Z-preview.N</Version>` + tag `vX.Y.Z-preview.N`（历史沿用过 `0.1.4-preview`、`0.2.8` 这类写法；tag 带 `v`，版本号不带）
- CHANGELOG 段落标题惯例：`## X.Y.Z（相对 v<上一版>）`
