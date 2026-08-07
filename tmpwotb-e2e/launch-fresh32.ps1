# FRESH32 live campaign launcher (detached). Validates the FRESH31
# capture fix (e323827): the generated x64dbg script now emits
# SetMemoryBreakpointCondition <addr>, 0 per armed address - the one line
# the FRESH9 probe (probe-membp-final.ps1, S1-S5 sentinels + static-hit.bin)
# proved but the trace omitted. FRESH29/31 both showed the pause-mid-window
# signature (window_cpu_delta_ms~4-5s in 25s, values_changed=true up to
# 22.46) with ZERO capture on every channel; in x64dbg memory-BP semantics
# condition 0 = break always.
#
# Expected: the first real ODWT_HIT addr=<addr> rip=<rip> line in
# $TEMP/od-wt-hits/od-wt-bp.log (or an odwt-<addr>.bin savedata hit file) -
# the write-site evidence that names the position-object writer for the
# armed x-family (score=1 consensus).
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh32.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh32.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh32-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh32-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh32 launched pid=" + $ps.Id)
