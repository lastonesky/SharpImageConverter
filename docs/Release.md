# 发布流程（Release）

本文是**发版的唯一操作口径**：版本号、tag、GitHub Release、Release 资产、NuGet 发布都由这里定义。

## 一、呼唤方式（约定）

需要发版时，直接对我说：

```
发版 1.0.1
```

可选补充：

- `发版 1.0.1-rc.1` —— 预发布版本
- `只打 tag 不发 NuGet` —— 极少用，仅在你明确要求时

我会按下面的《执行清单》做完前置动作（改版本号、提交、打 tag、推送），**Release 本身由 GitHub Actions 自动生成**，之后我盯 CI 并回报。
版本号不合法（不是 `X.Y.Z` / 该版本已被 NuGet 占用 / 工作树不干净）我会先停下来问，不会擅自换号。

## 二、唯一事实来源

| 项 | 位置 | 规则 |
|---|---|---|
| 包版本 | `src/SharpImageConverter.csproj` 的 `<Version>` | 三段式 `X.Y.Z`（NuGet 不接受 `1.0` 这种两段式） |
| git tag | `vX.Y.Z` | 与 `<Version>` 一一对应，附注 tag |
| NuGet 发布 | `.github/workflows/nuget-publish.yml` | 推 tag 自动触发（OIDC trusted publishing），**不要手动 `dotnet nuget push`** |
| GitHub Release | `.github/workflows/release.yml` | 推 tag 自动触发：三平台构建 CLI 资产 + 打包 + 创建 Release + 上传 |

## 三、执行清单（收到「发版 X.Y.Z」后）

0. **前置检查**：工作树干净、`master` 与两个远端一致、`X.Y.Z` 未被 NuGet 占用（探测命令见第四节坑 2）
1. **改版本号**：`src/SharpImageConverter.csproj` → `<Version>X.Y.Z</Version>`
2. **改 CHANGELOG**：把顶部 `## 未发布` 落成 `## X.Y.Z（相对 v<上一版>）`，并在最上面补一个空的 `## 未发布`
3. **提交并推 master**：`chore(release): bump version to X.Y.Z` → gitee + github
4. **打 tag 并推**：`vX.Y.Z` 推到两个远端。推 github 后同时触发两个工作流：
   - `nuget-publish` → 测试 + 打包 + 推 NuGet.org
   - `release` → win-x64 / linux-x64 / osx-arm64 三平台构建 CLI（各带冒烟测试）+ 打包 + 创建 Release + 上传资产
5. **盯 CI 并核验**：两个工作流都 success；Release 不是 Draft；资产齐全、`SHA256SUMS.txt` 覆盖全部资产
6. **回报**（格式见第七节）

**顺序很重要**：先改版本号再打 tag。反过来做会踩第四节坑 1。

**注意**：Release 现在由工作流自动创建，所以「版本号写错就发错」的代价变高了 —— 推 tag 之前一定先确认 csproj 里的版本号。

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
6. **Windows 文件系统不保存 Linux 可执行位**：打包 linux/osx 的 `tar.gz` 必须显式写入模式，否则用户解压后 `Permission denied`（`tools/build-cli.sh` 里用 GNU tar 的 `--mode`；macOS 的 BSD tar 不需要）。CI 在原生 Linux/macOS runner 上构建，天然没有这个问题。
7. **原生 WebP 库按 RID 选择**：`src/SharpImageConverter.csproj` 的 `_SicNativeRid` 决定拷贝哪套原生库。指定 `RuntimeIdentifier` 时以 RID 为准（跨平台发布不会混入宿主平台的原生库）；未指定时按宿主 OS。CLI 项目不再重复拷贝 `runtimes/**`。
8. **未签名的 arm64 二进制在 macOS 上会被直接杀掉**：冒烟测试里先做 `codesign --force --sign -` 再运行。
9. **macOS arm64 runner 不可靠**：排队 15 分钟后会被 GitHub 直接取消。所以 osx-arm64 的**发布产物**走 Intel 交叉构建（必得），arm64 实机验证只作为可选手动步骤。

## 五、自动化：`.github/workflows/release.yml`

| 触发方式 | 行为 |
|---|---|
| 推送 `v*` tag | 构建三平台 CLI → 冒烟测试 → 打包 → **创建并公开发布** Release、上传资产 |
| 手动 `workflow_dispatch`（输入一个已存在的 tag） | 同上，但创建的是**草稿** Release（试跑/补发用，不会公开） |

矩阵（win/linux 在**原生** runner 上构建，这样原生库按宿主 OS 选择即可正确，也才能真跑冒烟测试）：

| RID | runner | 冒烟测试 |
|---|---|---|
| win-x64 | `windows-latest` | 实机：`PNG→WebP→PNG` + `PNG→JPEG` |
| linux-x64 | `ubuntu-latest` | 实机：同上 |
| osx-arm64 | `macos-15-intel`（交叉构建） | 静态校验（`file` 确认 arm64 + 原生库为 arm64） |

**为什么 osx-arm64 走 Intel 交叉构建**：GitHub 的 macOS arm64 runner 容量紧张，实测排队 15 分钟后被直接取消（`The job was not acquired by Runner of type hosted even after multiple attempts`），不能当作发版的可靠前置。交叉构建能保证产出，且 `_SicNativeRid` 会正确地选 `runtimes/osx-arm64/native/*.dylib`（已确认这些库本身就是 arm64）。

**需要 arm64 实机验证时**：发版后手动 dispatch 一次，工作流会额外跑一个 `osx-arm64 实机冒烟（可选）` 任务（`macos-15`，`continue-on-error: true`）——拿到 runner 就实机验证，拿不到也不影响发布。

手动触发命令（草稿 Release）：

```bash
gh workflow run release.yml --repo lastonesky/SharpImageConverter --ref master -f tag=v1.0.1
```

## 六、Release 资产约定

| 资产 | 说明 |
|---|---|
| `SharpImageConverter.X.Y.Z.nupkg` | 由 `release` 工作流从**该 tag 的源码**打包上传；`nuget-publish` 工作流也把同版本推到了 nuget.org。同源同版本，但不保证逐字节相同（两者打包环境不同），以 nuget.org 上的为权威。 |
| `SharpImageConverter.X.Y.Z.snupkg` | 同上（符号包） |
| `SharpImageConverter.Cli-X.Y.Z-win-x64.exe` | 单文件自包含（原生库内嵌，运行时自解压），**无需安装 .NET** |
| `SharpImageConverter.Cli-X.Y.Z-linux-x64.tar.gz` | 同上 |
| `SharpImageConverter.Cli-X.Y.Z-osx-arm64.tar.gz` | 同上；由 Intel runner 交叉构建，CI 只做架构/原生库静态校验（若要 arm64 实机验证，发版后手动 dispatch 一次） |
| `SHA256SUMS.txt` | 覆盖以上全部资产 |

本机构建 CLI 资产用同名脚本（版本号自动取自 csproj，产物落在被 gitignore 的 `.artifacts/release/`）：

```bash
tools/build-cli.sh win-x64                       # 默认
tools/build-cli.sh win-x64 linux-x64 osx-arm64   # 多平台，需在对应平台上各自构建
```

发布参数：`-c Release -r <rid> --self-contained true -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
-p:DebugType=none -p:GenerateDocumentationFile=false`。

## 七、回报格式

每次发版我会回：

- tag 与指向的 commit（两个远端各一行 `git ls-remote` 证据）
- 两个工作流（`nuget-publish` / `release`）的运行链接与结论，以及每个平台的冒烟测试是否通过
- GitHub Release URL + 资产清单（含大小、状态）
- `SHA256SUMS.txt` 内容
- nuget.org 版本页链接
- 已知残留风险

## 八、版本号规范

- 正式版：`<Version>X.Y.Z</Version>` + tag `vX.Y.Z`
- 预发布：`<Version>X.Y.Z-preview.N</Version>` + tag `vX.Y.Z-preview.N`（历史沿用过 `0.1.4-preview`、`0.2.8` 这类写法；tag 带 `v`，版本号不带）
- CHANGELOG 段落标题惯例：`## X.Y.Z（相对 v<上一版>）`
