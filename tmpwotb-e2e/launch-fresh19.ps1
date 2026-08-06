# FRESH19 live campaign launcher (detached). Runs the autoloop with the
# FRESH19 session-prep flags; logs to .data\od-049-fresh19.log and writes the
# result to .data\od-049-fresh19-result.json. A pid file lets the operator
# confirm the launch and stop it if needed.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh19.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh19.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh19-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh19-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh19 launched pid=" + $ps.Id)
