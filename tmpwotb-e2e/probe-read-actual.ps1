$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
function Convert-ToFloatHex([double]$Value) {
    $bytes = [BitConverter]::GetBytes([float]$Value)
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join '')
}
# scan driver-shaped
$body = @{ FieldName='probe-read-actual'; FieldType='Float'; ExpectedValueHex=(Convert-ToFloatHex 0.0); FloatTolerance=600.35; MaxCandidates=10000; MinRegionSize=4096; Alignment=1 }
try {
    $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover') -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10) -Headers $h -TimeoutSec 300
    $addrs = @($r.candidates | ForEach-Object { [string]$_.absoluteAddress })
    Write-Host ('scan candidates=' + $addrs.Count)
    # chunk 500 driver-shaped read
    $chunk = $addrs | Select-Object -First 500
    $readBody = @{ Addresses = @($chunk); ValueKind = 'Float'; ValueSize = 4 }
    try {
        $rd = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body ($readBody | ConvertTo-Json -Depth 12 -Compress) -Headers $h -TimeoutSec 90
        Write-Host ('READ OK reads=' + $rd.reads.Count + ' readOk=' + $rd.readCount)
    } catch {
        $detail = ''
        if ($null -ne $_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = [string]$_.ErrorDetails.Message }
        Write-Host ('READ ERR: ' + $detail)
    }
    # bisect: single addresses in chunk
    $fail = @()
    foreach ($a in $chunk) {
        try {
            $null = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body (@{ Addresses = @($a); ValueKind = 'Float'; ValueSize = 4 } | ConvertTo-Json -Compress) -Headers $h -TimeoutSec 30
        } catch { $fail += $a }
    }
    Write-Host ('single-addr failures: ' + $fail.Count)
    if ($fail.Count) { $fail | Select-Object -First 6 | ForEach-Object { Write-Host ('  BAD: ' + $_) } }
} catch {
    $detail = ''
    if ($null -ne $_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = [string]$_.ErrorDetails.Message }
    Write-Host ('SCAN ERR: ' + $detail)
}
