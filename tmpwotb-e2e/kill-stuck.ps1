$me = $PID
$cutoff = (Get-Date).AddMinutes(-12)
$procs = @(Get-Process -Name powershell -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -ne $me -and $_.StartTime -gt $cutoff })
foreach ($p in $procs) {
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Write-Host ('killed ' + $p.Id) }
    catch { Write-Host ('skip ' + $p.Id) }
}
Write-Host ('done killed=' + $procs.Count)
