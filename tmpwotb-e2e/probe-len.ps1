$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
function Try-Read([string]$Name, [string]$Addr) {
    try {
        $null = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body (@{ Addresses = @($Addr); ValueKind = 'Float'; ValueSize = 4 } | ConvertTo-Json -Compress) -Headers $h -TimeoutSec 30
        Write-Host ($Name + ' ' + $Addr + ' OK')
    } catch { Write-Host ($Name + ' ' + $Addr + ' ERR invalid_address') }
}
# 7-char high value vs 8-char same value
Try-Read 'a' '0xF000000'
Try-Read 'b' '0x0F000000'
# 9-char (over 8)
Try-Read 'c' '0x100000000'
# 6-char
Try-Read 'd' '0xF00000'
# unpadded scan-style vs padded
Try-Read 'e' '0x4520000'
Try-Read 'f' '0x04520000'
