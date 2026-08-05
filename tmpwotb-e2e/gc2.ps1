$rv = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$j = Get-Content $rv.FullName -Raw | ConvertFrom-Json
try { $s = Invoke-RestMethod -Uri ($j.baseUri + '/api/v1/game/state') -Headers @{ 'X-WotBTreader-Capability' = [string]$j.capability } -TimeoutSec 5; Write-Host ('GATE=' + $s.verificationState + ' reason=' + $s.reasonCode) } catch { Write-Host ('GATE_ERR') }
