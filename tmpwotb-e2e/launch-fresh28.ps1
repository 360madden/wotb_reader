# FRESH28 live campaign launcher (detached). Validates FRESH27b (d293dde):
# the attach-smoke now fires on the LAST sampling round and the loop breaks
# immediately, so NO samples are stamped under the kept debugger (FRESH27:
# sampling rounds 3-50 under x32dbg collapsed the z consensus 0.92->0.22 and
# no family was emitted). Expected: strong z consensus restored (0.9+,
# span ~275, shift ~0), smoke at round 49 keeps the debugger (keptAttached=
# True), trace logs reused_attached_debugger + read_values ok read=4 mapped=4
# + a REAL window_values_changed=true|false verdict. true+hit = first
# odwt-*.bin writer report.
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh28.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh28.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh28-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh28-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh28 launched pid=" + $ps.Id)
