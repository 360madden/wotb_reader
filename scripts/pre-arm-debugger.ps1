#Requires -Version 5.1
<#
.SYNOPSIS
  Pre-arm the interactive debugger (x64dbg) for an offset-discovery session
  (OD-RECOVERY-017 protocol).

.DESCRIPTION
  OD-RECOVERY-016 lost the interactive root window because no debugger was
  started until rolling reached <=10 survivors, after which the 120s research
  lease flipped EvidenceStale. This script locates the installed x64dbg
  (known install roots), verifies it, and can launch it attached to a running
  wotblitz.exe so Find-what-writes (hardware write breakpoints) is available
  the moment rolling finishes.

  Cheat Engine is no longer part of the pipeline (removed 2026-08-03): its
  automated write-BP path was ruled out by OD-RECOVERY-020, and its remaining
  interactive role was superseded by x64dbg.

  Discovery order:
    - x64dbg: known install roots -> release\x32\x32dbg.exe (the x86 build,
      correct for the x86 game target) -> release\x64\x64dbg.exe fallback.

  Bitness note: the target game is x86 (WOW64-observed 32-bit; the scanner's
  GuardedMemoryReader resolves ImageFileMachineI386 with 32-bit pointers).
  We launch x32\x32dbg.exe DIRECTLY instead of the release\x96dbg.exe
  launcher: the launcher worked in a headless smoke test but in the live
  OD-044 run it stayed alive without ever spawning the debugger (its
  first-run config-dialog / elevation state machine is a live failure mode
  we cannot see), so the pre-arm was left with no debugger window. The
  game bitness is known and fixed, so the launcher's auto-select adds
  nothing but a failure surface -- skip it.

  Never logs private paths; the marker file (default %TEMP%\od-prearmed-debugger.json)
  holds resolved tool paths locally.

.EXITCODES
  0  At least one debugger located (and launched if -AutoAttach + game running)
  1  No debugger found
  2  Unexpected error
#>
[CmdletBinding()]
param(
    # Launch the preferred debugger attached to the running wotblitz process.
    [switch]$AutoAttach,
    [string]$MarkerPath = $(Join-Path $env:TEMP 'od-prearmed-debugger.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-PreArm([string]$Message) {
    Write-Host ("prearm: " + $Message)
}

function Find-X64Dbg {
    $roots = @('C:\work\tools\x64dbg', 'C:\x64dbg', 'C:\tools\x64dbg')
    foreach ($r in $roots) {
        # x32dbg.exe (x86 build) is the correct debugger for the x86 game;
        # launch it directly (see bitness note above). x64 build as fallback.
        $x = Join-Path $r 'release\x32\x32dbg.exe'
        if (Test-Path -LiteralPath $x) { return $x }
        $p = Join-Path $r 'release\x64\x64dbg.exe'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

try {
    $x64Exe = Find-X64Dbg
    if (-not $x64Exe) {
        Write-PreArm 'FAILED_no_debugger_found'
        exit 1
    }

    $x64Leaf = Split-Path -Leaf $x64Exe
    Write-PreArm ("x64dbg=" + $x64Leaf)

    $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1

    $launchedTool = $null
    if ($AutoAttach -and $game) {
        $p = Start-Process -FilePath $x64Exe -ArgumentList @('-p', "$($game.Id)") -PassThru
        # Direct x32dbg.exe launch (x86 build) for the x86 game; the window
        # that opens is x32dbg, which the write-trace process detection matches.
        # Derive the label from the resolved path so an x64-fallback machine
        # (no x32 build) does not misreport the attached tool.
        $launchedTool = if ($x64Leaf -eq 'x32dbg.exe') { 'x32dbg' } else { 'x64dbg' }
        Write-PreArm ("launched_x64dbg_attach pid=" + $game.Id + " process=" + $p.Id)
    }
    elseif ($AutoAttach -and -not $game) {
        Write-PreArm 'autoattach_skipped_no_game_process'
    }

    $marker = [pscustomobject]@{
        verifiedAtUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        x64dbgExe      = $x64Exe
        launchedTool   = $launchedTool
        gamePid        = if ($game) { $game.Id } else { $null }
    } | ConvertTo-Json
    Set-Content -LiteralPath $MarkerPath -Value $marker -Encoding ascii
    Write-PreArm ("marker=" + $MarkerPath)

    Write-PreArm 'OK debugger_armed'
    exit 0
}
catch {
    Write-PreArm ("FAILED_unexpected=" + $_.Exception.Message)
    exit 2
}
