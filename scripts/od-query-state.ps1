#Requires -Version 5.1
# Temporary helper (OD-018 session): query gate state from the research host.
param(
    [switch]$Snapshot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
$file = Get-ChildItem $dir -File -ErrorAction Stop |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$rv = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json

$headers = @{
    'X-WotBTreader-Capability' = [string]$rv.capability
    'Content-Type'             = 'application/json'
}

$state = Invoke-RestMethod -Uri "$($rv.baseUri)/api/v1/game/state" -Headers $headers
Write-Host ("state=" + $state.verificationState + " reason=" + $state.reasonCode)

if ($Snapshot) {
    $body = @{ valueKind = 'Double'; valueSize = 8; alignment = 8 } | ConvertTo-Json
    $snap = Invoke-RestMethod -Uri "$($rv.baseUri)/api/v1/game/discover/snapshot" -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body
    if ($snap.PSObject.Properties['error']) {
        Write-Host ("snapshot_error=" + $snap.error)
    }
    else {
        Write-Host ("snapshot_session=" + $snap.sessionId + " retained=" + $snap.retainedCount + " increased=" + $snap.increasedCount)
        try {
            $null = Invoke-RestMethod -Uri "$($rv.baseUri)/api/v1/game/discover/session/$($snap.sessionId)" `
                -Method Delete -Headers $headers
            Write-Host 'snapshot_discarded'
        }
        catch { Write-Host 'snapshot_discard_failed' }
    }
}
