$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
$f = Get-ChildItem $dir -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $f) { Write-Output 'no_rendezvous'; exit 0 }
$rv = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
try {
    $st = Invoke-RestMethod -Uri ($rv.baseUri + '/api/v1/game/state') -Headers @{
        'X-WotBTreader-Capability' = [string]$rv.capability
    }
    Write-Output ("state=" + $st.verificationState + " reason=" + $st.reasonCode +
        " present=" + $st.gamePresent + " expires=" + $st.evidenceExpiresAtUtc +
        " observed=" + $st.observedAtUtc)
}
catch {
    Write-Output ('host_err=' + $_.Exception.Message)
}
