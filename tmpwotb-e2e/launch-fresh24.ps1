# FRESH24 live campaign launcher (detached). Decisive round: the
# CPU-liveness discriminator (e00a6bf) reports window_liveness=running|frozen,
# settling whether the 3 prior no-hits were mechanism artifacts (frozen
# window) or real absences of per-frame writes.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh24.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh24.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh24-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh24-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh24 launched pid=" + $ps.Id)
