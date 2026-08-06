# FRESH22 live campaign launcher (detached). The round where the z survivor
# clears every gate: re-derived 60s band floor (1/3 of the +-90s sweep) +
# 10-unit span floor let the FRESH21-class survivor through the solo gate.
# 50-round default + adaptive trace window.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh22.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh22.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh22-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh22-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh22 launched pid=" + $ps.Id)
