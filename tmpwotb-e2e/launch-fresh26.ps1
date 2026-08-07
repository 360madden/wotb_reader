# FRESH26 live campaign launcher (detached). First trace under attach-once
# (fc128ca): the smoke keeps ONE debugger attached (scriptrun-resume,
# -KeepAttached) and the trace reuses it (-ReuseAttached skips its own attach)
# - eliminating the second-attach freeze that denied FRESH25's gate before the
# window opened. Watch for `reused_attached_debugger` and the first
# `window_values_changed=true|false` line on a window that actually runs.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh26.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh26.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh26-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh26-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh26 launched pid=" + $ps.Id)
