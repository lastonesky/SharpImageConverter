#Requires -Version 7
<#
    Full A/B performance run for SharpImageConverter.

    Builds an isolated benchmark harness twice - once against the current src/ and once
    against a git worktree of the baseline commit - runs the exact same test matrix on
    both and produces a Markdown comparison report.

    usage:
      .\run-bench.ps1 [-BaselineCommit <sha>] [-IterScale <double>] [-Rounds <int>] [-Filter <substr>]
                      [-SkipGen] [-NoHugeEncode]
#>
param(
    [string] $BaselineCommit = "23ab67b6a6cff21f0f62bef2286b85a35b461d75",
    [double] $IterScale = 1.0,
    [int]    $Rounds = 1,
    [string] $Filter = "",
    [switch] $SkipGen,
    [switch] $NoHugeEncode,
    [switch] $Rebuild
)

$ErrorActionPreference = 'Stop'

$repo   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # tools/perf -> repo root
$work   = Join-Path $repo '.perf'
$wt     = Join-Path $work 'baseline'
$corpus = Join-Path $work 'corpus'
$binCur = Join-Path $work 'bin/current'
$binBase= Join-Path $work 'bin/baseline'
$outDir = Join-Path $work 'results'
$bench  = Join-Path $PSScriptRoot 'SicBench/SicBench.csproj'
$examples = Join-Path $repo 'examples'

New-Item -ItemType Directory -Force -Path $work, $outDir | Out-Null

function Invoke-Step([string] $msg, [scriptblock] $body) {
    Write-Host "`n===== $msg =====" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) { throw "step failed: $msg (exit $LASTEXITCODE)" }
}

# 1. baseline worktree -------------------------------------------------------
if (-not (Test-Path (Join-Path $wt 'src/SharpImageConverter.csproj'))) {
    Invoke-Step "creating baseline worktree @ $BaselineCommit" {
        git -C $repo worktree add --detach $wt $BaselineCommit
    }
} else {
    Write-Host "baseline worktree already present at $wt"
}

# 2. build both --------------------------------------------------------------
Invoke-Step "building harness against CURRENT src/" {
    dotnet build $bench -c Release -v q --nologo -o $binCur
}
Invoke-Step "building harness against BASELINE worktree" {
    dotnet build $bench -c Release -v q --nologo -p:SicProject="$wt\src\SharpImageConverter.csproj" -o $binBase
}

# 3. corpus ------------------------------------------------------------------
if (-not $SkipGen) {
    Invoke-Step "generating test corpus (built with CURRENT version, shared by both)" {
        & (Join-Path $binCur 'SicBench.exe') --mode gen --corpus $corpus --examples $examples
    }
} else {
    Write-Host "skipping corpus generation"
}

# 4. run ---------------------------------------------------------------------
$hugeFlag = if ($NoHugeEncode) { @('--no-huge-encode') } else { @() }
$filterArg = if ($Filter) { @('--filter', $Filter) } else { @() }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$baseCsvs = @()
$curCsvs  = @()

for ($round = 1; $round -le $Rounds; $round++) {
    $suffix = if ($Rounds -gt 1) { "-r$round" } else { "" }

    Invoke-Step "running BASELINE (round $round)" {
        & (Join-Path $binBase 'SicBench.exe') --mode bench --corpus $corpus --tag "baseline-$BaselineCommit" `
            --out (Join-Path $outDir "baseline$suffix-$stamp.csv") --iter-scale $IterScale @hugeFlag @filterArg
    }
    $baseCsvs += (Join-Path $outDir "baseline$suffix-$stamp.csv")

    Invoke-Step "running CURRENT (round $round)" {
        & (Join-Path $binCur 'SicBench.exe') --mode bench --corpus $corpus --tag "current-HEAD" `
            --out (Join-Path $outDir "current$suffix-$stamp.csv") --iter-scale $IterScale @hugeFlag @filterArg
    }
    $curCsvs += (Join-Path $outDir "current$suffix-$stamp.csv")
}

# 5. if multiple rounds, keep the best (min median) per test -----------------
function Select-Best([string[]] $csvs, [string] $dest) {
    if ($csvs.Count -eq 1) { Copy-Item $csvs[0] $dest -Force; return }
    $best = @{}
    foreach ($f in $csvs) {
        foreach ($r in (Import-Csv $f)) {
            $k = "$($r.category)|$($r.name)|$($r.format)|$($r.tag)"
            $m = [double]$r.median_ms
            if (-not $best.ContainsKey($k) -or $m -lt [double]$best[$k].median_ms) { $best[$k] = $r }
        }
    }
    $best.Values | Export-Csv $dest -NoTypeInformation -Encoding utf8NoBOM
}

$baseFinal = Join-Path $outDir "baseline-final-$stamp.csv"
$curFinal  = Join-Path $outDir "current-final-$stamp.csv"
Select-Best $baseCsvs $baseFinal
Select-Best $curCsvs  $curFinal

# 6. compare -----------------------------------------------------------------
$report = Join-Path $repo "docs/PerfCompare-$stamp.md"
Invoke-Step "generating comparison report" {
    & (Join-Path $PSScriptRoot 'compare.ps1') -BaselineCsv $baseFinal -CurrentCsv $curFinal `
        -OutMarkdown $report -OutCsv (Join-Path $outDir "compare-$stamp.csv")
}

Write-Host "`nDone. Report: $report" -ForegroundColor Green
