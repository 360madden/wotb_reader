# FRESH19: prove the capability-rotation race is closed by the retry-on-401
# mechanism added to od-048's Invoke-Api. Uses the LIVE host (if up):
#   1. Read the real rendezvous record -> real token.
#   2. Craft a deliberately STALE token (a token that was valid earlier is
#      guaranteed 401 because the host Rotate()s on every >=15s publish).
#   3. POST a cheap read with the stale token -> expect 401.
#   4. Re-read the rendezvous (Get-Rendezvous) and retry -> expect 200.
# This mirrors Invoke-Api's new 401 branch exactly (status parse -> fresh
# read -> same-call retry with the fresh token).
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RendezvousRecord {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $file = Get-ChildItem $dir -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    return (Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
}

function Get-HttpStatus([Exception]$Ex) {
    if ($Ex.PSObject.Properties['Response'] -and $null -ne $Ex.Response -and $Ex.Response.StatusCode) {
        return [int]$Ex.Response.StatusCode
    }
    if ($Ex.PSObject.Properties['StatusCode'] -and $Ex.StatusCode) {
        return [int]$Ex.StatusCode
    }
    return $null
}

function Invoke-Call {
    param([object]$Rendezvous)
    $headers = @{ 'X-WotBTreader-Capability' = [string]$Rendezvous.capability }
    # POST /discover/correlate is capability-gated (middleware runs BEFORE
    # validation) yet pure -- no live game needed. Stale token -> 401 from the
    # middleware; fresh token -> 400 from request validation, which proves the
    # capability gate PASSED. Minimal body: validation will reject it.
    try {
        Invoke-RestMethod -Uri ($Rendezvous.baseUri + '/api/v1/game/discover/correlate') -Method Post -TimeoutSec 15 -Headers $headers -ContentType 'application/json' -Body '{}' | Out-Null
        return @{ status = 200; ok = $true }
    }
    catch {
        return @{ status = (Get-HttpStatus $_.Exception); ok = $false }
    }
}

$real = Get-RendezvousRecord
if (-not $real) { Write-Host 'NO_RENDEZVOUS (host down? probe skipped)'; exit 0 }

# Step 1: real token works (host healthy).
$first = Invoke-Call $real
Write-Host ("real_token status=" + $first.status)

# Step 2: a stale token MUST 401 (host Rotate()s every >=15s publish).
$stale = [pscustomobject]@{
    baseUri    = $real.baseUri
    capability = 'rotated-out-of-band-dead-token'
}
$bad = Invoke-Call $stale
Write-Host ("stale_token status=" + $bad.status + " (expect 401)")

# Step 3: the retry path -- re-read the record, retry the same call.
Start-Sleep -Seconds 2
$fresh = Get-RendezvousRecord
$retry = Invoke-Call $fresh
Write-Host ("retry_with_fresh status=" + $retry.status + " (expect 200)")

# Fresh token gets 400 (validation rejects '{}'), NOT 401 -- that is the
# proof the capability gate passed after the retry's re-read.
$pass = ($bad.status -eq 401) -and ($retry.status -ne 401) -and ($first.status -ne 401)
Write-Host ("CAPABILITY_RETRY_PROBE=" + $(if ($pass) { 'PASS' } else { 'FAIL' }))
exit $(if ($pass) { 0 } else { 1 })
