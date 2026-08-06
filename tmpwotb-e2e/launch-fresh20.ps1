# FRESH20 live campaign launcher (detached). Session 1 of the re-baselined
# 2-session M1 budget (roadmap Descope gate re-baseline, 2026-08-06).
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh20.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh20.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh20-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh20-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh20 launched pid=" + $ps.Id)
