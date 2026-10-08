#!/usr/bin/env bash
# 构建 CLI 的单文件自包含可执行文件（作为 GitHub Release 资产）。
#
# 用法：
#   tools/build-cli.sh [rid ...]     # 默认 win-x64
#   例如：tools/build-cli.sh win-x64 linux-x64 osx-arm64
#
# 产物（.artifacts/release/，已被 .gitignore 忽略）：
#   SharpImageConverter.Cli-<version>-win-x64.exe
#   SharpImageConverter.Cli-<version>-<rid>.tar.gz     （linux / osx）
#   SHA256SUMS.txt
#
# 说明：
# - 单文件 + 自包含（含 libwebp 等原生库，运行时自解压），使用者无需安装 .NET。
# - 版本号取自 src/SharpImageConverter.csproj 的 <Version>，因此必须先改版本号再执行本脚本。
# - 需在本机或 CI 的**对应平台**上构建（win 上建 win-x64、macOS 上建 osx-arm64），
#   这样原生库按宿主 OS 也能正确选择；CI 侧对应 .github/workflows/release.yml。
# - 脚本放 tools/ 根目录而非 tools/release/：.gitignore 的 `[Rr]elease/` 会匹配任意层级的 release 目录。
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

  echo "==> 发布 $RID（版本 $VERSION）"
  dotnet publish Cli/SharpImageConverter.Cli.csproj \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:GenerateDocumentationFile=false \
    -o "$STAGE/$RID" >/dev/null

  BIN="$STAGE/$RID/SharpImageConverter.Cli$EXT"
  if [ ! -f "$BIN" ]; then
    echo "错误：未找到产物 $BIN" >&2
    exit 1
  fi

  NAME="SharpImageConverter.Cli-$VERSION-$RID"
  if [ "$EXT" = ".exe" ]; then
    cp "$BIN" "$OUT/$NAME.exe"
  else
    # shellcheck disable=SC2086  # TAR_EXTRA 需要按空格拆成多个参数
    tar $TAR_EXTRA -czf "$OUT/$NAME.tar.gz" -C "$STAGE/$RID" "SharpImageConverter.Cli"
  fi
  echo "    -> $(ls -1 "$OUT/$NAME"* | tr '\n' ' ')"
done

rm -rf "$STAGE"
( cd "$OUT" && sha256 SharpImageConverter.Cli-* >SHA256SUMS.txt && cat SHA256SUMS.txt )
