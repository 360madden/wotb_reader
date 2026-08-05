$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
function Convert-ToFloatHex([double]$Value) {
    $bytes = [BitConverter]::GetBytes([float]$Value)
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join '')
}
foreach ($tol in @(0.001, 0.5, 2.0, 8.0, 50.0, 200.0)) {
    $body = @{ FieldName=('probe-tol-' + $tol); FieldType='Float'; ExpectedValueHex=(Convert-ToFloatHex 29.56); FloatTolerance=$tol; MaxCandidates=10000; MinRegionSize=4096; Alignment=1 }
    try {
        $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover') -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10) -Headers $h -TimeoutSec 300
        Write-Host ('tol=' + $tol + ' candidates=' + $r.candidates.Count + ' truncated=' + $r.truncated)
    } catch {
        $detail = ''
        if ($null -ne $_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = [string]$_.ErrorDetails.Message }
        Write-Host ('tol=' + $tol + ' ERR ' + $detail)
    }
}
