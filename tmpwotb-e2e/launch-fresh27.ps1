# FRESH27 live campaign launcher (detached). Re-validates attach-once after
# the FRESH26 dispatch + wire fixes (4b1611b): the smoke must report
# keptAttached=True (the -KeepAttached switch now actually reaches
# Invoke-AttachSmoke), and the trace must log `reused_attached_debugger`
# (no second attach). Read-FamilyValues now parses the real wire shape
# (absoluteAddress/observedValueHex), so watch for `read_values ok read=4
# mapped=4` and a REAL window_values_changed=true|false verdict with
# max_delta - the first one since the discriminator was built (FRESH24/25).
[CmdletBinding()]
param(
    [string]$LogPath = 'C:\work\wotb_reader\.data\od-049-fresh27.log',
    [string]$ErrPath = 'C:\work\wotb_reader\.data\od-049-fresh27.err.log',
    [string]$PidPath = 'C:\work\wotb_reader\.data\fresh27-pid.txt',
    [string]$ResultPath = 'C:\work\wotb_reader\.data\od-049-fresh27-result.json'
)
$ErrorActionPreference = 'Stop'
$ps = Start-Process -FilePath 'pwsh' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1',
    '-AttachSmokeOnFirstRound', '-StageViewpointOnly',
    '-ResultPath', $ResultPath
) -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath -WindowStyle Hidden -PassThru
Set-Content -LiteralPath $PidPath -Value $ps.Id
Write-Host ("fresh27 launched pid=" + $ps.Id)
