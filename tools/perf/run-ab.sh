#!/usr/bin/env bash
# SharpImageConverter 跨架构 A/B 基准（macOS / Linux 版，替代 run-bench.ps1）
#
# 与 run-bench.ps1 的差别：不依赖 PowerShell。其余口径保持一致——
#   1) 基线取 git worktree 上的 src/（同一台机器、同一份语料）；
#   2) 两侧交替运行多轮，降低跨进程漂移；
#   3) 每项取多轮中的**最小中位数**作为最终值（与 Select-Best 一致）。
#
# 用法：
#   tools/perf/run-ab.sh [基线提交] [轮数] [迭代缩放] [过滤器]
# 例：
#   tools/perf/run-ab.sh 4fd37f3 3 1.0
#   tools/perf/run-ab.sh 4fd37f3 2 1.0 resize      # 只跑名字含 resize 的项，用于迭代验证
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BASE_COMMIT="${1:-4fd37f3}"
ROUNDS="${2:-3}"
ITER_SCALE="${3:-1.0}"
FILTER="${4:-}"
# SicBench 的 Filter 为空串时匹配全部条目，因此这里总是显式传入，
# 避免在 bash 3.2 + set -u 下展开空数组触发 unbound variable。

WORK="$REPO/.perf"
WT="$WORK/baseline"
CORPUS="$WORK/corpus"
BIN_CUR="$WORK/bin/current"
BIN_BASE="$WORK/bin/baseline"
OUT="$WORK/results"
BENCH="$REPO/tools/perf/SicBench/SicBench.csproj"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

mkdir -p "$OUT"

if [ ! -f "$WT/src/SharpImageConverter.csproj" ]; then
  echo "==> 创建基线 worktree @ $BASE_COMMIT"
  git -C "$REPO" worktree add --detach "$WT" "$BASE_COMMIT"
fi

echo "==> 构建 current harness"
dotnet build "$BENCH" -c Release -v q --nologo -o "$BIN_CUR" >/dev/null
echo "==> 构建 baseline harness"
dotnet build "$BENCH" -c Release -v q --nologo \
  -p:SicProject="$WT/src/SharpImageConverter.csproj" -o "$BIN_BASE" >/dev/null

if [ ! -f "$CORPUS/manifest.csv" ]; then
  echo "==> 生成语料"
  "$BIN_CUR/SicBench" --mode gen --corpus "$CORPUS" --examples "$REPO/examples"
fi

STAMP="$(date +%Y%m%d-%H%M%S)"

best() {  # best <tag> <glob-prefix> <dest>
  local tag="$1" prefix="$2" dest="$3"
  python3 - "$OUT" "$prefix" "$STAMP" "$dest" <<'PY'
import csv, glob, os, sys
from statistics import median
out, prefix, stamp, dest = sys.argv[1:5]
# 只收本轮 stamp 的轮次文件。若用 f"{prefix}*.csv"，上一轮的 best() 产物
# （current-final-<旧stamp>.csv）也会被 glob 命中，于是把两个不同构建的
# 结果混进同一张表——曾因此让"新构建变慢的行"被旧构建的行顶掉，
# 表现为产物哈希时对时不对（DIFF/OK 随机）。这里用 -r*-<stamp> 精确锁定。
files = sorted(glob.glob(os.path.join(out, f"{prefix}-r*-{stamp}.csv")))
if not files:
    sys.exit(f"no csv for {prefix}-r*-{stamp}")

# 取「跨轮中位数」而不是「最小中位数」。
# 原因：Apple Silicon 上整进程的调度状态会在 P 核/E 核之间漂移，实测同一个
# **未被改动** 的 ResizeArea 行在某轮里 10 个样本一致地快 2.1 倍
# （baseline-r2 0.7026 ms vs 其余三轮 ~1.49 ms）。若按最小中位数挑选，
# 就会拿"快进程的基线"去比"普通进程的优化后"，凭空产生 0.475x 的假回归。
# 跨轮中位数对单个异常进程免疫，两侧同时受益，比较仍然是公平的。
groups = {}
for f in files:
    for r in csv.DictReader(open(f, encoding='utf-8')):
        if float(r['median_ms']) <= 0:
            continue
        k = (r['category'], r['name'], r['format'], r['tag'])
        groups.setdefault(k, []).append(r)

chosen = {}
for k, rows in groups.items():
    target = median(float(r['median_ms']) for r in rows)
    chosen[k] = min(rows, key=lambda r: abs(float(r['median_ms']) - target))

with open(dest, 'w', newline='', encoding='utf-8') as fh:
    w = csv.DictWriter(fh, fieldnames=list(next(iter(chosen.values())).keys()))
    w.writeheader()
    for r in chosen.values(): w.writerow(r)
print(f"  {dest}: {len(chosen)} 项（跨 {len(files)} 轮取中位数）")
PY
}

for r in $(seq 1 "$ROUNDS"); do
  echo "==> 第 $r/$ROUNDS 轮   filter='$FILTER'"
  "$BIN_BASE/SicBench" --mode bench --corpus "$CORPUS" --tag "baseline-$BASE_COMMIT" \
      --filter "$FILTER" \
      --out "$OUT/baseline-r$r-$STAMP.csv" --iter-scale "$ITER_SCALE" > "$OUT/baseline-r$r-$STAMP.log" 2>&1
  "$BIN_CUR/SicBench" --mode bench --corpus "$CORPUS" --tag "current" \
      --filter "$FILTER" \
      --out "$OUT/current-r$r-$STAMP.csv" --iter-scale "$ITER_SCALE" > "$OUT/current-r$r-$STAMP.log" 2>&1
done

echo "==> 取各轮最小中位数"
best baseline baseline "$OUT/baseline-final-$STAMP.csv"
best current  current  "$OUT/current-final-$STAMP.csv"

echo "==> 对比"
python3 "$REPO/tools/perf/compare_csv.py" \
  "$OUT/baseline-final-$STAMP.csv" "$OUT/current-final-$STAMP.csv" \
  基线 ARM优化后 > "$OUT/report-$STAMP.md"
cat "$OUT/report-$STAMP.md"
echo
echo "报告: $OUT/report-$STAMP.md"
