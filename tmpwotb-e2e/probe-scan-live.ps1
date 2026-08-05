$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
# Float 1.0 scan (tiny tolerance, few candidates expected)
$body = @{
    FieldName = 'probe-live-1.0'
    FieldType = 'Float'
    ExpectedValueHex = ([BitConverter]::ToString([BitConverter]::GetBytes([float]1.0)) -replace '-', '')
    FloatTolerance = 0.001
    MaxCandidates = 50
    MinRegionSize = 4096
    Alignment = 1
}
try {
    $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover') -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10) -Headers $h -TimeoutSec 60
    Write-Host ('SCAN OK candidates=' + $r.candidates.Count)
    $r.candidates | Select-Object -First 8 | ForEach-Object {
        Write-Host ('  addr=' + $_.absoluteAddress + ' base=' + $_.baseAddress + ' disp=' + $_.baseDisplacement + ' v=' + $_.observedValueHex)
    }
} catch {
    $detail = ''
    if ($null -ne $_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = [string]$_.ErrorDetails.Message }
    Write-Host ('SCAN ERR: ' + $detail)
}
