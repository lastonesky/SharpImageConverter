#Requires -Version 7
<#
    Run-to-run stability check: compare the per-round speed-up of two rounds.

    usage: .\variance.ps1 -R1B <csv> -R2B <csv> -R1C <csv> -R2C <csv>
#>
param(
    [Parameter(Mandatory = $true)] [string] $R1B,
    [Parameter(Mandatory = $true)] [string] $R2B,
    [Parameter(Mandatory = $true)] [string] $R1C,
    [Parameter(Mandatory = $true)] [string] $R2C
)

function Key($r) { "$($r.category)|$($r.name)|$($r.format)|$($r.tag)" }
function Map($csv) { $m = @{}; foreach ($r in (Import-Csv $csv)) { $m[(Key $r)] = $r }; return $m }

$b1 = Map $R1B; $b2 = Map $R2B; $c1 = Map $R1C; $c2 = Map $R2C

$rows = @()
foreach ($k in $b1.Keys) {
    if (-not ($b2.ContainsKey($k) -and $c1.ContainsKey($k) -and $c2.ContainsKey($k))) { continue }
    $x1 = [double]$b1[$k].median_ms; $x2 = [double]$b2[$k].median_ms
    $y1 = [double]$c1[$k].median_ms; $y2 = [double]$c2[$k].median_ms
    $s1 = if ($x1 -gt 0 -and $y1 -gt 0) { $x1 / $y1 } else { 0.0 }
    $s2 = if ($x2 -gt 0 -and $y2 -gt 0) { $x2 / $y2 } else { 0.0 }
    $spread = if ($s1 -gt 0 -and $s2 -gt 0 -and ($s1 + $s2) -gt 0) { [math]::Abs($s1 - $s2) / (($s1 + $s2) / 2) * 100 } else { 0.0 }
    $rows += [pscustomobject]@{
        Test   = $k
        B_R1   = [math]::Round($x1, 3); B_R2 = [math]::Round($x2, 3)
        C_R1   = [math]::Round($y1, 3); C_R2 = [math]::Round($y2, 3)
        S_R1   = [math]::Round($s1, 2); S_R2 = [math]::Round($s2, 2)
        Spread = [math]::Round($spread, 1)
    }
}

Write-Host "tests compared: $($rows.Count)"
Write-Host "`n--- worst 15 run-to-run spreads ---"
$rows | Sort-Object Spread -Descending | Select-Object -First 15 | Format-Table -AutoSize

$sorted = @($rows | ForEach-Object { $_.Spread } | Sort-Object)
Write-Host ("spread: median={0}%  p90={1}%  max={2}%" -f `
    [math]::Round($sorted[[math]::Floor($sorted.Count * 0.50)], 1),
    [math]::Round($sorted[[math]::Floor($sorted.Count * 0.90)], 1),
    [math]::Round($sorted[$sorted.Count - 1], 1))

Write-Host "`n--- sub-millisecond tests (most noise-prone) ---"
$rows | Where-Object { ($_.B_R1 -lt 1 -and $_.B_R2 -lt 1) -or ($_.C_R1 -lt 1 -and $_.C_R2 -lt 1) } |
    Sort-Object Spread -Descending | Select-Object -First 12 | Format-Table -AutoSize
