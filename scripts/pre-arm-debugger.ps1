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
    - x64dbg: known install roots -> release\x96dbg.exe (the auto-selecting
      launcher) -> release\x64\x64dbg.exe.

  Bitness note: the target game is x86 (WOW64-observed 32-bit; the scanner's
  GuardedMemoryReader resolves ImageFileMachineI386 with 32-bit pointers).
  x64dbg's x64 build cannot properly debug a 32-bit process, so this script
  prefers release\x96dbg.exe: the launcher inspects the target and starts
  x32\x32dbg.exe for WOW64 processes (verified in x64dbg_launcher.cpp
  loadPid -> IsWow64Process -> load32).

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
        # x96dbg.exe auto-selects x32/x64 by target bitness (preferred for the
        # x86 game); fall back to the x64 build only if the launcher is absent.
        $l = Join-Path $r 'release\x96dbg.exe'
        if (Test-Path -LiteralPath $l) { return $l }
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
        # The launcher resolves to x32dbg.exe when the target is a 32-bit
        # (WOW64) process, so the window that opens is the x32 debugger.
        $launchedTool = if ($x64Leaf -eq 'x96dbg.exe') { 'x96dbg(->x32dbg)' } else { 'x64dbg' }
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
