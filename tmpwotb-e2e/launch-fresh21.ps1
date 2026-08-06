# FRESH21 live campaign launcher (detached). Session 2 of the re-baselined
# 2-session M1 budget. 50-round default + adaptive trace window.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh21.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh21.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh21-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh21-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh21 launched pid=" + $ps.Id)
