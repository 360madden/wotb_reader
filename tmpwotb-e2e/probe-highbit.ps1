$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
function Try-Read([string]$Name, [object]$Body) {
    $json = $Body | ConvertTo-Json -Depth 12 -Compress
    try {
        $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body $json -Headers $h -TimeoutSec 30
        Write-Host ($Name + ': OK reads=' + $r.reads.Count + ' readOk=' + $r.readCount)
    } catch {
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response -and $_.Exception.Response.StatusCode) { $status = [int]$_.Exception.Response.StatusCode }
        $detail = ''
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace([string]$_.ErrorDetails.Message)) { $detail = ([string]$_.ErrorDetails.Message) }
        Write-Host ($Name + ': ERR status=' + $status + ' body=' + $detail)
    }
}
# high-bit 64-bit address (would overflow signed long)
Try-Read 'highbit' @{ Addresses = @('0x8000000000000000', '0x7FFFFFFFFFFFFFFF', '0x10000000'); ValueKind = 'Float'; ValueSize = 4 }
# 16-digit address in normal heap range
Try-Read 'norm64' @{ Addresses = @('0x7FF6ABCD00000000', '0x7FF6ABCD00000004'); ValueKind = 'Float'; ValueSize = 4 }
