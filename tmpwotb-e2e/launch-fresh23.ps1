# FRESH23 live campaign launcher (detached). Span-first solo selection +
# top-4 consensus arming (6f36067): the span-275.4 z consensus class wins
# the solo gate and up to 4 addresses are armed (DR0-DR3) so the per-frame
# writer fires the first odwt-*.bin hit report.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh23.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh23.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh23-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh23-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh23 launched pid=" + $ps.Id)
