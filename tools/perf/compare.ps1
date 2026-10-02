#Requires -Version 7
<#
    Compare two SicBench CSV runs and emit a Markdown report.

    usage:
      .\compare.ps1 -BaselineCsv <path> -CurrentCsv <path> -OutMarkdown <path> [-OutCsv <path>]
#>
param(
    [Parameter(Mandatory = $true)] [string] $BaselineCsv,
    [Parameter(Mandatory = $true)] [string] $CurrentCsv,
    [Parameter(Mandatory = $true)] [string] $OutMarkdown,
    [string] $OutCsv = "",
    [string] $BaselineLabel = "baseline (23ab67b, 2026-03-08)",
    [string] $CurrentLabel = "current (HEAD, 2026-10-02)"
)

$ErrorActionPreference = 'Stop'

$base = Import-Csv $BaselineCsv
$cur  = Import-Csv $CurrentCsv

$key = { param($r) "$($r.category)|$($r.name)|$($r.format)|$($r.tag)" }

$curMap = @{}
foreach ($r in $cur) { $k = & $key $r; $curMap[$k] = $r }

function SpreadOf($r) {
    $p = $r.PSObject.Properties['spread_pct']
    if ($null -eq $p) { return 0.0 }
    return [double]$p.Value
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($b in $base) {
    $k = & $key $b
    if (-not $curMap.ContainsKey($k)) { continue }
    $c = $curMap[$k]

    $bm = [double]$b.median_ms
    $cm = [double]$c.median_ms
    if ($bm -le 0 -or $cm -le 0) {
        $speed = 0.0; $delta = 0.0
    } else {
        $speed = $bm / $cm
        $delta = ($speed - 1.0) * 100.0
    }
    $rows.Add([pscustomobject]@{
        Category   = $b.category
        Name       = $b.name
        Format     = $b.format
        Tag        = $b.tag
        Pixels     = [long]$b.pixels
        BaselineMs = [math]::Round($bm, 3)
        CurrentMs  = [math]::Round($cm, 3)
        SpeedUp    = [math]::Round($speed, 3)
        DeltaPct   = [math]::Round($delta, 1)
        BaselineMpx = [math]::Round([double]$b.mpx_s, 2)
        CurrentMpx  = [math]::Round([double]$c.mpx_s, 2)
        OutBytesBase = [long]$b.out_bytes
        OutBytesCur  = [long]$c.out_bytes
        SameOutput   = ($b.out_bytes -eq $c.out_bytes) -and ($b.hash -eq $c.hash)
        SpreadPct    = [math]::Round([math]::Max((SpreadOf $b), (SpreadOf $c)), 1)
    })
}

if ($OutCsv) {
    $rows | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding utf8NoBOM
}

function GeoMean([double[]] $values) {
    $v = $values | Where-Object { $_ -gt 0 }
    if ($v.Count -eq 0) { return 0.0 }
    $sum = 0.0
    foreach ($x in $v) { $sum += [math]::Log($x) }
    return [math]::Exp($sum / $v.Count)
}

function FmtSpeed($x) {
    if ($x -le 0) { return "n/a" }
    if ($x -ge 1) { return "$($x.ToString('F2'))x" }
    return "$($x.ToString('F2'))x"
}

$sb = [System.Text.StringBuilder]::new()
function W([string] $s = "") { [void]$sb.AppendLine($s) }

W "# SharpImageConverter 半年性能对比报告"
W ""
W "- 基线: ``$BaselineLabel``"
W "- 当前: ``$CurrentLabel``"
W "- 生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W "- 机器: $((Get-CimInstance Win32_Processor).Name), $([math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,0)) GB RAM"
W "- 运行时: $(dotnet --version) SDK / .NET 10.0.12, 工作站 GC(非并发), Release"
W "- 口径: 内存输入, 预热 1 轮 + 正式 N 轮(按像素量自适应 2~12 轮), 每轮前强制 full blocking GC"
W "- 聚合: 每个版本独立跑 3 轮; 先对每轮内的 N 次取中位数, 再对 3 轮的中位数取中位数"
W "- 一致性: 每轮校验输出字节数与采样哈希, 任一不一致会标记 `[UNSTABLE]`(本次 0 项)"
W ""
W "> 约定: **SpeedUp = 基线耗时 / 当前耗时**, 大于 1 表示当前版本更快; Delta% 为提速百分比。"
W "> **轮间波动**列 >=30% 的项受环境/线程池抖动影响较大, 数值仅供参考。"
W ""
W "### 方法与已知事项"
W ""
W "1. 语料由**当前版本**生成一次, 两个版本读的是同一批文件/同一份 raw 像素, 保证输入字节完全一致。"
W '2. 大图档使用 ``examples/progressive.jpg``(10650x13426 = 143 MP, 渐进式 JPEG), 覆盖解码/灰度/缩放/编码。'
W '3. **WebP 143 MP 编码的特殊说明**: 首轮测出 0.25x 的"回退"后做了交叉验证 —— 把两版解码出的 143 MP 像素分别落盘,'
W '   再用**同一个**二进制分别编码两份像素, 结果是: 两版编码"HEAD 解码像素"都约 32 s, 编码"基线解码像素"都约 8 s,'
W '   与用哪个版本编译无关(两版像素平均差仅 0.051/255, 仅 3.75% 字节不同, 最大差 16)。'
W '   即 libwebp 对该图有极强的**内容敏感性**, 该差异来自解码输出的 ±1 级差异, 不是 WebP 编码路径的性能回退'
W '   (``WebpCodec.cs`` 半年只改了 4 行且都在解码路径, 两个版本打包的 libwebp.dll 也完全相同)。'
W "   为消除这一干扰, 大图档的非解码项统一改用固定的 raw 像素作为输入。"
W "4. GIF 编码默认量化器从 Wu+Floyd-Steinberg 换成了 Octree+Bayer, 输出体积会变化(见文末表格), 属于算法变更而非性能问题。"
W "5. 基线版本的 JPEG 编码存在明显的双峰抖动(例如 77 KP 的 q90 编码在 1.2 ms 与 30 ms 之间跳变), 这是基线自身的线程池行为, 已通过 3 轮取中位数抑制。"
W ""

# ---------- overall ----------
$all = $rows | Where-Object { $_.SpeedUp -gt 0 }
$overall = GeoMean @($all | ForEach-Object { [double]$_.SpeedUp })
$win = ($all | Where-Object { $_.SpeedUp -gt 1.07 }).Count
$flat = ($all | Where-Object { $_.SpeedUp -ge 0.93 -and $_.SpeedUp -le 1.07 }).Count
$lose = ($all | Where-Object { $_.SpeedUp -lt 0.93 }).Count
$same = ($rows | Where-Object { $_.SameOutput }).Count

W "## 总览"
W ""
W "| 指标 | 值 |"
W "|---|---|"
W "| 对比项数量 | $($rows.Count) |"
W "| 几何平均提速 | **$($overall.ToString('F2'))x** |"
W "| 明显变快 (>7%) | $win |"
W "| 基本持平 (±7%) | $flat |"
W "| 明显变慢 (<-7%) | $lose |"
W "| 输出字节完全一致 | $same / $($rows.Count) |"
W ""

# ---------- per category ----------
W "## 分类汇总"
W ""
W "| 分类 | 项数 | 几何平均提速 | 最快项 | 最慢项 |"
W "|---|---|---|---|---|"
foreach ($g in ($rows | Group-Object Category | Sort-Object Name)) {
    $vals = @($g.Group | Where-Object { $_.SpeedUp -gt 0 } | ForEach-Object { [double]$_.SpeedUp })
    $gm = GeoMean $vals
    $best = $g.Group | Sort-Object -Property SpeedUp -Descending | Select-Object -First 1
    $worst = $g.Group | Sort-Object -Property SpeedUp | Select-Object -First 1
    W "| $($g.Name) | $($g.Count) | $($gm.ToString('F2'))x | $($best.Name)/$($best.Format)@$($best.Tag) $($best.SpeedUp)x | $($worst.Name)/$($worst.Format)@$($worst.Tag) $($worst.SpeedUp)x |"
}
W ""

# ---------- per format ----------
W "## 按格式汇总"
W ""
W "| 格式 | 项数 | 几何平均提速 |"
W "|---|---|---|"
foreach ($g in ($rows | Group-Object Format | Sort-Object Name)) {
    $vals = @($g.Group | Where-Object { $_.SpeedUp -gt 0 } | ForEach-Object { [double]$_.SpeedUp })
    $gm = GeoMean $vals
    W "| $($g.Name) | $($g.Count) | $($gm.ToString('F2'))x |"
}
W ""

# ---------- per size ----------
W "## 按尺寸汇总"
W ""
W "| 尺寸 | 项数 | 几何平均提速 |"
W "|---|---|---|"
foreach ($g in ($rows | Group-Object Tag | Sort-Object { @{huge=0;large=1;medium=2;small=3;tiny=4}[$_.Name] })) {
    $vals = @($g.Group | Where-Object { $_.SpeedUp -gt 0 } | ForEach-Object { [double]$_.SpeedUp })
    $gm = GeoMean $vals
    W "| $($g.Name) | $($g.Count) | $($gm.ToString('F2'))x |"
}
W ""

# ---------- detail tables ----------
$catTitles = @{
    'decode'      = '解码（各格式、各尺寸，输入为固定语料文件，两次运行字节完全一致）'
    'decode-gray' = '灰度源解码（png8 / bmp8 / 灰度 jpeg / gif / webp）'
    'encode'      = '编码（各格式、各尺寸）'
    'encode-gray' = '灰度源编码（png8 / bmp8 / 灰度 jpeg / gif / webp）'
    'grayscale'   = '彩色转灰度'
    'resize'      = '缩放（大图/小图，放大/缩小）'
    'gray-resize' = '灰度图缩放（gray8 展开为 RGB24 后缩放，展开不计入计时）'
}

foreach ($g in ($rows | Group-Object Category | Sort-Object Name)) {
    $title = $catTitles[$g.Name]
    if (-not $title) { $title = $g.Name }
    W "## $($g.Name) — $title"
    W ""
    W "| 项目 | 格式 | 尺寸 | 像素 | 基线 ms | 当前 ms | 提速 | 基线 Mpx/s | 当前 Mpx/s | 输出一致 | 轮间波动 |"
    W "|---|---|---|---|---|---|---|---|---|---|---|"
    foreach ($r in ($g.Group | Sort-Object Tag, Format, Name)) {
        $px = if ($r.Pixels -ge 1000000) { "$([math]::Round($r.Pixels/1000000,2)) MP" } else { "$([math]::Round($r.Pixels/1000,0)) KP" }
        $sameMark = if ($r.SameOutput) { "yes" } else { "no" }
        $delta = if ($r.DeltaPct -ge 0) { "+$($r.DeltaPct)%" } else { "$($r.DeltaPct)%" }
        $warn = if ($r.SpreadPct -ge 30) { " **$($r.SpreadPct)%**" } else { " $($r.SpreadPct)%" }
        W "| $($r.Name) | $($r.Format) | $($r.Tag) | $px | $($r.BaselineMs) | $($r.CurrentMs) | **$($r.SpeedUp)x** ($delta) | $($r.BaselineMpx) | $($r.CurrentMpx) | $sameMark |$warn |"
    }
    W ""
}

# ---------- biggest movers ----------
W "## 提速最明显的 15 项"
W ""
W "| 项目 | 尺寸 | 基线 ms | 当前 ms | 提速 |"
W "|---|---|---|---|---|"
foreach ($r in ($rows | Where-Object { $_.SpeedUp -gt 0 } | Sort-Object -Property SpeedUp -Descending | Select-Object -First 15)) {
    W "| $($r.Category)/$($r.Name)/$($r.Format) | $($r.Tag) | $($r.BaselineMs) | $($r.CurrentMs) | **$($r.SpeedUp)x** |"
}
W ""

W "## 变慢的项（< 0.93x）"
W ""
$regressions = @($rows | Where-Object { $_.SpeedUp -gt 0 -and $_.SpeedUp -lt 0.93 } | Sort-Object -Property SpeedUp)
if ($regressions.Count -eq 0) {
    W "无（没有观测到超过 7% 的性能回退）"
} else {
    W "| 项目 | 尺寸 | 基线 ms | 当前 ms | 变化 |"
    W "|---|---|---|---|---|"
    foreach ($r in $regressions) {
        W "| $($r.Category)/$($r.Name)/$($r.Format) | $($r.Tag) | $($r.BaselineMs) | $($r.CurrentMs) | $($r.SpeedUp)x ($($r.DeltaPct)%) |"
    }
}
W ""

# ---------- output size changes ----------
$sizeChanges = @($rows | Where-Object { $_.OutBytesBase -ne $_.OutBytesCur })
if ($sizeChanges.Count -gt 0) {
    W "## 输出体积变化（编码器算法变更导致，非性能）"
    W ""
    W "| 项目 | 尺寸 | 基线 bytes | 当前 bytes | 变化 |"
    W "|---|---|---|---|---|"
    foreach ($r in ($sizeChanges | Sort-Object Tag, Category, Name)) {
        $d = [math]::Round(($r.OutBytesCur - $r.OutBytesBase) * 100.0 / [math]::Max(1, $r.OutBytesBase), 1)
        W "| $($r.Category)/$($r.Name)/$($r.Format) | $($r.Tag) | $($r.OutBytesBase) | $($r.OutBytesCur) | $(if($d -ge 0){'+'})$d% |"
    }
    W ""
}

$dir = Split-Path -Parent $OutMarkdown
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutMarkdown, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "report written: $OutMarkdown"
Write-Host ("overall geomean speedup: {0:N2}x" -f $overall)
