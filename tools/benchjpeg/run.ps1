$dll = "E:\src\SharpImageConverter\tools\benchjpeg\bin\Release\net10.0\benchjpeg.dll"
$sizes = @(128, 512, 1000, 2000)
$outFile = "E:\src\SharpImageConverter\tools\benchjpeg\results.txt"
"=== JPEG Encode Benchmark (quality=75, warmup=10, iters=20) ===" | Out-File $outFile -Encoding utf8

foreach ($dop in @(1, 2, 3, 4)) {
    "`n--- DOTNET_PROCESSOR_COUNT=$dop ---" | Out-File $outFile -Append -Encoding utf8
    foreach ($sz in $sizes) {
        $env:DOTNET_PROCESSOR_COUNT = "$dop"
        $result = & dotnet $dll $sz 20 10 2>&1
        $result | Out-File $outFile -Append -Encoding utf8
        Write-Host "p$dop size=$sz done"
    }
}
