$ErrorActionPreference = 'Stop'
$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
$h = @{ 'X-WotBTreader-Capability' = [string]$j.capability }

# gate state first
try {
    $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers $h -TimeoutSec 5
    Write-Host ('GATE=' + $s.verificationState + ' reason=' + $s.reasonCode + ' expires=' + $s.evidenceExpiresAtUtc)
} catch { Write-Host ('GATE_ERR: ' + $_.Exception.Message) }

function Try-Read([string]$Name, [object]$Body) {
    $json = $Body | ConvertTo-Json -Depth 12 -Compress
    try {
        $r = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/discover/read') -Method Post -ContentType 'application/json' -Body $json -Headers $h -TimeoutSec 90
        Write-Host ($Name + ': OK reads=' + $r.reads.Count + ' readOk=' + $r.readCount)
    } catch {
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        $detail = ''
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace([string]$_.ErrorDetails.Message)) {
            $detail = ([string]$_.ErrorDetails.Message)
        }
        $inner = $_.Exception.Message
        Write-Host ($Name + ': ERR status=' + $status + ' body=' + $detail + ' ex=' + $inner)
    }
}

# 1. 2-address minimal
Try-Read 'min2' @{ Addresses = @('0x10000000', '0x10000004'); ValueKind = 'Float'; ValueSize = 4 }
# 2. 500-address chunk with plain hex
$addrs = @()
for ($i = 0; $i -lt 500; $i++) { $addrs += ('0x{0:X}' -f (0x10000000 + 4 * $i)) }
Try-Read 'chunk500' @{ Addresses = $addrs; ValueKind = 'Float'; ValueSize = 4 }
# 3. uppercase 0X prefix
Try-Read 'upper0X' @{ Addresses = @('0X10000000', '0X10000004'); ValueKind = 'Float'; ValueSize = 4 }
# 4. no prefix
Try-Read 'noprefix' @{ Addresses = @('10000000', '10000004'); ValueKind = 'Float'; ValueSize = 4 }
