$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState) } catch { Write-Host 'GATE_ERR' }
function Try-Read([string]$Name, [object]$Body) {
    $json = $Body | ConvertTo-Json -Depth 12 -Compress
    try {
        $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body $json -Headers $h -TimeoutSec 30
        Write-Host ($Name + ': OK reads=' + $r.reads.Count)
    } catch {
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response -and $_.Exception.Response.StatusCode) { $status = [int]$_.Exception.Response.StatusCode }
        elseif ($_.Exception.PSObject.Properties['StatusCode']) { $status = [int]$_.Exception.StatusCode }
        $detail = ''
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace([string]$_.ErrorDetails.Message)) { $detail = ([string]$_.ErrorDetails.Message) }
        Write-Host ($Name + ': status=' + $status + ' body=' + $detail)
    }
}
# 1. valid-format dummy (gate-dead expected)
Try-Read 'dummy' @{ Addresses = @('0x10000000'); ValueKind = 'Float'; ValueSize = 4 }
# 2. high-bit 16-digit (overflow long -> invalid_address expected if validation catches it)
Try-Read 'highbit16' @{ Addresses = @('0x8000000000000000'); ValueKind = 'Float'; ValueSize = 4 }
# 3. 17 hex digits (over length cap -> invalid_address)
Try-Read 'long17' @{ Addresses = @('0x1FFFFFFFFFFFFFFFF'); ValueKind = 'Float'; ValueSize = 4 }
# 4. exactly long.MaxValue (should pass validation, then gate-dead)
Try-Read 'maxlong' @{ Addresses = @('0x7FFFFFFFFFFFFFFF'); ValueKind = 'Float'; ValueSize = 4 }
