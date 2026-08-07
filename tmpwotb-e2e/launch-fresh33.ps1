# FRESH33 live campaign launcher (detached). Runs the FRESH32b diagnostic
# instrumentation (uncommitted): the trace now captures the RAW x64dbg log
# tab (broad filter: bpm results, errors, ODWT lines, breakpoint list,
# register output) after injection, each poll tick, and after `bl` + `r rip`
# pre-detach - so a zero-hit run becomes explainable (did bpm arm? did the
# script error? is the debuggee paused at a write site, and what is RIP?).
# Also includes the FRESH31 fixes (e323827): LastWriteTime anchor date +
# SetMemoryBreakpointCondition addr, 0 per armed address.
#
# Expected: trace_log_lines>0 with the bpm/breakpoint evidence in the
# .family.json report + od-wt-raw.log; either ODWT_HIT rip= evidence (the
# first real write-site) or the exact x64dbg error explaining why the BPs
# cannot fire on this build/game.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh33.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh33.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh33-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh33-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh33 launched pid=" + $ps.Id)
