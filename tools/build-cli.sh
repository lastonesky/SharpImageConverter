#!/usr/bin/env bash
# 构建 CLI 的发布资产（用于 GitHub Release）。
#
# 用法：
#   tools/build-cli.sh [rid ...]     # 默认 win-x64
#   例如：tools/build-cli.sh win-x64 linux-x64 osx-arm64
#
# 产物（.artifacts/release/，已被 .gitignore 忽略）：
#   SharpImageConverter.Cli-<version>-win-x64.exe        单文件自包含
#   SharpImageConverter.Cli-<version>-linux-x64.tar.gz   单文件自包含（tar 内含单个可执行文件）
#   SharpImageConverter.Cli-<version>-osx-arm64.tar.gz   目录式（tar 内含同名目录）
#   SHA256SUMS.txt
#
# 两个平台差异的原因：
# - win / linux：单文件 + 自包含（含原生 libwebp，运行时自解压），使用者无需安装 .NET。
# - macOS：仓库里的 webp dylib 带着构建机的 @rpath（/Users/lastonesky/Project/libwebp/build）
#   与版本化依赖名（@rpath/libsharpyuv.0.dylib 等），别的机器上解析不了依赖。修复必须用
#   macOS 的 install_name_tool 在发布目录里改，因此 macOS 走**目录式**发布（原生库散落在
#   可执行文件旁），并在构建机上补 @loader_path rpath、按依赖名提供副本、再做 ad-hoc 签名。
#   ⇒ osx-arm64 必须在 macOS 上构建（CI 用 macos-15）。
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

# 修好 macOS 原生库的依赖解析（见文件头说明）
fix_macos_native_libs() {
  local dir="$1"
  cd "$dir"
  local f
  for f in libwebp.dylib libwebpdecoder.dylib libwebpmux.dylib libwebpdemux.dylib libsharpyuv.dylib; do
    [ -f "$f" ] || continue
    install_name_tool -add_rpath @loader_path "$f" 2>/dev/null || true
  done
  # 依赖声明用的是版本化名字（@rpath/libwebp.7.dylib 等），按这些名字提供副本
  cp -f libwebp.dylib        libwebp.7.dylib
  cp -f libsharpyuv.dylib    libsharpyuv.0.dylib
  cp -f libwebpdecoder.dylib libwebpdecoder.3.dylib
  cp -f libwebpmux.dylib     libwebpmux.3.dylib
  cp -f libwebpdemux.dylib   libwebpdemux.2.dylib
  # arm64 上所有可执行代码都必须有签名（ad-hoc 即可），否则 dlopen 会失败
  for f in ./*.dylib; do codesign --force --sign - "$f" >/dev/null 2>&1 || true; done
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
    echo "错误：osx-arm64 必须在 macOS 上构建（原生库依赖需要用 install_name_tool 修复）" >&2
    exit 1
  fi

  # 注意：变量后面紧邻中文时一律用 ${} 包裹 —— macOS 的 bash 3.2 在非 UTF-8 locale 下
  # 会把中文首字节当成变量名的一部分，报 "RID?: unbound variable"。
  echo "==> 发布 ${RID}（版本 ${VERSION}）"
  NAME="SharpImageConverter.Cli-$VERSION-$RID"

  if [ "$RID" = "osx-arm64" ]; then
    dotnet publish Cli/SharpImageConverter.Cli.csproj \
      -c Release -r "$RID" --self-contained true \
      -p:PublishSingleFile=false \
      -p:DebugType=none \
      -p:GenerateDocumentationFile=false \
      -o "$STAGE/$NAME" >/dev/null
    fix_macos_native_libs "$STAGE/$NAME"
    # shellcheck disable=SC2086
    tar $TAR_EXTRA -czf "$OUT/$NAME.tar.gz" -C "$STAGE" "$NAME"
  else
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
    cp "$BIN" "$OUT/$NAME$EXT"
  fi

  echo "    -> $(ls -1 "$OUT/$NAME"* | tr '\n' ' ')"
done

rm -rf "$STAGE"
( cd "$OUT" && sha256 SharpImageConverter.Cli-* >SHA256SUMS.txt && cat SHA256SUMS.txt )
