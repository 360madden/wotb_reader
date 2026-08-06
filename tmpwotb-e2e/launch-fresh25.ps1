# FRESH25 live campaign launcher (detached). Decisive round: the
# value-liveness discriminator (1c6690d) snapshots the armed addresses' float
# values at window start/end and reports window_values_changed=true|false,
# settling whether the battle world actually advances during the trace window
# (a playing replay moves z by tens of units per 25s; a paused/roster replay
# leaves it bit-identical).
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh25.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh25.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh25-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh25-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh25 launched pid=" + $ps.Id)
