# FRESH30 live campaign launcher (detached). Validates FRESH29b (d10d607):
# the write-trace now captures ODWT_HIT evidence through a FILE channel
# (setlogfile + SetBreakpointLogFile) instead of only the lossy UIA log tab
# and flaky savedata. FRESH29 proved the memory BP FIRED (game paused at
# window_cpu_delta_ms=4375 in 25s while the armed x-field moved 20.94 units
# per values_changed=true) but hits=0 - the evidence was lost in harvest.
# Expected: log_harvest_hits>0 source=bp-log-file and the first
# odwt-*.bin hit report (RIP/RVA, registers) for the x@1.000 consensus.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh30.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh30.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh30-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh30-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh30 launched pid=" + $ps.Id)
