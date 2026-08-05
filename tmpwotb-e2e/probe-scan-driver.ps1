$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
# Driver-exact scan: tolerance 600.35 (matches float 0.0 within 600.35 -> everything)
function Convert-ToFloatHex([double]$Value) {
    $bytes = [BitConverter]::GetBytes([float]$Value)
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join '')
}
$body = @{
    FieldName = 'probe-driver-shape'
    FieldType = 'Float'
    ExpectedValueHex = (Convert-ToFloatHex 0.0)
    FloatTolerance = 600.35
    MaxCandidates = 10000
    MinRegionSize = 4096
    Alignment = 1
}
try {
    $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover') -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 10) -Headers $h -TimeoutSec 300
    Write-Host ('SCAN OK candidates=' + $r.candidates.Count)
    $addrLen = @{}
    $zeros = 0; $nonhex = 0; $long16 = 0
    foreach ($c in $r.candidates) {
        $a = [string]$c.absoluteAddress
        $n = $a.Length
        if (-not ($addrLen.ContainsKey($n))) { $addrLen[$n] = 0 }
        $addrLen[$n] += 1
        $hexPart = if ($a.StartsWith('0x')) { $a.Substring(2) } else { $a }
        if ($hexPart -notmatch '^[0-9a-fA-F]+$') { $nonhex++ }
        if ($hexPart.Length -gt 16) { $long16++ }
        $v = [Convert]::ToInt64($hexPart, 16)
        if ($v -eq 0) { $zeros++ }
    }
    Write-Host ('addr-length histogram: ' + (($addrLen.GetEnumerator() | Sort-Object Key | ForEach-Object { $_.Key.ToString() + ':' + $_.Value }) -join ' '))
    Write-Host ('zeros=' + $zeros + ' nonhex=' + $nonhex + ' over16=' + $long16)
    $r.candidates | Select-Object -First 3 | ForEach-Object { Write-Host ('  sample addr=' + $_.absoluteAddress) }
} catch {
    $detail = ''
    if ($null -ne $_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = [string]$_.ErrorDetails.Message }
    Write-Host ('SCAN ERR: ' + $detail)
}
