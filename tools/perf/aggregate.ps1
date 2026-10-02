#Requires -Version 7
<#
    Aggregate several SicBench runs of the same build into one CSV by taking the
    per-test MEDIAN of the per-run medians (robust against one-off slow runs).

    usage: .\aggregate.ps1 -Out result.csv -In1 a.csv -In2 b.csv -In3 c.csv
#>
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [Parameter(Mandatory = $true)] [string] $In1,
    [string] $In2 = "",
    [string] $In3 = "",
    [string] $In4 = ""
)

$Inputs = @($In1, $In2, $In3, $In4) | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() }

function Key($r) { "$($r.category)|$($r.name)|$($r.format)|$($r.tag)" }

$byKey = @{}
foreach ($f in $Inputs) {
    foreach ($r in (Import-Csv $f)) {
        $k = Key $r
        if (-not $byKey.ContainsKey($k)) { $byKey[$k] = New-Object System.Collections.Generic.List[object] }
        $byKey[$k].Add($r)
    }
}

function Median([double[]] $v) {
    $s = $v | Sort-Object
    $n = $s.Count
    if ($n % 2 -eq 1) { return $s[[math]::Floor($n / 2)] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2.0
}

$agg = New-Object System.Collections.Generic.List[object]
foreach ($k in $byKey.Keys) {
    $rows = $byKey[$k]
    $medians = @($rows | ForEach-Object { [double]$_.median_ms })
    $mins    = @($rows | ForEach-Object { [double]$_.min_ms })
    $maxs    = @($rows | ForEach-Object { [double]$_.max_ms })
    $means   = @($rows | ForEach-Object { [double]$_.mean_ms })
    $m = Median $medians

    # keep the row whose median is closest to the aggregate median
    $pick = $rows | Sort-Object { [math]::Abs([double]$_.median_ms - $m) } | Select-Object -First 1

    $agg.Add([pscustomobject]@{
        category   = $pick.category
        name       = $pick.name
        format     = $pick.format
        tag        = $pick.tag
        width      = $pick.width
        height     = $pick.height
        pixels     = $pick.pixels
        iterations = $pick.iterations
        median_ms  = [math]::Round($m, 4)
        min_ms     = [math]::Round((Median $mins), 4)
        mean_ms    = [math]::Round((Median $means), 4)
        max_ms     = [math]::Round((Median $maxs), 4)
        mpx_s      = [math]::Round([double]$pick.pixels / 1000000.0 / ($m / 1000.0), 4)
        out_bytes  = $pick.out_bytes
        hash       = $pick.hash
        stable     = $pick.stable
        runs       = $rows.Count
        spread_pct = [math]::Round(((@($medians | Sort-Object)[-1] - @($medians | Sort-Object)[0]) / $m) * 100, 1)
    })
}

$agg | Sort-Object category, tag, format, name | Export-Csv -Path $Out -NoTypeInformation -Encoding utf8NoBOM
Write-Host "aggregated $($agg.Count) tests from $($Inputs.Count) runs -> $Out"
