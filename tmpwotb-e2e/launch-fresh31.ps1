# FRESH31 live campaign launcher (detached). Validates the FRESH30
# fail-closed fixes (929a66a): the battle-end log watcher
# (Test-BlitzBattleEnded) stops M1 rounds early with 'battle-ended-log'
# instead of trusting the decoded-duration timing model, and the pre-trace
# gate recheck skips the write-trace with a clean 'battle-ended-skip'
# verdict (exit 0) when the battle/gate is already gone.
#
# Expected outcome (per campaign spec): EITHER the trace runs inside the
# live window (values_changed=true + a real window) OR a clean
# battle-ended-skip verdict - NEVER a STOP_gate=Denied burn (exit 5).
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh31.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh31.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh31-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh31-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh31 launched pid=" + $ps.Id)
