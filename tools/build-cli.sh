#!/usr/bin/env bash
# 构建 CLI 的发布资产（用于 GitHub Release）。
#
# 用法：
#   tools/build-cli.sh [rid ...]     # 默认 win-x64
#   例如：tools/build-cli.sh win-x64 linux-x64 osx-arm64
#
# 产物（.artifacts/release/，已被 .gitignore 忽略）：
#   SharpImageConverter.Cli-<version>-win-x64.exe        单文件，依赖框架（需已安装 .NET 10 运行时）
#   SharpImageConverter.Cli-<version>-linux-x64.tar.gz   单文件，依赖框架（tar 内含单个可执行文件）
#   SharpImageConverter.Cli-<version>-osx-arm64.tar.gz   目录式，依赖框架（tar 内含同名目录）
#   SHA256SUMS.txt
#
# 自 1.3.0 起改为**依赖框架**发布（--self-contained false）：自包含运行时体积太大，
# 使用者需自行安装 .NET 10 运行时。win/linux 仍是单文件（托管程序集打进 exe），
# 资产形态与自包含版相同，只是体积大幅缩小。
#
# 编解码实现全部为纯托管代码（WebP 为纯 C# 编解码器），发布不携带任何原生库，
# 无需 install_name_tool / @rpath 修复。macOS 仍走目录式发布并做 ad-hoc 签名：
# arm64 上可执行文件必须有签名，签名动作必须在 macOS 上执行。
# ⇒ osx-arm64 必须在 macOS 上构建（CI 用 macos-15）。
#
# 版本号取自 src/SharpImageConverter.csproj 的 <Version>，因此必须先改版本号再执行本脚本。
# CI 侧对应 .github/workflows/release.yml。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(win-x64)

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SharpImageConverter.csproj | head -1)"
if [ -z "$VERSION" ]; then
  echo "错误：无法从 src/SharpImageConverter.csproj 读取 <Version>" >&2
  exit 1
fi

# GNU tar（Linux/WSL/git-bash）能强制写入文件模式；BSD tar（macOS）不需要，真实文件系统已保存可执行位。
TAR_EXTRA=""
if tar --version 2>/dev/null | grep -q 'GNU tar'; then
  TAR_EXTRA="--owner=0 --group=0 --numeric-owner --mode=u+rwx,go+rx,go-w"
fi

# macOS 只有 shasum
sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$@"
  else
    shasum -a 256 "$@"
  fi
}

# macOS 发布目录做 ad-hoc 签名（见文件头说明）
sign_macos_app() {
  local dir="$1"
  cd "$dir"
  # arm64 上可执行代码必须有签名（ad-hoc 即可），否则无法运行
  codesign --force --sign - ./SharpImageConverter.Cli >/dev/null 2>&1 || true
  cd - >/dev/null
}

STAGE="$ROOT/.artifacts/release/.stage"
OUT="$ROOT/.artifacts/release"
rm -rf "$STAGE"
mkdir -p "$STAGE" "$OUT"
rm -f "$OUT"/SharpImageConverter.Cli-* "$OUT"/SHA256SUMS.txt

for RID in "${RIDS[@]}"; do
  case "$RID" in
    win-x64) EXT=".exe" ;;
    linux-x64 | osx-arm64) EXT="" ;;
    *)
      echo "错误：不支持的 RID '$RID'（可用：win-x64 linux-x64 osx-arm64）" >&2
      exit 1
      ;;
  esac

  if [ "$RID" = "osx-arm64" ] && [ "$(uname -s)" != "Darwin" ]; then
    echo "错误：osx-arm64 必须在 macOS 上构建（ad-hoc 签名必须在 macOS 上执行）" >&2
    exit 1
  fi

  # 注意：变量后面紧邻中文时一律用 ${} 包裹 —— macOS 的 bash 3.2 在非 UTF-8 locale 下
  # 会把中文首字节当成变量名的一部分，报 "RID?: unbound variable"。
  echo "==> 发布 ${RID}（版本 ${VERSION}）"
  NAME="SharpImageConverter.Cli-$VERSION-$RID"

  if [ "$RID" = "osx-arm64" ]; then
    dotnet publish Cli/SharpImageConverter.Cli.csproj \
      -c Release -r "$RID" --self-contained false \
      -p:PublishSingleFile=false \
      -p:DebugType=none \
      -p:GenerateDocumentationFile=false \
      -o "$STAGE/$NAME" >/dev/null
    sign_macos_app "$STAGE/$NAME"
    # shellcheck disable=SC2086
    tar $TAR_EXTRA -czf "$OUT/$NAME.tar.gz" -C "$STAGE" "$NAME"
  else
    dotnet publish Cli/SharpImageConverter.Cli.csproj \
      -c Release -r "$RID" --self-contained false \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=none \
      -p:GenerateDocumentationFile=false \
      -o "$STAGE/$RID" >/dev/null

    BIN="$STAGE/$RID/SharpImageConverter.Cli$EXT"
    if [ ! -f "$BIN" ]; then
      echo "错误：未找到产物 $BIN" >&2
      exit 1
    fi
    if [ "$EXT" = ".exe" ]; then
      cp "$BIN" "$OUT/$NAME.exe"
    else
      # linux：单文件产物直接打成 tar（保留可执行位）
      # shellcheck disable=SC2086
      tar $TAR_EXTRA -czf "$OUT/$NAME.tar.gz" -C "$STAGE/$RID" "SharpImageConverter.Cli"
    fi
  fi

  echo "    -> $(ls -1 "$OUT/$NAME"* | tr '\n' ' ')"
done

rm -rf "$STAGE"
( cd "$OUT" && sha256 SharpImageConverter.Cli-* >SHA256SUMS.txt && cat SHA256SUMS.txt )
