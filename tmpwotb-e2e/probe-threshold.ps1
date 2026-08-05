$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
function Try-Read([string]$Name, [string]$Addr) {
    try {
        $null = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body (@{ Addresses = @($Addr); ValueKind = 'Float'; ValueSize = 4 } | ConvertTo-Json -Compress) -Headers $h -TimeoutSec 30
        Write-Host ($Name + ' ' + $Addr + ' OK')
    } catch {
        $detail = ''
        if ($null -ne $_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = ([string]$_.ErrorDetails.Message -replace '\s+', ' ') }
        Write-Host ($Name + ' ' + $Addr + ' ERR ' + $detail)
    }
}
# bisect the min threshold around 0x10000000
Try-Read 'lo' '0x04520000'
Try-Read 'hi' '0x10000000'
Try-Read 'mid1' '0x08000000'
Try-Read 'mid2' '0x0F000000'
Try-Read 'mid3' '0x0FF00000'
Try-Read 'mid4' '0x1000000'
