#Requires -Version 5.1
<#
.SYNOPSIS
  Pre-arm the interactive debugger (Cheat Engine 7.7 or x64dbg) for an
  offset-discovery session (OD-RECOVERY-017 protocol).

.DESCRIPTION
  OD-RECOVERY-016 lost the interactive root window because no debugger was
  started until rolling reached <=10 survivors, after which the 120s research
  lease flipped EvidenceStale. This script locates the installed debugger
  (registry-backed, not a fixed-folder guess), verifies it, and can launch it
  attached to a running wotblitz.exe so Find-what-writes is available the
  moment rolling finishes.

  Discovery order:
    - Cheat Engine: uninstall registry (HKLM/HKCU + WOW6432Node) DisplayName
      matching "Cheat Engine" -> InstallLocation -> cheatengine-x86_64.exe.
      Fallbacks: C:\Program Files\Cheat Engine (the real 7.7 install root),
      C:\Program Files (x86)\Cheat Engine.
    - x64dbg: known install roots -> release\x64\x64dbg.exe.

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
    # Prefer x64dbg over Cheat Engine when both are present.
    [switch]$PreferX64Dbg,
    [string]$MarkerPath = $(Join-Path $env:TEMP 'od-prearmed-debugger.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-PreArm([string]$Message) {
    Write-Host ("prearm: " + $Message)
}

function Find-CheatEngine {
    $roots = @()
    $uninstallRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $uninstallRoots) {
        if (-not (Test-Path $root)) { continue }
        $item = Get-ChildItem $root -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
            Where-Object {
                $null -ne $_.PSObject.Properties['DisplayName'] -and
                [string]$_.DisplayName -match 'Cheat Engine'
            } |
            Select-Object -First 1
        if ($item -and $item.InstallLocation -and (Test-Path $item.InstallLocation)) {
            $roots += $item.InstallLocation
        }
    }
    $roots += 'C:\Program Files\Cheat Engine', 'C:\Program Files (x86)\Cheat Engine'
    foreach ($r in ($roots | Select-Object -Unique)) {
        foreach ($exe in @('cheatengine-x86_64.exe', 'Cheat Engine.exe')) {
            $p = Join-Path $r $exe
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    return $null
}

function Find-X64Dbg {
    $roots = @('C:\work\tools\x64dbg', 'C:\x64dbg', 'C:\tools\x64dbg')
    foreach ($r in $roots) {
        $p = Join-Path $r 'release\x64\x64dbg.exe'
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

try {
    $ceExe = Find-CheatEngine
    $x64Exe = Find-X64Dbg
    if (-not $ceExe -and -not $x64Exe) {
        Write-PreArm 'FAILED_no_debugger_found'
        exit 1
    }

    $ceLeaf = if ($ceExe) { Split-Path -Leaf $ceExe } else { 'none' }
    $x64Leaf = if ($x64Exe) { Split-Path -Leaf $x64Exe } else { 'none' }
    Write-PreArm ("ce=" + $ceLeaf + " x64dbg=" + $x64Leaf)

    $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1

    $launchedTool = $null
    if ($AutoAttach -and $game) {
        $tool = if ($PreferX64Dbg -and $x64Exe) { 'x64dbg' } elseif ($ceExe) { 'ce' } elseif ($x64Exe) { 'x64dbg' } else { $null }
        if ($tool -eq 'x64dbg') {
            $p = Start-Process -FilePath $x64Exe -ArgumentList @('-p', "$($game.Id)") -PassThru
            $launchedTool = 'x64dbg'
            Write-PreArm ("launched_x64dbg_attach pid=" + $game.Id + " process=" + $p.Id)
        }
        elseif ($tool -eq 'ce') {
            # CE 7.x may accept -p <pid> to open the process on startup, but
            # support is version-dependent: verify during the live session that
            # Find-what-writes is actually attached before relying on it.
            # The marker's launchedTool field records what was started.
            $p = Start-Process -FilePath $ceExe -ArgumentList @('-p', "$($game.Id)") -PassThru
            $launchedTool = 'cheatengine'
            Write-PreArm ("launched_cheatengine_attach pid=" + $game.Id + " process=" + $p.Id)
        }
    }
    elseif ($AutoAttach -and -not $game) {
        Write-PreArm 'autoattach_skipped_no_game_process'
    }

    $marker = [pscustomobject]@{
        verifiedAtUtc  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        cheatEngineExe = $ceExe
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
