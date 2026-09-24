#!/usr/bin/env bash
#
# 跨分支测量 GIF 编解码耗时。
#
# 背景：GIF 的分阶段计时（--gif-debug / --gif-bench）是在 opt-gif 分支引入的，
# master 等分支没有这套代码，无法直接对比。
# 本脚本把 tools/gif-timing-master.patch 打到基准分支的临时 git 工作树上，
# 让两个分支跑完全相同的 --gif-bench 口径，从而量化优化前后的差异。
#
# 用法:
#   tools/gif-compare.sh <输入图片> [重复次数=15] [基准分支=master]
#
# 说明:
#   - 输入为 .gif 时会同时测量解码与编码；其他格式只测编码。
#   - 对比请以 median 为准：首轮 JIT 会明显拉高 max。
#   - 补丁针对 master @ 0bbf1f9；若基准分支的 GifDecoder 已有改动，git apply 可能冲突，需重新移植。

set -euo pipefail

INPUT=${1:-}
N=${2:-15}
BASE_BRANCH=${3:-master}

if [[ -z "$INPUT" ]]; then
    echo "用法: tools/gif-compare.sh <输入图片> [重复次数=15] [基准分支=master]"
    exit 1
fi

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PATCH="$REPO_ROOT/tools/gif-timing-master.patch"

if [[ ! -f "$PATCH" ]]; then
    echo "缺少补丁文件: $PATCH"
    exit 1
fi

if [[ ! -f "$INPUT" ]]; then
    echo "输入文件不存在: $INPUT"
    exit 1
fi

WORK=$(mktemp -d)
BASE_DIR="$WORK/base"
CURRENT_BRANCH=$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)

cleanup() {
    git -C "$REPO_ROOT" worktree remove --force "$BASE_DIR" >/dev/null 2>&1 || true
    rm -rf "$WORK" 2>/dev/null || echo "（提示：临时目录 $WORK 未能自动删除，可手动清理）"
}
trap cleanup EXIT

echo "== 准备基准分支工作树: $BASE_BRANCH =="
git -C "$REPO_ROOT" worktree add --detach "$BASE_DIR" "$BASE_BRANCH"

echo "== 应用计时补丁 =="
git -C "$BASE_DIR" apply "$PATCH"

echo "== 构建基准分支 ($BASE_BRANCH) =="
dotnet build "$BASE_DIR/Cli/SharpImageConverter.Cli.csproj" -c Release -v q --nologo

echo "== 构建当前分支 ($CURRENT_BRANCH) =="
dotnet build "$REPO_ROOT/Cli/SharpImageConverter.Cli.csproj" -c Release -v q --nologo

EXT="${INPUT##*.}"
cp "$INPUT" "$WORK/bench_input.$EXT"

echo
echo "################ 基准: $BASE_BRANCH ################"
dotnet run --project "$BASE_DIR/Cli/SharpImageConverter.Cli.csproj" -c Release --no-build -- \
    "$WORK"/bench_input.* --gif-bench "$N"

echo
echo "################ 当前: $CURRENT_BRANCH ################"
dotnet run --project "$REPO_ROOT/Cli/SharpImageConverter.Cli.csproj" -c Release --no-build -- \
    "$WORK"/bench_input.* --gif-bench "$N"
